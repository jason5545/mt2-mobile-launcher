// 版面與動畫移植自 Sts2MobileLauncher e568614（https://github.com/ModinMobileSTS/Sts2MobileLauncher），MIT；授權全文見 THIRD_PARTY_NOTICES.md。
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Views;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Navigation;
using Google.Android.Material.Shape;
using Mt2Mobile.Launcher.Pages;
using Mt2Mobile.Launcher.Ui;

namespace Mt2Mobile.Launcher;

/// <summary>頁面要主框架做的事（切分頁、開 Steam 帳號頁、匯入與安裝、開遊戲、還沒做的功能的提示）。MainActivity 實作；離屏快照用假的實作。</summary>
public interface ILauncherHost
{
    void OpenTab(int tab);
    void SelectTab(int tab);
    void OpenSteamAccount();
    /// <summary>Steam 下載：先通過簽章與登入檢查，再交給 :install 服務。</summary>
    void StartSteamDownload();
    void RequestCloudSync();
    void NotReady(string label);
    /// <summary>選原版的資料夾（SAF），選好就開始安裝</summary>
    void ImportGame();
    /// <summary>送給安裝服務：start（續跑）、cancel、stop-now</summary>
    void StartInstall(string action, string? tree = null);
    void LaunchGame();
    /// <summary>最近一次要求開始安裝的時間：服務還沒拿到鎖之前，遊戲頁也要繼續看進度</summary>
    DateTime InstallRequestedAt { get; }
}

/// <summary>主框架與離屏快照共用：建分頁、設定底部導覽列（照 Sts2MobileLauncher 的 configureNavigationBar）。</summary>
public static class LauncherShell
{
    public static View BuildPage(Context c, ILauncherHost host, int tab)
    {
        try
        {
            return tab switch
            {
                MainActivity.TabMods => new ModsPage(c, host).Build(),
                MainActivity.TabVersions => new VersionsPage(c, host).Build(),
                MainActivity.TabSettings => new SettingsPage(c, host).Build(),
                MainActivity.TabAbout => new AboutPage(c, host).Build(),
                _ => new GamePage(c, host).Build(),
            };
        }
        catch (Exception e)
        {
            LauncherLog.Write($"page {tab} failed: {e}");
            return UiKit.Text(c, e.Message, 14, Palette.Error);
        }
    }

    public static void ConfigureNavigationBar(Context c, BottomNavigationView nav)
    {
        nav.SetBackgroundColor(Palette.Surface);
        var iconColors = new ColorStateList(
            new[] { new[] { Android.Resource.Attribute.StateChecked }, Array.Empty<int>() },
            new int[] { Palette.OnSecondaryContainer, Palette.OnSurfaceVariant });
        var textColors = new ColorStateList(
            new[] { new[] { Android.Resource.Attribute.StateChecked }, Array.Empty<int>() },
            new int[] { Palette.OnSurface, Palette.OnSurfaceVariant });
        (int id, int title, string glyph)[] items =
        {
            (MainActivity.TabGame, Resource.String.tab_game, "stadia_controller"),
            (MainActivity.TabMods, Resource.String.tab_mods, "extension"),
            (MainActivity.TabVersions, Resource.String.tab_versions, "layers"),
            (MainActivity.TabSettings, Resource.String.tab_settings, "settings"),
            (MainActivity.TabAbout, Resource.String.tab_about, "info"),
        };
        for (int i = 0; i < items.Length; i++)
            nav.Menu.Add(0, items[i].id, i, items[i].title)!.SetIcon(UiKit.Symbol(c, items[i].glyph, iconColors, 24));
        nav.ItemIconTintList = iconColors;
        nav.ItemTextColor = textColors;
        nav.ItemRippleColor = ColorStateList.ValueOf(Color.Argb(72, 255, 190, 92));
        nav.ItemActiveIndicatorEnabled = true;
        nav.ItemActiveIndicatorColor = ColorStateList.ValueOf(Palette.SecondaryContainer);
        nav.ItemActiveIndicatorWidth = UiKit.Dp(c, 64);
        nav.ItemActiveIndicatorHeight = UiKit.Dp(c, 32);
        nav.ItemActiveIndicatorShapeAppearance = ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(UiKit.Dp(c, 16)).Build();
        nav.LabelVisibilityMode = NavigationBarView.LabelVisibilityLabeled;
        nav.SetItemTextAppearanceActiveBoldEnabled(true);
    }

}
