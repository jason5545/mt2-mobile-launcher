using System.Text;
using System.Text.RegularExpressions;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer;

/// <summary>
/// packs.py：開機與各場景依賴閉包裡的小 JSON 檔併成大檔（閉包的算法同執行環境的 ObjectStore，見 Python 版檔頭）。
/// 原檔的文字原樣接起來，不重新序列化。
/// </summary>
public static class Packs
{
    private const long PackBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> TmpKinds = ["TMP_FontAsset", "TMP_SpriteAsset", "TMP_Settings", "TMP_StyleSheet", "TMP_ColorGradient"];
    private const string SoFileId = "11400000";
    private static readonly Regex ResourcePathRe = PyRe.R(@"(?:^|/)Resources/(.+?)(?:\.[^./]+)+$");
    private static readonly Regex SafeRe = PyRe.R(@"[^A-Za-z0-9_.-]");

    private static object? Read(string path) => PyJson.Loads(PyStr.ReadTextSig(path));

    /// <summary>物件裡的全部 {guid, fileID} 參照（照出現順序去重），同 objectStore.ts 的 collectRefs</summary>
    public static List<(string guid, string fileId)> CollectRefs(object? root)
    {
        var o = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();
        var stack = new List<object?> { root };
        while (stack.Count > 0)
        {
            var v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (v is List<object?> l)
            {
                for (int i = l.Count - 1; i >= 0; i--) stack.Add(l[i]);
            }
            else if (v is PyDict d)
            {
                if (d.Get("guid") is string g && d.ContainsKey("fileID"))
                {
                    var k = (g, Py.Str(d["fileID"]));
                    if (seen.Add(k)) o.Add(k);
                    continue;
                }
                var vals = d.Values.ToList();
                for (int i = vals.Count - 1; i >= 0; i--) stack.Add(vals[i]);
            }
        }
        return o;
    }

