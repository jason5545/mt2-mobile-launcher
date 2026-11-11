using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Mt2Mobile.Importer.Serialized;
using Mt2Mobile.Importer.PyCompat;
using Mt2Mobile.Importer.Unity;

namespace Mt2Mobile.Importer;

/// <summary>
/// scenes.py 的 ScriptResolver：MonoBehaviour 的 m_Script → C# 類別。
/// .cs 照 namespace 與檔名、再到 build 的 MonoScript 表確認；DLL 裡的照 fileID（MD4）反查 MonoScript 表。
/// </summary>
public sealed class ScriptResolver
{
    private static readonly Regex NsRe = PyRe.M(@"^namespace" + PyRe.S + @"+([" + PyRe.WChars + @".]+)");
    private static readonly int EmptyId = UnityYaml.ScriptFileID("", "");

    private readonly string _assets;
    private readonly Dictionary<string, string> _guids;
    public Dictionary<(string, string), PyDict?> Cache { get; } = [];
    public HashSet<(string asm, string ns, string cls)> MonoScripts { get; } = [];
    private readonly Dictionary<int, List<(string, string, string)>> _byHash = [];

    public ScriptResolver(string assets, string gameData, Dictionary<string, string> guids)
    {
        _assets = assets;
        _guids = guids;
        string ggm = PyPath.Join(gameData, "globalgamemanagers.assets");
        if (File.Exists(ggm))
        {
            var env = new BuildEnv(gameData);
            foreach (var obj in env.LoadOne(ggm).Objects)
            {
                if (obj.TypeName != "MonoScript") continue;
                var t = obj.ReadTypeTree(boost: true);
                MonoScripts.Add((OrEmpty(t.Get("m_AssemblyName")), OrEmpty(t.Get("m_Namespace")), OrEmpty(t.Get("m_ClassName"))));
            }
        }
        foreach (var ms in MonoScripts.OrderBy(m => m.asm, PyStr.Comparer).ThenBy(m => m.ns, PyStr.Comparer).ThenBy(m => m.cls, PyStr.Comparer))
        {
            int h = UnityYaml.ScriptFileID(ms.ns, ms.cls);
            if (!_byHash.TryGetValue(h, out var l)) _byHash[h] = l = [];
            l.Add(ms);
        }
    }

    private static string OrEmpty(object? v) => Py.Truthy(v) ? (string)v! : "";

    private static string Asm(string name) => name.EndsWith(".dll", StringComparison.Ordinal) ? name[..^4] : name;

    /// <summary>回傳 {assembly, namespace, class, source?}；原版就是空的回傳 {missing}；解不開回傳 null</summary>
    public PyDict? Resolve(object? refObj)
    {
        if (refObj is not PyDict r || !Py.Truthy(r) || !r.ContainsKey("guid")) return null;
        var key = (Py.Str(r["guid"]), Py.Str(r["fileID"]));
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var v))
            {
                v = ResolveUncached(Py.Str(r["guid"]), (int)Py.ToLong(r["fileID"] is string s ? long.Parse(s, Py.Inv) : r["fileID"]));
                Cache[key] = v;
            }
            return v;
        }
    }

    private PyDict? ResolveUncached(string guid, int fileId)
    {
        _guids.TryGetValue(guid, out var target);
        if (target is not null && PyPath.Suffix(target) == ".cs")
        {
            string text = PyStr.ReadText(target);
            var m = NsRe.Match(text);
            string ns = m.Success ? m.Groups[1].Value : "";
            string stem = PyPath.Stem(target);
            var hits = MonoScripts.Where(ms => ms.ns == ns && ms.cls == stem).ToList();
            if (hits.Count != 1) return null;
            string asm = Asm(hits[0].asm);
            var o = new PyDict { ["assembly"] = asm, ["namespace"] = ns, ["class"] = stem };
            string asmDir = PyPath.Join(PyPath.Join(_assets, "Scripts"), asm);
            if (PyPath.IsRelativeTo(target, asmDir) && target != asmDir) o["source"] = PyPath.RelativeTo(target, asmDir);
            return o;
        }
        if (fileId == EmptyId)
            return new PyDict { ["missing"] = "原版 build 的 MonoScript 類別名、namespace、組件名都是空的，原版也是 missing script" };
        var cands = _byHash.GetValueOrDefault(fileId) ?? [];
        if (target is not null && PyPath.Suffix(target) == ".dll") cands = cands.Where(c => Asm(c.Item1) == PyPath.Stem(target)).ToList();
        if (cands.Count != 1) return null;
        var (a, n, c) = cands[0];
        return new PyDict { ["assembly"] = Asm(a), ["namespace"] = n, ["class"] = c };
    }
}

