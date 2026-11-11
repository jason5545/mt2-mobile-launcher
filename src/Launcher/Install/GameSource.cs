namespace Mt2Mobile.Launcher.Install;

/// <summary>
/// 原版從哪裡來。啟動器只複製 &lt;名稱&gt;_Data（不複製 exe、UnityPlayer.dll、MonoBleedingEdge），使用者可以選遊戲根目錄或 _Data 本身。
/// SAF（拆段 C）與驗收用的路徑匯入各一個實作。
/// </summary>
public interface IGameSource
{
    /// <summary>給 log 看的來源說明</summary>
    string Describe { get; }
    /// <summary>&lt;名稱&gt;_Data 的資料夾名</summary>
    string DataDirName { get; }
    /// <summary>_Data 底下的每個檔（用 / 分隔的相對路徑）</summary>
    IEnumerable<string> Files();
    Stream Open(string relative);
}

/// <summary>內部空間的一個資料夾（驗收用：原版先用 adb 推進 app 的私有空間）</summary>
public sealed class DirectorySource : IGameSource
{
    readonly string data;
    readonly string? description;

    public DirectorySource(string path, string? description = null)
    {
        this.description = description;
        path = Path.GetFullPath(path);
        if (Path.GetFileName(path).EndsWith("_Data", StringComparison.Ordinal) && Directory.Exists(Path.Combine(path, "Managed")))
            data = path;
        else
        {
            var hits = Directory.GetDirectories(path, "*_Data").Where(d => Directory.Exists(Path.Combine(d, "Managed"))).ToList();
            if (hits.Count != 1) throw new InstallException(Strings.Get(Resource.String.install_error_data_not_found_format, hits.Count));
            data = hits[0];
        }
    }

    public string Describe => description ?? "dir:" + data;
    public string DataDirName => Path.GetFileName(data);

    public IEnumerable<string> Files() =>
        Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(data, f).Replace('\\', '/'));

    public Stream Open(string relative) => File.OpenRead(Path.Combine(data, relative));
}

/// <summary>要顯示給使用者的錯誤（不是程式的錯）；State 是寫進 state.json 的狀態（unsupported、no-space、idle、failed）</summary>
public sealed class InstallException(string message, string state = "failed") : Exception(message)
{
    public string State { get; } = state;
}
