using Android.Content;
using Android.Graphics;
using Android.Text.Format;
using Android.Views;
using Android.Widget;
using Google.Android.Material.MaterialSwitch;
using Mt2Mobile.Launcher.SteamUi;
using Mt2Mobile.Launcher.Ui;

namespace Mt2Mobile.Launcher.Pages;

/// <summary>模組頁：版面同 STS2（頁首右邊是「允許載入 MOD」開關），MT2 還不支援模組，開關關著、不能切，下面是空狀態。</summary>
public sealed class ModsPage(Context a, ILauncherHost h)
{
    public View Build()
    {
        var toggle = UiKit.Horizontal(a);
        toggle.SetGravity(GravityFlags.CenterVertical);
        toggle.AddView(UiKit.Text(a, a.GetString(Resource.String.mods_allow_switch), 15, Palette.OnSurface));
        var sw = new MaterialSwitch(a) { Checked = false, Enabled = false };
        var sp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = UiKit.Dp(a, 8) };
        toggle.AddView(sw, sp);
        toggle.Alpha = 0.6f;

        var shell = UiKit.Vertical(a);
        shell.AddView(UiKit.TopAppBar(a, a.GetString(Resource.String.mods_title), toggle));
        var content = UiKit.Vertical(a);
        content.AddView(Widgets.EmptyState(a, "extension", a.GetString(Resource.String.mods_empty_title), a.GetString(Resource.String.mods_empty_message)), UiKit.MatchWrap(a, 8));
        shell.AddView(UiKit.ResponsiveScroll(a, content), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return shell;
    }
}

/// <summary>
/// 版本頁：分段切換（遊戲本體｜支援版本）、三顆膠囊按鈕（匯入｜Steam｜重新整理）、清單。
/// STS2 的第三段「相容包」對應到這裡的「支援版本」（版本綁定的 target）；「網盤」換成「重新整理」。
/// </summary>
public sealed class VersionsPage(Context a, ILauncherHost h)
{
    private static int _tab;