/// <summary>scenes.py：全部場景與 prefab 匯成場景樹 JSON（輸出格式見 Python 版檔頭）</summary>
public sealed class Scenes(ImportContext ctx)
{
    public static readonly PyDict BuiltinGuids = new()
    {
        ["0000000000000000e000000000000000"] = "unity default resources",
        ["0000000000000000f000000000000000"] = "unity_builtin_extra",
    };

    private static readonly string[] ComponentDrop = ["m_EditorHideFlags", "m_EditorClassIdentifier", "m_GameObject"];
    private static readonly HashSet<string> SceneSettings = ["OcclusionCullingSettings", "RenderSettings", "LightmapSettings", "NavMeshSettings"];

    private static readonly Regex AmbientProbe = PyRe.M(@"^  m_BakedAmbientProbeInLinear:\n((?:    sh\[" + PyRe.S + @"*\d+\]: [^\n]*\n)+)");
    private static readonly Regex ShLine = PyRe.R(@"sh\[" + PyRe.S + @"*\d+\]: ([^\n]+)");

    private static void LightingDataOf(PyDict settings, Dictionary<string, string> guids)
    {
        var refObj = Assets.Dict(Assets.Dict(settings.Get("LightmapSettings")).Get("m_LightingDataAsset"));
        object? g = Py.Truthy(refObj.Get("guid")) ? refObj.Get("guid") : "";
        if (g is not string gs || !guids.TryGetValue(gs, out var p) || !File.Exists(p)) return;
        string text = PyStr.ReadText(p);
        var m = AmbientProbe.Match(text);
        if (!m.Success) return;
        var coeffs = ShLine.Matches(m.Groups[1].Value).Select(x => (object?)PyFloat.Parse(x.Groups[1].Value)).ToList();
        if (coeffs.Count == 27)
            settings["LightingData"] = new PyDict { ["ambientProbe"] = coeffs, ["src"] = refObj.Get("guid") };
    }

    private sealed record Check(List<object?> Problems, List<object?> Unresolved, List<object?> Missing, long Reachable, PyDict Counts);

