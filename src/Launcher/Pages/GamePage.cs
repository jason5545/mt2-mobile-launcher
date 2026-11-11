// 版面與動畫移植自 Sts2MobileLauncher e568614（https://github.com/ModinMobileSTS/Sts2MobileLauncher），MIT；授權全文見 THIRD_PARTY_NOTICES.md。
using Android.Content;
using Android.OS;
using Android.Content.Res;
using Android.Graphics;
using Android.Text.Format;
using Android.Util;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Shape;
using Mt2Mobile.Launcher.SteamUi;
using Mt2Mobile.Launcher.Ui;

namespace Mt2Mobile.Launcher.Pages;

/// <summary>
/// 遊戲頁，版面照 Sts2MobileLauncher 的 GamePage：頁首（標題、Steam chip）、主卡片（版本與啟動按鈕）、兩張統計卡、「遊戲維護」「進階工具」各一排四格。
/// MT2 的差別：統計卡左邊是 Steam 授權（STS2 是模組狀態）；維護格的「全解鎖」換成「重新轉換」；進階格的「啟動設定檔」換成「Steam 下載」。
/// 主卡片依安裝狀態（GameState.Install）分四種：安裝中（進度、停止）、就緒（啟動遊戲）、沒裝完（繼續安裝）、沒有本體（匯入資料夾）。
/// 安裝中每秒讀一次 state.json 更新進度，裝完（服務放掉 install.lock）就重建整頁。
/// </summary>
public sealed class GamePage(Context activity, ILauncherHost host)
{
    private readonly Context _a = activity;
    private readonly ILauncherHost _h = host;

    private record ActionSpec(string Glyph, int Label, bool Highlight, Action OnClick);

    internal static readonly Dictionary<string, int> StepNames = new()
    {
        ["copy"] = Resource.String.install_step_copy, ["patch"] = Resource.String.install_step_patch, ["export"] = Resource.String.install_step_export,
        ["import"] = Resource.String.install_step_import, ["shaders"] = Resource.String.install_step_shaders,
        ["download"] = Resource.String.install_step_download,
    };

    /// <summary>服務已經拿到鎖，或剛送出開始的 10 秒內（:install 程序還在起來）</summary>
    private bool Installing(GameState state) => state.Install.Running || (DateTime.UtcNow - _h.InstallRequestedAt).TotalSeconds < 10;

