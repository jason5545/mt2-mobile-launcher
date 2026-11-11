using System.Numerics;
using Mt2Mobile.Importer.Serialized;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer;

/// <summary>
/// supplement.py：從原版 build 補 AssetRipper 漏掉的 [SerializeReference] 欄位與空殼 ScriptableObject，寫回匯出的 JSON
///（做法、對應方式、寫法見 Python 版檔頭）。Python 版在這一步把 UnityPy 的 C 加速版關掉，所以 typetree 用 Python 模式讀。
/// </summary>
public sealed class Supplement(ImportContext ctx)
{
    private static readonly string[] MbHeader = ["m_GameObject", "m_Enabled", "m_Script", "m_Name"];
    private const int Align = 0x4000;
    private static readonly HashSet<long> NullRids = [-1, -2, 0];
    private static readonly BigInteger Big = BigInteger.Pow(2, 53);
    public static readonly PyDict BuiltinFiles = new()
    {
        ["unity default resources"] = "0000000000000000e000000000000000",
        ["unity_builtin_extra"] = "0000000000000000f000000000000000",
    };
    private static readonly Dictionary<string, (string table, string fileId)> ExportTables = new()
    {
        ["Texture2D"] = ("textures", "2800000"),
        ["Sprite"] = ("sprites", "21300000"),
        ["Material"] = ("materials", "2100000"),
        ["MonoBehaviour"] = ("monobehaviours", "11400000"),
        ["Font"] = ("fonts", "12800000"),
    };
    private static readonly HashSet<string> SoBaseKeys =
        ["src", "fileID", "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier", "$build", "$subObjects"];

    public static object? BigToStr(object? v) => v switch
    {
        bool b => b,
        long l when BigInteger.Abs(l) >= Big => Py.Str(l),
        BigInteger bi when BigInteger.Abs(bi) >= Big => Py.Str(bi),
        PyDict d => Map(d, BigToStr),
        List<object?> l => l.Select(BigToStr).ToList(),
        _ => v,
    };

    private static PyDict Map(PyDict d, Func<object?, object?> f)
    {
        var o = new PyDict(d.Count);
        foreach (var (k, x) in d) o[k] = f(x);
        return o;
    }

    /// <summary>SerializeReference 的 {rid} 換成實際物件（{"$type", "$ns", "$asm", ...欄位}），拿掉 references 區</summary>
    public static PyDict InlineReferences(PyDict data)
    {
        var registry = new Dictionary<object, PyDict>();
        var refs = Assets.Dict(data.Get("references"));
        foreach (var r in Assets.AsList(refs.Get("RefIds", new List<object?>())).Cast<PyDict>())
            registry[Py.ToLong(r["rid"])] = r;

        object? Walk(object? v, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("SerializeReference 巢狀太深（可能有循環）");
            if (v is PyDict d)
            {
                if (d.Count == 1 && d.ContainsKey("rid"))
                {
                    long rid = Py.ToLong(d["rid"]);
                    if (NullRids.Contains(rid) || !registry.TryGetValue(rid, out var r)) return null;
                    var t = (PyDict)r["type"]!;
                    if (Py.Eq(t["class"], "")) return null;
                    var o = new PyDict { ["$type"] = t["class"], ["$ns"] = t["ns"], ["$asm"] = t["asm"] };
                    var data2 = Py.Truthy(r.Get("data")) ? r["data"] : new PyDict();
                    foreach (var (k, x) in (PyDict)Walk(data2, depth + 1)!) o[k] = x;
                    return o;
                }
                var od = new PyDict(d.Count);
                foreach (var (k, x) in d)
                    if (!Py.Eq(k, "references")) od[k] = Walk(x, depth + 1);
                return od;
            }
            if (v is List<object?> l) return l.Select(x => Walk(x, depth + 1)).ToList();
            return v;
        }
        return (PyDict)Walk(data, 0)!;
    }

    // ───────── 讀 build ─────────

    private sealed class BuildReader
    {
        public readonly string GameData;
        private readonly Dictionary<string, BuildFile?> _files = new(StringComparer.Ordinal);
        private List<(BuildFile sf, Dictionary<string, List<long>> container)>? _containers;
        public readonly List<string> Serialized;
        private readonly MonoTypeTrees _gen;
        private readonly Dictionary<(string, string, bool), TtNode> _nodes = [];
        private readonly Dictionary<(string, string), bool> _hasRefs = [];
        private readonly Dictionary<(string, long), PyTuple?> _scripts = [];
        private readonly BuildEnv _env;
        public const bool Boost = false;

        public BuildReader(string gameData)
        {
            GameData = gameData;
            _env = new BuildEnv(gameData);
            Serialized = Directory.EnumerateFiles(gameData).Select(PyPath.Name)
                .Where(n => (n.StartsWith("level", StringComparison.Ordinal) && n.Length > 5 && n[5..].All(char.IsAsciiDigit)) || PyPath.Suffix(n) == ".assets")
                .Order(PyStr.Comparer).ToList();
            var ggm = File("globalgamemanagers.assets")!;
            string version = ggm.Version.ToString().Split('c')[0];  // 2022.3.3f1c1 → 2022.3.3f1
            _gen = new MonoTypeTrees(version);
            foreach (var dll in PyPath.Glob(PyPath.Join(gameData, "Managed"), "*.dll").Order(PyPath.Comparer))
                _gen.LoadDll(System.IO.File.ReadAllBytes(dll));
        }

