using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer;

/// <summary>streaming.py：原版 build 的 StreamingAssets 搬到 &lt;out&gt;/streaming，寫 index.json（Addressables 的 bundle 與 SteamDeck 不搬）</summary>
public static class Streaming
{
    private static readonly HashSet<string> TextExts = [".json", ".vdf", ".txt", ".xml", ".csv", ".ini", ".cfg", ".yaml", ".yml"];
    private static readonly HashSet<string> SkipDirs = ["SteamDeck"];

    public static PyDict Export(string gameData, string outDir)
    {
        double t0 = ImportContext.Now();
        string src = PyPath.Join(gameData, "StreamingAssets");
        string dst = PyPath.Join(outDir, "streaming");
        var files = new PyDict();
        if (!Directory.Exists(src))
        {
            ImportContext.WriteJson(PyPath.Join(dst, "index.json"), new PyDict { ["files"] = files });
            return new PyDict { ["files"] = 0L, ["seconds"] = 0L };
        }
        long bytes = 0;
        foreach (var p in PyPath.RGlob(src, "*").Order(PyPath.Comparer))
        {
            if (!File.Exists(p)) continue;
            string rel = PyPath.RelativeTo(p, src);
            var parts = rel.Split('/');
            if (SkipDirs.Contains(parts[0])) continue;
            if (parts[0] == "aa" && !(parts.Length == 2 && PyPath.Suffix(p) == ".json")) continue;
            string target = PyPath.Join(dst, rel);
            Directory.CreateDirectory(PyPath.Parent(target));
            if (File.Exists(target)) File.Delete(target);
            ImportContext.Clone(p, target);
            long size = new FileInfo(p).Length;
            bytes += size;
            files[rel] = new PyDict { ["size"] = size, ["text"] = TextExts.Contains(PyPath.Suffix(p).ToLowerInvariant()) };
        }
        ImportContext.WriteJson(PyPath.Join(dst, "index.json"), new PyDict { ["files"] = files });
        return new PyDict { ["files"] = (long)files.Count, ["bytes"] = bytes, ["seconds"] = ImportContext.Seconds(t0) };
    }
}