    public View Build()
    {
        var state = GameState.Load(_a);
        var shell = UiKit.Vertical(_a);
        shell.SetBackgroundColor(Palette.Background);
        shell.AddView(UiKit.TopAppBar(_a, _a.GetString(Resource.String.game_home_title), BuildSteamChip()),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var content = UiKit.Vertical(_a);
        content.SetClipToPadding(false);
        content.SetPadding(0, 0, 0, UiKit.Dp(_a, 32));
        bool stale = Stale(state);
        content.AddView(BuildHeroCard(state, stale), UiKit.MatchWrap(_a));
        content.AddView(BuildStatusRow(state), UiKit.MatchWrap(_a, 20));
        content.AddView(SectionTitle(Resource.String.game_maintenance_section), UiKit.MatchWrap(_a, 20));
        content.AddView(BuildActionRow(MaintenanceActions(state, stale), 4), UiKit.MatchWrap(_a, 12));
        content.AddView(SectionTitle(Resource.String.game_advanced_tools_section), UiKit.MatchWrap(_a, 20));
        content.AddView(BuildActionRow(AdvancedActions(), 4), UiKit.MatchWrap(_a, 12));
        shell.AddView(UiKit.ResponsiveScroll(_a, content), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return shell;
    }

    private ActionSpec[] MaintenanceActions(GameState state, bool stale) => new ActionSpec[]
    {
        new("unarchive", Resource.String.game_action_import_body, !state.Ready && state.Install.Resumable == null, _h.ImportGame),
        new("file_download", Resource.String.import_save, false, () => _h.NotReady(_a.GetString(Resource.String.import_save))),
        new("file_upload", Resource.String.export_save, false, () => _h.NotReady(_a.GetString(Resource.String.export_save))),
        new("restart_alt", Resource.String.game_action_reinstall, stale, () => ConfirmReconvert(state)),
    };

    /// <summary>重新轉換：從匯出重跑（約十幾分鐘，期間不能玩），先問一次</summary>
    private void ConfirmReconvert(GameState state)
    {
        if (!state.Ready || Installing(state))
        {
            UiKit.Toast(_a, _a.GetString(state.Install.Running ? Resource.String.install_busy : Resource.String.reconvert_no_game));
            return;
        }
        new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(_a)
            .SetTitle(Resource.String.game_action_reinstall)!
            .SetMessage(_a.GetString(Resource.String.reconvert_message_format, ConversionCheck.EstimateMinutes(_a, state.Game!.TargetId)))!
            .SetPositiveButton(Resource.String.reconvert_start, (_, _) => _h.StartInstall("reconvert"))!
            .SetNegativeButton(Resource.String.cancel, (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    /// <summary>就緒時看轉換過期了沒有（ConversionCheck，背景算，第一次回 null；算完有過期就重建這一頁）</summary>
    private bool Stale(GameState state) => state.Ready && !Installing(state)
        && ConversionCheck.Stale(_a, state.Game!.TargetId, () => _h.OpenTab(MainActivity.TabGame)) is { Length: > 0 };

    private ActionSpec[] AdvancedActions() => new ActionSpec[]
    {
        new("folder_open", Resource.String.game_action_browse_files, false, () => _h.NotReady(_a.GetString(Resource.String.game_action_browse_files))),
        new("receipt_long", Resource.String.view_logs, false, () => LogViewer.Show(_a)),
        new("file_download", Resource.String.game_action_steam_download, false, _h.StartSteamDownload),
        new("settings", Resource.String.tab_settings, false, () => _h.SelectTab(MainActivity.TabSettings)),
    };

    private View BuildSteamChip()
    {
        string? account = SteamAccountStore.IsLoggedIn(_a) ? SteamAccountStore.AccountName(_a) : null;
        bool loggedIn = account != null;
        var fg = loggedIn ? Palette.OnSecondaryContainer : Palette.OnSurfaceVariant;
        var chip = UiKit.Horizontal(_a);
        chip.SetGravity(GravityFlags.Center);
        chip.SetPadding(UiKit.Dp(_a, 14), UiKit.Dp(_a, 6), UiKit.Dp(_a, 14), UiKit.Dp(_a, 6));
        chip.SetMinimumHeight(UiKit.Dp(_a, 32));
        chip.Clickable = true;
        chip.Focusable = true;
        chip.Background = UiKit.RoundedFill(_a, loggedIn ? Palette.SecondaryContainer : Palette.SurfaceContainerHigh, 100);
        chip.Foreground = UiKit.Ripple(_a, 100);
        chip.Click += (_, _) => _h.OpenSteamAccount();
        chip.AddView(UiKit.Icon(_a, loggedIn ? "cloud_done" : "login", fg, 18));
        var label = UiKit.Text(_a, account ?? _a.GetString(Resource.String.game_steam_login), 13, fg, TypefaceStyle.Bold).SingleLine();
        label.SetMaxWidth(UiKit.Dp(_a, 128));
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        lp.MarginStart = UiKit.Dp(_a, 6);
        chip.AddView(label, lp);
        return chip;
    }

    private View BuildHeroCard(GameState state, bool stale)
    {
        bool installing = Installing(state);
        FrameLayout card;
        if (state.Ready && !installing)
        {
            card = new AnimatedHeroCard(_a);
        }
        else
        {
            card = new FrameLayout(_a);
            var bg = UiKit.RoundedFill(_a, Palette.SurfaceContainer, 24);
            bg.SetStroke(UiKit.Dp(_a, 2), Palette.Divider, UiKit.Dp(_a, 8), UiKit.Dp(_a, 6));
            card.Background = bg;
            card.ClipToOutline = true;
        }
        var content = UiKit.Vertical(_a);
        int pad = UiKit.Dp(_a, 24);
        content.SetPadding(pad, pad, pad, pad);
        card.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        if (installing)
        {
            BuildInstalling(content, state);
            return card;
        }

        var muted = Color.Argb(179, 255, 255, 255);
        bool ready = state.Ready;
        bool resumable = !ready && state.Install.Resumable != null;
        string title = ready ? _a.GetString(Resource.String.game_hero_ready_title_format, "v" + state.Game!.Version)
            : resumable ? _a.GetString(Resource.String.install_resume_title)
            : _a.GetString(Resource.String.game_hero_missing_title);
        content.AddView(UiKit.Text(_a, title, 18, ready ? Palette.OnPrimaryContainer : Palette.OnSurface, TypefaceStyle.Bold).SingleLine());

        // 上一次安裝沒成功（不支援的版本、空間不夠、找不到 _Data…）就把原因放在副標題
        string? failure = state.Install.State is "failed" or "unsupported" or "no-space" or "idle" or "timeout" && !string.IsNullOrEmpty(state.Install.Message)
            ? _a.GetString(Resource.String.install_failed_format, state.Install.Message) : null;
        var sub = UiKit.Horizontal(_a);
        sub.SetGravity(GravityFlags.CenterVertical);
        sub.AddView(UiKit.Icon(_a, ready ? "check_circle" : "error", ready ? muted : Palette.OnSurfaceVariant, 16));
        // 裝的不是最新的 target（APK 加了新版本）：舊版照樣能開，下面多一個用 Steam 下載更新
        bool outdated = ready && state.Game!.TargetId != SupportedTarget.Latest.Id;
        string subText = ready ? Formatter.FormatFileSize(_a, state.Game!.Bytes) + " · OpenGL ES 3.0" + (stale ? "\n" + _a.GetString(Resource.String.conversion_stale) : "")
                + (outdated ? "\n" + _a.GetString(Resource.String.game_update_available_format, "v" + SupportedTarget.Latest.Version) : "")
            : failure ?? _a.GetString(resumable ? Resource.String.install_resume_subtitle : Resource.String.game_hero_missing_subtitle);
        var subLabel = UiKit.Text(_a, subText, 13, ready ? muted : Palette.OnSurfaceVariant);
        subLabel.SetMaxLines(6);
        sub.AddView(subLabel, UiKit.Weighted(_a, 1f, 6));
        content.AddView(sub, UiKit.MatchWrap(_a, 6));

        var button = ready ? HeroButton(_a.GetString(Resource.String.launch_game), "play_arrow", _h.LaunchGame)
            : resumable ? HeroButton(_a.GetString(Resource.String.install_resume), "restart_alt", () => _h.StartInstall("start"))
            : HeroButton(_a.GetString(Resource.String.game_hero_steam_download), "file_download", _h.StartSteamDownload);
        var bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(_a, 56)) { TopMargin = UiKit.Dp(_a, 24) };
        content.AddView(button, bp);
        // 轉換過期（APK 更新後指紋對不上）：啟動遊戲照樣可以（PNG 的路保留），下面多一個重新轉換
        if (ready && stale)
            content.AddView(HeroButton(_a.GetString(Resource.String.game_action_reinstall), "restart_alt", () => ConfirmReconvert(state), tonal: true),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(_a, 48)) { TopMargin = UiKit.Dp(_a, 12) });
        if (outdated)
            content.AddView(HeroButton(_a.GetString(Resource.String.game_action_update_format, "v" + SupportedTarget.Latest.Version), "file_download", _h.StartSteamDownload, tonal: true),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(_a, 48)) { TopMargin = UiKit.Dp(_a, 12) });
        // 沒有遊戲：主按鈕是 Steam 下載（開 Steam 頁的下載分頁），匯入資料夾是第二個
        if (!ready && !resumable)
            content.AddView(HeroButton(_a.GetString(Resource.String.install_pick_folder), "create_new_folder", _h.ImportGame, tonal: true),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, UiKit.Dp(_a, 48)) { TopMargin = UiKit.Dp(_a, 12) });
        return card;
    }

    /// <summary>安裝中：步驟、進度條、兩個停止鍵；每秒更新，服務放掉鎖就重建整頁</summary>
    private void BuildInstalling(LinearLayout content, GameState state)
    {
        content.AddView(UiKit.Text(_a, _a.GetString(Resource.String.install_running_title), 18, Palette.OnSurface, TypefaceStyle.Bold).SingleLine());
        var label = UiKit.Text(_a, "", 13, Palette.OnSurfaceVariant);
        content.AddView(label, UiKit.MatchWrap(_a, 6));
        var remain = UiKit.Text(_a, "", 13, Palette.OnSurfaceVariant);
        content.AddView(remain, UiKit.MatchWrap(_a, 2));
        var bar = new LinearProgressIndicator(_a) { Max = 1000 };
        bar.SetIndicatorColor(Palette.Primary);
        bar.TrackColor = Palette.SurfaceContainerHigh;
        content.AddView(bar, UiKit.MatchWrap(_a, 16));

        var row = UiKit.Horizontal(_a);
        row.AddView(HeroButton(_a.GetString(Resource.String.install_cancel), "block", () => _h.StartInstall("cancel"), tonal: true),
            new LinearLayout.LayoutParams(0, UiKit.Dp(_a, 48), 1f));
        var stop = HeroButton(_a.GetString(Resource.String.install_stop_now), "close", () => _h.StartInstall("stop-now"), tonal: true);
        row.AddView(stop, new LinearLayout.LayoutParams(0, UiKit.Dp(_a, 48), 1f) { MarginStart = UiKit.Dp(_a, 12) });
        content.AddView(row, UiKit.MatchWrap(_a, 20));

        void Show(InstallStatus st)
        {
            if (st.Step is { } step && st.Count > 0)
            {
                if (step == "download")
                {
                    var percent = Math.Clamp((int)(100L * st.Index / st.Count), 0, 100);
                    label.Text = _a.GetString(Resource.String.install_download_progress_format, percent);
                    remain.Text = _a.GetString(Resource.String.install_step_download);
                    bar.SetProgressCompat(percent * 10, true);
                    return;
                }
                var name = StepNames.TryGetValue(step, out var res) ? _a.GetString(res) : step;
                label.Text = _a.GetString(Resource.String.install_step_format, st.Index, st.Count, name, (int)st.StepElapsed);
                // 整個安裝還剩多久：這一步剩下的加上之後的步驟。這一步已經超過預估時，剩下的先算成預估的一成，並且說明
                bool over = st.EstSeconds > 0 && st.StepElapsed > st.EstSeconds;
                double left = (over ? 0.1 * st.EstSeconds : st.EstSeconds - st.StepElapsed) + st.EstAfter;
                remain.Text = _a.GetString(over ? Resource.String.install_remaining_over_format : Resource.String.install_remaining_format, Duration(left));
                // 進度條照時間：已經花的 ÷（已經花的＋還剩的），最多 99%
                double total = st.TotalElapsed + left;
                bar.SetProgressCompat(total > 0 ? (int)Math.Min(990, 1000 * st.TotalElapsed / total) : 0, true);
            }
            else
            {
                label.Text = _a.GetString(Resource.String.install_starting);
                remain.Text = "";
                bar.Indeterminate = true;
            }
        }
        Show(state.Install);

        var handler = new Handler(Looper.MainLooper!);
        bool attached = true;
        content.ViewDetachedFromWindow += (_, _) => attached = false;
        void Tick()
        {
            if (!attached) return;
            var now = GameState.Load(_a);
            if (!Installing(now))
            {
                _h.OpenTab(MainActivity.TabGame);
                return;
            }
            bar.Indeterminate = false;
            Show(now.Install);
            handler.PostDelayed(Tick, 1000);
        }
        handler.PostDelayed(Tick, 1000);
    }

    /// <summary>剩餘時間的說法：一分鐘以上用分鐘（無條件進位），以內用秒</summary>
    private string Duration(double seconds) => seconds >= 60
        ? _a.GetString(Resource.String.duration_minutes_format, (int)Math.Ceiling(seconds / 60))
        : _a.GetString(Resource.String.duration_seconds_format, Math.Max(1, (int)Math.Ceiling(seconds)));

    private MaterialButton HeroButton(string text, string glyph, Action onClick, bool tonal = false)
    {
        var button = new MaterialButton(_a);
        button.SetAllCaps(false);
        button.Text = text;
        button.SetTextSize(ComplexUnitType.Sp, tonal ? 14 : 16);
        button.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        button.Gravity = GravityFlags.Center;
        button.SetMinWidth(0);
        button.SetMinHeight(0);
        button.SetMinimumHeight(0);
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.SetPadding(UiKit.Dp(_a, 16), 0, UiKit.Dp(_a, 16), 0);
        button.IconPadding = UiKit.Dp(_a, 8);
        button.IconGravity = MaterialButton.IconGravityTextStart;
        button.ShapeAppearanceModel = ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(UiKit.Dp(_a, 28)).Build();
        var fg = tonal ? Palette.OnSecondaryContainer : Palette.OnPrimary;
        button.SetTextColor(fg);
        SetIcon(button, glyph, fg);
        button.BackgroundTintList = ColorStateList.ValueOf(tonal ? Palette.SecondaryContainer : Palette.Primary);
        button.RippleColor = ColorStateList.ValueOf(Color.Argb(96, 255, 255, 255));
        PressScale.Attach(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void SetIcon(MaterialButton button, string glyph, Color color)
    {
        button.Icon = UiKit.Symbol(_a, glyph, color, 24);
        // 著色要明確給：設成 null 時 MaterialButton 會把 drawable 自己的著色也清掉，vector 變回白色
        button.IconTint = ColorStateList.ValueOf(color);
        button.IconSize = UiKit.Dp(_a, 24);
    }

    private View BuildStatusRow(GameState state)
    {
        var row = UiKit.Horizontal(_a);
        row.BaselineAligned = false;
        row.SetGravity(GravityFlags.CenterVertical);

        string steamValue, steamSub;
        var own = SteamAccountStore.IsLoggedIn(_a) ? SteamAccountStore.LoadOwnership(_a) : null;
        if (own == null)
        {
            steamValue = _a.GetString(Resource.String.game_dashboard_steam_unknown);
            steamSub = _a.GetString(Resource.String.game_dashboard_steam_login_hint);
        }
        else
        {
            steamValue = _a.GetString(own.OwnsGame ? Resource.String.game_dashboard_steam_owned : Resource.String.game_dashboard_steam_not_owned);
            var (ownedDlc, supportedDlc) = TargetDlc.Counts(_a, own, state.Game?.TargetId ?? SupportedTarget.Latest.Id);
            steamSub = _a.GetString(Resource.String.game_dashboard_steam_dlc_format, ownedDlc, supportedDlc);
        }
        var steam = StatusCard("cloud_done", Resource.String.game_dashboard_steam_title, steamValue, steamSub);
        steam.Click += (_, _) => _h.OpenSteamAccount();

        var cloudStatus = SteamCloudStatusStore.Load(_a);
        string cloudValue = cloudStatus?.State switch
        {
            "syncing" => _a.GetString(Resource.String.cloud_status_syncing),
            "synced" => _a.GetString(Resource.String.cloud_status_synced),
            "conflict" => _a.GetString(Resource.String.cloud_status_conflict),
            "failed" => _a.GetString(Resource.String.cloud_status_failed),
            _ => _a.GetString(Resource.String.cloud_status_not_synced),
        };
        string cloudSub = cloudStatus?.State switch
        {
            "synced" when cloudStatus.LastSyncAtUtc is { } at => _a.GetString(
                Resource.String.cloud_status_synced_subtitle_format,
                at.ToLocalTime().ToString("HH:mm"), cloudStatus.CloudFiles, state.SaveFiles),
            "conflict" => _a.GetString(Resource.String.cloud_status_conflict_subtitle_format,
                cloudStatus.Reason, state.SaveFiles),
            "failed" => _a.GetString(Resource.String.cloud_status_failed_subtitle_format, state.SaveFiles),
            _ => _a.GetString(Resource.String.cloud_status_not_synced_subtitle_format,
                cloudStatus?.Reason ?? _a.GetString(Resource.String.cloud_status_login_hint), state.SaveFiles),
        };
        var cloud = StatusCard("cloud_sync", Resource.String.cloud_status_title, cloudValue, cloudSub);
        cloud.Click += (_, _) => _h.RequestCloudSync();

        row.AddView(steam, UiKit.Weighted(_a, 1f));
        row.AddView(cloud, UiKit.Weighted(_a, 1f, 12));
        return row;
    }

    private StatusCardView StatusCard(string glyph, int title, string value, string subText)
    {
        var card = new StatusCardView(_a);
        var watermark = UiKit.Icon(_a, glyph, Palette.Primary, 135);
        watermark.Alpha = 0.06f;
        watermark.Rotation = -15f;
        var wp = new FrameLayout.LayoutParams(UiKit.Dp(_a, 135), UiKit.Dp(_a, 135), GravityFlags.Bottom | GravityFlags.End);
        wp.SetMargins(0, 0, -UiKit.Dp(_a, 15), -UiKit.Dp(_a, 23));
        card.SetWatermark(watermark);
        card.AddView(watermark, wp);

        var content = UiKit.Vertical(_a);
        int pad = UiKit.Dp(_a, 16);
        content.SetPadding(pad, pad, pad, pad);
        card.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var header = UiKit.Horizontal(_a);
        header.SetGravity(GravityFlags.CenterVertical);
        var badge = new FrameLayout(_a) { Background = UiKit.RoundedFill(_a, Palette.PrimaryTint, 100) };
        badge.AddView(UiKit.Icon(_a, glyph, Palette.Primary, 16), new FrameLayout.LayoutParams(UiKit.Dp(_a, 16), UiKit.Dp(_a, 16), GravityFlags.Center));
        header.AddView(badge, new LinearLayout.LayoutParams(UiKit.Dp(_a, 28), UiKit.Dp(_a, 28)));
        header.AddView(UiKit.Text(_a, _a.GetString(title), 13, Palette.OnSurfaceVariant), UiKit.Weighted(_a, 1f, 8));
        content.AddView(header);

        var values = UiKit.Vertical(_a);
        values.AddView(UiKit.Text(_a, value, 22, Palette.OnSurface, TypefaceStyle.Bold).SingleLine());
        values.AddView(UiKit.Text(_a, subText, 11, Palette.OnSurfaceVariant).SingleLine(), UiKit.MatchWrap(_a, 4));
        content.AddView(values, UiKit.MatchWrap(_a, 10));
        return card;
    }

    private TextView SectionTitle(int res) => UiKit.Text(_a, _a.GetString(res), 16, Palette.OnSurface, TypefaceStyle.Bold);

    private View BuildActionRow(ActionSpec[] specs, int columns)
    {
        var container = UiKit.Vertical(_a);
        for (int start = 0; start < specs.Length; start += columns)
        {
            var row = UiKit.Horizontal(_a);
            row.BaselineAligned = false;
            row.SetGravity(GravityFlags.CenterVertical);
            for (int i = 0; i < columns; i++)
            {
                int index = start + i;
                var item = index < specs.Length ? ActionItem(specs[index]) : new View(_a);
                // 同一列等高、內容靠上：英文標籤換兩行（Steam download）時，圖示和第一行仍跟同列對齊
                var lp = UiKit.Weighted(_a, 1f, i > 0 ? 12 : 0);
                lp.Height = ViewGroup.LayoutParams.MatchParent;
                row.AddView(item, lp);
            }
            container.AddView(row, UiKit.MatchWrap(_a, start > 0 ? 12 : 0));
        }
        return container;
    }

    private View ActionItem(ActionSpec spec)
    {
        var item = new ActionItemView(_a, spec.Highlight);
        item.Click += (_, _) => spec.OnClick();
        var content = UiKit.Vertical(_a);
        content.SetGravity(GravityFlags.Center);
        content.SetPadding(UiKit.Dp(_a, 4), UiKit.Dp(_a, 14), UiKit.Dp(_a, 4), UiKit.Dp(_a, 14));
        content.AddView(UiKit.Icon(_a, spec.Glyph, Palette.Primary, 26));
        var label = UiKit.Text(_a, _a.GetString(spec.Label), 12, Palette.OnSurface);
        label.Gravity = GravityFlags.Center;
        label.SetMaxLines(2);
        content.AddView(label, UiKit.MatchWrap(_a, 8));
        item.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return item;
    }
}