    public View Build()
    {
        var state = GameState.Load(a);
        var shell = UiKit.Vertical(a);
        shell.AddView(UiKit.TopAppBar(a, a.GetString(Resource.String.versions_title)));
        var content = UiKit.Vertical(a);
        content.SetPadding(0, 0, 0, UiKit.Dp(a, 32));

        content.AddView(Widgets.Segmented(a,
            new[] { a.GetString(Resource.String.version_manager_tab_payloads), a.GetString(Resource.String.version_manager_tab_targets) },
            _tab, i => { _tab = i; h.OpenTab(MainActivity.TabVersions); }), UiKit.MatchWrap(a));

        content.AddView(Widgets.PillButtonRow(a,
            Widgets.PillButton(a, "file_upload", a.GetString(Resource.String.version_manager_import_short), h.ImportGame),
            Widgets.PillButton(a, "file_download", a.GetString(Resource.String.version_manager_steam_short), h.StartSteamDownload),
            Widgets.PillButton(a, "refresh", a.GetString(Resource.String.version_manager_refresh_short), () => h.OpenTab(MainActivity.TabVersions))),
            UiKit.MatchWrap(a, 16));

        if (_tab == 0)
        {
            if (state.Game is { } game)
                content.AddView(Widgets.ListItem(a, "folder_open", "v" + game.Version,
                    a.GetString(Resource.String.version_manager_payload_list_subtitle, Formatter.FormatFileSize(a, game.Bytes), game.Source),
                    () => { }, badge: a.GetString(Resource.String.version_manager_selected_badge)), UiKit.MatchWrap(a, 16));
            else
            {
                var empty = UiKit.Text(a, a.GetString(Resource.String.version_manager_no_payloads), 14, Palette.OnSurfaceVariant);
                empty.SetPadding(UiKit.Dp(a, 4), 0, UiKit.Dp(a, 4), 0);
                empty.SetLineSpacing(UiKit.Dp(a, 4), 1f);
                content.AddView(empty, UiKit.MatchWrap(a, 24));
            }
        }
        else
        {
            foreach (var t in SupportedTarget.All)
                content.AddView(Widgets.ListItem(a, "layers", $"{t.Game} {t.Version}",
                    a.GetString(Resource.String.version_manager_target_subtitle, t.Unity, t.Id), null), UiKit.MatchWrap(a, 16));
            var hint = UiKit.Text(a, a.GetString(Resource.String.version_manager_targets_hint), 13, Palette.OnSurfaceVariant);
            hint.SetPadding(UiKit.Dp(a, 4), 0, UiKit.Dp(a, 4), 0);
            hint.SetLineSpacing(UiKit.Dp(a, 4), 1f);
            content.AddView(hint, UiKit.MatchWrap(a, 16));
        }
        shell.AddView(UiKit.ResponsiveScroll(a, content), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return shell;
    }
}

/// <summary>設定頁：分組卡片。還沒做的項目照樣列出、變淡，讓之後要做的事一目了然。</summary>
public sealed class SettingsPage(Context a, ILauncherHost h)
{
    /// <summary>建置簽章：非官方又已同意的，點了可以撤回（Steam 功能關掉、登出）</summary>
    private View SigningItem()
    {
        bool official = OfficialSigning.IsOfficial(a), consented = OfficialSigning.Consented(a);
        string sub = official ? a.GetString(Resource.String.signing_build_official)
            : BuildBadge.Text(a) + "\n" + a.GetString(consented ? Resource.String.signing_settings_consented : Resource.String.signing_settings_not_consented);
        Action? click = official || !consented ? null : () =>
            new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(a)
                .SetTitle(Resource.String.signing_revoke_title)!
                .SetMessage(Resource.String.signing_revoke_message)!
                .SetNegativeButton(Resource.String.cancel, (IDialogInterfaceOnClickListener?)null)!
                .SetPositiveButton(Resource.String.signing_revoke, (_, _) =>
                {
                    bool complete = OfficialSigning.Revoke(a);
                    LauncherLog.Write($"unofficial build consent revoked (complete={complete})");
                    if (complete)
                        UiKit.Toast(a, a.GetString(Resource.String.signing_revoked));
                    else
                        RevokeNotice.Show(a);
                    h.OpenTab(MainActivity.TabSettings);
                })!
                .Show();
        return Widgets.ListItem(a, "badge", a.GetString(Resource.String.signing_settings_title), sub, click, circleIcon: false);
    }

    /// <summary>
    /// 移除遊戲：刪原版、安裝產物、Steam 下載暫存與上次的安裝結果，存檔、Cloud 狀態、登入、記錄都留著。
    /// 刪之前拿 install.lock 和 cloud/game.lock，安裝服務或遊戲在跑就不刪。
    /// </summary>
    private View RemoveGameItem()
    {
        var p = new Install.Paths(a.FilesDir!.AbsolutePath);
        string[] dirs = [p.Originals, p.Installs, p.SteamDownloads, p.Staging, p.AssetRipperBase];
        bool any = dirs.Any(Directory.Exists);
        Action? click = !any ? null : () =>
            new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(a)
                .SetTitle(Resource.String.settings_remove_game_title)!
                .SetMessage(Resource.String.settings_remove_game_message)!
                .SetNegativeButton(Resource.String.cancel, (IDialogInterfaceOnClickListener?)null)!
                .SetPositiveButton(Resource.String.settings_remove_game_confirm, (_, _) =>
                {
                    long bytes = RemoveGame(p, dirs);
                    UiKit.Toast(a, bytes < 0 ? a.GetString(Resource.String.settings_remove_game_busy)
                        : a.GetString(Resource.String.settings_remove_game_done, Formatter.FormatFileSize(a, bytes)));
                    h.OpenTab(MainActivity.TabSettings);
                })!
                .Show();
        return Widgets.ListItem(a, "remove_circle", a.GetString(Resource.String.settings_remove_game),
            a.GetString(any ? Resource.String.settings_remove_game_hint : Resource.String.settings_remove_game_none), click, enabled: any, circleIcon: false);
    }