        /// <summary>build 根目錄的序列化檔，或 Addressables bundle 裡的 CAB-…；不存在回 null（Python 版是例外，呼叫端都當成找不到）</summary>
        public BuildFile? File(string name)
        {
            if (_files.TryGetValue(name, out var f)) return f;
            if (name.StartsWith("CAB-", StringComparison.Ordinal))
            {
                Bundles();
                return _files.TryGetValue(name, out f) ? f : throw new KeyNotFoundException($"找不到 bundle 裡的 {name}");
            }
            string p = PyPath.Join(GameData, name);
            f = System.IO.File.Exists(p) ? _env.Load(p)[0] : throw new FileNotFoundException(p);
            _files[name] = f;
            return f;
        }

        public List<(BuildFile sf, Dictionary<string, List<long>> container)> Bundles()
        {
            if (_containers is null)
            {
                _containers = [];
                foreach (var p in PyPath.RGlob(PyPath.Join(GameData, "StreamingAssets"), "*.bundle").Order(PyPath.Comparer))
                {
                    foreach (var sf in _env.Load(p))
                    {
                        _files[sf.Name] = sf;
                        var ab = sf.Objects.FirstOrDefault(o => o.TypeName == "AssetBundle");
                        if (ab is null) continue;
                        var container = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                        var order = new List<string>();
                        foreach (PyTuple pair in Assets.AsList(ab.ReadTypeTree(Boost)["m_Container"]).Cast<PyTuple>())
                        {
                            string path = ((string)pair.Items[0]!).ToLowerInvariant();
                            if (!container.TryGetValue(path, out var l)) container[path] = l = [];
                            l.Add(Py.ToLong(((PyDict)((PyDict)pair.Items[1]!)["asset"]!)["m_PathID"]));
                        }
                        _containers.Add((sf, container));
                    }
                }
            }
            return _containers;
        }

        public static List<string> Externals(BuildFile sf) => sf.Externals();

