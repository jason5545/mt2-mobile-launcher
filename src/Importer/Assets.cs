using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mt2Mobile.Importer.Serialized;
using Mt2Mobile.Importer.PyCompat;
using Mt2Mobile.Importer.Unity;

using Mt2Mobile.Shared;

namespace Mt2Mobile.Importer;

/// <summary>
/// assets.py：AssetRipper 匯出的素材全部匯進 &lt;out&gt;/assets/（輸出格式見 Python 版檔頭）。
/// 原版 build 的讀取照 UnityPy（assets 階段是 UnityPy 的 C 加速版，所以 typetree 用 boost 模式讀）。
/// </summary>
public sealed class Assets(ImportContext ctx)
{
    private const bool Boost = true;
    private string AssetsDir => ctx.Assets;

    // ───────── 共用 ─────────

    public static void WriteMeshes(string outDir, PyDict table)
    {
        string d = PyPath.Join(outDir, "meshes");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        foreach (var (guid, mesh) in table) ImportContext.WriteJson(PyPath.Join(d, $"{guid}.json"), mesh);
        ImportContext.WriteJson(PyPath.Join(d, "index.json"), table.Keys.Cast<string>().Order(PyStr.Comparer).Cast<object?>().ToList());
        File.Delete(PyPath.Join(outDir, "meshes.json"));
    }

    private static readonly Regex FirstDocType = PyRe.M(@"^--- !u!\d+ &-?\d+(?: stripped)?\n(" + PyRe.W + @"+):");