    private (PyDict tree, Check check) BuildTree(List<UnityDoc> docs, ScriptResolver resolver)
    {
        var gos = new PyDict();
        var comps = new PyDict();
        var settings = new PyDict();
        var problems = new List<object?>();
        var unresolved = new List<object?>();
        var missing = new List<object?>();

        foreach (var d in docs)
        {
            if (d.Stripped || d.Type == "PrefabInstance")
            {
                problems.Add($"{d.Type} {d.FileID}：prefab 實例或 stripped 物件，這個匯入沒處理");
                continue;
            }
            var data = (PyDict)ctx.Yaml.DecodeInt32Arrays(UnityYaml.NormRefs(UnityYaml.StripMetaFields(d.Data)))!;
            if (d.Type == "GameObject")
            {
                gos[d.FileID] = new PyDict
                {
                    ["m_Name"] = data.Get("m_Name"),
                    ["m_IsActive"] = data.Get("m_IsActive"),
                    ["m_Layer"] = data.Get("m_Layer"),
                    ["m_TagString"] = data.Get("m_TagString"),
                    ["components"] = Assets.ListOf(data.Get("m_Component"))
                        .Where(c => Py.Truthy(((PyDict)c!).Get("component")))
                        .Select(c => ((PyDict)((PyDict)c!)["component"]!)["fileID"]).ToList(),
                };
            }
            else if (data.ContainsKey("m_GameObject"))
            {
                var rec = new PyDict { ["type"] = d.Type, ["classID"] = (long)d.ClassID, ["gameObject"] = Assets.Dict(data.Get("m_GameObject")).Get("fileID") };
                foreach (var (k, v) in data)
                    if (!ComponentDrop.Contains((string)k!)) rec[k] = v;
                if (d.Type == "MonoBehaviour")
                {
                    var script = resolver.Resolve(data.Get("m_Script"));
                    if (script is null) unresolved.Add(new PyDict { ["fileID"] = d.FileID, ["m_Script"] = data.Get("m_Script") });
                    else if (script.ContainsKey("missing")) missing.Add(new PyDict { ["fileID"] = d.FileID, ["m_Script"] = data.Get("m_Script") });
                    rec["script"] = script;
                }
                comps[d.FileID] = rec;
            }
            else if (SceneSettings.Contains(d.Type)) settings[d.Type] = data;
            else problems.Add($"{d.Type} {d.FileID}：不是 GameObject、元件或場景設定");
        }

        foreach (var (gid, goObj) in gos)
        {
            var go = (PyDict)goObj!;
            foreach (var cid in (List<object?>)go["components"]!)
            {
                var c = comps.Get(cid) as PyDict;
                if (c is null) problems.Add($"GameObject {Py.Str(gid)} 的元件 {Py.Str(cid)} 不存在");
                else if (!Py.Eq(c["gameObject"], gid)) problems.Add($"元件 {Py.Str(cid)} 的 m_GameObject 是 {Py.Str(c["gameObject"])}，不是 {Py.Str(gid)}");
            }
        }
        foreach (var (cid, cObj) in comps)
        {
            var c = (PyDict)cObj!;
            if (!(c["gameObject"] is { } g && gos.ContainsKey(g))) problems.Add($"元件 {Py.Str(cid)}（{Py.Str(c["type"])}）的 GameObject {Py.Str(c["gameObject"])} 不存在");
        }

        var tfOwner = new PyDict();
        foreach (var (gid, goObj) in gos)
        {
            var go = (PyDict)goObj!;
            var tfs = ((List<object?>)go["components"]!).Where(cid => (comps.Get(cid) as PyDict ?? new PyDict()).Get("type") is "Transform" or "RectTransform").ToList();
            if (tfs.Count != 1)
            {
                problems.Add($"GameObject {Py.Str(gid)} 有 {tfs.Count} 個 Transform");
                continue;
            }
            go["transform"] = tfs[0];
            tfOwner[tfs[0]] = gid;
        }
        var roots = new List<(long order, object? gid)>();
        foreach (var (gid, goObj) in gos)
        {
            var go = (PyDict)goObj!;
            var tf = comps.Get(go.Get("transform", "")) as PyDict;
            if (tf is null) continue;
            var father = Assets.Dict(tf.Get("m_Father")).Get("fileID");
            go["parent"] = Py.Truthy(father) ? tfOwner.Get(father) : null;
            if (Py.Truthy(father) && !tfOwner.ContainsKey(father)) problems.Add($"Transform {Py.Str(go["transform"])} 的 m_Father {Py.Str(father)} 不存在");
            var children = new List<object?>();
            go["children"] = children;
            foreach (var ch in Assets.ListOf(tf.Get("m_Children")))
            {
                var childGo = tfOwner.Get(Assets.Dict(ch).Get("fileID"));
                if (childGo is null)
                {
                    problems.Add($"Transform {Py.Str(go["transform"])} 的子物件 {Py.Repr(ch)} 不存在");
                    continue;
                }
                children.Add(childGo);
            }
            if (!Py.Truthy(father)) roots.Add((Py.ToLong(Py.Truthy(tf.Get("m_RootOrder")) ? tf.Get("m_RootOrder") : 0L), gid));
        }
        foreach (var (gid, goObj) in gos)
        {
            foreach (var ch in Assets.ListOf(((PyDict)goObj!).Get("children", new List<object?>())))
                if (!Py.Eq(((PyDict)gos[ch]!).Get("parent"), gid))
                    problems.Add($"{Py.Str(ch)} 列在 {Py.Str(gid)} 的 m_Children，但它的 m_Father 不是 {Py.Str(gid)}");
        }
        roots.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : Py.Compare(a.gid, b.gid));
        var rootIds = roots.Select(r => r.gid).ToList();

