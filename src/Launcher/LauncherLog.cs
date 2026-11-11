namespace Mt2Mobile.Launcher;

/// <summary>
/// 啟動器的日誌：logcat（tag mt2launcher）加 files/logs/launcher.log。
/// 呼叫端負責不寫密碼、驗證碼、token、完整 SteamID（docs/development.md「安全原則」）。
/// </summary>
public static class LauncherLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? LogDirectory { get; private set; }

    public static void Init(string filesDir)
    {
        LogDirectory = Path.Combine(filesDir, "logs");
        Directory.CreateDirectory(LogDirectory);
        _path = Path.Combine(LogDirectory, "launcher.log");
        // 超過 1 MB 就換一份，只留上一份
        if (File.Exists(_path) && new FileInfo(_path).Length > 1 << 20)
            File.Move(_path, _path + ".1", overwrite: true);
    }

    public static void Write(string message)
    {
        Android.Util.Log.Info("mt2launcher", message);
        if (_path == null)
            return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
            }
            // 沒有寫入權限是 UnauthorizedAccessException：記錄失敗不能打斷呼叫端（Terra 第 6 輪第 1 條：撤回途中寫 log）
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