    public static string? ResourcePath(string src)
    {
        var m = ResourcePathRe.Match(src);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static PyDict Export(string outDir)
    {
        double t0 = ImportContext.Now();
        string P(string rel) => PyPath.Join(outDir, rel);
        var idx = (PyDict)Read(P("scenes/index.json"))!;
        var mono = (PyDict)Read(P("assets/monobehaviours.json"))!;
        var texts = File.Exists(P("assets/texts.json")) ? (PyDict)Read(P("assets/texts.json"))! : new PyDict();
        var meshes = File.Exists(P("assets/meshes/index.json"))
            ? Assets.ListOf(Read(P("assets/meshes/index.json"))).Select(x => (string)x!).ToHashSet(StringComparer.Ordinal) : [];
        var prefabs = (PyDict)idx["prefabs"]!;
        var scenes = new PyDict();
        foreach (var e in ((PyDict)idx["scenes"]!).Values) scenes[((PyDict)e!)["name"]] = e;
        var buildOrder = Py.Truthy(idx.Get("buildOrder"))
            ? Assets.ListOf(idx["buildOrder"]).Cast<string>().ToList()
            : scenes.Values.Cast<PyDict>().OrderBy(e => e["buildIndex"], Comparer<object?>.Create(Py.Compare))
                .Where(e => Py.Compare(e["buildIndex"], 0L) >= 0).Select(e => (string)e["name"]!).ToList();

        string? PathOf(string guid, string fileId)
        {
            if (mono.ContainsKey(guid) && fileId == SoFileId) return $"assets/{((PyDict)mono[guid]!)["file"]}";
            if (prefabs.ContainsKey(guid)) return (string)((PyDict)prefabs[guid]!)["file"]!;
            if (Py.Truthy(Assets.Dict(texts.Get(guid)).Get("file"))) return null;
            if (meshes.Contains(guid)) return $"assets/meshes/{guid}.json";
            return null;
        }

        var refsCache = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        List<(string, string)> RefsOf(string rel)
        {
            if (!refsCache.TryGetValue(rel, out var r))
                refsCache[rel] = r = rel.StartsWith("assets/meshes/", StringComparison.Ordinal) ? [] : CollectRefs(Read(P(rel)));
            return r;
        }

        List<string> Closure(List<(string, string)> roots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            var frontier = roots;
            while (frontier.Count > 0)
            {
                var next = new List<(string, string)>();
                foreach (var (g, f) in frontier)
                {
                    var rel = PathOf(g, f);
                    if (string.IsNullOrEmpty(rel) || seen.Contains(rel) || !File.Exists(P(rel))) continue;
                    seen.Add(rel);
                    order.Add(rel);
                    next.AddRange(RefsOf(rel));
                }
                frontier = next;
            }
            return order;
        }

        var resources = new PyDict();
        foreach (var table in new[] { mono, prefabs, texts })
        {
            foreach (var (guid, e) in table)
            {
                var src = Assets.Dict(e).Get("src");
                var p = ResourcePath(Py.Truthy(src) ? (string)src! : "");
                if (!string.IsNullOrEmpty(p) && !resources.ContainsKey(p)) resources[p] = guid;
            }
        }
        var resRoots = resources.Values.Select(g => ((string)g!, mono.ContainsKey(g) ? SoFileId : "0")).ToList();

        var groups = new List<(string name, List<string> files)>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        void AddGroup(string name, List<string> files)
        {
            var fresh = new List<string>();
            foreach (var f in files)
                if (done.Add(f)) fresh.Add(f);
            if (fresh.Count > 0) groups.Add((name, fresh));
        }

        var first = buildOrder.Count > 0 ? (PyDict)scenes[buildOrder[0]]! : null;
        var boot = new List<string>();
        if (first is not null) boot.Add((string)first["file"]!);
        var bootRoots = new List<(string, string)>(resRoots);
        if (first is not null) bootRoots.AddRange(CollectRefs(Read(P((string)first["file"]!))));
        boot.AddRange(Closure(bootRoots));
        foreach (var m in mono.Values.Cast<PyDict>())
            if (m.Get("kind") is string k && TmpKinds.Contains(k)) boot.Add($"assets/{m["file"]}");
        AddGroup("boot", boot);
        foreach (var name in buildOrder.Skip(1))
        {
            var e = (PyDict)scenes[name]!;
            var files = new List<string> { (string)e["file"]! };
            files.AddRange(Closure(CollectRefs(Read(P((string)e["file"]!)))));
            AddGroup(name, files);
        }

        string packDir = P("packs");
        if (Directory.Exists(packDir)) Directory.Delete(packDir, true);
        Directory.CreateDirectory(packDir);
        var chunks = new List<object?>();
        long totalBytes = 0;
        foreach (var (name, files) in groups)
        {
            string safe = SafeRe.Replace(name, "_");
            var parts = new List<List<(string rel, string text)>> { new() };
            long size = 0;
            foreach (var rel in files)
            {
                string text = PyStr.Strip(PyStr.ReadTextSig(P(rel)));
                long len = Py.Len(text);
                if (parts[^1].Count > 0 && size + len > PackBytes)
                {
                    parts.Add([]);
                    size = 0;
                }
                parts[^1].Add((rel, text));
                size += len;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                string file = $"{safe}-{i}.json";
                var body = new StringBuilder("{");
                for (int j = 0; j < parts[i].Count; j++)
                {
                    if (j > 0) body.Append(',');
                    PyJson.AppendString(body, parts[i][j].rel);
                    body.Append(':').Append(parts[i][j].text);
                }
                body.Append('}');
                string b = body.ToString();
                PyStr.WriteText(PyPath.Join(packDir, file), b);
                totalBytes += Encoding.UTF8.GetByteCount(b);
                var chunk = new PyDict { ["file"] = file, ["group"] = name };
                if (name == "boot") chunk["boot"] = true;
                chunk["files"] = parts[i].Select(x => (object?)x.rel).ToList();
                chunks.Add(chunk);
            }
        }
        PyStr.WriteText(PyPath.Join(packDir, "index.json"), PyJson.Compact(new PyDict { ["chunks"] = chunks }));

        var kinds = new PyDict();
        foreach (var (_, files) in groups)
            foreach (var f in files)
            {
                string k = f.StartsWith("assets/meshes/", StringComparison.Ordinal) ? "mesh"
                    : f.StartsWith("scenes/prefabs/", StringComparison.Ordinal) ? "prefab"
                    : f.StartsWith("scenes/", StringComparison.Ordinal) ? "scene" : "so";
                kinds[k] = Py.ToLong(kinds.Get(k, 0L)) + 1;
            }
        var bootFiles = groups.Count > 0 && groups[0].name == "boot" ? groups[0].files : [];
        return new PyDict
        {
            ["groups"] = (long)groups.Count,
            ["chunks"] = (long)chunks.Count,
            ["files"] = (long)groups.Sum(g => g.files.Count),
            ["bootFiles"] = (long)bootFiles.Count,
            ["bootChunks"] = (long)chunks.Count(c => ((PyDict)c!).ContainsKey("boot")),
            ["kinds"] = kinds,
            ["megabytes"] = PyFloat.Round(totalBytes / 1e6, 1),
            ["seconds"] = ImportContext.Seconds(t0),
        };
    }
}
