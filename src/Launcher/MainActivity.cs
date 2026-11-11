using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Mt2Mobile.Steam;
using Mt2Mobile.Steam.SteamCloud;
using SteamKit2;
using Path = System.IO.Path;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Navigation;
using Google.Android.Material.Shape;
using Mt2Mobile.Launcher.GameHost;
using Mt2Mobile.Launcher.Pages;
using Mt2Mobile.Launcher.SteamUi;
using Mt2Mobile.Launcher.Ui;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 啟動器的主框架（主程序）：內容區加底部導覽列（遊戲、模組、版本、設定、關於），同 Sts2MobileLauncher 的 GameSettingsActivity.showMainShell。
/// 寬版（橫拿的平板）STS2 改用左側的 NavigationRail，這裡先只做手機直拿的版面。
/// 每次切分頁或回到前景都重建那一頁，狀態（Steam 登入、遊戲本體、安裝進度）一律從儲存的地方重讀。
/// 安裝在 :install（InstallService），遊戲在 :game（GameActivity）；這裡只送 Intent、讀它們寫的檔（GameState）。
/// </summary>
[Activity(Name = "org.mt2mobile.launcher.MainActivity", Label = "@string/app_name", MainLauncher = true, Exported = true,
    ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.ScreenLayout)]
public sealed class MainActivity : Activity, ILauncherHost
{
    public const int TabGame = 1, TabMods = 2, TabVersions = 3, TabSettings = 4, TabAbout = 5;
    const int PickFolder = 1, NotificationPermission = 2;
    private FrameLayout _content = null!;
    private BottomNavigationView _nav = null!;
    private int _currentTab = TabGame;
    private readonly SemaphoreSlim _cloudFlowGate = new(1, 1);
    private bool _reconsentAsked;

    public DateTime InstallRequestedAt { get; private set; } = DateTime.MinValue;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        LauncherLog.Init(FilesDir!.AbsolutePath);
        RevokeRelay.Register(this);
#if TEST_ENTRY
        // 自動驗收跳過官方建置提示（docs/phase4.md「debug 建置」）；發布的 APK 沒有這個入口
        if (Intent?.GetStringExtra("accept-unofficial") == "1") OfficialSigning.Accept(this);
#endif
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            Window!.SetDecorFitsSystemWindows(false);