    /// <summary>回傳刪掉的位元組數；安裝或遊戲正在跑就回 -1</summary>
    static long RemoveGame(Install.Paths p, string[] dirs)
    {
        FileStream? install = null, game = null;
        try
        {
            Directory.CreateDirectory(p.Launcher);
            var cloud = System.IO.Path.Combine(p.Files, "cloud");
            Directory.CreateDirectory(cloud);
            install = new FileStream(p.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            game = new FileStream(System.IO.Path.Combine(cloud, "game.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            install?.Dispose();
            LauncherLog.Write("remove game skipped: install or game running");
            return -1;
        }
        using (install)
        using (game)
        {
            long bytes = 0;
            foreach (var d in dirs.Where(Directory.Exists))
            {
                bytes += new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                Install.Util.DeleteDir(d);
            }
            if (File.Exists(p.State)) File.Delete(p.State);
            LauncherLog.Write($"game removed: {bytes} bytes");
            return bytes;
        }
    }

    /// <summary>Steam Cloud：狀態跟遊戲頁同一份（cloud/status.json），點了立刻同步；沒登入或沒同意建置簽章時，RequestCloudSync 會轉去 Steam 帳號頁</summary>
    private View CloudItem()
    {
        var s = SteamCloudStatusStore.Load(a);
        string state = s?.State switch
        {
            "syncing" => a.GetString(Resource.String.cloud_status_syncing),
            "synced" => a.GetString(Resource.String.cloud_status_synced),
            "conflict" => a.GetString(Resource.String.cloud_status_conflict),
            "failed" => a.GetString(Resource.String.cloud_status_failed),
            _ => a.GetString(Resource.String.cloud_status_not_synced),
        };
        string detail = s switch
        {
            { State: "synced", LastSyncAtUtc: { } at } => a.GetString(Resource.String.settings_cloud_synced_format,
                at.ToLocalTime().ToString("MM/dd HH:mm"), s.CloudFiles, s.Uploaded, s.Downloaded),
            { Reason.Length: > 0 } => s.Reason,
            null => a.GetString(Resource.String.cloud_status_login_hint),
            _ => "",
        };
        string sub = detail.Length > 0 ? state + " · " + detail + "\n" + a.GetString(Resource.String.settings_cloud_tap_hint)
            : state + "\n" + a.GetString(Resource.String.settings_cloud_tap_hint);
        return Widgets.ListItem(a, "cloud_sync", a.GetString(Resource.String.settings_steam_cloud), sub, h.RequestCloudSync, circleIcon: false);
    }

    public View Build()
    {
        var shell = UiKit.Vertical(a);
        shell.AddView(UiKit.TopAppBar(a, a.GetString(Resource.String.settings_title)));
        var content = UiKit.Vertical(a);
        content.SetPadding(0, 0, 0, UiKit.Dp(a, 32));
        string soon = a.GetString(Resource.String.settings_coming_soon);

        content.AddView(Widgets.SectionLabel(a, a.GetString(Resource.String.settings_section_account)), UiKit.MatchWrap(a));
        var account = Widgets.Card(a);
        account.AddView(Widgets.ListItem(a, "person", a.GetString(Resource.String.settings_steam_account),
            SteamAccountStore.IsLoggedIn(a) ? SteamAccountStore.AccountName(a) : a.GetString(Resource.String.settings_steam_not_logged_in),
            h.OpenSteamAccount, circleIcon: false));
        account.AddView(SigningItem());
        account.AddView(CloudItem());
        account.AddView(Widgets.ListItem(a, "sync", a.GetString(Resource.String.settings_webdav), soon, null, enabled: false, circleIcon: false));
        content.AddView(account, UiKit.MatchWrap(a, 10));

        content.AddView(Widgets.SectionLabel(a, a.GetString(Resource.String.settings_section_game)), UiKit.MatchWrap(a, 24));
        var game = Widgets.Card(a);
        game.AddView(Widgets.ListItem(a, "aspect_ratio", a.GetString(Resource.String.settings_orientation), a.GetString(Resource.String.settings_orientation_landscape), null, enabled: false, circleIcon: false));
        game.AddView(Widgets.ListItem(a, "speed", a.GetString(Resource.String.settings_renderer), "OpenGL ES 3.0", null, enabled: false, circleIcon: false));
        content.AddView(game, UiKit.MatchWrap(a, 10));

        content.AddView(Widgets.SectionLabel(a, a.GetString(Resource.String.settings_section_maintenance)), UiKit.MatchWrap(a, 24));
        var maintenance = Widgets.Card(a);
        maintenance.AddView(Widgets.ListItem(a, "receipt_long", a.GetString(Resource.String.view_logs), null, () => LogViewer.Show(a), circleIcon: false));
        maintenance.AddView(Widgets.ListItem(a, "delete", a.GetString(Resource.String.settings_clear_cache), null, () =>
        {
            long bytes = 0;
            foreach (var f in new DirectoryInfo(a.CacheDir!.AbsolutePath).EnumerateFileSystemInfos())
            {
                if (f is FileInfo fi)
                {
                    bytes += fi.Length;
                    fi.Delete();
                }
                else if (f is DirectoryInfo di)
                {
                    bytes += di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length);
                    di.Delete(true);
                }
            }
            UiKit.Toast(a, a.GetString(Resource.String.settings_clear_cache_done, Formatter.FormatFileSize(a, bytes)));
        }, circleIcon: false));
        maintenance.AddView(RemoveGameItem());
        content.AddView(maintenance, UiKit.MatchWrap(a, 10));

        shell.AddView(UiKit.ResponsiveScroll(a, content), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return shell;
    }
}

/// <summary>官方建置檢查的顯示（設定、關於共用）：官方就寫官方，否則一直標「非官方建置」與指紋前 16 碼（docs/phase4.md「debug 建置」）。</summary>
public static class BuildBadge
{
    public static string Text(Context a) => OfficialSigning.IsOfficial(a)
        ? a.GetString(Resource.String.signing_build_official)
        : a.GetString(Resource.String.signing_build_unofficial_format, OfficialSigning.Fingerprint(a)[..16]);
}

/// <summary>關於頁：app 名稱與版本、聲明、致謝與授權（點了開專案網頁）。</summary>
public sealed class AboutPage(Context a, ILauncherHost h)
{
    private static readonly (string Name, string License, int Use, string Url)[] Credits =
    {
        ("Sts2MobileLauncher", "MIT", Resource.String.about_credit_sts2, "https://github.com/ModinMobileSTS/Sts2MobileLauncher"),
        ("SteamKit2", "LGPL-2.1", Resource.String.about_credit_steamkit, "https://github.com/SteamRE/SteamKit"),
        ("AssetRipper", "GPL-3.0", Resource.String.about_credit_assetripper, "https://github.com/AssetRipper/AssetRipper"),
        ("Material Components for Android", "Apache-2.0", Resource.String.about_credit_material, "https://github.com/material-components/material-components-android"),
        ("Material Symbols", "Apache-2.0", Resource.String.about_credit_symbols, "https://github.com/google/material-design-icons"),
        ("QRCoder", "MIT", Resource.String.about_credit_qrcoder, "https://github.com/Shane32/QRCoder"),
        (".NET", "MIT", Resource.String.about_credit_dotnet, "https://github.com/dotnet/runtime"),
    };

    public View Build()
    {
        var shell = UiKit.Vertical(a);
        shell.AddView(UiKit.TopAppBar(a, a.GetString(Resource.String.about_title)));
        var content = UiKit.Vertical(a);
        content.SetPadding(0, 0, 0, UiKit.Dp(a, 32));

        var hero = UiKit.Horizontal(a);
        hero.SetGravity(GravityFlags.CenterVertical);
        hero.Background = UiKit.RoundedFill(a, Palette.SurfaceContainer, 24);
        hero.SetPadding(UiKit.Dp(a, 20), UiKit.Dp(a, 20), UiKit.Dp(a, 20), UiKit.Dp(a, 20));
        var icon = new ImageView(a);
        icon.SetImageResource(Resource.Mipmap.ic_launcher);
        hero.AddView(icon, new LinearLayout.LayoutParams(UiKit.Dp(a, 64), UiKit.Dp(a, 64)));
        var texts = UiKit.Vertical(a);
        texts.AddView(UiKit.Text(a, a.GetString(Resource.String.app_name), 20, Palette.OnSurface, TypefaceStyle.Bold));
        texts.AddView(UiKit.Text(a, a.GetString(Resource.String.about_tagline), 13, Palette.OnSurfaceVariant), UiKit.MatchWrap(a, 6));
        string version = a.PackageManager!.GetPackageInfo(a.PackageName!, 0)!.VersionName ?? "?";
        texts.AddView(UiKit.Text(a, a.GetString(Resource.String.about_version_format, version), 13, Palette.Primary), UiKit.MatchWrap(a, 6));
        texts.AddView(UiKit.Text(a, BuildBadge.Text(a), 12, OfficialSigning.IsOfficial(a) ? Palette.OnSurfaceVariant : Palette.Error), UiKit.MatchWrap(a, 4));
        hero.AddView(texts, UiKit.Weighted(a, 1f, 16));
        content.AddView(hero, UiKit.MatchWrap(a));

        var disclaimer = UiKit.Text(a, a.GetString(Resource.String.about_disclaimer), 14, Palette.OnSurfaceVariant);
        disclaimer.SetLineSpacing(UiKit.Dp(a, 4), 1f);
        disclaimer.SetPadding(UiKit.Dp(a, 4), 0, UiKit.Dp(a, 4), 0);
        content.AddView(disclaimer, UiKit.MatchWrap(a, 16));

        content.AddView(Widgets.SectionLabel(a, a.GetString(Resource.String.about_credits_title)), UiKit.MatchWrap(a, 24));
        var credits = Widgets.Card(a);
        foreach (var (name, license, use, url) in Credits)
            credits.AddView(Widgets.ListItem(a, "open_in_new", name, license + " · " + a.GetString(use),
                () => a.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url))), circleIcon: false));
        content.AddView(credits, UiKit.MatchWrap(a, 10));

        shell.AddView(UiKit.ResponsiveScroll(a, content), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return shell;
    }
}

/// <summary>檢視日誌：launcher.log 最後 300 行，等寬字，可以選取複製。</summary>
public static class LogViewer
{
    public static void Show(Context c)
    {
        string text = "(no log)";
        var path = LauncherLog.LogDirectory == null ? null : System.IO.Path.Combine(LauncherLog.LogDirectory, "launcher.log");
        if (path != null && File.Exists(path))
            text = string.Join('\n', File.ReadLines(path).TakeLast(300));
        var view = UiKit.Text(c, text, 11, Palette.OnSurface);
        view.Typeface = Typeface.Monospace;
        view.SetTextIsSelectable(true);
        int pad = UiKit.Dp(c, 16);
        view.SetPadding(pad, pad, pad, pad);
        var scroll = new ScrollView(c);
        scroll.AddView(view);
        var dialog = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(c)
            .SetTitle(Resource.String.view_logs)!
            .SetView(scroll)!
            .SetPositiveButton(Resource.String.ok, (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .Show()!;
        scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
    }
}
