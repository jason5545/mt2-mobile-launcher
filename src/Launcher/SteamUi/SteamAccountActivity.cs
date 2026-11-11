using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Text.Format;
using Android.Util;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Shape;
using Mt2Mobile.Launcher.Ui;
using Mt2Mobile.Steam;
using SteamKit2;

namespace Mt2Mobile.Launcher.SteamUi;

/// <summary>
/// Steam 頁。沒登入：分段切換「帳號密碼｜QR code」；登入成功後用 refresh token 登 CM、做授權檢查，結果存起來給遊戲頁顯示。
/// 已登入：分段切換「下載｜帳號」（照 STS2 的 Steam 頁，下載在前）。下載：遊戲卡、「從 Steam 下載遊戲」，下載中換成進度面板（階段、速度、已傳輸、剩餘時間、停止），
/// 下載本身在 :install 服務跑，這頁每秒讀 state.json；帳號：帳號、授權結果、重新檢查、登出。
/// 每個動作開一條新的 SteamSession，做完就關；登入期間開前景服務保活（使用者會切到 Steam App 按核准）。
/// </summary>
[Activity(Name = "org.mt2mobile.launcher.SteamAccountActivity", Label = "@string/steam_account_title",
    ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.ScreenLayout)]
public sealed class SteamAccountActivity : Activity
{
    public const string ExtraTab = "tab";
    public const int TabDownload = 0, TabAccount = 1;
    private static int _loginTab;
    private int _tab = TabDownload;
    /// <summary>剛送出下載的時間：:install 程序起來、拿到鎖之前，面板也要先出現</summary>
    private DateTime _downloadRequestedAt = DateTime.MinValue;
    private Handler? _ticker;
    private LinearLayout _content = null!;
    private TextView? _status;
    private ImageView? _qrImage;
    private MaterialButton? _primaryButton;
    private EditText? _username, _password;
    private CancellationTokenSource? _cts;
    private bool Busy => _cts != null;

