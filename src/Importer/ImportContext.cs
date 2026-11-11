using Mt2Mobile.Importer.PyCompat;
using Mt2Mobile.Importer.Unity;

namespace Mt2Mobile.Importer;

/// <summary>一次匯入的共用狀態：路徑、YAML 規則（int32ArrayKeys）、log。函式庫不寫 Console，log 交給呼叫端。</summary>
public sealed class ImportContext
{
    /// <summary>匯出的 ExportedProject/Assets</summary>
    public required string Assets { get; init; }
    /// <summary>原版 build 的 Data 資料夾（沒有就是 null）</summary>
    public string? GameData { get; init; }
    public required string Out { get; init; }
    public required UnityYaml Yaml { get; init; }
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>平行處理的執行緒數（sprite、場景）；結果的順序固定，不受這個值影響</summary>
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>write_json：json.dumps(ensure_ascii=False, separators=(",", ":"))，資料夾不存在就建</summary>
    public static void WriteJson(string path, object? obj)
    {
        Directory.CreateDirectory(PyPath.Parent(path));
        PyStr.WriteText(path, PyJson.Compact(obj));
    }

    /// <summary>clone：APFS 上 Python 用 clonefile；這裡一般複製（內容相同）</summary>
    public static void Clone(string src, string dst) => File.Copy(src, dst, overwrite: false);

    public static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>round(time.time() - t, 1)</summary>
    public static double Seconds(double t0) => PyFloat.Round(Now() - t0, 1);
}