    public static string? FirstDocTypeOf(string path)
    {
        var m = FirstDocType.Match(UnityYaml.ReadHead(path, 400));
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>build 裡存資產的檔案：globalgamemanagers.assets、resources.assets、sharedassets*、Addressables bundle</summary>
    public static List<string> BuildAssetFiles(string gameData)
    {
        var files = new List<string> { PyPath.Join(gameData, "globalgamemanagers.assets"), PyPath.Join(gameData, "resources.assets") };
        files.AddRange(PyPath.Glob(gameData, "sharedassets*.assets").Order(PyPath.Comparer));
        string aa = PyPath.Join(gameData, "StreamingAssets/aa");
        if (Directory.Exists(aa)) files.AddRange(PyPath.RGlob(aa, "*.bundle").Order(PyPath.Comparer));
        return files.Where(File.Exists).ToList();
    }

    public sealed class Category(string name, string assets)
    {
        public string Name { get; } = name;
        public List<(string guid, string path)> Sources { get; } = [];
        public long Exported;
        public List<object?> Failed { get; } = [];

        public string Rel(string p) => PyPath.RelativeTo(p, assets);

        public void Fail(string p, string err) => Failed.Add(new PyDict { ["src"] = Rel(p), ["error"] = err });

        public PyDict Report() => new() { ["source"] = (long)Sources.Count, ["exported"] = Exported, ["failed"] = Failed };
    }

    /// <summary>例外的 str(e)：Python 版的失敗訊息都是 str(e)</summary>
    private static string Err(Exception e) => e.Message;

    /// <summary>f"{type(e).__name__}: {e}"</summary>
    private static string ErrT(Exception e) => $"{ExceptionName(e)}: {e.Message}";

    public static string ExceptionName(Exception e) => e switch
    {
        KeyNotFoundException => "KeyError",
        FileNotFoundException or DirectoryNotFoundException => "FileNotFoundError",
        InvalidCastException => "TypeError",
        _ => "ValueError",
    };

    // ───────── 分類 ─────────

    public static readonly PyDict NotImported = new()
    {
        ["ComputeShader"] = "compute shader：WebGL2 沒有 compute，用到時再看",
        ["VisualEffectAsset"] = "VFX Graph：還沒做",
        ["ShaderVariantCollection"] = "編輯器與預熱用，執行環境不需要",
        ["LightingDataAsset"] = "烘焙光照資料：還沒做",
        ["LightProbes"] = "烘焙光照資料：還沒做",
        ["Prefab/Scene"] = "scenes.py（場景樹匯入）",
        ["Script"] = "C# 原始碼與 DLL：轉換器的輸入，不是素材",
    };

    private static readonly HashSet<string> YamlExts = [".asset", ".mat", ".anim", ".controller", ".overrideController", ".physicMaterial", ".shadervariants", ".playable"];
    private static readonly HashSet<string> AudioExts = [".ogg", ".wav", ".mp3", ".aif", ".aiff"];

    private static readonly string[] CategoryNames =
        ["textures", "sprites", "fonts", "materials", "shaders", "animations", "animators", "meshes", "physicMaterials", "monobehaviours", "texts", "audioclips", "audiomixers"];

    public (Dictionary<string, Category> cats, Dictionary<string, long> skipped) Classify(Dictionary<string, string> guids)
    {
        var cats = CategoryNames.ToDictionary(n => n, n => new Category(n, AssetsDir));
        var skipped = new Dictionary<string, long>();
        void Skip(string k) => skipped[k] = skipped.GetValueOrDefault(k) + 1;
        foreach (var (guid, p) in guids.OrderBy(kv => kv.Value, PyStr.Comparer))
        {
            if (!File.Exists(p)) continue;  // 資料夾也有 .meta
            string ext = PyPath.Suffix(p);
            if (ext == ".png") cats["textures"].Sources.Add((guid, p));
            else if (ext is ".ttf" or ".otf") cats["fonts"].Sources.Add((guid, p));
            else if (ext == ".shader") cats["shaders"].Sources.Add((guid, p));
            else if (AudioExts.Contains(ext.ToLowerInvariant())) cats["audioclips"].Sources.Add((guid, p));
            else if (ext == ".mixer") cats["audiomixers"].Sources.Add((guid, p));
            else if (ext is ".txt" or ".bytes" or ".json") cats["texts"].Sources.Add((guid, p));
            else if (ext is ".cs" or ".dll") Skip("Script");
            else if (ext is ".prefab" or ".unity") Skip("Prefab/Scene");
            else if (YamlExts.Contains(ext))
            {
                string? t = FirstDocTypeOf(p);
                switch (t)
                {
                    case "Sprite": cats["sprites"].Sources.Add((guid, p)); break;
                    case "Material": cats["materials"].Sources.Add((guid, p)); break;
                    case "AnimationClip": cats["animations"].Sources.Add((guid, p)); break;
                    case "AnimatorController" or "AnimatorOverrideController": cats["animators"].Sources.Add((guid, p)); break;
                    case "Mesh": cats["meshes"].Sources.Add((guid, p)); break;
                    case "PhysicMaterial": cats["physicMaterials"].Sources.Add((guid, p)); break;
                    case "Shader": cats["shaders"].Sources.Add((guid, p)); break;
                    case "MonoBehaviour": cats["monobehaviours"].Sources.Add((guid, p)); break;
                    default: Skip(t is { Length: > 0 } ? t : $"未知（{ext}）"); break;
                }
            }
            else Skip($"其他副檔名 {(ext.Length > 0 ? ext : "(無)")}");
        }
        return (cats, skipped);
    }

    // ───────── 貼圖 ─────────

    private static readonly string[] TextureImporterKeys =
        ["textureType", "textureShape", "alphaUsage", "alphaIsTransparency", "spriteMode", "spritePixelsToUnits", "spriteMeshType", "spriteBorder",
         "spritePivot", "nPOTScale", "maxTextureSize", "textureFormat", "flipbookRows", "flipbookColumns", "isReadable"];

    private static PyDict TextureImporter(string meta)
    {
        var docs = UnityYaml.LoadYamlText(PyStr.ReadText(meta));
        if (docs.Count == 0) throw new IndexOutOfRangeException("list index out of range");
        var imp = Dict(docs[0].Get("TextureImporter"));
        var mips = Dict(imp.Get("mipmaps"));
        var ts = Dict(imp.Get("textureSettings"));
        var o = new PyDict();
        foreach (var k in TextureImporterKeys)
            if (imp.TryGetValue(k, out var v)) o[k] = v;
        o["sRGBTexture"] = mips.Get("sRGBTexture");
        o["enableMipMap"] = mips.Get("enableMipMap");
        o["filterMode"] = ts.Get("filterMode");
        o["aniso"] = ts.Get("aniso");
        o["mipBias"] = ts.Get("mipBias");
        o["wrapU"] = ts.Get("wrapU");
        o["wrapV"] = ts.Get("wrapV");
        o["wrapW"] = ts.Get("wrapW");
        return o;
    }

    /// <summary>`x or {}`：真值才用</summary>
    public static PyDict Dict(object? v) => Py.Truthy(v) ? (PyDict)v! : new PyDict();

    public static List<object?> ListOf(object? v) => Py.Truthy(v) ? AsList(v) : [];

    /// <summary>list 或 tuple 都當 list 走</summary>
    public static List<object?> AsList(object? v) => v switch
    {
        List<object?> l => l,
        PyTuple t => [.. t.Items],
        _ => throw new InvalidCastException($"'{v?.GetType().Name}' object is not iterable"),
    };

    private HashSet<string> OriginalTextureNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // path_id_map.json 在 ExportedProject 的上一層（Assets → ExportedProject → 匯出根目錄），
        // 匯出根目錄的位置隨 AssetRipper 設定而變，往上找幾層。
        string? path = null;
        string dir = ctx.Assets;
        for (int i = 0; i < 3 && dir.Length > 1; i++, dir = PyPath.Parent(dir))
        {
            string candidate = PyPath.Join(dir, "AuxiliaryFiles/path_id_map.json");
            if (File.Exists(candidate)) { path = candidate; break; }
        }
        if (path is null)
        {
            ctx.Log("  找不到 AuxiliaryFiles/path_id_map.json，貼圖名稱保留 AssetRipper 的 _N 後綴");
            return names;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var file in doc.RootElement.GetProperty("Files").EnumerateArray())
        {
            if (!file.TryGetProperty("Assets", out var assets)) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("Type", out var type) || type.GetString() != "Texture2D") continue;
                if (asset.TryGetProperty("Name", out var name) && name.GetString() is { } value) names.Add(value);
            }
        }
        return names;
    }

    private static string TextureName(string path, HashSet<string> originalNames)
    {
        // AssetRipper 撞名時會在檔名加 _N，但原始 m_Name 沒有這段；本來就以數字結尾的名稱不能去掉。
        string name = PyPath.Stem(path);
        int split = name.LastIndexOf('_');
        if (split <= 0 || !int.TryParse(name[(split + 1)..], out _)) return name;
        string baseName = name[..split];
        return !originalNames.Contains(name) && originalNames.Contains(baseName) ? baseName : name;
    }

    public PyDict ExportTextures(Category cat)
    {
        var table = new PyDict();
        var originalNames = OriginalTextureNames();
        string texDir = PyPath.Join(ctx.Out, "textures");
        Directory.CreateDirectory(texDir);
        foreach (var (guid, p) in cat.Sources)
        {
            int w, h;
            string mode = "";
            PyDict importer;
            RawTexture? raw;
            try
            {
                // 匯出時保留原始壓縮資料的（Mt2Mobile.TextureExport，檔名照舊是 .png）：寬高、格式、層數讀容器的標頭，複製成 .mt2t
                raw = RawTexture.ReadHeader(p);
                if (raw is null) (w, h, mode) = PngInfo.Read(p);
                else (w, h) = (raw.Width, raw.Height);
                importer = TextureImporter(p + ".meta");
                ImportContext.Clone(p, PyPath.Join(texDir, raw is null ? $"{guid}.png" : guid + RawTexture.Extension));
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            table[guid] = raw is null
                ? new PyDict
                {
                    ["name"] = TextureName(p, originalNames), ["src"] = cat.Rel(p), ["file"] = $"textures/{guid}.png",
                    ["width"] = (long)w, ["height"] = (long)h, ["pngMode"] = mode, ["importer"] = importer,
                }
                : new PyDict
                {
                    ["name"] = TextureName(p, originalNames), ["src"] = cat.Rel(p), ["file"] = $"textures/{guid}{RawTexture.Extension}",
                    ["width"] = (long)w, ["height"] = (long)h, ["format"] = RawTexture.FormatName(raw.Format), ["mips"] = (long)raw.Mips, ["importer"] = importer,
                };
            cat.Exported++;
        }
        return table;
    }

    /// <summary>Texture2DArray（textureShape 4）的層數寫進 textures.json 的 depth（原版 build 讀）</summary>
    public PyDict TextureArrayDepths(string gameData, PyDict textures)
    {
        var rep = new PyDict { ["matched"] = 0L, ["unmatched"] = new List<object?>() };
        var arrays = textures.Where(kv => Py.Eq(Dict(((PyDict)kv.Value!)["importer"]).Get("textureShape"), 4L)).ToList();
        if (arrays.Count == 0) return rep;
        if (!File.Exists(PyPath.Join(gameData, "globalgamemanagers.assets")))
        {
            rep["unmatched"] = arrays.Select(kv => ((PyDict)kv.Value!)["src"]).Cast<string>().Order(PyStr.Comparer).Cast<object?>().ToList();
            return rep;
        }
        var byName = new Dictionary<string, List<(long w, long h, long d)>>();
        foreach (var f in BuildAssetFiles(gameData))
        {
            var env = new BuildEnv(gameData);
            foreach (var sf in env.Load(f))
                foreach (var obj in sf.Objects)
                {
                    if (obj.TypeName != "Texture2DArray") continue;
                    var t = obj.ReadTypeTree(Boost);
                    string name = (string)t["m_Name"]!;
                    if (!byName.TryGetValue(name, out var l)) byName[name] = l = [];
                    l.Add((Py.ToLong(t["m_Width"]), Py.ToLong(t["m_Height"]), Py.ToLong(t["m_Depth"])));
                }
        }
        long matched = 0;
        var unmatched = (List<object?>)rep["unmatched"]!;
        foreach (var (_, recObj) in arrays)
        {
            var rec = (PyDict)recObj!;
            var hits = new HashSet<long>();
            foreach (var hh in byName.GetValueOrDefault((string)rec["name"]!) ?? [])
                if (Py.Eq(hh.w, rec["width"]) && Py.Eq(hh.h * hh.d, rec["height"])) hits.Add(hh.d);
            if (hits.Count != 1)
            {
                unmatched.Add(rec["src"]);
                continue;
            }
            rec["depth"] = hits.First();
            matched++;
        }
        rep["matched"] = matched;
        return rep;
    }

    // ───────── Sprite ─────────

    private static readonly string[] SpriteDrop = ["m_PhysicsShape", "m_Bones", "m_AtlasRD", "m_RenderDataKey", "m_AtlasTags", "m_SpriteID"];
    private static readonly string[] RdMeshKeys = ["m_SubMeshes", "m_IndexBuffer", "m_VertexData", "m_Bindpose"];

    private (PyDict? rec, string? err) SpriteWorker(string guid, string path, string meshDir)
    {
        try
        {
            var docs = ctx.Yaml.LoadUnityDocs(path);
            var sprite = docs.First(d => d.Type == "Sprite").Data;
            var rd = Dict(sprite.Get("m_RD"));
            var channels = UnityYaml.DecodeVertexData(Dict(rd.Get("m_VertexData")));
            if (!Py.Truthy(sprite.Get("m_Bones")))
            {
                channels.Remove("blendWeight");
                channels.Remove("blendIndices");
            }
            channels.Remove("uv0");
            var mesh = new PyDict
            {
                ["vertexCount"] = Py.ToLong(Py.Truthy(Dict(rd.Get("m_VertexData")).Get("m_VertexCount")) ? Dict(rd.Get("m_VertexData")).Get("m_VertexCount") : 0L),
                ["subMeshes"] = UnityYaml.NormRefs(Py.Truthy(rd.Get("m_SubMeshes")) ? rd.Get("m_SubMeshes") : new List<object?>()),
                ["indices"] = UnityYaml.DecodeIndexBuffer(rd.Get("m_IndexBuffer") as string, 0),
            };
            foreach (var (name, ch) in channels) mesh[name] = ((PyDict)ch!)["values"];
            var dims = new PyDict();
            foreach (var (name, ch) in channels) dims[name] = ((PyDict)ch!)["dimension"];
            mesh["dimensions"] = dims;
            ImportContext.WriteJson(PyPath.Join(meshDir, $"{guid}.json"), mesh);
            var rec = new PyDict();
            foreach (var (k, v) in UnityYaml.StripMetaFields(sprite))
                if (!(k is string s && SpriteDrop.Contains(s))) rec[k] = v;
            var rdOut = new PyDict();
            foreach (var (k, v) in rd)
                if (!(k is string s && RdMeshKeys.Contains(s))) rdOut[k] = v;
            rec["m_RD"] = rdOut;
            rec["vertexCount"] = mesh["vertexCount"];
            rec["mesh"] = $"sprite-meshes/{guid}.json";
            return ((PyDict)UnityYaml.NormRefs(rec)!, null);
        }
        catch (Exception e)
        {
            return (null, e is InvalidOperationException { Message: "Sequence contains no matching element" } ? "StopIteration: " : ErrT(e));
        }
    }

    public PyDict ExportSprites(Category cat, PyDict textures)
    {
        string meshDir = PyPath.Join(ctx.Out, "sprite-meshes");
        Directory.CreateDirectory(meshDir);
        var results = new (PyDict? rec, string? err)[cat.Sources.Count];
        Parallel.For(0, cat.Sources.Count, new ParallelOptions { MaxDegreeOfParallelism = ctx.Parallelism },
            i => results[i] = SpriteWorker(cat.Sources[i].guid, cat.Sources[i].path, meshDir));
        var table = new PyDict();
        for (int i = 0; i < results.Length; i++)
        {
            var (guid, p) = cat.Sources[i];
            var (rec, err) = results[i];
            if (err is not null)
            {
                cat.Fail(p, err);
                continue;
            }
            var tex = Dict(Dict(rec!.Get("m_RD")).Get("texture")).Get("guid");
            if (!(tex is not null && textures.ContainsKey(tex)))
            {
                cat.Fail(p, $"貼圖 {Py.Str(tex)} 不在 textures.json");
                continue;
            }
            rec["src"] = cat.Rel(p);
            table[guid] = rec;
            cat.Exported++;
        }
        return Sorted(table);
    }

    /// <summary>dict(sorted(table.items()))</summary>
    public static PyDict Sorted(PyDict d)
    {
        var o = new PyDict(d.Count);
        foreach (var (k, v) in d.OrderBy(kv => kv.Key, Comparer<object?>.Create(Py.Compare))) o[k] = v;
        return o;
    }

    /// <summary>有透明 padding 的 sprite：m_Rect、m_Pivot、m_Offset、textureRectOffset 用原版 build 的值蓋回去</summary>
    public PyDict RebaseSprites(string gameData, PyDict sprites)
    {
        var unmatched = new List<object?>();
        var ambiguous = new List<object?>();
        var rep = new PyDict { ["matched"] = 0L, ["rebased"] = 0L, ["unmatched"] = unmatched, ["ambiguous"] = ambiguous };
        if (!File.Exists(PyPath.Join(gameData, "globalgamemanagers.assets")))
        {
            unmatched.Add("找不到原版 build（設定檔 import.game）");
            return rep;
        }
        var byName = new Dictionary<string, List<PyDict>>(StringComparer.Ordinal);
        foreach (var f in BuildAssetFiles(gameData))
        {
            var env = new BuildEnv(gameData);
            foreach (var sf in env.Load(f))
                foreach (var obj in sf.Objects)
                {
                    if (obj.TypeName != "Sprite") continue;
                    var t = obj.ReadTypeTree(Boost);
                    string name = (string)t["m_Name"]!;
                    if (!byName.TryGetValue(name, out var l)) byName[name] = l = [];
                    l.Add(t);
                }
        }

        static bool SameRect(PyDict a, PyDict b) =>
            new[] { "x", "y", "width", "height" }.All(k => Math.Abs(Py.ToDouble(a[k]!) - Py.ToDouble(b[k]!)) < 0.01);

        string[] pick = ["m_Rect", "m_Pivot", "m_Offset"];
        long matched = 0, rebased = 0;
        foreach (var (_, recObj) in sprites)
        {
            var rec = (PyDict)recObj!;
            var recTr = (PyDict)((PyDict)rec["m_RD"]!)["textureRect"]!;
            var hits = (byName.GetValueOrDefault((string)rec["m_Name"]!) ?? [])
                .Where(t => SameRect((PyDict)((PyDict)t["m_RD"]!)["textureRect"]!, recTr)).ToList();
            if (hits.Count == 0)
            {
                unmatched.Add(rec["src"]);
                continue;
            }
            var vals = hits.Select(h =>
            {
                var d = new PyDict();
                foreach (var k in pick) d[k] = h[k];
                d["o"] = ((PyDict)h["m_RD"]!)["textureRectOffset"];
                return PyJson.Dumps(d, null, ", ", ": ", sortKeys: true);
            }).Distinct().Count();
            if (vals > 1)
            {
                ambiguous.Add(rec["src"]);
                continue;
            }
            var b = hits[0];
            matched++;
            var bRect = (PyDict)b["m_Rect"]!;
            var rRect = (PyDict)rec["m_Rect"]!;
            if (!(Py.Eq(bRect["width"], rRect["width"]) && Py.Eq(bRect["height"], rRect["height"]))) rebased++;
            foreach (var k in pick)
                rec[k] = b[k] is PyDict bd ? FloatDict(bd) : b[k];
            ((PyDict)rec["m_RD"]!)["textureRectOffset"] = FloatDict((PyDict)((PyDict)b["m_RD"]!)["textureRectOffset"]!);
        }
        rep["matched"] = matched;
        rep["rebased"] = rebased;
        return rep;
    }

    private static PyDict FloatDict(PyDict d)
    {
        var o = new PyDict(d.Count);
        foreach (var (k, v) in d) o[k] = Py.ToDouble(v!);
        return o;
    }

    // ───────── 字型 ─────────

    public PyDict ExportFonts(Category cat)
    {
        var table = new PyDict();
        string fontDir = PyPath.Join(ctx.Out, "fonts");
        Directory.CreateDirectory(fontDir);
        foreach (var (guid, p) in cat.Sources)
        {
            PyDict importer;
            string ext;
            try
            {
                var docs = UnityYaml.LoadYamlText(PyStr.ReadText(p + ".meta"));
                if (docs.Count == 0) throw new IndexOutOfRangeException("list index out of range");
                importer = Dict(docs[0].Get("TrueTypeFontImporter"));
                ext = PyPath.Suffix(p).ToLowerInvariant();
                ImportContext.Clone(p, PyPath.Join(fontDir, $"{guid}{ext}"));
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            table[guid] = new PyDict
            {
                ["name"] = PyPath.Stem(p), ["src"] = cat.Rel(p), ["file"] = $"fonts/{guid}{ext}",
                ["bytes"] = new FileInfo(p).Length, ["importer"] = UnityYaml.NormRefs(importer),
            };
            cat.Exported++;
        }
        return table;
    }

    // ───────── shader ─────────

    private static readonly Regex ShaderNameRe = PyRe.M(@"^" + PyRe.S + @"*Shader" + PyRe.S + @"+""([^""]+)""");
    private static readonly Regex BlobLineRe = PyRe.M(@"^  compressedBlob: [0-9a-f]*\n");
    private static readonly Regex MNameRe = PyRe.M(@"^  m_Name: (.+)$");
    private static readonly string[] PassDrop =
        ["m_EditorDataHash", "m_NameIndices", "m_Platforms", "m_HasInstancingVariant", "m_HasProceduralInstancingVariant", "m_ProgramMask",
         "m_PackageRequirements", "m_CombinedStatus", "m_StatusMessage", "m_Requirements"];

    public static object? CollapseState(object? v)
    {
        if (v is PyDict d)
        {
            if (d.Count == 2 && d.ContainsKey("val") && d.ContainsKey("name"))
                return Py.Truthy(d["name"]) ? new PyDict { ["val"] = d["val"], ["prop"] = d["name"] } : d["val"];
            var o = new PyDict(d.Count);
            foreach (var (k, x) in d) o[k] = CollapseState(x);
            return o;
        }
        if (v is List<object?> l) return l.Select(CollapseState).ToList();
        return v;
    }

    private static List<object?> ShaderProps(PyDict parsed)
    {
        var props = new List<object?>();
        foreach (PyDict p in ListOf(Dict(parsed.Get("m_PropInfo")).Get("m_Props")).Cast<PyDict>())
        {
            var q = new PyDict();
            foreach (var (k, v) in p)
                if (!((string)k!).StartsWith("m_DefValue[", StringComparison.Ordinal)) q[k] = v;
            q["m_DefValue"] = Enumerable.Range(0, 4).Select(i => p.Get($"m_DefValue[{i}]")).ToList();
            props.Add(q);
        }
        return props;
    }

    private static List<object?> ShaderSubShaders(PyDict parsed)
    {
        var o = new List<object?>();
        foreach (PyDict ss in ListOf(parsed.Get("m_SubShaders")).Cast<PyDict>())
        {
            var passes = new List<object?>();
            foreach (PyDict ps in ListOf(ss.Get("m_Passes")).Cast<PyDict>())
            {
                var d = new PyDict();
                foreach (var (k, v) in ps)
                {
                    string key = (string)k!;
                    if (PassDrop.Contains(key) || key.StartsWith("prog", StringComparison.Ordinal) || key == "serializedVersion") continue;
                    d[k] = v;
                }
                passes.Add(CollapseState(d));
            }
            var rest = new PyDict();
            foreach (var (k, v) in ss)
                if (k is not ("m_Passes" or "m_PackageRequirements")) rest[k] = v;
            rest["m_Passes"] = passes;
            o.Add(rest);
        }
        return o;
    }

    private (object? name, PyDict rec) ShaderRecord(string ypath, string relTo)
    {
        string text = BlobLineRe.Replace(PyStr.ReadText(ypath), "");
        var shader = ctx.Yaml.LoadUnityDocs(ypath, text).First(d => d.Type == "Shader").Data;
        var parsed = Dict(shader.Get("m_ParsedForm"));
        object? name = Py.Truthy(parsed.Get("m_Name")) ? parsed.Get("m_Name") : shader.Get("m_Name");
        return (name, new PyDict
        {
            ["name"] = name,
            ["yamlSrc"] = PyPath.RelativeTo(ypath, relTo),
            ["m_PropInfo"] = ShaderProps(parsed),
            ["m_KeywordNames"] = parsed.Get("m_KeywordNames"),
            ["m_FallbackName"] = parsed.Get("m_FallbackName"),
            ["m_SubShaders"] = ShaderSubShaders(parsed),
        });
    }

    public (PyDict table, PyDict names) ExportShaders(Category cat, string? shaderSrc)
    {
        var yamlByName = new Dictionary<string, string>(StringComparer.Ordinal);
        if (shaderSrc is not null && cat.Sources.Any(s => PyPath.Suffix(s.path) == ".shader"))
        {
            string yamlAssets = PyPath.Join(shaderSrc, "Assets");
            foreach (var p in PyPath.RGlob(yamlAssets, "*.asset").Order(PyPath.Comparer))
            {
                if (FirstDocTypeOf(p) != "Shader") continue;
                var m = MNameRe.Match(UnityYaml.ReadHead(p, 4000));
                if (m.Success) yamlByName[PyStr.Strip(m.Groups[1].Value)] = p;
            }
        }
        var table = new PyDict();
        var names = new PyDict();
        foreach (var (guid, p) in cat.Sources)
        {
            object? name;
            PyDict rec;
            try
            {
                if (PyPath.Suffix(p) == ".shader")
                {
                    string head = PyStr.ReadText(p);
                    var m = ShaderNameRe.Match(head.Length > 2000 ? head[..2000] : head);
                    if (!m.Success) throw new InvalidOperationException("讀不到 Shader \"名稱\"");
                    if (!yamlByName.TryGetValue(m.Groups[1].Value, out var ypath))
                        throw new InvalidOperationException($"YAML 匯出裡沒有 {m.Groups[1].Value}（import.shaderExport）");
                    (name, rec) = ShaderRecord(ypath, PyPath.Join(shaderSrc!, "Assets"));
                }
                else (name, rec) = ShaderRecord(p, AssetsDir);
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            names[guid] = name;
            var t = new PyDict { ["src"] = cat.Rel(p) };
            foreach (var (k, v) in rec) t[k] = v;
            table[guid] = t;
            cat.Exported++;
        }
        return (table, names);
    }

    // ───────── 單一物件的 YAML 資產 ─────────

    public PyDict ExportSingle(Category cat, string typeName, Action<PyDict, PyDict>? extra = null)
    {
        var table = new PyDict();
        foreach (var (guid, p) in cat.Sources)
        {
            PyDict rec;
            try
            {
                var docs = ctx.Yaml.LoadUnityDocs(p);
                var obj = docs.FirstOrDefault(d => d.Type == typeName) ?? throw new StopIterationException();
                var norm = (PyDict)UnityYaml.NormRefs(UnityYaml.StripMetaFields(obj.Data))!;
                rec = new PyDict { ["src"] = cat.Rel(p), ["fileID"] = obj.FileID };
                foreach (var (k, v) in norm) rec[k] = v;
                extra?.Invoke(rec, obj.Data);
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            table[guid] = rec;
            cat.Exported++;
        }
        return table;
    }

    /// <summary>Python 的 next(…) 沒有元素時丟 StopIteration，str(e) 是空字串</summary>
    public sealed class StopIterationException() : Exception("");

    private static readonly PyDict BuiltinGuids = new()
    {
        ["0000000000000000f000000000000000"] = "unity_builtin_extra",
        ["0000000000000000e000000000000000"] = "unity default resources",
    };

    public PyDict ExportMaterials(Category cat, PyDict shaderNames) => ExportSingle(cat, "Material", (rec, _) =>
    {
        var sh = Dict(rec.Get("m_Shader"));
        rec["shaderName"] = shaderNames.Get(sh.Get("guid", ""));
        if (sh.Get("guid") is { } g && BuiltinGuids.ContainsKey(g))
            rec["builtinShader"] = new PyDict { ["resources"] = BuiltinGuids[g], ["fileID"] = sh["fileID"] };
    });

    /// <summary>材質的內建 shader（unity_builtin_extra 的 fileID）→ shader 名稱（查法見 Python 版的說明）</summary>
    public PyDict NameBuiltinShaders(string gameData, PyDict materials)
    {
        var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (_, mObj) in materials)
        {
            var m = (PyDict)mObj!;
            if (m.Get("builtinShader") is PyDict b && Py.Truthy(b) && Py.Eq(b["resources"], "unity_builtin_extra"))
            {
                string key = Py.Truthy(m.Get("m_Name")) ? (string)m["m_Name"]! : "";
                if (!byName.TryGetValue(key, out var s)) byName[key] = s = [];
                s.Add(Py.Str(b["fileID"]));
            }
        }
        var seen = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        var env = new BuildEnv(gameData);
        foreach (var path in BuildAssetFiles(gameData))
        {
            if (PyPath.Suffix(path) == ".bundle") continue;
            foreach (var sf in env.Load(path))
                foreach (var obj in sf.Objects)
                {
                    if (obj.TypeName != "Material") continue;
                    string shaderName;
                    HashSet<string>? fids;
                    try
                    {
                        var mat = obj.ReadTypeTree(Boost);
                        if (!(mat.Get("m_Name") is string mn && byName.TryGetValue(mn, out fids)) || fids.Count == 0) continue;
                        var shader = env.Deref(sf, (PyDict)mat["m_Shader"]!).ReadTypeTree(Boost);
                        shaderName = (string)((PyDict)shader["m_ParsedForm"]!)["m_Name"]!;
                    }
                    catch (Exception)
                    {
                        continue;  // 讀不到的材質不算數
                    }
                    foreach (var fid in fids)
                    {
                        if (!seen.TryGetValue(fid, out var names)) seen[fid] = names = new PyDict();
                        names[shaderName] = Py.ToLong(names.Get(shaderName, 0L)) + 1;
                    }
                }
        }
        var table = new PyDict();
        var failed = new List<object?>();
        string extra = PyPath.Join(gameData, "Resources/unity_builtin_extra");
        Dictionary<string, BuildObject>? extraObjs = null;
        if (File.Exists(extra)) extraObjs = env.LoadOne(extra).Objects.ToDictionary(o => o.PathId.ToString(Py.Inv));
        foreach (var fid in byName.Values.SelectMany(s => s).Distinct().OrderBy(f => long.Parse(f, Py.Inv)))
        {
            var names = seen.GetValueOrDefault(fid) ?? new PyDict();
            if (names.Count == 1) table[fid] = names.Keys.First();
            else if (names.Count > 1) failed.Add($"fileID {fid} 對到多個 shader：{Py.Repr(names)}");
            else if (extraObjs is not null && extraObjs.TryGetValue(fid, out var eo) && eo.TypeName == "Shader")
                table[fid] = ((PyDict)eo.ReadTypeTree(Boost)["m_ParsedForm"]!)["m_Name"];
            else failed.Add($"fileID {fid}：build 裡沒有對得到的材質，unity_builtin_extra 也沒有這個 Shader");
        }
        foreach (var (_, mObj) in materials)
        {
            var m = (PyDict)mObj!;
            if (m.Get("builtinShader") is PyDict b && Py.Truthy(b) && Py.Eq(b["resources"], "unity_builtin_extra") && table.ContainsKey(Py.Str(b["fileID"])))
            {
                b["name"] = table[Py.Str(b["fileID"])];
                m["shaderName"] = b["name"];
            }
        }
        return new PyDict { ["table"] = table, ["failed"] = failed };
    }

    public PyDict ExportMeshes(Category cat) => ExportSingle(cat, "Mesh", (rec, raw) =>
    {
        var vd = Dict(raw.Get("m_VertexData"));
        var channels = UnityYaml.DecodeVertexData(vd);
        rec.Remove("m_VertexData");
        rec.Remove("m_IndexBuffer");
        rec.Remove("m_CompressedMesh");
        rec["vertexCount"] = Py.ToLong(Py.Truthy(vd.Get("m_VertexCount")) ? vd.Get("m_VertexCount") : 0L);
        rec["indices"] = UnityYaml.DecodeIndexBuffer(raw.Get("m_IndexBuffer") as string, Py.ToLong(Py.Truthy(raw.Get("m_IndexFormat")) ? raw.Get("m_IndexFormat") : 0L));
        rec["channels"] = channels;
        if (Py.Truthy(raw.Get("m_MeshCompression"))) throw new InvalidOperationException("m_MeshCompression 非 0，要改解 m_CompressedMesh");
    });

    public PyDict ExportAnimators(Category cat)
    {
        var table = new PyDict();
        foreach (var (guid, p) in cat.Sources)
        {
            UnityDoc root;
            PyDict objects;
            try
            {
                var docs = ctx.Yaml.LoadUnityDocs(p);
                root = docs.FirstOrDefault(d => d.Type is "AnimatorController" or "AnimatorOverrideController") ?? throw new StopIterationException();
                objects = new PyDict();
                foreach (var d in docs)
                {
                    var o = new PyDict { ["type"] = d.Type };
                    foreach (var (k, v) in (PyDict)UnityYaml.NormRefs(UnityYaml.StripMetaFields(d.Data))!) o[k] = v;
                    objects[d.FileID] = o;
                }
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            table[guid] = new PyDict
            {
                ["name"] = root.Data.Get("m_Name"), ["src"] = cat.Rel(p), ["type"] = root.Type, ["root"] = root.FileID, ["objects"] = objects,
            };
            cat.Exported++;
        }
        return table;
    }

    // ───────── MonoBehaviour 資產 ─────────

    public static string? MonoKind(PyDict data)
    {
        bool Has(string k) => data.ContainsKey(k);
        if ((Has("m_FaceInfo") && Has("m_GlyphTable")) || Has("m_fontInfo")) return "TMP_FontAsset";
        if (Has("m_SpriteGlyphTable") || Has("spriteInfoList")) return "TMP_SpriteAsset";
        if (Has("m_StyleList")) return "TMP_StyleSheet";
        if (Has("topLeft") && Has("topRight") && Has("bottomLeft") && Has("bottomRight")) return "TMP_ColorGradient";
        if (Has("m_defaultFontAsset")) return "TMP_Settings";
        if (Has("components") && !Has("isGlobal") && !Has("m_RendererFeatures")) return "VolumeProfile";
        return null;
    }

    public PyDict ExportMonoBehaviours(Category cat, Dictionary<string, string> guids, string? gameData)
    {
        var resolver = gameData is not null ? new ScriptResolver(AssetsDir, gameData, guids) : null;

        string? ClassOf(PyDict data)
        {
            var script = Dict(data.Get("m_Script"));
            var sp = guids.GetValueOrDefault(Py.Str(script.Get("guid", "")));
            if (script.Get("guid", "") is string g && guids.TryGetValue(g, out sp) && PyPath.Suffix(sp) == ".cs") return PyPath.Stem(sp);
            var r = resolver is not null && Py.Truthy(script.Get("guid")) ? resolver.Resolve(script) : null;
            return r is not null && Py.Truthy(r) ? r.Get("class") as string : null;
        }

        string objDir = PyPath.Join(ctx.Out, "objects");
        Directory.CreateDirectory(objDir);
        var table = new PyDict();
        foreach (var (guid, p) in cat.Sources)
        {
            PyDict data;
            string? sp, cls;
            try
            {
                var docs = ctx.Yaml.LoadUnityDocs(p).Where(d => d.Type == "MonoBehaviour").ToList();
                var mono = docs.FirstOrDefault(d => d.FileID == "11400000") ?? (docs.Count > 0 ? docs[0] : throw new IndexOutOfRangeException("list index out of range"));
                data = mono.Data;
                var script = Dict(data.Get("m_Script"));
                sp = script.Get("guid", "") is string g ? guids.GetValueOrDefault(g) : null;
                cls = ClassOf(data);
                var rec = new PyDict { ["src"] = cat.Rel(p), ["fileID"] = mono.FileID };
                foreach (var (k, v) in (PyDict)UnityYaml.NormRefs(UnityYaml.StripMetaFields(data))!) rec[k] = v;
                var subs = new PyDict();
                foreach (var d in docs)
                {
                    if (ReferenceEquals(d, mono)) continue;
                    var s = new PyDict { ["$script"] = ClassOf(d.Data) };
                    foreach (var (k, v) in (PyDict)UnityYaml.NormRefs(UnityYaml.StripMetaFields(d.Data))!) s[k] = v;
                    subs[d.FileID] = s;
                }
                if (subs.Count > 0) rec["$subObjects"] = subs;
                ImportContext.WriteJson(PyPath.Join(objDir, $"{guid}.json"), rec);
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            table[guid] = new PyDict
            {
                ["name"] = data.Get("m_Name"),
                ["src"] = cat.Rel(p),
                ["kind"] = MonoKind(data),
                ["script"] = cls,
                ["scriptIsAssembly"] = (sp is not null && PyPath.Suffix(sp) == ".dll") || (sp is null && cls is not null),
                ["file"] = $"objects/{guid}.json",
            };
            cat.Exported++;
        }
        return table;
    }

    public PyDict ExportTexts(Category cat)
    {
        var table = new PyDict();
        string d = PyPath.Join(ctx.Out, "texts");
        foreach (var (guid, p) in cat.Sources)
        {
            PyDict rec;
            try
            {
                var raw = File.ReadAllBytes(p);
                string? text = PyStr.TryDecodeUtf8(raw, out var t) ? t : null;
                rec = new PyDict { ["name"] = PyPath.Stem(p), ["src"] = cat.Rel(p) };
                if (text is not null) rec["text"] = text;
                if (text is null || PyPath.Suffix(p) == ".bytes")
                {
                    Directory.CreateDirectory(d);
                    ImportContext.Clone(p, PyPath.Join(d, $"{guid}.bytes"));
                    rec["file"] = $"texts/{guid}.bytes";
                    rec["bytes"] = (long)raw.Length;
                }
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            table[guid] = rec;
            cat.Exported++;
        }
        return table;
    }

    // ───────── AudioClip ─────────

    private static readonly string[] AudioImporterKeys = ["loadInBackground", "ambisonic", "preloadAudioData", "forceToMono", "normalize"];

    public PyDict ExportAudioClips(Category cat)
    {
        var table = new PyDict();
        string d = PyPath.Join(ctx.Out, "audioclips");
        Directory.CreateDirectory(d);
        foreach (var (guid, p) in cat.Sources)
        {
            PyDict imp;
            string ext;
            try
            {
                var docs = UnityYaml.LoadYamlText(PyStr.ReadText(p + ".meta"));
                if (docs.Count == 0) throw new IndexOutOfRangeException("list index out of range");
                imp = Dict(docs[0].Get("AudioImporter"));
                ext = PyPath.Suffix(p).ToLowerInvariant();
                ImportContext.Clone(p, PyPath.Join(d, $"{guid}{ext}"));
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            var importer = new PyDict();
            foreach (var k in AudioImporterKeys)
                if (imp.TryGetValue(k, out var v)) importer[k] = v;
            importer["defaultSettings"] = imp.Get("defaultSettings");
            var rec = new PyDict
            {
                ["name"] = PyPath.Stem(p), ["src"] = cat.Rel(p), ["file"] = $"audioclips/{guid}{ext}",
                ["bytes"] = new FileInfo(p).Length, ["importer"] = importer,
            };
            if (ext == ".ogg")
                foreach (var (k, v) in OggVorbisInfo(p)) rec[k] = v;
            table[guid] = rec;
            cat.Exported++;
        }
        return table;
    }

    public static PyDict OggVorbisInfo(string p)
    {
        var data = File.ReadAllBytes(p);
        int i = data.AsSpan().IndexOf("\x01vorbis"u8);
        int last = data.AsSpan().LastIndexOf("OggS"u8);
        if (i < 0 || last < 0 || last + 14 > data.Length) return new PyDict();
        if (i + 16 > data.Length) return new PyDict();
        long channels = data[i + 11];
        long frequency = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 12, 4));
        long samples = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(last + 6, 8));
        if (frequency <= 0 || samples < 0) return new PyDict();
        return new PyDict { ["channels"] = channels, ["frequency"] = frequency, ["samples"] = samples };
    }

    // ───────── AudioMixer ─────────

    public PyDict ExportAudioMixers(Category cat)
    {
        var table = new PyDict();
        foreach (var (guid, p) in cat.Sources)
        {
            PyDict rec;
            try
            {
                var docs = ctx.Yaml.LoadUnityDocs(p);
                var root = docs.FirstOrDefault(d => d.Type == "AudioMixerController") ?? throw new StopIterationException();
                var effects = new PyDict();
                foreach (var d in docs)
                    if (d.Type == "AudioMixerEffectController")
                        effects[d.FileID] = UnityYaml.NormRefs(UnityYaml.StripMetaFields(d.Data));
                var groups = new PyDict();
                foreach (var d in docs)
                {
                    if (d.Type != "AudioMixerGroupController") continue;
                    var g = (PyDict)UnityYaml.NormRefs(UnityYaml.StripMetaFields(d.Data))!;
                    var effs = new List<object?>();
                    foreach (var r in ListOf(g.Get("m_Effects")))
                    {
                        if (!Py.Truthy(r)) continue;
                        var e = effects.Get(Py.Str(((PyDict)r!)["fileID"]));
                        if (!Py.Truthy(e)) continue;
                        var ed = (PyDict)e!;
                        effs.Add(new PyDict
                        {
                            ["name"] = ed.Get("m_EffectName"), ["bypass"] = ed.Get("m_Bypass", 0L),
                            ["parameters"] = Py.Truthy(ed.Get("m_Parameters")) ? ed.Get("m_Parameters") : new List<object?>(),
                            ["mixLevel"] = ed.Get("m_MixLevel"),
                        });
                    }
                    groups[d.FileID] = new PyDict
                    {
                        ["name"] = g.Get("m_Name"),
                        ["groupID"] = g.Get("m_GroupID"),
                        ["children"] = ListOf(g.Get("m_Children")).Where(Py.Truthy).Select(c => (object?)Py.Str(((PyDict)c!)["fileID"])).ToList(),
                        ["volume"] = g.Get("m_Volume"),
                        ["pitch"] = g.Get("m_Pitch"),
                        ["mute"] = g.Get("m_Mute", 0L),
                        ["solo"] = g.Get("m_Solo", 0L),
                        ["bypassEffects"] = g.Get("m_BypassEffects", 0L),
                        ["effects"] = effs,
                    };
                }
                var snapshots = new PyDict();
                foreach (var d in docs)
                {
                    if (d.Type != "AudioMixerSnapshotController") continue;
                    var values = new PyDict();
                    foreach (var (k, v) in Dict(d.Data.Get("m_FloatValues"))) values[Py.Str(k)] = v;
                    snapshots[d.FileID] = new PyDict { ["name"] = d.Data.Get("m_Name"), ["values"] = values };
                }
                var rr = (PyDict)UnityYaml.NormRefs(UnityYaml.StripMetaFields(root.Data))!;
                rec = new PyDict
                {
                    ["name"] = rr.Get("m_Name"),
                    ["src"] = cat.Rel(p),
                    ["output"] = rr.Get("m_OutputGroup"),
                    ["master"] = Py.Str(Dict(rr.Get("m_MasterGroup")).Get("fileID")),
                    ["snapshotOrder"] = ListOf(rr.Get("m_Snapshots")).Where(Py.Truthy).Select(x => (object?)Py.Str(((PyDict)x!)["fileID"])).ToList(),
                    ["startSnapshot"] = Py.Str(Dict(rr.Get("m_StartSnapshot")).Get("fileID")),
                    ["updateMode"] = rr.Get("m_UpdateMode", 0L),
                    ["exposed"] = Py.Truthy(rr.Get("m_ExposedParameters")) ? rr.Get("m_ExposedParameters") : new List<object?>(),
                    ["groups"] = groups,
                    ["snapshots"] = snapshots,
                };
            }
            catch (Exception e)
            {
                cat.Fail(p, Err(e));
                continue;
            }
            table[guid] = rec;
            cat.Exported++;
        }
        return table;
    }

    // ───────── FMOD bank ─────────

    public PyDict ExportBanks()
    {
        string srcDir = PyPath.Join(AssetsDir, "StreamingAssets");
        var banks = PyPath.Glob(srcDir, "*.bank").Order(PyPath.Comparer).ToList();
        string audio = PyPath.Join(ctx.Out, "audio");
        Directory.CreateDirectory(audio);
        var failed = new List<object?>();
        var files = new PyDict();
        long exported = 0;
        foreach (var p in banks)
        {
            try
            {
                ImportContext.Clone(p, PyPath.Join(audio, PyPath.Name(p)));
            }
            catch (Exception e)
            {
                failed.Add(new PyDict { ["src"] = PyPath.RelativeTo(p, AssetsDir), ["error"] = Err(e) });
                continue;
            }
            exported++;
            files[PyPath.Name(p)] = new FileInfo(p).Length;
        }
        return new PyDict { ["source"] = (long)banks.Count, ["exported"] = exported, ["failed"] = failed, ["files"] = files };
    }

    // ───────── Unity 內建資源（從原版 build） ─────────

    public const string BuiltinExtra = "0000000000000000f000000000000000";
    public const string BuiltinDefault = "0000000000000000e000000000000000";

    private static readonly (string fileId, string type, string name)[] BuiltinObjects =
    [
        ("10754", "Material", "Sprites-Default"),
        ("10758", "Material", "Sprites-Mask"),
        ("10905", "Sprite", "UISprite"),
        ("10907", "Sprite", "Background"),
        ("10911", "Sprite", "InputFieldBackground"),
        ("10917", "Sprite", "UIMask"),
    ];

    private static readonly (string fileId, string name)[] BuiltinDefaultMeshes =
        [("10202", "Cube"), ("10206", "Cylinder"), ("10207", "Sphere"), ("10208", "Capsule"), ("10209", "Plane"), ("10210", "Quad")];

    /// <summary>_pptr_to_none：UnityPy 的 {m_FileID, m_PathID} → None 或 {buildFileID, buildPathID}；map 的 [(key, value)…] → dict；tuple → list</summary>
    public static object? PptrToNone(object? v)
    {
        switch (v)
        {
            case PyDict d:
                if (d.Count == 2 && d.ContainsKey("m_FileID") && d.ContainsKey("m_PathID"))
                    return !Py.Truthy(d["m_PathID"]) ? null : new PyDict { ["buildFileID"] = d["m_FileID"], ["buildPathID"] = Py.Str(d["m_PathID"]) };
                var o = new PyDict(d.Count);
                foreach (var (k, x) in d) o[k] = PptrToNone(x);
                return o;
            case List<object?> l:
                if (l.Count > 0 && l.All(x => x is PyTuple { Items.Length: 2 } t && t.Items[0] is string))
                {
                    var m = new PyDict();
                    foreach (PyTuple t in l.Cast<PyTuple>()) m[t.Items[0]] = PptrToNone(t.Items[1]);
                    return m;
                }
                return l.Select(PptrToNone).ToList();
            case PyTuple t:
                return t.Items.Select(PptrToNone).ToList();
            default:
                return v;
        }
    }

    private void ExportBuiltinMeshes(string gameData, PyDict table, PyDict rep, List<object?> failed)
    {
        rep["source"] = Py.ToLong(rep["source"]) + BuiltinDefaultMeshes.Length;
        string path = PyPath.Join(gameData, "Resources/unity default resources");
        if (!File.Exists(path))
        {
            failed.Add(new PyDict { ["src"] = path, ["error"] = "找不到 unity default resources" });
            return;
        }
        var env = new BuildEnv(gameData);
        var byId = env.LoadOne(path).Objects.ToDictionary(o => o.PathId.ToString(Py.Inv));
        foreach (var (fileId, name) in BuiltinDefaultMeshes)
        {
            try
            {
                byId.TryGetValue(fileId, out var obj);
                if (obj is null || obj.TypeName != "Mesh")
                    throw new InvalidOperationException($"pathID {fileId} 不是 Mesh（{(obj is not null ? obj.TypeName : "沒有這個物件")}）");
                var t = obj.ReadTypeTree(Boost);
                if (!Py.Eq(t["m_Name"], name)) throw new InvalidOperationException($"pathID {fileId} 是 {Py.Str(t["m_Name"])}，不是 {name}");
                if (Py.Truthy(t.Get("m_MeshCompression"))) throw new InvalidOperationException("m_MeshCompression 非 0，要改解 m_CompressedMesh");
                var vd = ((PyDict)t["m_VertexData"]!).Copy();
                var data = Bytes(vd.Pop("m_DataSize"));
                vd["_typelessdata"] = PyStr.Hex(data);
                vd["m_DataSize"] = (long)data.Length;
                var ib = t["m_IndexBuffer"];
                table[$"{BuiltinDefault}:{fileId}"] = new PyDict
                {
                    ["type"] = "Mesh",
                    ["name"] = name,
                    ["build"] = $"unity default resources#{fileId}",
                    ["meshAsset"] = new PyDict
                    {
                        ["m_Name"] = name,
                        ["vertexCount"] = Py.ToLong(vd["m_VertexCount"]),
                        ["indices"] = UnityYaml.DecodeIndexBuffer(ib is string s ? s : PyStr.Hex(Bytes(ib)), Py.ToLong(Py.Truthy(t.Get("m_IndexFormat")) ? t.Get("m_IndexFormat") : 0L)),
                        ["channels"] = UnityYaml.DecodeVertexData(vd),
                        ["m_SubMeshes"] = PptrToNone(t["m_SubMeshes"]),
                        ["m_LocalAABB"] = PptrToNone(t["m_LocalAABB"]),
                    },
                };
                rep["exported"] = Py.ToLong(rep["exported"]) + 1;
            }
            catch (Exception e)
            {
                failed.Add(new PyDict { ["src"] = $"Mesh {name}", ["error"] = ErrT(e) });
            }
        }
    }

    /// <summary>bytes(x)：bytes 或整數清單</summary>
    public static byte[] Bytes(object? v) => v switch
    {
        byte[] b => b,
        List<object?> l => l.Select(x => checked((byte)Py.ToLong(x))).ToArray(),
        PyTuple t => t.Items.Select(x => checked((byte)Py.ToLong(x))).ToArray(),
        _ => throw new InvalidCastException($"cannot convert '{v?.GetType().Name}' object to bytes"),
    };

    /// <summary>在 build 的資產檔裡依（類別, 名稱）找物件</summary>
    private Dictionary<(string, string), List<(string fn, BuildObject obj)>> FindBuildObjects(BuildEnv env, string gameData, HashSet<(string type, string name)> wanted)
    {
        var found = new Dictionary<(string, string), List<(string, BuildObject)>>();
        var types = wanted.Select(w => w.type).ToHashSet();
        foreach (var path in BuildAssetFiles(gameData))
        {
            if (PyPath.Suffix(path) == ".bundle") continue;
            string fn = PyPath.Name(path);
            foreach (var sf in env.Load(path))
                foreach (var obj in sf.Objects)
                {
                    if (!types.Contains(obj.TypeName)) continue;
                    string? name = obj.PeekName(Boost);
                    if (name is not null && wanted.Contains((obj.TypeName, name)))
                    {
                        if (!found.TryGetValue((obj.TypeName, name), out var l)) found[(obj.TypeName, name)] = l = [];
                        l.Add((fn, obj));
                    }
                }
        }
        return found;
    }

    public (PyDict table, PyDict report) ExportBuiltin(string gameData)
    {
        var failed = new List<object?>();
        var rep = new PyDict { ["source"] = (long)BuiltinObjects.Length, ["exported"] = 0L, ["failed"] = failed };
        var table = new PyDict();
        if (!File.Exists(PyPath.Join(gameData, "globalgamemanagers.assets")))
        {
            failed.Add(new PyDict { ["src"] = gameData, ["error"] = "找不到原版 build（設定檔 import.game）" });
            return (table, rep);
        }
        var env = new BuildEnv(gameData);
        var found = FindBuildObjects(env, gameData, BuiltinObjects.Select(b => (b.type, b.name)).ToHashSet());
        foreach (var (fileId, typeName, name) in BuiltinObjects)
        {
            var hits = found.GetValueOrDefault((typeName, name)) ?? [];
            if (hits.Count != 1)
            {
                failed.Add(new PyDict { ["src"] = $"{typeName} {name}", ["error"] = $"build 裡找到 {hits.Count} 個" });
                continue;
            }
            var (fn, obj) = hits[0];
            try
            {
                var raw = obj.ReadTypeTree(Boost);
                var t = (PyDict)PptrToNone(raw)!;
                var rec = new PyDict { ["type"] = typeName, ["name"] = name, ["build"] = $"{fn}#{obj.PathId}" };
                if (typeName == "Sprite")
                {
                    var rdRaw = (PyDict)raw["m_RD"]!;
                    var texObj = env.Deref(obj.File, (PyDict)rdRaw["texture"]!);
                    var tex = texObj.ReadTypeTree(Boost);
                    string texFile = $"textures/builtin-{name}.png";
                    BuiltinTexture.SavePng(env, texObj, tex, PyPath.Join(ctx.Out, texFile));
                    var ts = (PyDict)tex["m_TextureSettings"]!;
                    rec["texture"] = new PyDict
                    {
                        ["name"] = tex["m_Name"], ["file"] = texFile, ["width"] = tex["m_Width"], ["height"] = tex["m_Height"],
                        ["filterMode"] = ts["m_FilterMode"], ["wrapU"] = ts["m_WrapU"], ["wrapV"] = ts["m_WrapV"],
                        ["colorSpace"] = tex.Get("m_ColorSpace"),
                    };
                    var rd = (PyDict)t["m_RD"]!;
                    var vd = ((PyDict)rd["m_VertexData"]!).Copy();
                    var data = Bytes(vd.Pop("m_DataSize"));
                    vd["_typelessdata"] = PyStr.Hex(data);
                    vd["m_DataSize"] = (long)data.Length;
                    var ib = rd["m_IndexBuffer"];
                    var channels = UnityYaml.DecodeVertexData(vd);
                    rec["mesh"] = new PyDict
                    {
                        ["vertexCount"] = vd["m_VertexCount"],
                        ["indices"] = UnityYaml.DecodeIndexBuffer(ib is string s ? s : PyStr.Hex(Bytes(ib)), 0),
                        ["position"] = ((PyDict)channels["position"]!)["values"],
                    };
                    var sprite = new PyDict();
                    foreach (var (k, v) in t)
                        if (!SpriteDrop.Contains((string)k!)) sprite[k] = v;
                    var rdOut = new PyDict();
                    foreach (var (k, v) in rd)
                        if (!RdMeshKeys.Contains((string)k!) && (string)k! != "texture") rdOut[k] = v;
                    sprite["m_RD"] = rdOut;
                    rec["sprite"] = sprite;
                }
                else
                {
                    var shader = env.Deref(obj.File, (PyDict)raw["m_Shader"]!).ReadTypeTree(Boost);
                    rec["shaderName"] = ((PyDict)shader["m_ParsedForm"]!)["m_Name"];
                    var mat = new PyDict();
                    foreach (var (k, v) in t)
                        if ((string)k! != "m_Shader") mat[k] = v;
                    rec["material"] = mat;
                }
                table[$"{BuiltinExtra}:{fileId}"] = rec;
                rep["exported"] = Py.ToLong(rep["exported"]) + 1;
            }
            catch (Exception e)
            {
                failed.Add(new PyDict { ["src"] = $"{typeName} {name}", ["error"] = ErrT(e) });
            }
        }
        ExportBuiltinMeshes(gameData, table, rep, failed);
        return (table, rep);
    }

    // ───────── ProjectSettings ─────────

    public PyDict ExportProject(string src)
    {
        var o = new PyDict();
        foreach (var p in PyPath.Glob(PyPath.Join(src, "ProjectSettings"), "*.asset").Order(PyPath.Comparer))
        {
            var docs = ctx.Yaml.LoadUnityDocs(p);
            var d = new PyDict();
            foreach (var doc in docs) d[doc.Type] = UnityYaml.NormRefs(doc.Data);
            o[PyPath.Stem(p)] = d;
        }
        return o;
    }

    // ───────── Addressables ─────────

    private static readonly Dictionary<string, string[]> AddressableExtAlt = new()
    {
        [".wav"] = [".ogg"], [".mp3"] = [".ogg"], [".shader"] = [".asset"], [".shadergraph"] = [".asset"], [".fbx"] = [".prefab"], [".obj"] = [".prefab"],
    };

    /// <summary>SerializationUtilities.ReadObjectFromByteArray：只解 key 會用到的字串、整數、Hash128</summary>
    private static object? CatalogObject(byte[] data, int i)
    {
        byte t = data[i];
        i++;
        switch (t)
        {
            case 0: { int n = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i)); return System.Text.Encoding.ASCII.GetString(data, i + 4, n); }
            case 1: { int n = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i)); return System.Text.Encoding.Unicode.GetString(data, i + 4, n); }
            case 2: return (long)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i));
            case 3: return (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
            case 4: return (long)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i));
            case 5: return new PyTuple("hash", System.Text.Encoding.ASCII.GetString(data, i + 1, data[i]).ToLowerInvariant());
            default: return null;
        }
    }

    /// <summary>os.path.splitext</summary>
    private static (string root, string ext) SplitExt(string p)
    {
        int sep = p.LastIndexOf('/');
        int dot = p.LastIndexOf('.');
        if (dot > sep)
        {
            int f = sep + 1;
            while (f < dot && p[f] == '.') f++;
            if (f < dot) return (p[..dot], p[dot..]);
        }
        return (p, "");
    }

    public (PyDict table, PyDict report) ExportAddressables(string gameData, Dictionary<string, string> guids)
    {
        string catPath = PyPath.Join(gameData, "StreamingAssets/aa/catalog.json");
        if (!File.Exists(catPath))
            return (new PyDict { ["guids"] = new PyDict(), ["addresses"] = new PyDict() },
                new PyDict { ["source"] = 0L, ["exported"] = 0L, ["failed"] = new List<object?> { "沒有 catalog.json" } });
        var cat = (PyDict)PyJson.Loads(PyStr.ReadText(catPath))!;
        var bucketsRaw = Convert.FromBase64String((string)cat["m_BucketDataString"]!);
        var keysRaw = Convert.FromBase64String((string)cat["m_KeyDataString"]!);
        var entriesRaw = Convert.FromBase64String((string)cat["m_EntryDataString"]!);
        var ids = (List<object?>)cat["m_InternalIds"]!;
        string root = PyPath.Parent(AssetsDir);
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (g, p) in guids)
            if (PyPath.IsRelativeTo(p, root)) byPath[PyPath.RelativeTo(p, root)] = g;

        string? ExportedGuid(string internalId)
        {
            if (!internalId.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            if (byPath.TryGetValue(internalId, out var g)) return g;
            var (stem, ext) = SplitExt(internalId);
            var hits = (AddressableExtAlt.GetValueOrDefault(ext.ToLowerInvariant()) ?? []).Where(alt => byPath.ContainsKey(stem + alt)).Select(alt => byPath[stem + alt]).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }

        int nEntries = BinaryPrimitives.ReadInt32LittleEndian(entriesRaw);
        int stride = nEntries != 0 ? (int)Math.Floor((entriesRaw.Length - 4) / (double)nEntries) : 16;
        var entryIds = Enumerable.Range(0, nEntries).Select(k => BinaryPrimitives.ReadInt32LittleEndian(entriesRaw.AsSpan(4 + k * stride))).ToArray();
        var guidTable = new PyDict();
        var addresses = new PyDict();
        var failed = new List<object?>();
        int nBuckets = BinaryPrimitives.ReadInt32LittleEndian(bucketsRaw);
        int pos = 4;
        for (int b = 0; b < nBuckets; b++)
        {
            int off = BinaryPrimitives.ReadInt32LittleEndian(bucketsRaw.AsSpan(pos));
            int cnt = BinaryPrimitives.ReadInt32LittleEndian(bucketsRaw.AsSpan(pos + 4));
            var ents = Enumerable.Range(0, cnt).Select(j => BinaryPrimitives.ReadInt32LittleEndian(bucketsRaw.AsSpan(pos + 8 + 4 * j))).ToArray();
            pos += 8 + 4 * cnt;
            var key = CatalogObject(keysRaw, off);
            var paths = ents.Select(e => (string)ids[entryIds[e]]!).Where(s => s.StartsWith("Assets/", StringComparison.Ordinal)).ToList();
            if (paths.Count == 0 || key is long || key is null) continue;
            var g = ExportedGuid(paths[0]);
            if (key is PyTuple kt)
            {
                if (g is not null) guidTable[kt.Items[1]] = g;
                else failed.Add($"{Py.Str(kt.Items[1])} {paths[0]}");
            }
            else if (g is not null) addresses[key] = g;
        }
        var report = new PyDict
        {
            ["source"] = (long)(guidTable.Count + failed.Count), ["exported"] = (long)guidTable.Count,
            ["addresses"] = (long)addresses.Count, ["failed"] = failed,
        };
        return (new PyDict { ["guids"] = guidTable, ["addresses"] = addresses }, report);
    }

    // ───────── 主程式 ─────────

    public PyDict ExportAll(string? shaderSrc, Dictionary<string, string> guids, string? gameData)
    {
        double t0 = ImportContext.Now();
        if (Directory.Exists(ctx.Out)) Directory.Delete(ctx.Out, true);
        Directory.CreateDirectory(ctx.Out);
        var (cats, skipped) = Classify(guids);
        var timings = new PyDict();
        T Step<T>(string name, Func<T> fn)
        {
            double t = ImportContext.Now();
            var r = fn();
            timings[name] = ImportContext.Seconds(t);
            return r;
        }
        string Out(string n) => PyPath.Join(ctx.Out, n);

        var textures = Step("textures", () => ExportTextures(cats["textures"]));
        var textureArrays = Step("textureArrays", () => gameData is not null ? TextureArrayDepths(gameData, textures)
            : new PyDict { ["matched"] = 0L, ["unmatched"] = new List<object?> { "沒有原版 build" } });
        ImportContext.WriteJson(Out("textures.json"), textures);
        var sprites = Step("sprites", () => ExportSprites(cats["sprites"], textures));
        var spriteRebase = Step("spriteRebase", () => gameData is not null ? RebaseSprites(gameData, sprites)
            : new PyDict { ["matched"] = 0L, ["rebased"] = 0L, ["unmatched"] = new List<object?> { "沒有原版 build" }, ["ambiguous"] = new List<object?>() });
        ImportContext.WriteJson(Out("sprites.json"), sprites);
        ImportContext.WriteJson(Out("fonts.json"), Step("fonts", () => ExportFonts(cats["fonts"])));
        var (shaders, shaderNames) = Step("shaders", () => ExportShaders(cats["shaders"], shaderSrc));
        ImportContext.WriteJson(Out("shaders.json"), shaders);
        var materials = Step("materials", () => ExportMaterials(cats["materials"], shaderNames));
        var builtinShaders = Step("builtinShaders", () => gameData is not null ? NameBuiltinShaders(gameData, materials)
            : new PyDict { ["table"] = new PyDict(), ["failed"] = new List<object?> { "沒有原版 build" } });
        ImportContext.WriteJson(Out("materials.json"), materials);
        ImportContext.WriteJson(Out("animations.json"), Step("animations", () => ExportSingle(cats["animations"], "AnimationClip")));
        ImportContext.WriteJson(Out("animators.json"), Step("animators", () => ExportAnimators(cats["animators"])));
        WriteMeshes(ctx.Out, Step("meshes", () => ExportMeshes(cats["meshes"])));
        ImportContext.WriteJson(Out("physicMaterials.json"), Step("physicMaterials", () => ExportSingle(cats["physicMaterials"], "PhysicMaterial")));
        ImportContext.WriteJson(Out("monobehaviours.json"), Step("monobehaviours", () => ExportMonoBehaviours(cats["monobehaviours"], guids, gameData)));
        ImportContext.WriteJson(Out("texts.json"), Step("texts", () => ExportTexts(cats["texts"])));
        ImportContext.WriteJson(Out("audioclips.json"), Step("audioclips", () => ExportAudioClips(cats["audioclips"])));
        ImportContext.WriteJson(Out("audiomixers.json"), Step("audiomixers", () => ExportAudioMixers(cats["audiomixers"])));
        var banks = Step("banks", ExportBanks);
        var builtin = Step("builtin", () => gameData is not null ? ExportBuiltin(gameData)
            : (new PyDict(), new PyDict { ["source"] = 0L, ["exported"] = 0L, ["failed"] = new List<object?>() }));
        ImportContext.WriteJson(Out("builtin.json"), builtin.Item1);
        ImportContext.WriteJson(Out("project.json"), Step("project", () => ExportProject(PyPath.Parent(AssetsDir))));
        var addressables = Step("addressables", () => gameData is not null ? ExportAddressables(gameData, guids)
            : (new PyDict { ["guids"] = new PyDict(), ["addresses"] = new PyDict() }, new PyDict { ["source"] = 0L, ["exported"] = 0L, ["failed"] = new List<object?> { "沒有原版 build" } }));
        ImportContext.WriteJson(Out("addressables.json"), addressables.Item1);

        var categories = new PyDict();
        foreach (var (name, c) in cats) categories[name] = c.Report();
        categories["banks"] = banks;
        categories["builtin"] = builtin.Item2;
        categories["addressables"] = addressables.Item2;
        var notImported = new PyDict();
        foreach (var (k, n) in skipped.OrderBy(kv => kv.Key, PyStr.Comparer))
            notImported[k] = new PyDict { ["count"] = n, ["reason"] = NotImported.Get(k, "") };
        timings["total"] = ImportContext.Seconds(t0);
        var report = new PyDict
        {
            ["categories"] = categories,
            ["builtinShaders"] = builtinShaders,
            ["notImported"] = notImported,
            ["spriteRebase"] = spriteRebase,
            ["textureArrays"] = textureArrays,
            ["notes"] = AssetNotes(sprites, textures),
            ["seconds"] = timings,
        };
        // Python 版的 seconds 在 notes 之後才算 total；計時不在比對範圍內
        ImportContext.WriteJson(Out("report.json"), report);
        return report;
    }

    private PyDict AssetNotes(PyDict sprites, PyDict textures)
    {
        var tmpFonts = new PyDict();
        var mono = PyJson.LoadDict(PyPath.Join(ctx.Out, "monobehaviours.json"));
        foreach (var (guid, mObj) in mono)
        {
            var m = (PyDict)mObj!;
            if (!Py.Eq(m["kind"], "TMP_FontAsset")) continue;
            var obj = PyJson.LoadDict(PyPath.Join(ctx.Out, (string)m["file"]!));
            tmpFonts[Py.Str(m["name"]) + $"（{((string)guid!)[..Math.Min(8, ((string)guid!).Length)]}）"] = new PyDict
            {
                ["atlasPopulationMode"] = obj.Get("m_AtlasPopulationMode"),
                ["characters"] = (long)ListOf(obj.Get("m_CharacterTable")).Count,
                ["sourceFontFile"] = Dict(obj.Get("m_SourceFontFile")).Get("guid"),
            };
        }
        var arrays = new PyDict();
        foreach (var (g, t) in textures)
            if (Py.Eq(Dict(((PyDict)t!)["importer"]).Get("textureShape"), 4L)) arrays[g] = ((PyDict)t)["name"];
        long quads = sprites.Values.Count(s => Py.Eq(((PyDict)s!).Get("vertexCount"), 4L));
        var mats = PyJson.LoadDict(PyPath.Join(ctx.Out, "materials.json"));
        var builtinMats = new PyDict();
        foreach (var (_, mObj) in mats)
        {
            var m = (PyDict)mObj!;
            if (m.ContainsKey("builtinShader")) builtinMats[m["m_Name"]] = m["builtinShader"];
        }
        var withoutShader = mats.Values.Cast<PyDict>().Where(m => !Py.Truthy(m["shaderName"]) && !m.ContainsKey("builtinShader"))
            .Select(m => m["m_Name"]).OrderBy(x => x, Comparer<object?>.Create(Py.Compare)).ToList();
        return new PyDict
        {
            ["tmpFonts"] = Sorted(tmpFonts),
            ["tmpDynamicFonts"] = (long)tmpFonts.Values.Count(f => Py.Eq(((PyDict)f!)["atlasPopulationMode"], 1L)),
            ["texture2DArrays"] = arrays,
            ["spriteMeshes"] = new PyDict { ["quad"] = quads, ["tight"] = sprites.Count - quads },
            ["builtinShaderMaterials"] = builtinMats,
            ["materialsWithoutShader"] = withoutShader,
        };
    }
}
