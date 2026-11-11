using Android.Content;
using Android.OS;
using Mt2Mobile.Launcher.Install;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 轉換過期了沒有：用這個 APK 算每一步期望的指紋（Installer.Fingerprints，跟安裝服務同一套），跟 install.json 記的比。
/// 以前遊戲頁只看 complete，APK 更新後舊的轉換照樣跑；現在對不上就在遊戲頁提示重新轉換（docs/perf-battle.md「B 段」）。
/// 算指紋要讀 APK 裡的檔、載入 AssetRipper 的組件、開一次 EGL 查 GPU，放背景執行緒；結果照「APK 的 lastUpdateTime＋install.json 的修改時間」快取。
/// </summary>
public static class ConversionCheck
{
    static readonly object gate = new();
    static string? cachedKey, runningKey;
    static string[]? cachedStale;

    /// <summary>
    /// 有快取就回傳過期的步驟（空的 = 沒過期）；還沒算過回 null，背景算完而且有過期的步驟時在主執行緒叫 onStale（遊戲頁重建）
    /// </summary>
    public static string[]? Stale(Context context, string targetId, Action onStale)
    {
        var p = new Paths(context.FilesDir!.AbsolutePath);
        var json = p.Install(targetId).Json;
        var key = $"{context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.LastUpdateTime}|{targetId}|{(File.Exists(json) ? File.GetLastWriteTimeUtc(json).Ticks : 0)}";
        lock (gate)
        {
            if (cachedKey == key) return cachedStale;
            if (runningKey == key) return null;
            runningKey = key;
        }
        var app = context.ApplicationContext!;
        var main = new Handler(Looper.MainLooper!);
        Task.Run(() =>
        {
            string[] stale;
            try { stale = Compute(app, p, targetId); }
            catch (Exception e)
            {
                LauncherLog.Write($"conversion check failed: {e.GetType().Name} {e.Message}");
                stale = [];
            }
            lock (gate)
            {
                cachedKey = key;
                cachedStale = stale;
                if (runningKey == key) runningKey = null;
            }
            LauncherLog.Write($"conversion check {targetId}: {(stale.Length == 0 ? "up to date" : "stale " + string.Join(",", stale))}");
            if (stale.Length > 0) main.Post(onStale);
        });
        return null;
    }

    static string[] Compute(Context context, Paths p, string targetId)
    {
        var j = Util.ReadJson(p.Install(targetId).Json);
        var original = Util.ReadJson(Path.Combine(p.Original(targetId), "original.json"));
        if (j is null || original is null) return [];
        var rt = Install.Runtime.Extract(context, p, LauncherLog.Write);
        var t = Target.LoadAll(rt.Targets).FirstOrDefault(x => x.Id == targetId);
        if (t is null) return [];
        var fp = Installer.Fingerprints(rt, t, original, DeviceTextures.Plan(p));
        return Installer.Work.Where(s => j["steps"]?[s]?["fp"]?.GetValue<string>() != fp[s]).ToArray();
    }

    /// <summary>重新轉換（匯出、importer、shaders）大概要幾分鐘：這支手機上次的秒數（launcher/step-times.json）</summary>
    public static int EstimateMinutes(Context context, string targetId)
    {
        var p = new Paths(context.FilesDir!.AbsolutePath);
        var times = Util.ReadJson(Path.Combine(p.Launcher, "step-times.json"))?[targetId];
        double s = 0;
        foreach (var step in new[] { "export", "import", "shaders" })
            s += times?[step]?.GetValue<double>() ?? 0;
        // 沒有紀錄：10/24 那次手機上是 298.5＋466.8＋29.1 秒
        return Math.Max(1, (int)Math.Ceiling((s > 0 ? s : 794) / 60));
    }
}
