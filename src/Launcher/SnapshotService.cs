// 只在驗收用的建置（TEST_ENTRY）：發布的 APK 不帶離屏快照
#if TEST_ENTRY
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomNavigation;
using Mt2Mobile.Launcher.Ui;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 開發用：不開畫面，把五個分頁照手機直拿的解析度離屏畫成 PNG（files/snapshots/tab-&lt;n&gt;.png），結束寫 files/snapshots/done.txt。
/// 手機正在被使用時，測介面不能把 app 拉到最前面，所以用這個看版面。沒有 export，只能用 root 叫：
///   su -c am start-foreground-service -n org.mt2mobile.launcher/org.mt2mobile.launcher.SnapshotService
/// 沒有系統列，所以頁首上緣、導覽列下緣不會讓出狀態列與手勢列的高度。
/// </summary>
[Service(Name = "org.mt2mobile.launcher.SnapshotService", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class SnapshotService : Service, ILauncherHost
{
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel("snapshot", "Snapshot", NotificationImportance.Min));
        StartForeground(3, new Notification.Builder(this, "snapshot").SetContentTitle("snapshot")!.SetSmallIcon(Resource.Drawable.ic_ms_image_24)!.Build()!,
            ForegroundService.TypeDataSync);
        new Handler(Looper.MainLooper!).Post(() =>
        {
            var dir = System.IO.Path.Combine(FilesDir!.AbsolutePath, "snapshots");
            Directory.CreateDirectory(dir);
            File.Delete(System.IO.Path.Combine(dir, "done.txt"));
            string result = "ok";
            try
            {
                LauncherLog.Init(FilesDir!.AbsolutePath);
                Render(dir);
            }
            catch (Exception e)
            {
                result = e.ToString();
                LauncherLog.Write($"snapshot failed: {e}");
            }
            File.WriteAllText(System.IO.Path.Combine(dir, "done.txt"), result + "\n");
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        });
        return StartCommandResult.NotSticky;
    }

    private void Render(string dir)
    {
        var c = new ContextThemeWrapper(this, Resource.Style.Theme_Mt2Launcher);
        var dm = Resources!.DisplayMetrics!;
        int w = Math.Min(dm.WidthPixels, dm.HeightPixels), h = Math.Max(dm.WidthPixels, dm.HeightPixels);
        for (int tab = MainActivity.TabGame; tab <= MainActivity.TabAbout; tab++)
        {
            var shell = UiKit.Vertical(c);
            shell.SetBackgroundColor(Palette.Background);
            shell.AddView(LauncherShell.BuildPage(c, this, tab), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            var nav = new BottomNavigationView(c);
            LauncherShell.ConfigureNavigationBar(c, nav);
            nav.SelectedItemId = tab;
            shell.AddView(nav, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            shell.Measure(View.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly), View.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly));
            shell.Layout(0, 0, w, h);
            using var bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
            shell.Draw(new Canvas(bitmap));
            using var file = File.Create(System.IO.Path.Combine(dir, $"tab-{tab}.png"));
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, file);
        }
    }

    public void OpenTab(int tab) { }
    public void SelectTab(int tab) { }
    public void OpenSteamAccount() { }
    public void StartSteamDownload() { }
    public void RequestCloudSync() { }
    public void NotReady(string label) { }
    public void ImportGame() { }
    public void StartInstall(string action, string? tree = null) { }
    public void LaunchGame() { }
    public DateTime InstallRequestedAt => DateTime.MinValue;
}
#endif
