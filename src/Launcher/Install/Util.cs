using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mt2Mobile.Launcher.Install;

static class Util
{
    public static readonly UTF8Encoding Utf8 = new(false);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>先寫暫存檔再 rename：讀的一方（另一個程序）不會讀到寫一半的檔</summary>
    public static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, node.ToJsonString(Indented), Utf8);
        File.Move(tmp, path, true);
    }

    public static JsonObject? ReadJson(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    /// <summary>路徑照 UTF-8 位元組排序，同 `LC_ALL=C sort`</summary>
    public static readonly Comparer<string> Utf8Order = Comparer<string>.Create((a, b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));

    /// <summary>
    /// `shasum -a 256` 格式清單（`&lt;雜湊&gt;  ./&lt;路徑&gt;`，照路徑排序）本身的 SHA256。
    /// 跟 `find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256 | shasum -a 256` 相同（Phase 2、3 的總雜湊）
    /// </summary>
    public static string ListTotal(IEnumerable<KeyValuePair<string, string>> hashes) =>
        Hex(SHA256.HashData(Utf8.GetBytes(string.Concat(hashes.OrderBy(h => h.Key, Utf8Order).Select(h => $"{h.Value}  ./{h.Key}\n")))));

    /// <summary>資料夾底下每個檔的 SHA256（key 是用 / 分隔的相對路徑）</summary>
    public static SortedDictionary<string, string> HashTree(string dir)
    {
        var res = new SortedDictionary<string, string>(Utf8Order);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            using var s = File.OpenRead(f);
            res[Path.GetRelativePath(dir, f).Replace('\\', '/')] = Hex(SHA256.HashData(s));
        }
        return res;
    }

    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public static string Sha256(string text) => Hex(SHA256.HashData(Utf8.GetBytes(text)));

    public static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>/proc/self/status 的 VmHWM、VmRSS（kB）</summary>
    public static (long hwm, long rss) Memory()
    {
        long hwm = 0, rss = 0;
        foreach (var l in File.ReadLines("/proc/self/status"))
        {
            if (l.StartsWith("VmHWM:")) hwm = long.Parse(l.Split(':')[1].Trim().Split(' ')[0]);
            else if (l.StartsWith("VmRSS:")) rss = long.Parse(l.Split(':')[1].Trim().Split(' ')[0]);
        }
        return (hwm, rss);
    }
}

/// <summary>安裝的 log：files/logs/install-&lt;時間&gt;.log，每行前面是時間。不寫 token、帳號這類資料（這一步也還沒有）</summary>
public sealed class InstallLog : IDisposable
{
    readonly object gate = new();
    readonly StreamWriter writer;
    public string Path { get; }

    public InstallLog(string dir)
    {
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read), Util.Utf8) { AutoFlush = true };
    }

    public void Write(string line)
    {
        lock (gate) writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        Android.Util.Log.Info("mt2-install", line);
    }

    public void Dispose()
    {
        lock (gate) writer.Dispose();
    }
}
