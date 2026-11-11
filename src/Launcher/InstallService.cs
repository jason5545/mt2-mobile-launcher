using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.OS.Storage;
using Mt2Mobile.Launcher.Install;
using Mt2Mobile.Launcher.SteamUi;
using Mt2Mobile.Steam;
using SteamKit2;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 安裝服務：跑在自己的程序（:install），foreground service（dataSync），整段持有 PARTIAL_WAKE_LOCK（螢幕關掉也要跑）。
/// 安裝期間持有 launcher/install.lock 的檔案鎖；介面拿得到鎖就代表沒有在跑的安裝。
/// Intent 的 action：start（預設；沒有來源就續跑上一次的安裝）、reconvert（遊戲頁的「重新轉換」：從匯出重跑）、cancel（在步驟之間停）、stop-now（直接結束這個程序：每一步都能從頭重跑）。
/// tree：介面用 SAF 選的資料夾（tree URI，已經 TakePersistableUriPermission），有的話從複製原版開始。
/// 驗收用的入口（只有 TEST_ENTRY 建置，見 Launcher.csproj）：source（app 私有空間裡的原版路徑）、keep-work、fake-avail-kb。
/// 服務不 export；adb 的 root 照樣叫得起來。
/// </summary>
[Service(Name = "org.mt2mobile.launcher.InstallService", Process = ":install", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class InstallService : Service
{
    const string Channel = "install";
    const int NotificationId = 1;
    const string WarningChannel = "steam-warning";
    // 1 是安裝的前景通知、2 是 SteamKeepAliveService、3 是 SnapshotService：通知 ID 整個 app 共用，不分程序
    const int RevokeNotificationId = 4;
    /// <summary>這個程序已經在裝了（裝完程序就結束，所以不用歸零）</summary>
    static int running;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var p = new Paths(FilesDir!.AbsolutePath);
        var action = intent?.GetStringExtra("action") ?? "start";
        if (action == "stop-now") Process.KillProcess(Process.MyPid());
        if (action == "cancel")
        {
            Directory.CreateDirectory(p.Launcher);
            File.WriteAllText(p.Cancel, "");
            return StartCommandResult.NotSticky;
        }
        // 帶 type 的 StartForeground 要 API 29；28 照 manifest 的 foregroundServiceType
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, Note(GetString(Resource.String.install_starting), 0, 0), ForegroundService.TypeDataSync);
        else StartForeground(NotificationId, Note(GetString(Resource.String.install_starting), 0, 0));
        if (Interlocked.Exchange(ref running, 1) == 1) return StartCommandResult.NotSticky; // 這個程序已經在裝
        var opt = Options(intent);
        var tree = intent?.GetStringExtra("tree");
        // Steam 頁的「本體分塊並行數」：設定存在介面那邊，按下載時帶過來（跨程序不讀 SharedPreferences）
        int chunks = SteamDownloadSettings.Normalize(intent?.GetIntExtra(SteamDownloadSettings.Extra, SteamDownloadSettings.Default) ?? SteamDownloadSettings.Default);
        _ = Task.Run(() => Run(p, opt, string.IsNullOrEmpty(tree) ? null : Android.Net.Uri.Parse(tree), startId, action, chunks));
        return StartCommandResult.NotSticky;
    }

    InstallOptions Options(Intent? intent)
    {
#if TEST_ENTRY
        var source = intent?.GetStringExtra("source");
        var fake = intent?.GetStringExtra("fake-avail-kb");
        return new InstallOptions
        {
            Source = string.IsNullOrEmpty(source) ? null : new DirectorySource(source),
            KeepWork = intent?.GetStringExtra("keep-work") == "1",
            FakeAvailKb = string.IsNullOrEmpty(fake) ? null : long.Parse(fake),
        };
#else
        return new InstallOptions();
#endif
    }

    async Task Run(Paths p, InstallOptions opt, Android.Net.Uri? tree, int startId, string action, int chunks)
    {
        var wake = ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "mt2:install")!;
        wake.Acquire();
        Directory.CreateDirectory(p.Launcher);
        FileStream? lockFile = null;
        SteamAccessScope? steamAccess = null;
        using var log = new InstallLog(p.Logs);
        using var state = new StateWriter(p.State);
        state.Set("log", log.Path);
        string result = "failed", message = "";
        try
        {
            // .NET 在 Unix 上用 flock 實作 FileShare.None：別的程序（介面）開同一個檔會失敗
            lockFile = new FileStream(p.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(p.Cancel)) File.Delete(p.Cancel);
            if (action == "steam-download")
            {
                state.Set("source", "steam");
                // 先記存取世代再看同意與登入：之後登出、撤回同意（不論在哪個程序）都會讓世代對不上，下載停下來
                try { steamAccess = SteamAccess.Begin(this); }
                catch (Exception e) { throw new InstallException(GetString(Resource.String.install_error_access_unreadable_format, e.GetType().Name), "idle"); }
                if (!OfficialSigning.Allowed(this))
                    throw new InstallException(GetString(Resource.String.install_error_unconsented), "idle");
                if (!SteamAccountStore.IsLoggedIn(this))
                    throw new InstallException(GetString(Resource.String.install_error_not_logged_in), "idle");
            }
            // AssetRipper 的 ExecutingDirectory 用 AppContext.BaseDirectory（Android 上是空字串），要在碰到它的型別之前設（Phase 2）
            Directory.CreateDirectory(p.AssetRipperBase);
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", p.AssetRipperBase + "/");
            // SAF 的來源在這裡建（要查資料夾結構，找不到 _Data 時錯誤要寫進 state.json）
            if (tree is not null) opt = new InstallOptions { Source = new SafSource(this, tree), KeepWork = opt.KeepWork, FakeAvailKb = opt.FakeAvailKb };
            if (action == "reconvert") opt = new InstallOptions { KeepWork = opt.KeepWork, FakeAvailKb = opt.FakeAvailKb, ForceConvert = true };
            var sourceLabel = action == "steam-download" ? "steam" : opt.Source?.Describe ?? (opt.ForceConvert ? "（重新轉換）" : "（續跑）");
            log.Write($"pid={Process.MyPid()} uid={Process.MyUid()} cpus={System.Environment.ProcessorCount} source={sourceLabel} keepWork={opt.KeepWork} fakeAvail={opt.FakeAvailKb}");
            var rt = Install.Runtime.Extract(this, p, log.Write);
            var notifying = new NotifyingState(state, this);
            if (action == "steam-download")
            {
                opt = await DownloadSteamSourceAsync(p, rt, log, notifying, state, chunks, steamAccess!);
                // 下載完就不再用 Steam；之後的安裝步驟不受登出影響
                steamAccess!.Dispose();
                steamAccess = null;
            }
            var installer = new Installer(p, rt, log, notifying, opt, () => opt.FakeAvailKb ?? AvailableKb(p.Files));
            result = installer.Run();
            message = GetString(result == "done" ? Resource.String.install_result_done : Resource.String.install_result_cancelled);
        }
        catch (InstallException e)
        {
            result = e.State;
            message = e.Message;
            log.Write($"[停止] {e.Message}");
        }
        catch (System.OperationCanceledException) when (steamAccess is not null && !steamAccess.IsCurrent())
        {
            result = "cancelled";
            message = GetString(Resource.String.install_result_steam_stopped);
            log.Write("Steam 下載已停止：登出或撤回同意（存取世代換了）");
        }
        catch (System.OperationCanceledException)
        {
            result = "cancelled";
            message = GetString(Resource.String.install_result_steam_cancelled);
            log.Write("Steam 下載已取消");
        }
        catch (Exception e)
        {
            message = e is SteamException se ? Strings.Of(this, se) : e.GetType().Name + ": " + e.Message;
            log.Write("[例外] " + e);
        }
        finally
        {
            steamAccess?.Dispose();
            // 儲存空間整個寫不了時，這兩行也可能丟例外（Terra 第 5 輪第 2 條）：不能因此跳過下面的收尾與結束程序
            try
            {
                state.Finish(result, message);
                log.Write($"結束：{result} {message}");
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn("mt2-install", $"could not record the install result: {e.GetType().Name}");
            }
            lockFile?.Dispose();
            wake.Release();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf(startId);
            // 服務停了程序還會留著當快取程序（拆段 A 實測約 290 MB RSS），AssetRipper 等的靜態狀態也還在：直接結束，下次從新程序開始
            Process.KillProcess(Process.MyPid());
        }
    }

    /// <summary>
    /// 從 Steam 下載本體與支援的 DLC。access 的 Token 從一開始就帶進連線、登入、授權、manifest、每個 chunk；
    /// 登出或撤回同意（換代）時整段取消，每個階段結束也再確認一次（下載項目是 0 個時也會檢查）。
    /// 已經送出的 chunk 請求取消之後可能照樣完成，寫好的檔留著給下次續傳。
    /// </summary>
    async Task<InstallOptions> DownloadSteamSourceAsync(Paths p, Runtime rt, InstallLog log, IStepSink state, StateWriter raw, int chunks,
        SteamAccessScope access)
    {
        var ct = access.Token;
        // 讀的是這段下載那一代的登入：開始之後才換了登入，就不拿新登入的 token 跑舊的下載（Terra 第 13 輪）
        var login = SteamAccountStore.Load(this, access, out _) ?? throw new InstallException(GetString(Resource.String.install_error_login_invalid), "idle");
        await using var session = new SteamSession(log.Write);
        var logon = await session.LogOnAsync(login, ct);
        access.ThrowIfStale();
        if (logon is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked or EResult.InvalidSignature)
        {
            // 只有還是同一個世代才清：使用者已經登出又登入別的帳號時，不會清掉新的登入
            if (!SteamAccountStore.ClearIfCurrent(this, access))
            {
                // 撤回沒有完成：這個程序記的待補換代在程序結束時就沒了，先把這一代交給介面程序（還在的話）再撤回一次（Terra 第 7 輪）；
                // 再發通知：這時 state.json 與記錄檔多半也寫不進去，不能只靠安裝結果
                RevokeRelay.Send(this, access.Generation);
                NotifyRevokeIncomplete();
                throw new InstallException(GetString(Resource.String.install_error_login_invalid_revoke_format, SteamAccountStore.RevokeIncompleteMessage), "idle");
            }
            throw new InstallException(GetString(Resource.String.install_error_login_invalid), "idle");
        }
        if (logon != EResult.OK)
            throw new InstallException(GetString(Resource.String.steam_login_failed_format, logon.ToString()), "idle");

        var ownership = await session.CheckOwnershipAsync(ct);
        access.ThrowIfStale();
        if (!ownership.OwnsGame)
            throw new InstallException(GetString(Resource.String.install_error_not_owned), "idle");

        var target = Target.LoadAll(rt.Targets).FirstOrDefault(t => t.Id == SupportedTarget.Latest.Id)
            ?? throw new InstallException(GetString(Resource.String.install_error_no_steam_target), "unsupported");
        var steamTarget = target.Steam
            ?? throw new InstallException(GetString(Resource.String.install_error_no_manifest), "unsupported");
        var dlc = SteamDlcPolicy.Resolve(ownership.OwnedDlc, steamTarget.Dlc);
        foreach (var id in dlc.UnsupportedOwnedDlcIds)
            log.Write($"DLC {id} 這個版本還沒支援");

        var depots = new[] { steamTarget.BaseDepot }.Concat(dlc.DepotsToDownload.Select(x => x.ToDepotSpec())).ToArray();
        var downloadRoot = p.SteamDownload(target.Id);
        using (var downloader = session.CreateDepotDownloader())
        {
            var plan = await downloader.PrepareAsync(depots, steamTarget.DataDir, downloadRoot, ct);
            access.ThrowIfStale();
            var downloadKb = (long)Math.Ceiling(plan.BytesToDownload / 1024d * target.SpaceOverhead);
            var needKb = checked(target.NeedKb(Installer.Steps) + downloadKb);
            var available = AvailableKb(p.Files);
            log.Write($"空間：Steam 下載與安裝還要 {needKb} kB，可用 {available} kB");
            if (available < needKb)
                throw new InstallException(GetString(Resource.String.install_error_no_space_steam_format, Strings.Gb(needKb), Strings.Gb(available), Strings.Gb(needKb - available)), "no-space");

            log.Write($"Steam 下載準備：files={plan.Files.Count} bytes={plan.TotalBytes} pending_files={plan.PendingFiles} pending_bytes={plan.BytesToDownload} concurrent_chunks={chunks}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastNotified = -1;
            var lastNotifyAt = DateTime.UtcNow;
            await downloader.DownloadAsync(plan, progress =>
            {
                if (progress.TotalBytes <= 0) return;
                if (progress.CompletedBytes != progress.TotalBytes &&
                    progress.CompletedBytes - lastNotified < 8 * 1024 * 1024 &&
                    (DateTime.UtcNow - lastNotifyAt).TotalSeconds < 1)
                    return;
                int count = (int)Math.Min(int.MaxValue, progress.TotalBytes);
                int index = progress.TotalBytes <= int.MaxValue
                    ? (int)progress.CompletedBytes
                    : (int)(progress.CompletedBytes / (double)progress.TotalBytes * count);
                state.Step("download", Math.Clamp(index, 0, count), count, 0, 0);
                raw.Download(progress.CompletedBytes, progress.TotalBytes);
                lastNotified = progress.CompletedBytes;
                lastNotifyAt = DateTime.UtcNow;
            }, () => File.Exists(p.Cancel), ct, maxConcurrentChunks: chunks);
            access.ThrowIfStale();
            sw.Stop();
            log.Write($"Steam 下載完成：files={plan.Files.Count} bytes={plan.TotalBytes} seconds={sw.Elapsed.TotalSeconds:F2}");
        }

        return new InstallOptions
        {
            Source = new DirectorySource(downloadRoot, $"steam:{target.Id}"),
            CleanupSourceAfterCopy = () => Util.DeleteDir(downloadRoot),
        };
    }

    /// <summary>Android 15：dataSync 每 24 小時合計 6 小時。到了就停，留下可以續跑的狀態（每一步都能從頭重跑）</summary>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        var p = new Paths(FilesDir!.AbsolutePath);
        Util.WriteJson(p.State, new System.Text.Json.Nodes.JsonObject { ["state"] = "timeout", ["message"] = GetString(Resource.String.install_result_timeout) });
        Process.KillProcess(Process.MyPid());
    }

    /// <summary>可以用的空間：StorageManager 會把系統可清的快取算進去，拿不到再用 StatFs</summary>
    long AvailableKb(string path)
    {
        try
        {
            var sm = (StorageManager)GetSystemService(StorageService)!;
            return sm.GetAllocatableBytes(sm.GetUuidForPath(new Java.IO.File(path))) / 1024;
        }
        catch (Java.IO.IOException) { return new StatFs(path).AvailableBytes / 1024; }
    }

    internal Notification Note(string text, int index, int count)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, GetString(Resource.String.install_channel), NotificationImportance.Low));
        var b = new Notification.Builder(this, Channel)
            .SetContentTitle(GetString(Resource.String.install_notification_title))
            .SetContentText(text)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true);
        if (count > 0) b.SetProgress(count, index, false);
        return b.Build()!;
    }

    internal void Notify(string text, int index, int count) =>
        ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, Note(text, index, count));

    /// <summary>
    /// :install 自己發現 token 失效、撤回又沒有完成（Terra 第 5 輪第 2 條）：這時儲存空間多半整個寫不了，state.json 與記錄檔都可能寫不進去，
    /// 介面讀不到安裝結果裡的警告。另外發一則通知講撤回沒有完成和處置方式：不是前景服務那則，StopForeground 與程序結束都不會收掉；
    /// 點了打開系統的「應用程式資訊」（強制停止在那一頁）。logcat 也寫一行。使用者沒給通知權限或關掉這個頻道時系統不會顯示，
    /// 背景程序也不能自己開畫面：介面程序還在的話由 RevokeRelay 在介面跳對話框；介面不在又什麼都寫不進去，寫成已知限制
    /// （docs/phase4.md「撤回、登出時停下進行中的 Steam 工作」）。
    /// </summary>
    void NotifyRevokeIncomplete()
    {
        Android.Util.Log.Warn("mt2-install", "steam access revoke incomplete (generation or lock not writable): force stop the launcher or restart the phone");
        try
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(WarningChannel, GetString(Resource.String.steam_warning_channel), NotificationImportance.High));
            var details = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings,
                Android.Net.Uri.FromParts("package", PackageName, null)).AddFlags(ActivityFlags.NewTask);
            manager.Notify(RevokeNotificationId, new Notification.Builder(this, WarningChannel)
                .SetContentTitle(GetString(Resource.String.steam_revoke_incomplete_title))
                .SetContentText(SteamAccountStore.RevokeIncompleteMessage)
                .SetStyle(new Notification.BigTextStyle().BigText(SteamAccountStore.RevokeIncompleteMessage))
                .SetSmallIcon(Android.Resource.Drawable.StatNotifyError)
                .SetContentIntent(PendingIntent.GetActivity(this, 0, details, PendingIntentFlags.Immutable))
                .SetAutoCancel(true)
                .Build()!);
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("mt2-install", $"steam revoke notification failed: {e.GetType().Name}");
        }
    }

    /// <summary>進入步驟時順便更新通知</summary>
    sealed class NotifyingState(StateWriter inner, InstallService service) : IStepSink
    {
        static readonly Dictionary<string, int> Names = new()
        {
            ["download"] = Resource.String.install_step_download,
            ["copy"] = Resource.String.install_step_copy, ["patch"] = Resource.String.install_step_patch, ["export"] = Resource.String.install_step_export,
            ["import"] = Resource.String.install_step_import, ["shaders"] = Resource.String.install_step_shaders,
        };

        public void Step(string step, int index, int count, double est, double estAfter)
        {
            inner.Step(step, index, count, est, estAfter);
            if (step == "download")
            {
                int percent = count <= 0 ? 0 : Math.Clamp((int)(100L * index / count), 0, 100);
                service.Notify(service.GetString(Resource.String.install_download_progress_format, percent), percent, 100);
                return;
            }
            service.Notify($"{index}/{count} {(Names.TryGetValue(step, out var name) ? service.GetString(name) : step)}", index, count);
        }
    }
}
