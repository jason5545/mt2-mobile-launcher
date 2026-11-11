using System.Text.Json.Nodes;
using Android.Content;
using Mt2Mobile.Launcher.Install;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 遊戲本體、安裝與存檔的狀態，給遊戲頁、版本頁顯示。都從安裝服務寫的檔讀（docs/phase4.md「原版、匯出、輸出放哪裡」），跨程序不用 binder：
/// 本體：installs/&lt;target&gt;/install.json 是 complete 的那份，大小與來源讀 originals/&lt;target&gt;/original.json。
/// 安裝：launcher/install.lock 拿不到就是正在裝；state.json 是最後一次的進度或結果；有 install.json 卻沒裝完就是可以繼續。
/// 存檔：files/saves/ 底下（GameActivity 的 PersistentDir 是 saves/&lt;名稱&gt;）。
/// </summary>
public sealed record GameState(InstalledGame? Game, InstallStatus Install, int SaveFiles, long SaveBytes)
{
    public bool Ready => Game != null && !Install.Running;

    public static GameState Load(Context context)
    {
        var files = context.FilesDir!.AbsolutePath;
        var p = new Paths(files);
        InstalledGame? game = null;
        string? resumable = null;
        foreach (var d in Directory.Exists(p.Installs) ? Directory.GetDirectories(p.Installs) : [])
        {
            var id = Path.GetFileName(d);
            var j = Read(new InstallDir(d).Json);
            if (j is null) continue;
            if (j["complete"]?.GetValue<bool>() == true)
            {
                var o = Read(Path.Combine(p.Original(id), "original.json"));
                var t = SupportedTarget.All.FirstOrDefault(x => x.Id == id);
                game = new InstalledGame(t?.Version ?? id, id, o?["bytes"]?.GetValue<long>() ?? 0, o?["source"]?.GetValue<string>() ?? "");
            }
            else resumable = id;
        }

        int saveFiles = 0;
        long saveBytes = 0;
        var saves = new DirectoryInfo(Path.Combine(files, "saves"));
        if (saves.Exists)
            foreach (var f in saves.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                saveFiles++;
                saveBytes += f.Length;
            }
        return new GameState(game, InstallStatus.Load(p, resumable), saveFiles, saveBytes);
    }

    static JsonObject? Read(string path)
    {
        try { return Util.ReadJson(path); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
        {
            LauncherLog.Write($"{path} unreadable: {e.Message}");
            return null;
        }
    }
}

/// <summary>originals/&lt;target&gt;/original.json 與 target 的顯示資訊。Bytes 是原版 _Data 的大小，Source 是匯入來源（saf:…、dir:…）。</summary>
public sealed record InstalledGame(string Version, string TargetId, long Bytes, string Source);

/// <summary>
/// 安裝的狀態。Running：安裝服務持有 install.lock；State、Message、Step…：launcher/state.json（服務每秒寫，結束時寫結果）；
/// Resumable：有沒裝完的 install.json（下次按「繼續安裝」從第一個沒完成的步驟跑）。
/// Source 是 steam 代表這一輪從 Steam 下載；DownloadedBytes、TotalBytes 是下載的位元組數（Steam 頁的進度面板用）。
/// </summary>
public sealed record InstallStatus(bool Running, string? State, string? Message, string? Step, int Index, int Count, double StepElapsed, double EstSeconds, double EstAfter, double TotalElapsed, string? Resumable,
    string? Source = null, long DownloadedBytes = 0, long TotalBytes = 0)
{
    public static InstallStatus Load(Paths p, string? resumable)
    {
        bool running = false;
        try
        {
            Directory.CreateDirectory(p.Launcher);
            using var f = new FileStream(p.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { running = true; }
        JsonObject? st = null;
        try { st = Util.ReadJson(p.State); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { } // 服務正在換檔（先寫暫存再 rename，極少碰到）
        return new InstallStatus(running,
            st?["state"]?.GetValue<string>(), st?["message"]?.GetValue<string>(), st?["step"]?.GetValue<string>(),
            Num(st?["index"]), Num(st?["count"]), Dbl(st?["stepElapsed"]), Dbl(st?["estSeconds"]), Dbl(st?["estAfter"]),
            DateTime.TryParse(st?["started"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t0) ? (DateTime.UtcNow - t0).TotalSeconds : 0,
            resumable, st?["source"]?.GetValue<string>(), Lng(st?["downloadedBytes"]), Lng(st?["totalBytes"]));
    }

    static int Num(JsonNode? n) => n is null ? 0 : (int)n.GetValue<double>();
    static long Lng(JsonNode? n) => n is null ? 0 : (long)n.GetValue<double>();
    static double Dbl(JsonNode? n) => n?.GetValue<double>() ?? 0;
}

/// <summary>啟動器支援的遊戲版本（版本綁定的 target）。DLL 雜湊清單在 APK 的 targets/*.json，這裡只列顯示用的資訊。
/// 照版本由舊到新排：Steam 下載抓 <see cref="Latest"/>；已經裝好的舊版照樣能開，到使用者用 Steam 下載更新為止。</summary>
public sealed record SupportedTarget(string Id, string Game, string Version, string Unity)
{
    public static readonly SupportedTarget[] All =
    {
        new("mt2-2.2.1-win", "Monster Train 2", "2.2.1", "2022.3.62f2"),
        new("mt2-2.3.0-win", "Monster Train 2", "2.3.0", "2022.3.62f2"),
    };

    /// <summary>最新的版本：Steam 下載的版本，也是還沒裝遊戲時畫面上顯示的版本</summary>
    public static SupportedTarget Latest => All[^1];
}