        /// <summary>PPtr 的 m_FileID → 序列化檔（0 是自己）；Unity 內建資源檔與找不到的回 null</summary>
        public BuildFile? Target(BuildFile sf, long fid)
        {
            if (fid == 0) return sf;
            try
            {
                return File(Externals(sf)[(int)(fid - 1)]);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public PyDict MonoBehaviourHead(BuildObject obj)
            => obj.ReadTypeTree(Boost, TypeTrees.Tpk(114, obj.File.Version), checkRead: false);

        /// <summary>MonoBehaviour 的腳本 →（組件, 命名空間, 類別）</summary>
        public PyTuple? ScriptOf(BuildFile sf, BuildObject obj)
        {
            var head = MonoBehaviourHead(obj);
            var script = (PyDict)head["m_Script"]!;
            long fid = Py.ToLong(script["m_FileID"]), pid = Py.ToLong(script["m_PathID"]);
            var tsf = Target(sf, fid);
            if (tsf is null || pid == 0) return null;
            var key = (tsf.Name, pid);
            if (!_scripts.TryGetValue(key, out var r))
            {
                var o = tsf.Get(pid);
                if (o is not null && o.TypeName == "MonoScript")
                {
                    var s = o.ReadTypeTree(Boost);
                    r = new PyTuple(s["m_AssemblyName"], s["m_Namespace"], s["m_ClassName"]);
                }
                _scripts[key] = r;
            }
            return r;
        }

        private static bool IsReadMapType(string t) => t is "SInt8" or "UInt8" or "char" or "short" or "SInt16" or "unsigned short" or "UInt16"
            or "int" or "SInt32" or "unsigned int" or "UInt32" or "Type*" or "long long" or "SInt64" or "unsigned long long" or "UInt64"
            or "FileSize" or "float" or "double" or "bool" or "string" or "TypelessData";

        /// <summary>supplement.py 的 _fix：生成器跟 Unity 實際序列化不同的三處</summary>
        private static TtNode Fix(List<(string type, string name, int level, int flag)> nodes, bool dropHeader)
        {
            var o = new List<(string, string, int, int)>();
            int i = 0;
            while (i < nodes.Count)
            {
                var n = nodes[i];
                int end = i + 1;
                while (end < nodes.Count && nodes[end].level > n.level) end++;
                if (dropHeader && n.level == 1 && MbHeader.Contains(n.name))
                {
                    i = end;
                    continue;
                }
                if (n.type.StartsWith("LazyLoadReference", StringComparison.Ordinal))
                {
                    o.Add(("PPtr<Object>", n.name, n.level, n.flag));
                    o.Add(("int", "m_FileID", n.level + 1, 0));
                    o.Add(("SInt64", "m_PathID", n.level + 1, 0));
                    i = end;
                    continue;
                }
                string typ = n.type;
                if (end - i > 1 && nodes[i + 1].type == "Array" && IsReadMapType(typ))
                {
                    var elem = i + 3 < end ? nodes[i + 3] : default;
                    bool hasElem = i + 3 < end;
                    if (!(typ == "string" && hasElem && elem.type == "char")) typ = "vector";
                }
                int flag = n.flag;
                if (!dropHeader && n.level == 1 && n.name == "m_Enabled") flag |= Align;
                o.Add((typ, n.name, n.level, flag));
                i++;
            }
            return TtNode.FromList(o);
        }

        private static string Dll(string asm) => asm.EndsWith(".dll", StringComparison.Ordinal) ? asm : asm + ".dll";

        public TtNode Nodes(string asm, string ns, string cls, bool monobehaviour)
        {
            var key = (asm, ns.Length > 0 ? $"{ns}.{cls}" : cls, monobehaviour);
            if (!_nodes.TryGetValue(key, out var n))
                _nodes[key] = n = Fix(_gen.GetNodes(Dll(asm), key.Item2), !monobehaviour);
            return n;
        }

        /// <summary>檔案有附 type tree（bundle）就用檔案的，否則問生成器</summary>
        public TtNode? RefNode(PyDict refObj, BuildFile assetsFile)
        {
            var t = (PyDict)refObj["type"]!;
            string cls = (string)t["class"]!, ns = (string)t["ns"]!, asm = (string)t["asm"]!;
            if (cls == "") return null;
            foreach (var rt in assetsFile.RefTypes)
                if (rt.ClassName == cls && rt.Namespace == ns && rt.AsmName == asm && rt.OldType.Nodes.Count > 0)
                    return TypeTrees.FromAssetRipper(rt.OldType.Nodes);
            return Nodes(asm, ns, cls, monobehaviour: false);
        }

        public bool HasSerializeReference(string asm, string ns, string cls)
        {
            var key = (asm, ns.Length > 0 ? $"{ns}.{cls}" : cls);
            if (!_hasRefs.TryGetValue(key, out var r))
            {
                try
                {
                    r = _gen.GetNodes(Dll(asm), key.Item2).Any(n => n.type == "managedReference");
                }
                catch (Exception)
                {
                    r = false;
                }
                _hasRefs[key] = r;
            }
            return r;
        }

        /// <summary>整個 MonoBehaviour，SerializeReference 已經換成實際物件</summary>
        public PyDict ReadMonoBehaviour(BuildFile sf, BuildObject obj)
        {
            if (sf.EnableTypeTree)
                return InlineReferences(obj.ReadTypeTree(Boost, refTypes: r => RefNode(r, sf)));
            var script = ScriptOf(sf, obj) ?? throw new InvalidOperationException("找不到 MonoScript");
            return InlineReferences(obj.ReadTypeTree(Boost, Nodes((string)script.Items[0]!, (string)script.Items[1]!, (string)script.Items[2]!, true), checkRead: true, refTypes: r => RefNode(r, sf)));
        }

        public PyDict Read(BuildObject obj, bool checkRead = true) => obj.ReadTypeTree(Boost, checkRead: checkRead);

        public string? PeekName(BuildObject obj) => obj.PeekName(Boost);
    }

    // ───────── build 物件 → 匯出參照 ─────────

    private static bool Close(object? a, object? b)
    {
        if (a is bool || b is bool) return Py.ToLong(a) == Py.ToLong(b);
        if (Py.IsNumber(a) && Py.IsNumber(b))
        {
            double x = Py.ToDouble(a!), y = Py.ToDouble(b!);
            return Math.Abs(x - y) <= 1e-4 * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)));
        }
        return Py.Eq(a, b);
    }

    private static readonly HashSet<string> ScalarSkip = ["src", "file", "fileID", "m_Script", "m_GameObject", "m_EditorClassIdentifier", "m_EditorHideFlags", "serializedVersion"];

    /// <summary>攤平成 {路徑: 數值或字串}；PPtr、參照、長陣列不比</summary>
    private static PyDict Scalars(object? v, string prefix = "", int depth = 0, PyDict? o = null)
    {
        o ??= new PyDict();
        if (depth > 3) return o;
        if (v is PyDict d)
        {
            var keys = d.Keys.ToList();
            bool isPPtr = (keys.Count == 2 && d.ContainsKey("m_FileID") && d.ContainsKey("m_PathID"))
                || (d.ContainsKey("fileID") && keys.All(k => k is "fileID" or "guid" or "type"));
            if (isPPtr) return o;
            foreach (var (k, x) in d)
            {
                if (k is string s && ScalarSkip.Contains(s)) continue;
                Scalars(x, prefix.Length > 0 ? $"{prefix}.{Py.Str(k)}" : Py.Str(k), depth + 1, o);
            }
        }
        else if ((v is bool or long or BigInteger or double) || (v is string str && Py.Len(str) <= 200))
        {
            o[prefix] = v;
        }
        return o;
    }

    private static IEnumerable<string> Guids(object? v)
    {
        if (v is PyDict d)
        {
            if (d.Get("guid") is string g) yield return g;
            foreach (var x in d.Values)
                foreach (var r in Guids(x))
                    yield return r;
        }
        else if (v is List<object?> l)
        {
            foreach (var x in l)
                foreach (var r in Guids(x))
                    yield return r;
        }
    }

    private sealed class ExportResolver
    {
        private readonly BuildReader _reader;
        private readonly string _out;
        private readonly Dictionary<(string, object?), List<(string guid, PyDict rec)>> _candidates = new(new TupleKey());
        private readonly Dictionary<string, long> _usage = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, long), (PyDict? ref_, string status)> _cache = [];
        public readonly PyDict Stats = new();
        public readonly List<object?> Problems = [];

        private sealed class TupleKey : IEqualityComparer<(string, object?)>
        {
            public bool Equals((string, object?) a, (string, object?) b) => a.Item1 == b.Item1 && Py.Eq(a.Item2, b.Item2);
            public int GetHashCode((string, object?) k) => HashCode.Combine(k.Item1, k.Item2 is string s ? s.GetHashCode() : 0);
        }

        public ExportResolver(BuildReader reader, string outRoot)
        {
            _reader = reader;
            _out = outRoot;
            foreach (var (typ, (table, _)) in ExportTables)
            {
                string p = PyPath.Join(outRoot, $"assets/{table}.json");
                if (!File.Exists(p)) continue;
                foreach (var (guid, recObj) in PyJson.LoadDict(p))
                {
                    var rec = (PyDict)recObj!;
                    var name = rec.ContainsKey("name") ? rec.Get("name") : rec.Get("m_Name");
                    var key = (typ, name);
                    if (!_candidates.TryGetValue(key, out var l)) _candidates[key] = l = [];
                    l.Add(((string)guid!, rec));
                }
            }
            // 偏好：場景與 prefab 參照最多次的 guid（只算 AssetRipper 匯出的參照）
            foreach (var f in PyPath.RGlob(PyPath.Join(outRoot, "scenes"), "*.json"))
            {
                if (!File.Exists(f)) continue;
                var data = PyJson.LoadFile(f);
                if (data is not PyDict dd || dd.Get("components") is not PyDict comps) continue;
                foreach (var m in comps.Values)
                {
                    if (m is PyDict md && md.ContainsKey("$build")) continue;
                    foreach (var g in Guids(m)) _usage[g] = _usage.GetValueOrDefault(g) + 1;
                }
            }
        }

        public void Known(BuildFile sf, long pathId, PyDict ref_, string status) => _cache[(sf.Name, pathId)] = (ref_, status);

        private PyDict Record(string typ, PyDict rec)
            => typ == "MonoBehaviour" ? PyJson.LoadDict(PyPath.Join(_out, $"assets/{rec["file"]}")) : rec;

        private PyDict BuildData(BuildFile sf, BuildObject obj)
        {
            if (obj.TypeName == "MonoBehaviour")
            {
                try
                {
                    return _reader.ReadMonoBehaviour(sf, obj);
                }
                catch (Exception)
                {
                    return new PyDict();
                }
            }
            return _reader.Read(obj);
        }

        private static PyDict PairsToDict(object? v)
        {
            if (v is PyDict d) return d;
            var o = new PyDict();
            foreach (var item in Assets.AsList(v))
            {
                if (item is not PyTuple { Items.Length: 2 } t) throw new InvalidCastException("cannot convert dictionary update sequence element");
                o[t.Items[0]] = t.Items[1];
            }
            return o;
        }

        /// <summary>set(x)：dict 的鍵；list 的元素（元素不能是 dict／list）</summary>
        private static IEnumerable<object?> SetOf(object? v) => v switch
        {
            PyDict d => d.Keys,
            List<object?> l => l.Select(x => x is PyDict or List<object?> ? throw new InvalidCastException($"unhashable type: '{(x is PyDict ? "dict" : "list")}'") : x),
            _ => throw new InvalidCastException("object is not iterable"),
        };

        private static object? Index(object? v, object? key) => v switch
        {
            PyDict d => d[key],
            List<object?> l => l[(int)Py.ToLong(key)],
            _ => throw new InvalidCastException("object is not subscriptable"),
        };

        private static bool Equivalent(string typ, PyDict build, PyDict rec)
        {
            switch (typ)
            {
                case "Texture2D":
                    return Py.Eq(build.Get("m_Width"), rec.Get("width")) && Py.Eq(build.Get("m_Height"), rec.Get("height"));
                case "Sprite":
                {
                    var pairs = new[]
                    {
                        ((PyDict)build["m_Rect"]!, (PyDict)rec["m_Rect"]!),
                        ((PyDict)((PyDict)build["m_RD"]!)["textureRect"]!, (PyDict)((PyDict)rec["m_RD"]!)["textureRect"]!),
                    };
                    return pairs.All(p => new[] { "x", "y", "width", "height" }.All(k => Close(p.Item1[k], p.Item2[k])));
                }
                case "Material":
                {
                    var props = (PyDict)build["m_SavedProperties"]!;
                    var bf = PairsToDict(props["m_Floats"]);
                    var bc = PairsToDict(props["m_Colors"]);
                    var rp = (PyDict)rec["m_SavedProperties"]!;
                    var rf = rp["m_Floats"];
                    var rc = rp["m_Colors"];
                    var kf = bf.Keys.Concat(SetOf(rf)).Distinct(KeyEq.Instance).ToList();
                    foreach (var k in kf)
                    {
                        bool inR = rf is PyDict rfd ? rfd.ContainsKey(k) : Assets.AsList(rf).Any(x => Py.Eq(x, k));
                        if (!bf.ContainsKey(k) || !inR || !Close(bf[k], Index(rf, k))) return false;
                    }
                    var kc = bc.Keys.Concat(SetOf(rc)).Distinct(KeyEq.Instance).ToList();
                    foreach (var k in kc)
                    {
                        bool inR = rc is PyDict rcd ? rcd.ContainsKey(k) : Assets.AsList(rc).Any(x => Py.Eq(x, k));
                        if (!bc.ContainsKey(k) || !inR) return false;
                        var b = (PyDict)bc[k]!;
                        var r = Index(rc, k);
                        if ("rgba".Any(c => !Close(b[c.ToString()], Index(r, c.ToString())))) return false;
                    }
                    return true;
                }
                case "Font":
                    return Py.Eq((long)(Py.Truthy(build.Get("m_FontData")) ? Assets.AsList(build["m_FontData"]).Count : 0), rec.Get("bytes"));
                default:
                {
                    var a = Scalars(build);
                    var b = Scalars(rec);
                    var common = a.Keys.Where(b.ContainsKey).ToList();
                    return common.Count > 0 && common.All(k => Close(a[k], b[k]));
                }
            }
        }

        private sealed class KeyEq : IEqualityComparer<object?>
        {
            public static readonly KeyEq Instance = new();
            public new bool Equals(object? a, object? b) => Py.Eq(a, b);
            public int GetHashCode(object? o) => o is string s ? s.GetHashCode() : 0;
        }

        public (PyDict? ref_, string status) Resolve(BuildFile sf, long pathId)
        {
            var key = (sf.Name, pathId);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var obj = sf.Get(pathId);
            (PyDict? ref_, string status) result;
            if (obj is null) result = (null, "build 裡沒有這個物件");
            else
            {
                string typ = obj.TypeName;
                string? name = _reader.PeekName(obj);
                var cands = _candidates.GetValueOrDefault((typ, name)) ?? [];
                if (!ExportTables.ContainsKey(typ)) result = (null, $"不處理的型別 {typ}");
                else if (cands.Count == 0) result = (null, $"匯出裡沒有 {typ}「{Py.Str(name)}」");
                else if (cands.Count == 1 && typ == "MonoBehaviour")
                    result = (new PyDict { ["guid"] = cands[0].guid, ["fileID"] = ExportTables[typ].fileId }, "唯一");
                else
                {
                    var build = BuildData(sf, obj);
                    var same = cands.Where(c => Equivalent(typ, build, Record(typ, c.rec))).ToList();
                    if (same.Count == 0) result = (null, $"{typ}「{Py.Str(name)}」有 {cands.Count} 個候選，內容都對不上");
                    else
                    {
                        same = same.OrderBy(c => -_usage.GetValueOrDefault(c.guid))
                            .ThenBy(c => c.rec.Get("src", "") as string ?? "", PyStr.Comparer).ToList();
                        result = (new PyDict { ["guid"] = same[0].guid, ["fileID"] = ExportTables[typ].fileId }, same.Count == 1 ? "唯一" : "副本");
                    }
                }
            }
            _cache[key] = result;
            string stat = result.ref_ is not null ? result.status : "失敗";
            Stats[stat] = Py.ToLong(Stats.Get(stat, 0L)) + 1;
            if (result.ref_ is null)
                Problems.Add(new PyDict { ["file"] = sf.Name, ["pathID"] = pathId.ToString(Py.Inv), ["error"] = result.status });
            return result;
        }
    }

    // ───────── 補資料 ─────────

    private sealed class Supplementer(BuildReader reader, ExportResolver resolver)
    {
        public readonly PyDict Objects = new();
        public readonly List<object?> ObjectFailures = [];
        public readonly PyDict Components = new();
        public readonly List<object?> Failed = [];
        public readonly PyDict Refs = new();
        public readonly List<object?> AmbiguousPrefabs = [];
        public readonly List<object?> UnmatchedPrefabs = [];

        private static void Inc(PyDict d, object? k) => d[k] = Py.ToLong(d.Get(k, 0L)) + 1;

        /// <summary>PPtr → 匯出參照</summary>
        public object? Convert(object? data, BuildFile sf, Dictionary<long, object?> localMap)
        {
            object? Walk(object? v)
            {
                if (v is PyDict d)
                {
                    if (d.Count == 2 && d.ContainsKey("m_FileID") && d.ContainsKey("m_PathID"))
                    {
                        long fid = Py.ToLong(d["m_FileID"]), pid = Py.ToLong(d["m_PathID"]);
                        if (pid == 0)
                        {
                            Inc(Refs, "空");
                            return null;
                        }
                        if (fid == 0 && localMap.TryGetValue(pid, out var local))
                        {
                            Inc(Refs, "本地");
                            return new PyDict { ["fileID"] = local };
                        }
                        string? ext = fid > 0 ? BuildReader.Externals(sf)[(int)(fid - 1)] : null;
                        if (ext is not null && BuiltinFiles.ContainsKey(ext))
                        {
                            Inc(Refs, "內建資源");
                            return new PyDict { ["guid"] = BuiltinFiles[ext], ["fileID"] = pid.ToString(Py.Inv) };
                        }
                        var targetSf = reader.Target(sf, fid);
                        if (targetSf is null)
                        {
                            Inc(Refs, "外部檔不在 build 裡");
                            return new PyDict { ["build"] = $"{Py.Str(ext)}#{pid}", ["unresolved"] = true };
                        }
                        var (r, status) = resolver.Resolve(targetSf, pid);
                        Inc(Refs, r is not null ? $"外部（{status}）" : "外部對不到");
                        return r ?? new PyDict { ["build"] = $"{targetSf.Name}#{pid}", ["unresolved"] = true };
                    }
                    var o = new PyDict(d.Count);
                    foreach (var (k, x) in d) o[k] = Walk(x);
                    return o;
                }
                if (v is List<object?> l) return l.Select(Walk).ToList();
                return v;
            }
            return Walk(data);
        }

        private static PyDict Fields(PyDict data)
        {
            var o = new PyDict();
            foreach (var (k, v) in data)
                if (!(k is "m_GameObject" or "m_Enabled" or "m_Script" or "m_Name")) o[k] = v;
            return o;
        }

        // ScriptableObject
        public long DoObjects(string outRoot)
        {
            string assets = PyPath.Join(outRoot, "assets");
            var table = PyJson.LoadDict(PyPath.Join(assets, "monobehaviours.json"));
            var byContainer = new Dictionary<string, (BuildFile, long)>(StringComparer.Ordinal);
            foreach (var (sf, container) in reader.Bundles())
                foreach (var (path, pids) in container)
                    foreach (var pid in pids)
                        if (sf.Get(pid)!.TypeName == "MonoBehaviour") byContainer[path] = (sf, pid);
            var byName = new Dictionary<(string, string?), List<(BuildFile, long)>>();
            foreach (var name in reader.Serialized)
            {
                var sf = reader.File(name)!;
                foreach (var o in sf.Objects)
                {
                    if (o.TypeName != "MonoBehaviour") continue;
                    try
                    {
                        var s = reader.ScriptOf(sf, o);
                        if (s is not null && !Py.Truthy(((PyDict)reader.MonoBehaviourHead(o)["m_GameObject"]!)["m_PathID"]))
                        {
                            var key = ((string)s.Items[2]!, reader.PeekName(o));
                            if (!byName.TryGetValue(key, out var l)) byName[key] = l = [];
                            l.Add((sf, o.PathId));
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            // 先對完全部，補過的資產互相參照時直接對得到
            var matched = new List<(string guid, PyDict rec, BuildFile sf, long pid)>();
            foreach (var (guidObj, recObj) in table)
            {
                var rec = (PyDict)recObj!;
                string guid = (string)guidObj!;
                (BuildFile, long)? hit = byContainer.TryGetValue("assets/" + (Py.Truthy(rec.Get("src")) ? (string)rec["src"]! : "").ToLowerInvariant(), out var h) ? h : null;
                if (hit is null && Py.Truthy(rec.Get("script")))
                {
                    var cands = byName.GetValueOrDefault(((string)rec["script"]!, rec.Get("name") as string)) ?? [];
                    hit = cands.Count == 1 ? cands[0] : null;
                }
                if (hit is null) continue;
                var (sf, pid) = hit.Value;
                resolver.Known(sf, pid, new PyDict { ["guid"] = guid, ["fileID"] = "11400000" }, "補過的資產");
                matched.Add((guid, rec, sf, pid));
            }
            long n = 0;
            foreach (var (guid, rec, sf, pid) in matched)
            {
                string p = PyPath.Join(assets, (string)rec["file"]!);
                var data = PyJson.LoadDict(p);
                bool shell = !data.Keys.Any(k => !(k is string s && SoBaseKeys.Contains(s)));
                var obj = sf.Get(pid)!;
                PyDict build;
                try
                {
                    var script = reader.ScriptOf(sf, obj);
                    if (!(shell || data.ContainsKey("$build") || (script is not null && reader.HasSerializeReference((string)script.Items[0]!, (string)script.Items[1]!, (string)script.Items[2]!))))
                        continue;
                    build = reader.ReadMonoBehaviour(sf, obj);
                }
                catch (Exception e)
                {
                    ObjectFailures.Add(new PyDict { ["asset"] = rec.Get("src"), ["build"] = $"{sf.Name}#{pid}", ["error"] = Trunc(e.Message, 200) });
                    continue;
                }
                var fields = (PyDict)BigToStr(Convert(Fields(build), sf, []))!;
                if (fields.Count == 0 && shell) continue;
                var o = new PyDict();
                foreach (var k in new[] { "src", "fileID", "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier", "$subObjects" })
                    if (data.ContainsKey(k)) o[k] = data[k];
                foreach (var (k, v) in fields) o[k] = v;
                o["$build"] = $"{sf.Name}#{pid}";
                PyStr.WriteText(p, PyJson.Compact(o));
                Inc(Objects, Py.Truthy(rec.Get("script")) ? rec["script"] : "?");
                n++;
            }
            return n;
        }

        private static string Trunc(string s, int n) => Py.Len(s) <= n ? s : new string(s.EnumerateRunes().Take(n).SelectMany(r => r.ToString()).ToArray());

        // 場景：fileID = pathID
        public long Scene(PyDict sceneJson, string buildName)
        {
            var sf = reader.File(buildName)!;
            var local = new Dictionary<long, object?>();
            foreach (var k in ((PyDict)sceneJson["components"]!).Keys.Concat(((PyDict)sceneJson["gameObjects"]!).Keys))
                local[long.Parse((string)k!, Py.Inv)] = k;
            long n = 0;
            foreach (var (cid, compObj) in (PyDict)sceneJson["components"]!)
            {
                var comp = (PyDict)compObj!;
                if (!(comp.Get("script") is PyDict script && Py.Truthy(script) && script.ContainsKey("assembly")
                    && reader.HasSerializeReference((string)script["assembly"]!, (string)script["namespace"]!, (string)script["class"]!)))
                    continue;
                var obj = sf.Get(long.Parse((string)cid!, Py.Inv));
                PyDict data;
                try
                {
                    if (obj is null || obj.TypeName != "MonoBehaviour") throw new InvalidOperationException("build 裡同 pathID 的物件不是 MonoBehaviour");
                    var s = reader.ScriptOf(sf, obj);
                    if (s is null || !(Py.Eq(((string)s.Items[0]!).EndsWith(".dll", StringComparison.Ordinal) ? ((string)s.Items[0]!)[..^4] : s.Items[0], script["assembly"])
                        && Py.Eq(s.Items[1], script["namespace"]) && Py.Eq(s.Items[2], script["class"])))
                        throw new InvalidOperationException($"腳本不同：build {Py.Repr(s)}");
                    data = reader.ReadMonoBehaviour(sf, obj);
                }
                catch (Exception e)
                {
                    Failed.Add(new PyDict { ["file"] = buildName, ["component"] = cid, ["class"] = script["class"], ["error"] = Trunc(e.Message, 200) });
                    continue;
                }
                foreach (var (k, v) in (PyDict)BigToStr(Convert(Fields(data), sf, local))!) comp[k] = v;
                comp["$build"] = $"{buildName}#{Py.Str(cid)}";
                Inc(Components, script["class"]);
                n++;
            }
            return n;
        }

        // prefab：階層簽名對應
        private Dictionary<string, List<(string file, long goPid)>>? _roots;

        private Dictionary<string, List<(string, long)>> BuildRoots()
        {
            if (_roots is not null) return _roots;
            _roots = new Dictionary<string, List<(string, long)>>(StringComparer.Ordinal);
            foreach (var name in reader.Serialized)
            {
                if (name.StartsWith("level", StringComparison.Ordinal) || name == "globalgamemanagers.assets") continue;
                var sf = reader.File(name)!;
                foreach (var o in sf.Objects)
                {
                    if (o.TypeName is not ("Transform" or "RectTransform")) continue;
                    var t = reader.Read(o);
                    if (Py.ToLong(((PyDict)t["m_Father"]!)["m_PathID"]) != 0) continue;
                    long goPid = Py.ToLong(((PyDict)t["m_GameObject"]!)["m_PathID"]);
                    string goName = reader.PeekName(sf.Get(goPid) ?? throw new KeyNotFoundException(goPid.ToString(Py.Inv)))!;
                    if (!_roots.TryGetValue(goName, out var l)) _roots[goName] = l = [];
                    l.Add((name, goPid));
                }
            }
            return _roots;
        }

        private sealed record TreeNode(List<(string name, int index)> Path, object GoId, List<(object id, string kind)> Comps, object? Name);

        private List<TreeNode> BuildTree(BuildFile sf, long goPid, List<(string, int)> path)
        {
            var go = reader.Read(sf.Get(goPid) ?? throw new KeyNotFoundException(goPid.ToString(Py.Inv)));
            var comps = new List<(object, string)>();
            long? transformPid = null;
            foreach (PyDict c in Assets.AsList(go["m_Component"]).Cast<PyDict>())
            {
                long pid = Py.ToLong(((PyDict)c["component"]!)["m_PathID"]);
                var o = sf.Get(pid) ?? throw new KeyNotFoundException(pid.ToString(Py.Inv));
                string kind = o.TypeName;
                if (kind == "MonoBehaviour")
                {
                    var s = reader.ScriptOf(sf, o);
                    kind = $"MB:{(s is not null ? Py.Str(s.Items[2]) : "?")}";
                }
                else if (kind is "Transform" or "RectTransform") transformPid = pid;
                comps.Add((pid, kind));
            }
            var o2 = new List<TreeNode> { new(path, goPid, comps, go["m_Name"]) };
            if (transformPid is not null)
            {
                var t = reader.Read(sf.Get(transformPid.Value)!);
                int i = 0;
                foreach (PyDict ch in Assets.AsList(t["m_Children"]).Cast<PyDict>())
                {
                    var childTf = reader.Read(sf.Get(Py.ToLong(ch["m_PathID"])) ?? throw new KeyNotFoundException("child"));
                    long childGo = Py.ToLong(((PyDict)childTf["m_GameObject"]!)["m_PathID"]);
                    string? name = reader.PeekName(sf.Get(childGo) ?? throw new KeyNotFoundException(childGo.ToString(Py.Inv)));
                    o2.AddRange(BuildTree(sf, childGo, [.. path, (name!, i)]));
                    i++;
                }
            }
            return o2;
        }

        private static List<TreeNode> ExportTree(PyDict prefab, object goId, List<(string, int)> path)
        {
            var go = (PyDict)((PyDict)prefab["gameObjects"]!)[goId]!;
            var comps = new List<(object, string)>();
            foreach (var cid in (List<object?>)go["components"]!)
            {
                if (((PyDict)prefab["components"]!).Get(cid) is not PyDict c)
                {
                    comps.Add((cid!, "?"));
                    continue;
                }
                string kind = (string)c["type"]!;
                if (kind == "MonoBehaviour") kind = $"MB:{Py.Str(Assets.Dict(c.Get("script")).Get("class", "?"))}";
                comps.Add((cid!, kind));
            }
            var o = new List<TreeNode> { new(path, goId, comps, go["m_Name"]) };
            int i = 0;
            foreach (var ch in (List<object?>)go["children"]!)
            {
                var childName = ((PyDict)((PyDict)prefab["gameObjects"]!)[ch]!)["m_Name"];
                o.AddRange(ExportTree(prefab, ch!, [.. path, ((string)childName!, i)]));
                i++;
            }
            return o;
        }

        private static bool SameSignature(List<TreeNode> a, List<TreeNode> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Path.SequenceEqual(b[i].Path) || !Py.Eq(a[i].Name, b[i].Name)) return false;
                if (!a[i].Comps.Select(c => c.kind).SequenceEqual(b[i].Comps.Select(c => c.kind))) return false;
            }
            return true;
        }

        public long Prefab(PyDict prefabJson)
        {
            var targets = new List<(object? cid, PyDict c)>();
            foreach (var (cid, cObj) in (PyDict)prefabJson["components"]!)
            {
                var c = (PyDict)cObj!;
                if (c.Get("script") is PyDict sc && Py.Truthy(sc) && sc.ContainsKey("assembly")
                    && reader.HasSerializeReference((string)sc["assembly"]!, (string)sc["namespace"]!, (string)sc["class"]!))
                    targets.Add((cid, c));
            }
            if (targets.Count == 0) return 0;
            var roots = (List<object?>)prefabJson["roots"]!;
            if (roots.Count != 1)
            {
                UnmatchedPrefabs.Add(new PyDict { ["prefab"] = prefabJson["src"], ["error"] = $"根物件 {roots.Count} 個" });
                return 0;
            }
            var etree = ExportTree(prefabJson, roots[0]!, []);
            var rootName = ((PyDict)((PyDict)prefabJson["gameObjects"]!)[roots[0]]!)["m_Name"] as string;
            var matches = new List<(BuildFile sf, List<TreeNode> btree)>();
            foreach (var (fname, goPid) in (rootName is not null ? BuildRoots().GetValueOrDefault(rootName) : null) ?? [])
            {
                var sf = reader.File(fname)!;
                var btree = BuildTree(sf, goPid, []);
                if (SameSignature(btree, etree)) matches.Add((sf, btree));
            }
            if (matches.Count == 0)
            {
                UnmatchedPrefabs.Add(new PyDict { ["prefab"] = prefabJson["src"], ["error"] = "build 裡沒有階層相同的副本" });
                return 0;
            }
            // 每份副本都補一次，結果要相同
            var results = new List<List<(object? cid, object? data, string build)>>();
            foreach (var (sf, btree) in matches)
            {
                var local = new Dictionary<long, object?>();
                foreach (var (bn, en) in btree.Zip(etree))
                {
                    local[Py.ToLong(bn.GoId)] = en.GoId;
                    foreach (var ((bpid, _), (ecid, _)) in bn.Comps.Zip(en.Comps)) local[Py.ToLong(bpid)] = ecid;
                }
                var inverse = new Dictionary<object, long>(new KeyEqNonNull());
                foreach (var (k, v) in local) inverse[v!] = k;
                var outList = new List<(object?, object?, string)>();
                foreach (var (cid, _) in targets)
                {
                    var obj = sf.Get(inverse[cid!])!;
                    var data = reader.ReadMonoBehaviour(sf, obj);
                    outList.Add((cid, BigToStr(Convert(Fields(data), sf, local)), $"{sf.Name}#{obj.PathId}"));
                }
                results.Add(outList);
            }
            var first = results[0];
            bool ambiguous = results.Skip(1).Any(r => r.Count != first.Count || r.Zip(first).Any(x => !Py.Eq(x.First.Item1, x.Second.Item1) || !Py.Eq(x.First.Item2, x.Second.Item2)));
            if (ambiguous)
            {
                AmbiguousPrefabs.Add(new PyDict { ["prefab"] = prefabJson["src"], ["copies"] = results.Select(r => (object?)r[0].Item3).ToList() });
                return 0;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                var (_, c) = targets[i];
                foreach (var (k, v) in (PyDict)first[i].Item2!) c[k] = v;
                c["$build"] = first[i].Item3;
                Inc(Components, ((PyDict)c["script"]!)["class"]);
            }
            return targets.Count;
        }

        private sealed class KeyEqNonNull : IEqualityComparer<object>
        {
            public new bool Equals(object? a, object? b) => Py.Eq(a, b);
            public int GetHashCode(object o) => o is string s ? s.GetHashCode() : Py.IsNumber(o) ? Py.ToDouble(o).GetHashCode() : 0;
        }
    }

    private static (PyDict resources, PyDict stats) ExportResources(BuildReader reader, ExportResolver resolver)
    {
        var gm = reader.File("globalgamemanagers")!;
        var rm = gm.Objects.FirstOrDefault(o => o.TypeName == "ResourceManager");
        var o = new PyDict();
        var stats = new PyDict();
        if (rm is null) return (new PyDict(), stats);
        foreach (PyTuple pair in Assets.AsList(reader.Read(rm, checkRead: false)["m_Container"]).Cast<PyTuple>())
        {
            string path = (string)pair.Items[0]!;
            var pptr = (PyDict)pair.Items[1]!;
            long fid = Py.ToLong(pptr["m_FileID"]), pid = Py.ToLong(pptr["m_PathID"]);
            string? ext = fid > 0 ? BuildReader.Externals(gm)[(int)(fid - 1)] : null;
            PyDict? r = null;
            if (ext is not null && BuiltinFiles.ContainsKey(ext))
                r = new PyDict { ["guid"] = BuiltinFiles[ext], ["fileID"] = pid.ToString(Py.Inv) };
            else
            {
                var sf = reader.Target(gm, fid);
                if (sf is not null && sf.Get(pid) is { } target && ExportTables.ContainsKey(target.TypeName))
                    (r, _) = resolver.Resolve(sf, pid);
            }
            string stat = r is not null ? "對到" : "對不到";
            stats[stat] = Py.ToLong(stats.Get(stat, 0L)) + 1;
            string lk = path.ToLowerInvariant();
            if (o.Get(lk) is not List<object?> l) o[lk] = l = [];
            l.Add(r);
        }
        return (o, stats);
    }

    /// <summary>要先有 assets 與 scenes 的輸出</summary>
    public PyDict Export()
    {
        double t0 = ImportContext.Now();
        string outRoot = ctx.Out;
        var reader = new BuildReader(ctx.GameData ?? throw new InvalidOperationException("supplement 要有原版 build"));
        var resolver = new ExportResolver(reader, outRoot);
        var sup = new Supplementer(reader, resolver);

        void Write(string path, object? obj) => PyStr.WriteText(path, PyJson.Compact(obj));

        long nObjects = sup.DoObjects(outRoot);
        string scenesDir = PyPath.Join(outRoot, "scenes");
        var index = PyJson.LoadDict(PyPath.Join(scenesDir, "index.json"));
        long nScene = 0, nPrefab = 0;
        foreach (var infoObj in ((PyDict)index["scenes"]!).Values)
        {
            var info = (PyDict)infoObj!;
            if (info.Get("buildIndex") is null) continue;
            string p = PyPath.Join(outRoot, (string)info["file"]!);
            var data = PyJson.LoadDict(p);
            long k = sup.Scene(data, $"level{Py.Str(info["buildIndex"])}");
            if (k > 0)
            {
                Write(p, data);
                nScene += k;
            }
        }
        foreach (var infoObj in ((PyDict)index["prefabs"]!).Values)
        {
            var info = (PyDict)infoObj!;
            string p = PyPath.Join(outRoot, (string)info["file"]!);
            var data = PyJson.LoadDict(p);
            long k = sup.Prefab(data);
            if (k > 0)
            {
                Write(p, data);
                nPrefab += k;
            }
        }
        var (resources, resStats) = ExportResources(reader, resolver);
        Write(PyPath.Join(outRoot, "assets/resources.json"), resources);
        var report = new PyDict
        {
            ["resources"] = resStats,
            ["objects"] = sup.Objects,
            ["objectFailures"] = sup.ObjectFailures,
            ["sceneComponents"] = nScene,
            ["prefabComponents"] = nPrefab,
            ["components"] = sup.Components,
            ["componentFailures"] = sup.Failed,
            ["refs"] = sup.Refs,
            ["ambiguousPrefabs"] = sup.AmbiguousPrefabs,
            ["unmatchedPrefabs"] = sup.UnmatchedPrefabs,
            ["resolver"] = resolver.Stats,
            ["resolverProblems"] = resolver.Problems,
            ["seconds"] = ImportContext.Seconds(t0),
        };
        Write(PyPath.Join(outRoot, "supplement.json"), report);
        ctx.Log($"  ScriptableObject {Py.Str(Py.ToLong(sup.Objects.Values.Sum(v => Py.ToLong(v))))} 個（{sup.ObjectFailures.Count} 個失敗）；元件：場景 {nScene}、prefab {nPrefab}（{sup.Failed.Count} 個失敗）");
        _ = nObjects;
        return report;
    }
}