        var seen = new HashSet<object?>();
        var stack = new List<object?>(rootIds);
        while (stack.Count > 0)
        {
            var gid = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (seen.Contains(gid))
            {
                problems.Add($"GameObject {Py.Str(gid)} 在樹裡出現兩次");
                continue;
            }
            seen.Add(gid);
            stack.AddRange(Assets.ListOf(((PyDict)gos[gid]!).Get("children", new List<object?>())));
        }
        foreach (var gid in gos.Keys.Where(g => !seen.Contains(g)).OrderBy(g => g, Comparer<object?>.Create(Py.Compare)))
            problems.Add($"GameObject {Py.Str(gid)}（{Py.Str(((PyDict)gos[gid]!)["m_Name"])}）從根走不到");

        var tree = new PyDict { ["roots"] = rootIds, ["gameObjects"] = gos, ["components"] = comps, ["settings"] = settings };
        var counts = new PyDict();
        foreach (var g in comps.Values.Select(c => (string)((PyDict)c!)["type"]!).GroupBy(t => t).OrderBy(g => g.Key, PyStr.Comparer))
            counts[g.Key] = (long)g.Count();
        counts["GameObject"] = (long)gos.Count;
        return (tree, new Check(problems, unresolved, missing, seen.Count, counts));
    }

    /// <summary>iter_refs：資料裡所有指向別的檔案的 PPtr（{guid, fileID}）</summary>
    private static IEnumerable<PyDict> IterRefs(object? v)
    {
        if (v is PyDict d)
        {
            if (d.Count == 2 && d.ContainsKey("guid") && d.ContainsKey("fileID"))
            {
                yield return d;
                yield break;
            }
            foreach (var x in d.Values)
                foreach (var r in IterRefs(x))
                    yield return r;
        }
        else if (v is List<object?> l)
        {
            foreach (var x in l)
                foreach (var r in IterRefs(x))
                    yield return r;
        }
    }

    /// <summary>EditorBuildSettings 啟用的場景（第 N 個是 build 的 level&lt;N&gt;）；沒有就讀原版 build 的 BuildSettings</summary>
    public List<string> BuildSceneOrder(string? gameData)
    {
        string p = PyPath.Join(PyPath.Parent(ctx.Assets), "ProjectSettings/EditorBuildSettings.asset");
        if (File.Exists(p))
        {
            var docs = ctx.Yaml.LoadUnityDocs(p);
            var scenes = docs.Where(d => d.Type == "EditorBuildSettings").Select(d => Assets.ListOf(d.Data.Get("m_Scenes"))).FirstOrDefault() ?? [];
            return scenes.Cast<PyDict>().Where(s => Py.Truthy(s.Get("enabled"))).Select(s => PyPath.Stem((string)s["path"]!)).ToList();
        }
        return gameData is not null ? BuildScenePaths(gameData).Select(PyPath.Stem).ToList() : [];
    }

    /// <summary>原版 build 的 BuildSettings.scenes（UnityPy 照型別樹讀會多要 8 bytes，不檢查長度）</summary>
    public static List<string> BuildScenePaths(string gameData)
    {
        string gm = PyPath.Join(gameData, "globalgamemanagers");
        if (!File.Exists(gm)) return [];
        var env = new BuildEnv(gameData);
        foreach (var o in env.LoadOne(gm).Objects)
            if (o.TypeName == "BuildSettings")
                return Assets.ListOf(o.ReadTypeTree(boost: true, checkRead: false).Get("scenes")).Cast<string>().ToList();
        return [];
    }

    /// <summary>每個指向 unity_builtin_extra 的參照，到原版 build 的同一個物件裡找原始 PPtr 位元組，確認指向 builtin.json 記的物件</summary>
    private PyDict VerifyBuiltinRefs(string gameData, List<string> sceneOrder)
    {
        var builtin = PyJson.LoadDict(PyPath.Join(ctx.Out, "assets/builtin.json"));
        long checkedN = 0;
        var mismatched = new List<object?>();
        for (int level = 0; level < sceneOrder.Count; level++)
        {
            string scene = sceneOrder[level];
            string buildFile = PyPath.Join(gameData, $"level{level}");
            string sceneJson = PyPath.Join(ctx.Out, $"scenes/{scene}.json");
            if (!File.Exists(buildFile) || !File.Exists(sceneJson))
            {
                mismatched.Add(new PyDict { ["scene"] = scene, ["error"] = "找不到 build 檔或場景 JSON" });
                continue;
            }
            var env = new BuildEnv(gameData);
            var sf = env.LoadOne(buildFile);
            var externals = sf.Externals();
            var data = PyJson.LoadDict(sceneJson);
            foreach (var (cid, compObj) in (PyDict)data["components"]!)
            {
                foreach (var (field, value) in (PyDict)compObj!)
                {
                    if (Py.Eq(field, "m_Script")) continue;
                    var refs = value is List<object?> l ? l : [value];
                    foreach (var refObj in refs)
                    {
                        if (!(refObj is PyDict r && r.Get("guid") is { } g && BuiltinGuids.ContainsKey(g))) continue;
                        checkedN++;
                        var entry = builtin.Get($"{Py.Str(r["guid"])}:{Py.Str(r["fileID"])}") as PyDict;
                        bool ok = false;
                        if (entry is not null && Py.Truthy(entry))
                        {
                            var parts = ((string)entry["build"]!).Split('#');
                            string fn = parts[0];
                            int idx = externals.IndexOf(fn);
                            if (idx >= 0)
                            {
                                var pattern = new byte[12];
                                BinaryPrimitives.WriteInt32LittleEndian(pattern, idx + 1);
                                BinaryPrimitives.WriteInt64LittleEndian(pattern.AsSpan(4), long.Parse(parts[1], Py.Inv));
                                ok = sf.Get(long.Parse((string)cid!, Py.Inv))!.RawData.AsSpan().IndexOf(pattern) >= 0;
                            }
                        }
                        if (!ok)
                            mismatched.Add(new PyDict
                            {
                                ["scene"] = scene, ["component"] = cid, ["field"] = field, ["ref"] = r,
                                ["expected"] = entry is not null && Py.Truthy(entry) ? entry["name"] : entry,
                            });
                    }
                }
            }
        }
        return new PyDict { ["checked"] = checkedN, ["mismatched"] = mismatched };
    }

    /// <summary>guid → 去向（assets 的哪張表、prefab、scriptableobject…），外部參照的統計用</summary>
    private Dictionary<string, string> AssetCategories(Dictionary<string, string> guids)
    {
        var cats = new Dictionary<string, string>(StringComparer.Ordinal);
        string assetsOut = PyPath.Join(ctx.Out, "assets");
        foreach (var name in new[] { "textures", "sprites", "fonts", "materials", "shaders", "animations", "animators", "meshes", "physicMaterials", "monobehaviours", "texts", "audioclips" })
        {
            string p = PyPath.Join(assetsOut, $"{name}.json");
            if (!File.Exists(p)) continue;
            foreach (var g in PyJson.LoadDict(p).StringKeys) cats[g] = name;
        }
        foreach (var (g, p) in guids)
        {
            if (cats.ContainsKey(g)) continue;
            string suffix = PyPath.Suffix(p);
            if (suffix == ".prefab") cats[g] = "prefabs";
            else if (suffix == ".unity") cats[g] = "scenes";
            else if (suffix is ".cs" or ".dll") cats[g] = "scripts";
            else if (suffix == ".asset" && File.Exists(p))
            {
                string? t = Assets.FirstDocTypeOf(p);
                if (t is not null && Assets.NotImported.ContainsKey(t)) cats[g] = $"未匯入（{t}）";
            }
        }
        return cats;
    }

    private static readonly Regex HeaderTypeRe = PyRe.M(@"^--- !u!\d+ &-?\d+(?: stripped)?[ \t]*\n(" + PyRe.W + @"+):");

    /// <summary>一個場景或 prefab 處理完的摘要（樹本身已經寫檔），主執行緒照檔案順序彙總</summary>
    private sealed class FileResult
    {
        public List<string> HeaderTypes = [];
        public List<string>? Suspect;
        public string? Error;
        public Check? Check;
        public List<string> SettingsKeys = [];
        public List<(string? cat, string guid)> Refs = [];
        public PyDict? Entry;
        public string? Name;
        public bool IsScene;
    }

    private FileResult ProcessFile(string guid, string p, Dictionary<string, string> guids, ScriptResolver resolver,
        Dictionary<string, string> cats, Dictionary<string, string> renamed, List<string> sceneOrder, bool writeNow, out PyDict? deferred)
    {
        deferred = null;
        var r = new FileResult();
        string rel = PyPath.RelativeTo(p, ctx.Assets);
        string text;
        PyDict tree;
        Check check;
        try
        {
            text = PyStr.ReadText(p);
            foreach (Match m in HeaderTypeRe.Matches(text)) r.HeaderTypes.Add(m.Groups[1].Value);
            var sk = ctx.Yaml.SuspectHexKeys(text);
            if (sk.Count > 0) r.Suspect = sk.Order(PyStr.Comparer).ToList();
            var docs = ctx.Yaml.LoadUnityDocs(p, text);
            (tree, check) = BuildTree(docs, resolver);
        }
        catch (Exception e)
        {
            r.Error = $"{Assets.ExceptionName(e)}: {e.Message}";
            return r;
        }
        LightingDataOf((PyDict)tree["settings"]!, guids);
        r.Check = check;
        r.SettingsKeys = ((PyDict)tree["settings"]!).StringKeys.ToList();
        foreach (var (_, cObj) in (PyDict)tree["components"]!)
        {
            var data = new PyDict();
            foreach (var (k, v) in (PyDict)cObj!)
                if (!Py.Eq(k, "m_Script")) data[k] = v;
            foreach (var rf in IterRefs(data))
            {
                string g = Py.Str(rf["guid"]);
                r.Refs.Add((BuiltinGuids.Get(g) as string ?? cats.GetValueOrDefault(g), g));
            }
        }
        string name = PyPath.Stem(p);
        if (PyPath.Suffix(p) == ".unity" && renamed.TryGetValue(name, out var rn))
        {
            rel = rn;
            name = PyPath.Stem(rel);
        }
        var entry = new PyDict { ["name"] = name, ["src"] = rel, ["roots"] = (long)((List<object?>)tree["roots"]!).Count, ["counts"] = check.Counts };
        string file;
        if (PyPath.Suffix(p) == ".unity")
        {
            file = $"scenes/{name}.json";
            var e = new PyDict { ["guid"] = guid, ["file"] = file, ["buildIndex"] = sceneOrder.Contains(name) ? (long)sceneOrder.IndexOf(name) : null };
            foreach (var (k, v) in entry) e[k] = v;
            r.Entry = e;
            r.IsScene = true;
        }
        else
        {
            file = $"scenes/prefabs/{guid}.json";
            var e = new PyDict { ["file"] = file };
            foreach (var (k, v) in entry) e[k] = v;
            r.Entry = e;
        }
        r.Name = name;
        var outObj = new PyDict { ["guid"] = guid, ["name"] = name, ["src"] = rel };
        foreach (var (k, v) in tree) outObj[k] = v;
        if (writeNow) ImportContext.WriteJson(PyPath.Join(ctx.Out, file), outObj);
        else deferred = outObj;
        return r;
    }

    public PyDict Export(string gameData, Dictionary<string, string> guids)
    {
        double t0 = ImportContext.Now();
        string outDir = PyPath.Join(ctx.Out, "scenes");
        // hierarchy.json 是轉換器的輸出，清資料夾時留著
        var keep = new Dictionary<string, byte[]>();
        if (File.Exists(PyPath.Join(outDir, "hierarchy.json"))) keep["hierarchy.json"] = File.ReadAllBytes(PyPath.Join(outDir, "hierarchy.json"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(PyPath.Join(outDir, "prefabs"));
        foreach (var (n, data) in keep) File.WriteAllBytes(PyPath.Join(outDir, n), data);

        var resolver = new ScriptResolver(ctx.Assets, gameData, guids);
        var sceneOrder = BuildSceneOrder(gameData);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(PyPath.Join(PyPath.Parent(ctx.Assets), "ProjectSettings/EditorBuildSettings.asset")))
        {
            var paths = BuildScenePaths(gameData);
            for (int i = 0; i < paths.Count; i++)
                renamed[$"level{i}"] = paths[i].StartsWith("Assets/", StringComparison.Ordinal) ? paths[i]["Assets/".Length..] : paths[i];
        }
        var cats = AssetCategories(guids);
        var files = guids.Where(kv => PyPath.Suffix(kv.Value) is ".unity" or ".prefab" && File.Exists(kv.Value))
            .OrderBy(kv => kv.Key, PyStr.Comparer).ThenBy(kv => kv.Value, PyPath.Comparer).ToList();

        // 輸出檔名撞在一起（同名場景）時，Python 是後寫的蓋掉先寫的：這些檔留到彙總時照順序寫
        var outName = files.Select(f =>
        {
            if (PyPath.Suffix(f.Value) != ".unity") return $"prefabs/{f.Key}";
            string name = PyPath.Stem(f.Value);
            return renamed.TryGetValue(name, out var rn) ? PyPath.Stem(rn) : name;
        }).ToList();
        var dupNames = outName.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        // 每個檔在工作執行緒裡解析、建樹、寫檔，只把摘要帶回來
        var results = new FileResult[files.Count];
        var deferred = new PyDict?[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = ctx.Parallelism }, i =>
        {
            results[i] = ProcessFile(files[i].Key, files[i].Value, guids, resolver, cats, renamed, sceneOrder, !dupNames.Contains(outName[i]), out deferred[i]);
        });

        var index = new PyDict { ["scenes"] = new PyDict(), ["prefabs"] = new PyDict() };
        var totals = new Dictionary<string, long>();
        var headerTotals = new Dictionary<string, long>();
        var problems = new PyDict();
        var unresolved = new PyDict();
        var missingD = new PyDict();
        var extRefs = new Dictionary<string, long>();
        var unknownRefs = new List<KeyValuePair<string, long>>();
        var unknownIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var suspect = new PyDict();
        var failed = new List<object?>();
        void Add(Dictionary<string, long> d, string k, long n) => d[k] = d.GetValueOrDefault(k) + n;

        for (int i = 0; i < files.Count; i++)
        {
            var (guid, p) = files[i];
            string rel = PyPath.RelativeTo(p, ctx.Assets);
            var r = results[i];
            foreach (var t in r.HeaderTypes) Add(headerTotals, t, 1);
            if (r.Suspect is not null) suspect[rel] = r.Suspect.Cast<object?>().ToList();
            if (r.Error is not null)
            {
                failed.Add(new PyDict { ["src"] = rel, ["error"] = r.Error });
                continue;
            }
            var check = r.Check!;
            foreach (var (k, v) in check.Counts) Add(totals, (string)k!, Py.ToLong(v));
            foreach (var k in r.SettingsKeys) Add(totals, "settings:" + k, 1);
            if (check.Problems.Count > 0) problems[rel] = check.Problems;
            if (check.Unresolved.Count > 0) unresolved[rel] = check.Unresolved;
            if (check.Missing.Count > 0) missingD[rel] = check.Missing;
            foreach (var (cat, g) in r.Refs)
            {
                Add(extRefs, cat ?? "未知", 1);
                if (cat is null)
                {
                    if (!unknownIndex.TryGetValue(g, out var ix))
                    {
                        unknownIndex[g] = ix = unknownRefs.Count;
                        unknownRefs.Add(new(g, 0));
                    }
                    unknownRefs[ix] = new(g, unknownRefs[ix].Value + 1);
                }
            }
            if (r.IsScene) ((PyDict)index["scenes"]!)[r.Name] = r.Entry;
            else ((PyDict)index["prefabs"]!)[guid] = r.Entry;
            if (deferred[i] is { } outObj) ImportContext.WriteJson(PyPath.Join(ctx.Out, (string)r.Entry!["file"]!), outObj);
        }

        index["buildOrder"] = sceneOrder.Cast<object?>().ToList();
        ImportContext.WriteJson(PyPath.Join(outDir, "index.json"), index);
        var builtinCheck = VerifyBuiltinRefs(gameData, sceneOrder);

        PyDict SortedCounts(Dictionary<string, long> d)
        {
            var o = new PyDict();
            foreach (var (k, v) in d.OrderBy(kv => kv.Key, PyStr.Comparer)) o[k] = v;
            return o;
        }
        var resolvedVals = resolver.Cache.Values.Where(v => v is not null && Py.Truthy(v) && v.ContainsKey("class")).ToList();
        var byAssembly = new Dictionary<string, long>();
        foreach (var v in resolvedVals) Add(byAssembly, (string)v!["assembly"]!, 1);
        // Counter.most_common()：數量多的在前，同數量照第一次出現的順序
        var unknownSorted = new PyDict();
        foreach (var kv in unknownRefs.Select((kv, i) => (kv, i)).OrderByDescending(x => x.kv.Value).ThenBy(x => x.i))
            unknownSorted[kv.kv.Key] = kv.kv.Value;
        var report = new PyDict
        {
            ["files"] = new PyDict
            {
                ["scenes"] = (long)((PyDict)index["scenes"]!).Count,
                ["prefabs"] = (long)((PyDict)index["prefabs"]!).Count,
                ["source"] = (long)files.Count,
                ["failed"] = failed,
            },
            ["objects"] = new PyDict { ["parsed"] = SortedCounts(totals), ["headers"] = SortedCounts(headerTotals) },
            ["scripts"] = new PyDict
            {
                ["monoScriptsInBuild"] = (long)resolver.MonoScripts.Count,
                ["distinctRefs"] = (long)resolver.Cache.Count,
                ["resolved"] = (long)resolvedVals.Count,
                ["unresolved"] = unresolved,
                ["missingInOriginal"] = missingD,
                ["byAssembly"] = SortedCounts(byAssembly),
            },
            ["treeProblems"] = problems,
            ["externalRefs"] = SortedCounts(extRefs),
            ["builtinRefs"] = builtinCheck,
            ["unknownRefGuids"] = unknownSorted,
            ["suspectHexKeys"] = suspect,
            ["seconds"] = ImportContext.Seconds(t0),
        };
        ImportContext.WriteJson(PyPath.Join(outDir, "report.json"), report);
        return report;
    }
}