        var shell = UiKit.Vertical(this);
        shell.SetBackgroundColor(Palette.Background);
        _content = new FrameLayout(this);
        UiKit.ApplySystemBarPadding(_content, left: true, right: true);
        shell.AddView(_content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _nav = new BottomNavigationView(this);
        LauncherShell.ConfigureNavigationBar(this, _nav);
        _nav.ItemSelected += (_, e) =>
        {
            OpenTab(e.Item.ItemId);
            e.Handled = true;
        };
        shell.AddView(_nav, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(shell);

        int saved = GetPreferences(FileCreationMode.Private)!.GetInt("last_tab", TabGame);
        _currentTab = saved is >= TabGame and <= TabAbout ? saved : TabGame;
        // 頁面在 OnResume 建（從 Steam 帳號頁、遊戲回來時也要重建）
        _nav.SelectedItemId = _currentTab;
        LauncherLog.Write($"launcher started ({PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName})");
    }

    protected override void OnResume()
    {
        base.OnResume();
        RevokeNotice.Resumed(this);
        OpenTab(_currentTab);
        if (!IsGameProcessRunning())
            _ = RunCloudWorkflowAsync(launchTarget: null, background: true);
        AskReconsentOnOpen();
    }

    protected override void OnPause()
    {
        RevokeNotice.Paused(this);
        base.OnPause();
    }

    public void OpenTab(int tab)
    {
        _currentTab = tab;
        GetPreferences(FileCreationMode.Private)!.Edit()!.PutInt("last_tab", tab).Apply();
        var page = LauncherShell.BuildPage(this, this, tab);
        _content.RemoveAllViews();
        _content.AddView(page, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    /// <summary>切到指定分頁，連導覽列的選取一起換。</summary>
    public void SelectTab(int tab)
    {
        if (_nav.SelectedItemId == tab)
            OpenTab(tab);
        else
            _nav.SelectedItemId = tab;
    }

    public void OpenSteamAccount() => OpenSteamPage(SteamAccountActivity.TabAccount);

    /// <summary>
    /// Steam 頁：先過官方建置檢查，非官方又沒同意就跳風險提示（預設是取消）。tab 是登入後先顯示的分頁。
    /// 照 STS2 的 Steam 頁：下載要在 Steam 頁的下載分頁按「從 Steam 下載遊戲」才開始，同意或登入完都不會自己開始。
    /// </summary>
    private void OpenSteamPage(int tab)
    {
        var intent = new Intent(this, typeof(SteamAccountActivity)).PutExtra(SteamAccountActivity.ExtraTab, tab);
        if (OfficialSigning.Allowed(this))
        {
            StartActivity(intent);
            return;
        }
        AskSigningConsent(() => StartActivity(intent));
    }

    /// <summary>非官方建置的風險提示（預設是取消）；同意才記下來並接著做 onAccepted</summary>
    private void AskSigningConsent(Action onAccepted)
    {
        var fp = OfficialSigning.Fingerprint(this);
        new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.signing_title)!
            .SetMessage(GetString(Resource.String.signing_message, fp[..16], OfficialSigning.OfficialFingerprint?[..16] ?? GetString(Resource.String.signing_official_none)))!
            .SetNegativeButton(Resource.String.cancel, (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton(Resource.String.signing_accept, (_, _) =>
            {
                OfficialSigning.Accept(this);
                LauncherLog.Write($"unofficial build accepted ({fp[..16]})");
                onAccepted();
            })!
            .Show();
    }

    /// <summary>
    /// APK 裝過或更新過、同意要重問時，一打開啟動器就問，不等使用者去點 Steam 的入口（10/24 兩次：沒同意就開遊戲，Steam 身分與 DLC 沒載入，
    /// 開著 DLC 開始的那局不能繼續）。每次開啟動器最多問一次，取消了就不再跳；開遊戲時還會再擋一次（LaunchGame）
    /// </summary>
    private void AskReconsentOnOpen()
    {
        if (_reconsentAsked || !OfficialSigning.NeedsReconsent(this) || IsGameProcessRunning())
            return;
        _reconsentAsked = true;
        LauncherLog.Write("apk updated: asking signing consent again");
        AskSigningConsent(() =>
        {
            RefreshGamePage();
            _ = RunCloudWorkflowAsync(launchTarget: null, background: false);
        });
    }

    public void NotReady(string label) => UiKit.Toast(this, GetString(Resource.String.not_ready_format, label));

    public void StartSteamDownload()
    {
        if (GameState.Load(this).Install.Running)
        {
            UiKit.Toast(this, GetString(Resource.String.install_busy));
            return;
        }
        OpenSteamPage(SteamAccountActivity.TabDownload);
    }

    public void RequestCloudSync()
    {
        if (!OfficialSigning.Allowed(this) || !SteamAccountStore.IsLoggedIn(this))
        {
            OpenSteamAccount();
            return;
        }
        _ = RunCloudWorkflowAsync(launchTarget: null, background: false);
    }

    /// <summary>選原版的資料夾（SAF）。選好在 OnActivityResult 拿永久的讀取權限，交給安裝服務</summary>
    public void ImportGame()
    {
        if (GameState.Load(this).Install.Running)
        {
            UiKit.Toast(this, GetString(Resource.String.install_busy));
            return;
        }
        UiKit.Toast(this, GetString(Resource.String.install_pick_hint));
        StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree).AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission), PickFolder);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickFolder || resultCode != Result.Ok || data?.Data is not { } tree) return;
        ContentResolver!.TakePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission);
        LauncherLog.Write($"import picked {tree}");
        StartInstall("start", tree.ToString());
    }

    /// <summary>送給安裝服務：start（tree 有值就從複製原版開始，沒有就續跑）、reconvert（從匯出重跑）、cancel、stop-now</summary>
    public void StartInstall(string action, string? tree = null)
    {
        if (action is "start" or "steam-download" or "reconvert")
            InstallRequestedAt = DateTime.UtcNow;
        SendInstall(this, action, tree);
        OpenTab(TabGame);
    }

    /// <summary>送給 :install 服務（Steam 頁也用）。開始類的動作用前景服務；Android 13 起通知要權限，不給也照樣裝，只是看不到進度通知</summary>
    public static void SendInstall(Activity a, string action, string? tree = null)
    {
        var intent = new Intent(a, typeof(InstallService)).PutExtra("action", action);
        if (tree != null) intent.PutExtra("tree", tree);
        if (action == "steam-download") intent.PutExtra(SteamDownloadSettings.Extra, SteamDownloadSettings.ConcurrentChunks(a));
        if (action is "start" or "steam-download" or "reconvert")
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && a.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                UiKit.Toast(a, a.GetString(Resource.String.install_notification_permission));
                a.RequestPermissions([Android.Manifest.Permission.PostNotifications], NotificationPermission);
            }
            a.StartForegroundService(intent);
        }
        else a.StartService(intent);
    }

    /// <summary>開遊戲：GameActivity 在自己的程序與 task（:game），自己找裝好的那份</summary>
    public void LaunchGame()
    {
        var state = GameState.Load(this);
        if (state.Install.Running)
        {
            UiKit.Toast(this, GetString(Resource.String.install_busy));
            return;
        }
        if (state.Game == null)
        {
            UiKit.Toast(this, GetString(Resource.String.game_launch_disabled_message));
            return;
        }
        var target = state.Game.TargetId;
        // 沒同意就開，遊戲拿不到 Steam 身分與 DLC：APK 更新過、或本來就用 Steam 的，先擋下來讓使用者選；從沒用過 Steam 的照常開
        if (!OfficialSigning.Allowed(this) && (OfficialSigning.NeedsReconsent(this) || SteamAccountStore.IsLoggedIn(this)))
        {
            new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(this)
                .SetTitle(Resource.String.launch_unconsented_title)!
                .SetMessage(OfficialSigning.NeedsReconsent(this) ? Resource.String.launch_unconsented_updated : Resource.String.launch_unconsented_message)!
                .SetPositiveButton(Resource.String.launch_unconsented_accept, (_, _) =>
                    AskSigningConsent(() => _ = RunCloudWorkflowAsync(target, background: false)))!
                .SetNeutralButton(Resource.String.launch_unconsented_anyway, (_, _) =>
                {
                    LauncherLog.Write("launch without signing consent (Steam identity and DLC off)");
                    _ = RunCloudWorkflowAsync(target, background: false);
                })!
                .SetNegativeButton(Resource.String.cancel, (IDialogInterfaceOnClickListener?)null)!
                .Show();
            return;
        }
        _ = RunCloudWorkflowAsync(target, background: false);
    }

    private async Task RunCloudWorkflowAsync(string? launchTarget, bool background)
    {
        await _cloudFlowGate.WaitAsync();
        SteamAccessScope? access = null;
        try
        {
            if (IsDestroyed || IsGameProcessRunning())
                return;

            if (!OfficialSigning.Allowed(this))
            {
                SaveCloudStatus("not-synced", GetString(Resource.String.cloud_reason_unconsented));
                if (launchTarget != null) LaunchGameNow(launchTarget);
                return;
            }
            if (!SteamAccountStore.IsLoggedIn(this))
            {
                SaveCloudStatus("not-synced", GetString(Resource.String.cloud_reason_not_logged_in));
                if (launchTarget != null) LaunchGameNow(launchTarget);
                return;
            }

            // 整段流程（包括衝突對話框開著的時候）記著開始時的存取世代：登出、撤回同意就停，選擇也不會套到下一個登入
            try
            {
                access = SteamAccess.Begin(this);
            }
            catch (Exception e)
            {
                LauncherLog.Write($"steam access generation unreadable: {e.GetType().Name}");
                SaveCloudStatus("failed", GetString(Resource.String.cloud_reason_access_unreadable));
                if (!background) UiKit.Toast(this, GetString(Resource.String.cloud_status_failed));
                return;
            }

            SaveCloudStatus("syncing", GetString(Resource.String.cloud_reason_connecting));
            CloudAttempt attempt;
            try
            {
                attempt = await Task.Run(() => RunCloudAttemptAsync(access, null));
            }
            catch (Exception) when (!access.IsCurrent())
            {
                StoppedByRevoke();
                return;
            }
            catch (Exception e)
            {
                LogCloudSyncFailure(e);
                SaveCloudStatus("failed", GetString(Resource.String.cloud_reason_failed));
                if (launchTarget != null && await AskOpenWithoutSyncAsync())
                    LaunchUnlessRevoked(launchTarget, access);
                else if (!background)
                    UiKit.Toast(this, GetString(Resource.String.cloud_status_failed));
                return;
            }

            if (attempt.SkipReason != null)
            {
                // 撤回了（包括撤回沒有完成）就交給 StoppedByRevoke 先跳警告再記狀態（Terra 第 6 輪第 3 條）
                if (access.IsCurrent())
                    SaveCloudStatus("not-synced", attempt.SkipReason, hosts: attempt.Hosts);
                LaunchUnlessRevoked(launchTarget, access, attempt.SkipReason);
                return;
            }

            var result = attempt.Result!;
            var hosts = new HashSet<string>(attempt.Hosts, StringComparer.OrdinalIgnoreCase);
            while (result.Kind == SteamCloudSyncResultKind.ConflictRequired)
            {
                SaveCloudStatus("conflict", GetString(Resource.String.cloud_reason_conflicts_format, result.Conflicts.Count), result.CloudFileCount, hosts: hosts);
                RefreshGamePage();
                var choice = await AskConflictChoiceAsync(result);
                if (choice == SteamCloudConflictChoice.Cancel)
                {
                    SaveCloudStatus("conflict", GetString(Resource.String.cloud_reason_conflict_pending), result.CloudFileCount, hosts: hosts);
                    return;
                }
                // 對話框開著的時候登出或撤回：這個選擇作廢
                if (!access.IsCurrent())
                {
                    StoppedByRevoke();
                    return;
                }

                // 選擇只套用到這份清單（綁帳號鍵）；重新比對時有任何變化，同步會回傳新的清單，這個迴圈再問一次
                var approval = SteamCloudConflictApproval.For(result, choice);
                SaveCloudStatus("syncing", GetString(Resource.String.cloud_reason_applying), result.CloudFileCount, hosts: hosts);
                try
                {
                    attempt = await Task.Run(() => RunCloudAttemptAsync(access, approval));
                }
                catch (Exception) when (!access.IsCurrent())
                {
                    StoppedByRevoke();
                    return;
                }
                catch (Exception e)
                {
                    LogCloudSyncFailure(e);
                    SaveCloudStatus("failed", GetString(Resource.String.cloud_reason_failed), result.CloudFileCount, hosts: hosts);
                    if (launchTarget != null && await AskOpenWithoutSyncAsync())
                        LaunchUnlessRevoked(launchTarget, access);
                    else if (!background)
                        UiKit.Toast(this, GetString(Resource.String.cloud_status_failed));
                    return;
                }

                hosts.UnionWith(attempt.Hosts);
                if (attempt.SkipReason != null)
                {
                    if (access.IsCurrent())
                        SaveCloudStatus("not-synced", attempt.SkipReason, result.CloudFileCount, hosts: hosts);
                    LaunchUnlessRevoked(launchTarget, access, attempt.SkipReason);
                    return;
                }
                result = attempt.Result!;
            }

            if (result.Kind == SteamCloudSyncResultKind.Cancelled)
                return;

            SaveCloudStatus("synced", "", result.CloudFileCount, result.Downloaded, result.Uploaded, hosts);
            LaunchUnlessRevoked(launchTarget, access);
        }
        finally
        {
            access?.Dispose();
            _cloudFlowGate.Release();
            RefreshGamePage();
        }
    }

    /// <summary>
    /// 同步途中登出、撤回同意或 token 失效（存取世代換了，或這個程序撤回過）：已經停下來，不開遊戲。reason 是狀態上顯示的原因；
    /// 撤回沒有完成（世代寫不了或拿不到鎖）時另外跳對話框，請使用者強制停止啟動器。
    /// 對話框先跳、狀態後記：儲存空間整個寫不了時記狀態會丟例外，不能擋住警告（Terra 第 6 輪第 3 條）。
    /// </summary>
    private void StoppedByRevoke(string? reason = null)
    {
        // 背景同步時這個 Activity 可能已經不在前景：跳在當時前景的畫面上，沒有就等回到前景（Terra 第 8 輪第 3 條）
        if (reason == SteamAccountStore.RevokeIncompleteMessage)
            RevokeNotice.ShowWhenVisible();
        LauncherLog.Write("steam cloud sync stopped: steam access changed (logged out, consent revoked or token invalid)");
        try { SaveCloudStatus("not-synced", reason ?? GetString(Resource.String.cloud_reason_stopped)); }
        catch (Exception e) { LauncherLog.Write($"cloud status not saved after a revoke: {e.GetType().Name}"); }
    }

    /// <summary>
    /// 從同步流程開遊戲前最後一次確認存取世代（Terra 第 3 輪第 3 條）：同步成功、回 Skip、或「不同步直接開啟」對話框開著的時候，
    /// 只要登出、撤回同意，或 token 失效被清掉（也會換代），就不開。使用者要不用 Steam 開，再按一次啟動（那時沒登入，直接開）。
    /// 同步之前就沒同意或沒登入的兩條路（RunCloudWorkflowAsync 開頭）沒有開始任何 Steam 工作，照舊直接開。
    /// </summary>
    private void LaunchUnlessRevoked(string? target, SteamAccessScope access, string? reason = null)
    {
        // IsCurrent 也看這個程序的取消狀態：token 失效時 ClearIfCurrent 就算世代沒寫成，這裡也當成已撤回
        if (!access.IsCurrent())
        {
            StoppedByRevoke(reason);
            if (target != null)
                UiKit.Toast(this, GetString(Resource.String.cloud_launch_stopped));
            return;
        }
        if (target != null)
            LaunchGameNow(target);
    }

    private static void LogCloudSyncFailure(Exception exception)
    {
        if (exception is SteamCloudProtocolException or SteamCloudHttpException or SteamCloudRemoteChangedException or SteamCloudLocalChangedException)
            LauncherLog.Write($"steam cloud sync failed: {exception.GetType().Name}: {exception.Message}");
        else
            LauncherLog.Write($"steam cloud sync failed: {exception.GetType().Name}");
    }

    private async Task<CloudAttempt> RunCloudAttemptAsync(SteamAccessScope access, SteamCloudConflictApproval? approval)
    {
        access.ThrowIfStale();
        var login = SteamAccountStore.Load(this, access, out var loginKey);
        if (login is null)
        {
            // 這一代的登入解不開才清（受世代保護）；不是這一代的登入（中途登出或換了登入）不清（Terra 第 13 輪）
            if (SteamAccountStore.HasLoginOf(this, access) && !SteamAccountStore.ClearIfCurrent(this, access))
                return CloudAttempt.Skip(SteamAccountStore.RevokeIncompleteMessage);
            return CloudAttempt.Skip(GetString(Resource.String.cloud_reason_login_invalid));
        }

        // 這次嘗試全程持有遊戲鎖（GameActivity 開機時要拿到同一把才開）：同步途中遊戲開不起來，不會一邊同步一邊寫存檔。
        // using：成功、取消、例外都在離開這裡時放掉；衝突對話框開著的時候、開遊戲之前都已經不在這裡面。
        // 只擋得住遵守這把鎖的寫入者。
        using var gameLock = TryLockGame();
        if (gameLock is null)
            return CloudAttempt.Skip(GetString(Resource.String.cloud_reason_game_running));

        var hosts = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        void RecordHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            hosts.TryAdd(host, 0);
            LauncherLog.Write($"steam cloud transfer host: {host}");
        }

        // 登出、撤回（換代）會取消整段：連線、登入、授權、清單、下載、上傳都帶這個 token
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(access.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        await using var session = new SteamSession(LauncherLog.Write);
        await session.ConnectAsync(ct);
        var logon = await session.LogOnAsync(login, ct);
        access.ThrowIfStale();
        if (logon is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked or EResult.InvalidSignature)
        {
            if (!SteamAccountStore.ClearIfCurrent(this, access))
                return CloudAttempt.Skip(SteamAccountStore.RevokeIncompleteMessage);
            return CloudAttempt.Skip(GetString(Resource.String.cloud_reason_login_expired));
        }
        if (logon != EResult.OK)
            throw new SteamException(GetString(Resource.String.steam_login_failed_format, logon.ToString()));

        var identity = await session.GetIdentityAsync(ct);
        var ownership = await RefreshOwnershipAsync(session, identity, access, loginKey!, ct);
        var cachedIdentity = SteamAccountStore.LoadGameIdentity(this);
        // 遊戲只用目前這一次登入存的身分（Terra 第 10、11 輪）：舊版或之前的登入存的，帳號一樣也重寫
        if (cachedIdentity is null || cachedIdentity.LoginKey != loginKey
            || cachedIdentity.SteamId != identity.SteamId || cachedIdentity.PersonaName != identity.PersonaName)
            SteamAccountStore.SaveGameIdentity(this, identity, access, loginKey!);

        if (!ownership.OwnsGame)
            return CloudAttempt.Skip(GetString(Resource.String.cloud_reason_not_owned), hosts.Keys.ToArray());

        string files = FilesDir!.AbsolutePath;
        var manager = new SteamCloudSyncManager(
            Path.Combine(files, "saves", "persistent"),
            Path.Combine(files, "cloud"));
        // Cloud 基準綁帳號：取清單之前就用這次登入的 SteamID 算帳號鍵（不寫 log）。
        // 每次替換、移除、上傳、寫 state 之前檢查世代；替換、移除手機檔、寫 state 在跨程序的鎖裡確認世代才做，
        // 換代（登出、撤回、另一個登入頁存入新登入）不會夾在檢查和寫入之間（Terra 第 13 輪）
        var accountKey = SteamCloudAccount.KeyFor(identity.SteamId);
        var sync = await manager.SynchronizeAsync(Mt2Steam.AppId, accountKey, session.Cloud, approval, RecordHost,
            checkpoint: access.ThrowIfStale,
            localWrite: write => SteamAccess.WriteIfCurrent(this, access, write, rollback: () => { }),
            cancellationToken: ct);
        int cloudFiles = sync.Kind == SteamCloudSyncResultKind.Synced ? manager.GetCloudFileCount() : sync.CloudFileCount;
        return new CloudAttempt(sync, null, cloudFiles, hosts.Keys.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// 每次同步都重查授權（Jason 10/27 定），買了、退了 DLC 下次同步就跟上；查到不擁有也照新結果。
    /// 查詢失敗（逾時、網路）時，只能用完整、而且明確是這個 SteamID 查的快取，不然就停下來，身分也不寫。
    /// 取消與撤回不算查詢失敗，照樣往外丟。
    /// </summary>
    private async Task<SteamAccountStore.Ownership> RefreshOwnershipAsync(
        SteamSession session, SteamIdentity identity, SteamAccessScope access, string loginKey, CancellationToken ct)
    {
        OwnershipResult? fresh = null;
        Exception? failure = null;
        try
        {
            fresh = await session.CheckOwnershipAsync(ct);
        }
        catch (Exception e) when (e is not System.OperationCanceledException)
        {
            failure = e;
        }
        access.ThrowIfStale();

        if (fresh is not null)
        {
            SteamAccountStore.SaveOwnership(this, fresh, identity.SteamId, access, loginKey);
            return SteamAccountStore.LoadOwnership(this) ?? throw new SteamException(GetString(Resource.String.steam_ownership_not_saved));
        }

        var cached = SteamAccountStore.LoadOwnership(this);
        if (cached is { HasOwnedDlcIds: true } && cached.SteamId == identity.SteamId)
        {
            LauncherLog.Write($"steam ownership refresh failed ({failure?.GetType().Name}); using the cached result checked for this account");
            return cached;
        }
        LauncherLog.Write($"steam ownership refresh failed ({failure?.GetType().Name}); no cached result for this account");
        throw new SteamException(GetString(Resource.String.steam_ownership_unknown));
    }

    private Task<SteamCloudConflictChoice> AskConflictChoiceAsync(SteamCloudSyncResult result)
    {
        var conflicts = result.Conflicts;
        var completion = new TaskCompletionSource<SteamCloudConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (IsDestroyed || IsFinishing)
            {
                completion.TrySetResult(SteamCloudConflictChoice.Cancel);
                return;
            }

            var details = string.Join("\n", conflicts.Select(file => GetString(
                Resource.String.cloud_conflict_file_time_format,
                file.LocalRelativePath,
                file.DeviceSha1 is null ? GetString(Resource.String.cloud_conflict_missing) : FormatTime(file.DeviceModifiedAtUtc),
                file.CloudDeleted ? GetString(Resource.String.cloud_conflict_deleted)
                    : file.CloudSha1 is null ? GetString(Resource.String.cloud_conflict_missing)
                    : FormatTime(file.CloudModifiedAtUtc))));
            int deviceCount = conflicts.Count(c => c.DeviceSha1 is not null);
            int cloudCount = conflicts.Count(c => c.CloudSha1 is not null);
            var message = GetString(Resource.String.cloud_conflict_message_format, deviceCount, cloudCount, details);
            // 這個帳號在這支手機上第一次同步（換了帳號、或舊版沒記帳號）：選 Cloud 會把手機獨有的檔移到備份（Jason 10/27 定，對話框要講）
            if (result.FirstSyncForAccount)
                message += GetString(Resource.String.cloud_conflict_first_sync);
            if (result.DeviceOnlyCount > 0)
                message += GetString(Resource.String.cloud_conflict_device_only_format, result.DeviceOnlyCount);
            var dialog = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(this)
                .SetTitle(Resource.String.cloud_conflict_title)!
                .SetMessage(message)!
                .SetPositiveButton(Resource.String.cloud_conflict_use_cloud, (_, _) => completion.TrySetResult(SteamCloudConflictChoice.UseCloud))!
                .SetNegativeButton(Resource.String.cloud_conflict_use_device, (_, _) => completion.TrySetResult(SteamCloudConflictChoice.UseDevice))!
                .SetNeutralButton(Resource.String.cancel, (_, _) => completion.TrySetResult(SteamCloudConflictChoice.Cancel))!
                .SetOnCancelListener(new DialogCancelListener(() => completion.TrySetResult(SteamCloudConflictChoice.Cancel)))!
                .Show();
            dialog.SetCanceledOnTouchOutside(false);
        });
        return completion.Task;
    }

    private Task<bool> AskOpenWithoutSyncAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (IsDestroyed || IsFinishing)
            {
                completion.TrySetResult(false);
                return;
            }
            new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(this)
                .SetTitle(Resource.String.cloud_network_failed_title)!
                .SetMessage(Resource.String.cloud_network_failed_message)!
                .SetNegativeButton(Resource.String.cancel, (_, _) => completion.TrySetResult(false))!
                .SetPositiveButton(Resource.String.cloud_open_anyway, (_, _) => completion.TrySetResult(true))!
                .SetOnCancelListener(new DialogCancelListener(() => completion.TrySetResult(false)))!
                .Show();
        });
        return completion.Task;
    }

    private void SaveCloudStatus(string state, string reason, int cloudFiles = 0, int downloaded = 0, int uploaded = 0,
        IEnumerable<string>? hosts = null)
    {
        var previous = SteamCloudStatusStore.Load(this);
        var newHosts = (hosts ?? previous?.Hosts ?? Array.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
        SteamCloudStatusStore.Save(this, new SteamCloudStatusStore.Status(
            state,
            state == "synced" ? DateTimeOffset.UtcNow : previous?.LastSyncAtUtc,
            state == "synced" ? downloaded : previous?.Downloaded ?? 0,
            state == "synced" ? uploaded : previous?.Uploaded ?? 0,
            cloudFiles > 0 ? cloudFiles : previous?.CloudFiles ?? 0,
            reason,
            newHosts));
        RefreshGamePage();
    }

    private void RefreshGamePage()
    {
        // 設定頁也有 Cloud 狀態，停在那頁時一起重建
        if (IsDestroyed || _currentTab is not (TabGame or TabSettings)) return;
        RunOnUiThread(() =>
        {
            if (!IsDestroyed && _currentTab is TabGame or TabSettings)
                OpenTab(_currentTab);
        });
    }

    /// <summary>遊戲在跑（或這個啟動器正在同步，拿著同一把鎖）</summary>
    private bool IsGameProcessRunning()
    {
        using var probe = TryLockGame();
        return probe is null;
    }

    /// <summary>
    /// 拿 cloud/game.lock，跟 GameActivity 同一把（.NET 在 Unix 上用 flock 實作 FileShare.None，跨程序互斥）。
    /// 拿不到回 null。呼叫端用 using，離開時一定放掉；程序被殺時系統也會放掉。
    /// </summary>
    private FileStream? TryLockGame()
    {
        var lockPath = Path.Combine(FilesDir!.AbsolutePath, "cloud", "game.lock");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void LaunchGameNow(string target)
    {
        if (IsGameProcessRunning())
        {
            UiKit.Toast(this, GetString(Resource.String.install_busy));
            return;
        }
        LauncherLog.Write($"launch {target}");
        var intent = new Intent(this, typeof(GameActivity)).PutExtra("target", target);
        // 這個程序撤回過、還沒補換代（Terra 第 7 輪）：遊戲程序從磁碟讀的身分與授權快取可能是撤回前的，這次不給 Steam 身分與 DLC
        if (SteamAccess.RevokePending(this))
        {
            LauncherLog.Write("launch without steam identity: a steam access revoke is still pending");
            intent.PutExtra(GameActivity.ExtraSteamRevoked, true);
        }
        StartActivity(intent);
    }

    private static string FormatTime(DateTimeOffset? time) =>
        time?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "—";

    private sealed record CloudAttempt(SteamCloudSyncResult? Result, string? SkipReason, int CloudFileCount, string[] Hosts)
    {
        public static CloudAttempt Skip(string reason, string[]? hosts = null) => new(null, reason, 0, hosts ?? Array.Empty<string>());
    }

    private sealed class DialogCancelListener(Action cancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => cancel();
    }
}