    private string DeviceName => $"MT2 Mobile Launcher ({Build.Model})";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RevokeRelay.Register(this);
        // 官方建置檢查：入口在 MainActivity.OpenSteamAccount；從別的地方直接開進來也擋（非官方又沒同意就不開）
        if (!OfficialSigning.Allowed(this))
        {
            Finish();
            return;
        }
        _tab = Intent?.GetIntExtra(ExtraTab, TabDownload) ?? TabDownload;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            Window!.SetDecorFitsSystemWindows(false);
        var shell = UiKit.Vertical(this);
        shell.SetBackgroundColor(Palette.Background);
        shell.AddView(BuildTopBar());
        _content = UiKit.Vertical(this);
        _content.SetPadding(0, 0, 0, UiKit.Dp(this, 32));
        var scroll = UiKit.ResponsiveScroll(this, _content);
        UiKit.ApplySystemBarPadding(scroll, bottom: true);
        shell.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(shell);
        Render();
    }

    protected override void OnResume()
    {
        base.OnResume();
        RevokeNotice.Resumed(this);
        // 從別的地方回來（遊戲頁按了停止、下載在背景跑完）就重畫下載分頁
        if (_content != null && !Busy && SteamAccountStore.IsLoggedIn(this) && _tab == TabDownload)
            Render();
    }

    protected override void OnPause()
    {
        RevokeNotice.Paused(this);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _ticker?.RemoveCallbacksAndMessages(null);
        _cts?.Cancel();
        base.OnDestroy();
    }

    private View BuildTopBar()
    {
        var bar = UiKit.Horizontal(this);
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(UiKit.Dp(this, 8), UiKit.Dp(this, 16), UiKit.Dp(this, 20), UiKit.Dp(this, 12));
        UiKit.ApplySystemBarPadding(bar, top: true);
        var back = new FrameLayout(this) { Clickable = true, Foreground = UiKit.Ripple(this, 100) };
        back.AddView(UiKit.Icon(this, "arrow_back", Palette.OnSurface, 24), new FrameLayout.LayoutParams(UiKit.Dp(this, 24), UiKit.Dp(this, 24), GravityFlags.Center));
        back.Click += (_, _) => Finish();
        bar.AddView(back, new LinearLayout.LayoutParams(UiKit.Dp(this, 48), UiKit.Dp(this, 48)));
        bar.AddView(UiKit.Text(this, GetString(Resource.String.steam_account_title), 22, Palette.OnSurface, TypefaceStyle.Bold).SingleLine(), UiKit.Weighted(this, 1f, 8));
        return bar;
    }

    private void Render()
    {
        _ticker?.RemoveCallbacksAndMessages(null);
        _content.RemoveAllViews();
        _qrImage = null;
        _username = _password = null;
        _primaryButton = null;
        _status = null;
        if (SteamAccountStore.IsLoggedIn(this))
        {
            _content.AddView(Widgets.Segmented(this,
                new[] { GetString(Resource.String.steam_tab_download), GetString(Resource.String.steam_tab_account) },
                _tab, i =>
                {
                    if (i == _tab || Busy)
                        return;
                    _tab = i;
                    Render();
                }), UiKit.MatchWrap(this));
            if (_tab == TabDownload)
                RenderDownload();
            else
                RenderAccount();
        }
        else
            RenderLogin();
    }

    // ── 下載 ─────────────────────────────────────────────────

    private bool Installing(InstallStatus st) => st.Running || (DateTime.UtcNow - _downloadRequestedAt).TotalSeconds < 10;

    private void RenderDownload()
    {
        var state = GameState.Load(this);
        var target = SupportedTarget.Latest;
        var own = SteamAccountStore.LoadOwnership(this);

        var game = Widgets.Card(this);
        game.AddView(Widgets.ListItem(this, "stadia_controller", GetString(Resource.String.steam_download_game_title_format, target.Game, target.Version),
            GetString(Resource.String.steam_download_game_subtitle), null, circleIcon: false));
        game.AddView(Widgets.ListItem(this, "extension", GetString(Resource.String.steam_download_dlc_title),
            own == null ? GetString(Resource.String.steam_download_dlc_unknown) : DlcText(Resource.String.steam_download_dlc_format, own),
            null, circleIcon: false));
        _content.AddView(game, UiKit.MatchWrap(this, 16));

        if (Installing(state.Install))
        {
            // 剛按下載、服務還沒寫 source 時也算 Steam 下載；匯入資料夾的安裝在跑就只說明
            if (state.Install.Source == "steam" || (DateTime.UtcNow - _downloadRequestedAt).TotalSeconds < 10)
                RenderProgressPanel(state.Install);
            else
                _content.AddView(Note(GetString(Resource.String.steam_download_other_install)), UiKit.MatchWrap(this, 16));
            return;
        }

        var chunks = Widgets.Card(this);
        chunks.AddView(Widgets.ListItem(this, "speed", GetString(Resource.String.steam_download_chunks_title),
            GetString(Resource.String.steam_download_chunks_value, SteamDownloadSettings.ConcurrentChunks(this)), PickChunks, circleIcon: false));
        _content.AddView(chunks, UiKit.MatchWrap(this, 12));

        // 上一輪是 Steam 下載：留下結果
        if (state.Install.Source == "steam" && state.Install.State is { } last && last != "running")
            _content.AddView(Note(last == "done" ? GetString(Resource.String.steam_download_done)
                : GetString(Resource.String.steam_download_failed_format, state.Install.Message ?? last)), UiKit.MatchWrap(this, 16));
        // 裝的是舊版（APK 加了新的 target）：這次下載是更新到 target.Version，不是重新下載同一版
        bool update = state.Game != null && state.Game.TargetId != target.Id;
        if (state.Game != null)
            _content.AddView(Note(update ? GetString(Resource.String.steam_download_update_note, state.Game.Version, target.Version)
                : GetString(Resource.String.steam_download_installed_note, state.Game.Version)), UiKit.MatchWrap(this, 16));

        bool owns = own?.OwnsGame ?? true;
        var button = PrimaryButton(update ? GetString(Resource.String.steam_download_update_button, target.Version)
            : GetString(state.Game != null ? Resource.String.steam_download_again_button : Resource.String.steam_download_button), "file_download");
        button.Enabled = owns;
        if (!owns)
            button.Alpha = 0.5f;
        button.Click += (_, _) =>
        {
            if (state.Game == null)
            {
                StartDownload();
                return;
            }
            new MaterialAlertDialogBuilder(this)
                .SetTitle(update ? GetString(Resource.String.steam_download_update_title, target.Version) : GetString(Resource.String.steam_download_again_title))!
                .SetMessage(update ? GetString(Resource.String.steam_download_update_message, target.Version, state.Game.Version)
                    : GetString(Resource.String.steam_download_again_message, state.Game.Version))!
                .SetNegativeButton(Resource.String.cancel, (Android.Content.IDialogInterfaceOnClickListener?)null)!
                .SetPositiveButton(update ? GetString(Resource.String.steam_download_update_button, target.Version) : GetString(Resource.String.steam_download_again_button), (_, _) => StartDownload())!
                .Show();
        };
        _content.AddView(button, ButtonParams(16));
        if (!owns)
            _content.AddView(Note(GetString(Resource.String.steam_download_not_owned)), UiKit.MatchWrap(this, 12));
    }

    /// <summary>本體分塊並行數：照 STS2 的 1／2／4；下載中這張卡不會出現（換成進度面板），新的值下次下載才用</summary>
    private void PickChunks()
    {
        var values = SteamDownloadSettings.Options;
        int current = SteamDownloadSettings.ConcurrentChunks(this);
        // 單選圓點在這個主題下看不到，目前的值直接寫在選項後面
        string[] labels = values.Select((v, i) => GetString(i switch
        {
            0 => Resource.String.steam_download_chunks_option_1,
            1 => Resource.String.steam_download_chunks_option_2,
            _ => Resource.String.steam_download_chunks_option_4,
        }) + (v == current ? GetString(Resource.String.steam_download_chunks_current) : "")).ToArray();
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.steam_download_chunks_title)!
            .SetItems(labels, (_, e) =>
            {
                SteamDownloadSettings.SetConcurrentChunks(this, values[e.Which]);
                LauncherLog.Write($"steam download concurrent chunks = {values[e.Which]}");
                Render();
            })!
            .SetNegativeButton(Resource.String.cancel, (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    private void StartDownload()
    {
        if (GameState.Load(this).Install.Running)
        {
            UiKit.Toast(this, GetString(Resource.String.install_busy));
            return;
        }
        LauncherLog.Write("steam download requested from Steam page");
        _downloadRequestedAt = DateTime.UtcNow;
        MainActivity.SendInstall(this, "steam-download");
        Render();
    }

    /// <summary>下載中：每秒讀 state.json，速度用最近 5 秒的位元組差算；下載完進到安裝步驟時改顯示第幾步</summary>
    private void RenderProgressPanel(InstallStatus first)
    {
        var card = UiKit.Vertical(this);
        card.Background = UiKit.RoundedFill(this, Palette.SurfaceContainer, 24);
        int pad = UiKit.Dp(this, 20);
        card.SetPadding(pad, pad, pad, pad);

        var header = UiKit.Horizontal(this);
        header.SetGravity(GravityFlags.CenterVertical);
        header.AddView(UiKit.Text(this, GetString(Resource.String.steam_download_panel_title), 18, Palette.OnSurface, TypefaceStyle.Bold).SingleLine(), UiKit.Weighted(this, 1f));
        var phase = UiKit.Text(this, "", 12, Palette.OnSecondaryContainer, TypefaceStyle.Bold);
        phase.Background = UiKit.RoundedFill(this, Palette.SecondaryContainer, 100);
        phase.SetPadding(UiKit.Dp(this, 12), UiKit.Dp(this, 4), UiKit.Dp(this, 12), UiKit.Dp(this, 4));
        header.AddView(phase);
        card.AddView(header);

        var speed = UiKit.Text(this, "", 14, Palette.Primary, TypefaceStyle.Bold);
        card.AddView(speed, UiKit.MatchWrap(this, 16));

        var stats = UiKit.Horizontal(this);
        stats.Background = UiKit.RoundedFill(this, Palette.SurfaceContainerHigh, 16);
        stats.SetPadding(UiKit.Dp(this, 16), UiKit.Dp(this, 12), UiKit.Dp(this, 16), UiKit.Dp(this, 12));
        TextView Stat(int label)
        {
            var col = UiKit.Vertical(this);
            col.AddView(UiKit.Text(this, GetString(label), 12, Palette.OnSurfaceVariant));
            var value = UiKit.Text(this, "—", 15, Palette.OnSurface, TypefaceStyle.Bold).SingleLine();
            col.AddView(value, UiKit.MatchWrap(this, 4));
            stats.AddView(col, UiKit.Weighted(this, 1f));
            return value;
        }
        var transferred = Stat(Resource.String.steam_download_transferred);
        var eta = Stat(Resource.String.steam_download_eta);
        card.AddView(stats, UiKit.MatchWrap(this, 12));

        var progressLabel = UiKit.Text(this, "", 13, Palette.OnSurfaceVariant);
        card.AddView(progressLabel, UiKit.MatchWrap(this, 16));
        var bar = new LinearProgressIndicator(this) { Max = 1000 };
        bar.SetIndicatorColor(Palette.Primary);
        bar.TrackColor = Palette.SurfaceContainerHigh;
        card.AddView(bar, UiKit.MatchWrap(this, 8));

        var stop = PrimaryButton(GetString(Resource.String.steam_download_stop), "close");
        stop.BackgroundTintList = ColorStateList.ValueOf(Palette.SecondaryContainer);
        stop.SetTextColor(Palette.OnSecondaryContainer);
        stop.IconTint = ColorStateList.ValueOf(Palette.OnSecondaryContainer);
        stop.Click += (_, _) =>
        {
            LauncherLog.Write("steam download stop requested");
            MainActivity.SendInstall(this, "cancel");
            stop.Enabled = false;
        };
        card.AddView(stop, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(this, 48)) { TopMargin = UiKit.Dp(this, 20) });
        _content.AddView(card, UiKit.MatchWrap(this, 16));

        var samples = new Queue<(DateTime At, long Bytes)>();
        void Show(InstallStatus st)
        {
            if (st.Step == "download" && st.TotalBytes > 0)
            {
                var now = DateTime.UtcNow;
                samples.Enqueue((now, st.DownloadedBytes));
                while (samples.Count > 1 && (now - samples.Peek().At).TotalSeconds > 5)
                    samples.Dequeue();
                var (t0, b0) = samples.Peek();
                double secs = (now - t0).TotalSeconds;
                double rate = secs >= 1 ? (st.DownloadedBytes - b0) / secs : 0;
                int percent = (int)Math.Clamp(100L * st.DownloadedBytes / st.TotalBytes, 0, 100);
                phase.Text = GetString(Resource.String.steam_download_phase_download);
                speed.Text = rate > 0 ? GetString(Resource.String.steam_download_speed_format, Formatter.FormatFileSize(this, (long)rate)) : GetString(Resource.String.steam_download_speed_unknown);
                transferred.Text = $"{Formatter.FormatShortFileSize(this, st.DownloadedBytes)} / {Formatter.FormatShortFileSize(this, st.TotalBytes)}";
                eta.Text = rate > 0 ? Duration((st.TotalBytes - st.DownloadedBytes) / rate) : "—";
                progressLabel.Text = GetString(Resource.String.steam_download_progress_format, percent);
                bar.Indeterminate = false;
                bar.SetProgressCompat((int)Math.Min(1000, 1000L * st.DownloadedBytes / st.TotalBytes), true);
            }
            else if (st.Step is { } step && st.Count > 0)
            {
                var name = Pages.GamePage.StepNames.TryGetValue(step, out var res) ? GetString(res) : step;
                phase.Text = GetString(Resource.String.steam_download_phase_install);
                speed.Text = GetString(Resource.String.steam_download_install_step_format, st.Index, st.Count, name);
                if (st.TotalBytes > 0)
                    transferred.Text = Formatter.FormatShortFileSize(this, st.TotalBytes);
                eta.Text = "—";
                progressLabel.Text = "";
                bar.Indeterminate = true;
            }
            else
            {
                phase.Text = GetString(Resource.String.steam_download_phase_connect);
                speed.Text = GetString(Resource.String.steam_download_speed_unknown);
                progressLabel.Text = "";
                bar.Indeterminate = true;
            }
        }
        Show(first);

        _ticker = new Handler(Looper.MainLooper!);
        void Tick()
        {
            var st = GameState.Load(this).Install;
            if (!Installing(st))
            {
                Render();
                return;
            }
            Show(st);
            _ticker!.PostDelayed(Tick, 1000);
        }
        _ticker.PostDelayed(Tick, 1000);
    }

    private string Duration(double seconds) => seconds >= 60
        ? GetString(Resource.String.duration_minutes_format, (int)Math.Ceiling(seconds / 60))
        : GetString(Resource.String.duration_seconds_format, Math.Max(1, (int)Math.Ceiling(seconds)));

    private string DlcText(int format, SteamAccountStore.Ownership own)
    {
        var (owned, supported) = TargetDlc.Counts(this, own, SupportedTarget.Latest.Id);
        return GetString(format, owned, supported);
    }

    private TextView Note(string text)
    {
        var t = UiKit.Text(this, text, 14, Palette.OnSurfaceVariant);
        t.SetLineSpacing(UiKit.Dp(this, 4), 1f);
        t.SetPadding(UiKit.Dp(this, 4), 0, UiKit.Dp(this, 4), 0);
        return t;
    }

    // ── 帳號 ─────────────────────────────────────────────────

    private void RenderAccount()
    {
        var card = UiKit.Horizontal(this);
        card.SetGravity(GravityFlags.CenterVertical);
        card.Background = UiKit.RoundedFill(this, Palette.SecondaryContainer, 24);
        card.SetPadding(UiKit.Dp(this, 20), UiKit.Dp(this, 20), UiKit.Dp(this, 20), UiKit.Dp(this, 20));
        var avatar = new FrameLayout(this) { Background = UiKit.RoundedFill(this, Palette.PrimaryTint, 100) };
        avatar.AddView(UiKit.Icon(this, "person", Palette.Primary, 28), new FrameLayout.LayoutParams(UiKit.Dp(this, 28), UiKit.Dp(this, 28), GravityFlags.Center));
        card.AddView(avatar, new LinearLayout.LayoutParams(UiKit.Dp(this, 52), UiKit.Dp(this, 52)));
        var texts = UiKit.Vertical(this);
        texts.AddView(UiKit.Text(this, GetString(Resource.String.steam_account_logged_in_as), 13, Palette.OnSecondaryContainer));
        texts.AddView(UiKit.Text(this, SteamAccountStore.AccountName(this) ?? "", 20, Palette.OnSurface, TypefaceStyle.Bold).SingleLine(), UiKit.MatchWrap(this, 6));
        card.AddView(texts, UiKit.Weighted(this, 1f, 16));
        _content.AddView(card, UiKit.MatchWrap(this));

        _content.AddView(Widgets.SectionLabel(this, GetString(Resource.String.steam_license_title)), UiKit.MatchWrap(this, 24));
        var license = Widgets.Card(this);
        var own = SteamAccountStore.LoadOwnership(this);
        if (own == null)
        {
            license.AddView(Widgets.ListItem(this, "error", GetString(Resource.String.steam_license_owns_game), GetString(Resource.String.steam_license_not_checked), null, circleIcon: false));
        }
        else
        {
            license.AddView(Widgets.ListItem(this, own.OwnsGame ? "check_circle" : "block", GetString(Resource.String.steam_license_owns_game),
                GetString(own.OwnsGame ? Resource.String.steam_license_owned : Resource.String.steam_license_not_owned), null, circleIcon: false));
            license.AddView(Widgets.ListItem(this, "extension", DlcText(Resource.String.steam_license_dlc_format, own),
                GetString(Resource.String.steam_license_checked_format, own.CheckedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm")), null, circleIcon: false));
        }
        _content.AddView(license, UiKit.MatchWrap(this, 10));

        _status = StatusText();
        _content.AddView(_status, UiKit.MatchWrap(this, 16));

        _primaryButton = PrimaryButton(GetString(Resource.String.steam_recheck), "refresh");
        _primaryButton.Click += (_, _) =>
        {
            if (Busy)
                _cts!.Cancel();
            else
                _ = RecheckAsync();
        };
        _content.AddView(_primaryButton, ButtonParams(16));

        var logout = Widgets.PillButton(this, "close", GetString(Resource.String.steam_logout), ConfirmLogout);
        _content.AddView(logout, ButtonParams(12));
    }

    private void ConfirmLogout()
    {
        new MaterialAlertDialogBuilder(this)
            .SetTitle(Resource.String.steam_logout_confirm_title)!
            .SetMessage(Resource.String.steam_logout_confirm_message)!
            .SetNegativeButton(Resource.String.cancel, (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton(Resource.String.steam_logout, (_, _) =>
            {
                _cts?.Cancel();
                bool complete = SteamAccountStore.Clear(this);
                LauncherLog.Write($"steam logged out (complete={complete})");
                Render();
                if (!complete)
                    RevokeNotice.Show(this);
            })!
            .Show();
    }

    private async Task RecheckAsync()
    {
        await RunAsync(async (session, access, ct) =>
        {
            // 先拿世代再讀登入（Terra 第 13 輪）：讀到的一定是這一代的登入。中途換了登入或登出就讀不到，什麼都不清；
            // 是這一代的登入、token 卻解不開（Keystore 的金鑰不見了）才清，而且跟 token 失效一樣受世代保護
            var login = SteamAccountStore.Load(this, access, out var loginKey);
            if (login == null)
            {
                if (SteamAccountStore.HasLoginOf(this, access) && !SteamAccountStore.ClearIfCurrent(this, access))
                {
                    RevokeNotice.ShowWhenVisible();
                    throw new SteamException(SteamAccountStore.RevokeIncompleteMessage);
                }
                return;
            }
            SetStatus(Resource.String.steam_status_signing_in);
            var result = await session.LogOnAsync(login, ct);
            access.ThrowIfStale();
            if (result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked or EResult.InvalidSignature)
            {
                if (!SteamAccountStore.ClearIfCurrent(this, access))
                {
                    RevokeNotice.ShowWhenVisible();
                    throw new SteamException(SteamAccountStore.RevokeIncompleteMessage);
                }
                throw new SteamException(GetString(Resource.String.steam_status_token_expired));
            }
            if (result != EResult.OK)
                throw new SteamException(GetString(Resource.String.steam_login_failed_format, result.ToString()));
            SetStatus(Resource.String.steam_status_checking_license);
            var ownership = await session.CheckOwnershipAsync(ct);
            var identity = await session.GetIdentityAsync(ct);
            SteamAccountStore.SaveGameIdentity(this, identity, access, loginKey!);
            SteamAccountStore.SaveOwnership(this, ownership, identity.SteamId, access, loginKey!);
        });
    }

    // ── 沒登入 ───────────────────────────────────────────────

    private void RenderLogin()
    {
        _content.AddView(Widgets.Segmented(this,
            new[] { GetString(Resource.String.steam_login_tab_password), GetString(Resource.String.steam_login_tab_qr) },
            _loginTab, i =>
            {
                if (i == _loginTab)
                    return;
                _cts?.Cancel();
                _loginTab = i;
                Render();
            }), UiKit.MatchWrap(this));

        if (_loginTab == 0)
        {
            // 帳號名稱不能用 VisiblePassword 擋選字：Bitwarden 這類密碼管理器會把它也當成密碼欄，兩格都填密碼
            _username = Field(GetString(Resource.String.steam_username_hint), InputTypes.ClassText | InputTypes.TextFlagNoSuggestions, View.AutofillHintUsername);
            _content.AddView(_username, FieldParams(20));
            _password = Field(GetString(Resource.String.steam_password_hint), InputTypes.ClassText | InputTypes.TextVariationPassword, View.AutofillHintPassword);
            _content.AddView(_password, FieldParams(12));
            _status = StatusText();
            _content.AddView(_status, UiKit.MatchWrap(this, 12));
            _primaryButton = PrimaryButton(GetString(Resource.String.steam_login_button), "login");
            _primaryButton.Click += (_, _) =>
            {
                if (Busy)
                {
                    _cts!.Cancel();
                    return;
                }
                string user = _username!.Text?.Trim() ?? "", pass = _password!.Text ?? "";
                if (user.Length == 0 || pass.Length == 0)
                    return;
                // 密碼從欄位拿出來就清掉，之後只存在這次登入的記憶體裡
                _password.Text = "";
                _ = LoginAsync((session, ct) => session.LoginWithCredentialsAsync(DeviceName, user, pass, new UiAuthenticator(this, s => SetStatusText(s)), ct));
            };
            _content.AddView(_primaryButton, ButtonParams(16));
        }
        else
        {
            var qrBox = new FrameLayout(this) { Background = UiKit.RoundedFill(this, Color.White, 20) };
            int pad = UiKit.Dp(this, 12);
            qrBox.SetPadding(pad, pad, pad, pad);
            _qrImage = new ImageView(this);
            _qrImage.SetScaleType(ImageView.ScaleType.FitCenter);
            qrBox.AddView(_qrImage, new FrameLayout.LayoutParams(UiKit.Dp(this, 220), UiKit.Dp(this, 220)));
            var qrWrap = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = UiKit.Dp(this, 24), Gravity = GravityFlags.CenterHorizontal };
            _content.AddView(qrBox, qrWrap);
            _status = StatusText();
            _status.Gravity = GravityFlags.Center;
            _content.AddView(_status, UiKit.MatchWrap(this, 16));
            _primaryButton = PrimaryButton(GetString(Resource.String.steam_login_cancel), "close");
            _primaryButton.Click += (_, _) =>
            {
                if (Busy)
                    _cts!.Cancel();
                else
                    Render();
            };
            _content.AddView(_primaryButton, ButtonParams(16));
            var hint = UiKit.Text(this, GetString(Resource.String.steam_qr_hint), 13, Palette.OnSurfaceVariant);
            hint.SetLineSpacing(UiKit.Dp(this, 4), 1f);
            _content.AddView(hint, UiKit.MatchWrap(this, 16));
            _ = LoginAsync((session, ct) => session.LoginWithQrAsync(DeviceName,
                url => RunOnUiThread(() => _qrImage?.SetImageBitmap(QrBitmap.Render(url))), ct));
        }

        var note = UiKit.Text(this, GetString(Resource.String.steam_login_security_note), 12, Palette.OnSurfaceVariant);
        note.SetLineSpacing(UiKit.Dp(this, 3), 1f);
        note.Alpha = 0.8f;
        _content.AddView(note, UiKit.MatchWrap(this, 24));
    }

    private async Task LoginAsync(Func<SteamSession, CancellationToken, Task<SteamLogin>> authenticate)
    {
        await RunAsync(async (session, access, ct) =>
        {
            SetStatus(_loginTab == 1 ? Resource.String.steam_status_waiting_qr : Resource.String.steam_status_signing_in);
            var login = await authenticate(session, ct);
            // 登入途中撤回同意或登出（世代換了）：不寫，token 不會被寫回去。
            // 之後寫身分與授權都帶這一次登入的識別：同一世代又存了別的登入（另一個登入頁），就不寫（Terra 第 11 輪）
            var loginKey = SteamAccountStore.Save(this, login, access);
            LauncherLog.Write("steam login saved");
            SetStatus(Resource.String.steam_status_signing_in);
            var result = await session.LogOnAsync(login, ct);
            access.ThrowIfStale();
            if (result != EResult.OK)
                throw new SteamException(GetString(Resource.String.steam_login_failed_format, result.ToString()));
            SetStatus(Resource.String.steam_status_checking_license);
            var ownership = await session.CheckOwnershipAsync(ct);
            var identity = await session.GetIdentityAsync(ct);
            SteamAccountStore.SaveGameIdentity(this, identity, access, loginKey);
            SteamAccountStore.SaveOwnership(this, ownership, identity.SteamId, access, loginKey);
        });
    }

    /// <summary>
    /// 開一條 SteamSession 跑 work：連線、保活、取消、錯誤顯示、做完重畫。
    /// 開始時記下存取世代（SteamAccess）：登出、撤回同意會取消整段，寫入也會被擋下。
    /// </summary>
    private async Task RunAsync(Func<SteamSession, SteamAccessScope, CancellationToken, Task> work)
    {
        if (Busy)
            return;
        SteamAccessScope access;
        try
        {
            access = SteamAccess.Begin(this);
        }
        catch (Exception e)
        {
            LauncherLog.Write($"steam access generation unreadable: {e.GetType().Name}");
            SetStatusText(GetString(Resource.String.steam_status_failed_format, e.GetType().Name));
            return;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(access.Token);
        _cts.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = _cts.Token;
        SetBusyUi(true);
        SteamKeepAliveService.Start(this);
        bool ok = false;
        try
        {
            await Task.Run(async () =>
            {
                await using var session = new SteamSession(LauncherLog.Write);
                SetStatus(Resource.String.steam_status_connecting);
                await session.ConnectAsync(ct);
                await work(session, access, ct);
            }, ct);
            ok = true;
        }
        catch (System.OperationCanceledException)
        {
            LauncherLog.Write("steam action cancelled");
            SetStatus(Resource.String.steam_status_cancelled);
        }
        catch (Exception e)
        {
            LauncherLog.Write($"steam action failed: {e.GetType().Name}: {e.Message}");
            SetStatusText(GetString(Resource.String.steam_status_failed_format, SteamErrors.Describe(this, e)));
        }
        finally
        {
            SteamKeepAliveService.Stop(this);
            _cts.Dispose();
            _cts = null;
            access.Dispose();
            if (!IsDestroyed)
            {
                // 成功，或登入狀態跟畫面不一致（登入頁拿到了 token、帳號頁的 token 失效被清掉）就重畫，失敗訊息留著
                bool showingLogin = _username != null || _qrImage != null;
                if (ok || SteamAccountStore.IsLoggedIn(this) == showingLogin)
                {
                    string? keep = _status?.Text;
                    Render();
                    if (!ok && _status != null)
                        _status.Text = keep;
                }
                else
                {
                    SetBusyUi(false);
                }
            }
        }
    }

    private void SetBusyUi(bool busy)
    {
        if (_primaryButton == null)
            return;
        _primaryButton.Text = busy ? GetString(Resource.String.steam_login_cancel)
            : SteamAccountStore.IsLoggedIn(this) ? GetString(Resource.String.steam_recheck)
            : _loginTab == 1 ? GetString(Resource.String.steam_qr_restart)
            : GetString(Resource.String.steam_login_button);
        if (_username != null)
            _username.Enabled = !busy;
        if (_password != null)
            _password.Enabled = !busy;
    }

    private void SetStatus(int res) => SetStatusText(GetString(res));

    private void SetStatusText(string text) => RunOnUiThread(() =>
    {
        if (_status != null)
            _status.Text = text;
    });

    // ── 元件 ─────────────────────────────────────────────────

    private TextView StatusText()
    {
        var t = UiKit.Text(this, "", 14, Palette.OnSurface);
        t.SetLineSpacing(UiKit.Dp(this, 4), 1f);
        t.SetPadding(UiKit.Dp(this, 4), 0, UiKit.Dp(this, 4), 0);
        return t;
    }

    private EditText Field(string hint, InputTypes type, string autofillHint)
    {
        var field = new EditText(this) { Hint = hint, InputType = type };
        field.SetAutofillHints(autofillHint);
        field.ImportantForAutofill = ImportantForAutofill.Yes;
        // 密碼類 InputType 會把字型換成等寬，兩格統一用預設字型
        field.Typeface = Typeface.Default;
        field.SetSingleLine(true);
        field.SetTextColor(Palette.OnSurface);
        field.SetHintTextColor(Palette.OnSurfaceVariant);
        field.SetTextSize(ComplexUnitType.Sp, 16);
        field.Background = UiKit.RoundedFill(this, Palette.SurfaceContainerHigh, 16);
        field.SetPadding(UiKit.Dp(this, 18), 0, UiKit.Dp(this, 18), 0);
        return field;
    }

    private LinearLayout.LayoutParams FieldParams(int topDp) =>
        new(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(this, 56)) { TopMargin = UiKit.Dp(this, topDp) };

    private LinearLayout.LayoutParams ButtonParams(int topDp) =>
        new(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(this, 56)) { TopMargin = UiKit.Dp(this, topDp) };

    private MaterialButton PrimaryButton(string text, string glyph)
    {
        var button = new MaterialButton(this) { Text = text };
        button.SetAllCaps(false);
        button.SetTextSize(ComplexUnitType.Sp, 16);
        button.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        button.SetTextColor(Palette.OnPrimary);
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.Icon = UiKit.Symbol(this, glyph, Palette.OnPrimary, 22);
        button.IconTint = ColorStateList.ValueOf(Palette.OnPrimary);
        button.IconSize = UiKit.Dp(this, 22);
        button.IconGravity = MaterialButton.IconGravityTextStart;
        button.IconPadding = UiKit.Dp(this, 8);
        button.BackgroundTintList = ColorStateList.ValueOf(Palette.Primary);
        button.RippleColor = ColorStateList.ValueOf(Color.Argb(96, 255, 255, 255));
        button.ShapeAppearanceModel = ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(UiKit.Dp(this, 28)).Build();
        PressScale.Attach(button);
        return button;
    }
}
