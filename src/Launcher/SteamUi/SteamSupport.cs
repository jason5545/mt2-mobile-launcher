using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Widget;
using AndroidX.Core.Content;
using Google.Android.Material.Dialog;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;

namespace Mt2Mobile.Launcher.SteamUi;

/// <summary>
/// Steam Guard 交給畫面處理：要驗證碼時跳對話框（取消就讓登入失敗），要手機核准時更新狀態文字、回傳 true 讓 SteamKit2 繼續輪詢。
/// 驗證碼只經過對話框與 SteamKit2，不留存、不寫 log。
/// </summary>
public sealed class UiAuthenticator(Activity activity, Action<string> setStatus) : IAuthenticator
{
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        activity.RunOnUiThread(() => setStatus(activity.GetString(Resource.String.steam_status_confirm_on_phone)));
        return Task.FromResult(true);
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        Prompt(activity.GetString(Resource.String.steam_guard_device_title),
            activity.GetString(previousCodeWasIncorrect ? Resource.String.steam_guard_device_retry : Resource.String.steam_guard_device_message));

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        Prompt(activity.GetString(Resource.String.steam_guard_email_title),
            activity.GetString(previousCodeWasIncorrect ? Resource.String.steam_guard_email_retry_format : Resource.String.steam_guard_email_message_format, email));

    private Task<string> Prompt(string title, string message)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            var input = new EditText(activity)
            {
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagCapCharacters,
            };
            input.SetSingleLine(true);
            var box = new FrameLayout(activity);
            int pad = Ui.UiKit.Dp(activity, 24);
            box.SetPadding(pad, 0, pad, 0);
            box.AddView(input);
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(title)!
                .SetMessage(message)!
                .SetView(box)!
                .SetCancelable(false)!
                .SetPositiveButton(Resource.String.ok, (_, _) => tcs.TrySetResult(input.Text?.Trim() ?? ""))!
                .SetNegativeButton(Resource.String.cancel, (_, _) => tcs.TrySetCanceled())!
                .Show();
            input.RequestFocus();
        });
        return tcs.Task;
    }
}

/// <summary>
/// 登入期間的前景服務。使用者切到 Steam App 按核准時，這個 app 退到背景；Android 14 起背景的快取程序會被凍結，
/// 輪詢就停了。前景服務讓程序在登入完成前保持活著（同 STS2 的 SteamAuthForegroundService，這裡只負責保活，工作在畫面那邊）。
/// </summary>
[Service(Name = "org.mt2mobile.launcher.SteamKeepAliveService", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class SteamKeepAliveService : Service
{
    private const string Channel = "steam_login";

    public static void Start(Context c) => c.StartForegroundService(new Intent(c, typeof(SteamKeepAliveService)));
    public static void Stop(Context c) => c.StopService(new Intent(c, typeof(SteamKeepAliveService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, GetString(Resource.String.steam_keepalive_channel), NotificationImportance.Low));
        var notification = new Notification.Builder(this, Channel)
            .SetContentTitle(GetString(Resource.String.app_name))!
            .SetContentText(GetString(Resource.String.steam_keepalive_text))!
            .SetSmallIcon(Resource.Drawable.ic_ms_login_24)!
            .SetOngoing(true)!
            .Build()!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(2, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(2, notification);
        return StartCommandResult.NotSticky;
    }
}

/// <summary>QR code 畫成 Bitmap：黑模組、白底（含 QRCoder 預設的 4 格留白，掃描比較穩）。</summary>
public static class QrBitmap
{
    public static Bitmap Render(string text, int moduleSize = 10)
    {
        using var data = new QRCodeGenerator().CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        int n = matrix.Count, size = n * moduleSize;
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            var row = matrix[y / moduleSize];
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = row[x / moduleSize] ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
        }
        var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        bitmap.SetPixels(pixels, 0, size, 0, 0, size, size);
        return bitmap;
    }
}

/// <summary>
/// 撤回（登出、撤回同意、token 失效）沒有完成時的提示：世代檔寫不了，別的程序不保證停下來。照實講，請使用者強制停止啟動器，
/// 不宣稱已經停下所有工作（Terra 第 4 輪）。
/// </summary>
public static class RevokeNotice
{
    private static readonly object Gate = new();
    private static Activity? _visible;
    private static bool _pending;

    public static void Show(Context context)
    {
        try
        {
            new MaterialAlertDialogBuilder(context)
                .SetTitle(Resource.String.steam_revoke_incomplete_title)!
                .SetMessage(SteamAccountStore.RevokeIncompleteMessage)!
                .SetPositiveButton(Resource.String.ok, (IDialogInterfaceOnClickListener?)null)!
                .Show();
        }
        catch (Exception e)
        {
            LauncherLog.Write($"revoke notice dialog failed: {e.GetType().Name}");
            Toast.MakeText(context, SteamAccountStore.RevokeIncompleteMessage, ToastLength.Long)!.Show();
        }
    }

    /// <summary>介面的 Activity 回到前景時叫（MainActivity、SteamAccountActivity 的 OnResume）：有等著的警告就跳。</summary>
    public static void Resumed(Activity activity)
    {
        bool show;
        lock (Gate)
        {
            _visible = activity;
            show = _pending;
            _pending = false;
        }
        if (show)
            Show(activity);
    }

    public static void Paused(Activity activity)
    {
        lock (Gate)
        {
            if (_visible == activity)
                _visible = null;
        }
    }

    /// <summary>
    /// 任何執行緒都能叫（RevokeRelay）：排到 UI 執行緒，在那時候 resumed 的 Activity 上跳；沒有就記著，等下一個回到前景的 Activity。
    /// 不在背景執行緒先挑 Activity（Terra 第 8 輪第 3 條）：排進去之後原本那個可能已經暫停、換成另一個 Activity。
    /// Resumed／Paused 也都在 UI 執行緒，所以那時候讀到的 _visible 就是前景的那個。
    /// </summary>
    public static void ShowWhenVisible() => new Handler(Looper.MainLooper!).Post(ShowOnVisible);

    private static void ShowOnVisible()
    {
        Activity? activity;
        lock (Gate)
        {
            activity = _visible;
            if (activity is null || activity.IsFinishing || activity.IsDestroyed)
            {
                _pending = true;
                return;
            }
        }
        Show(activity);
    }
}

/// <summary>
/// :install 撤回沒有完成時，把那一代交給介面程序（Terra 第 7 輪第 2 條）。:install 記的待補換代只在自己的記憶體裡，程序結束就沒了，
/// 通知也可能被擋；介面程序還在的話，收到後用 RevokeIfGeneration 再撤回一次（介面的 Cloud 工作會停），還是沒完成就跳對話框
/// （不在前景就等回到前景再跳）。
/// - 只送給這個 app（SetPackage）。receiver 用 ContextCompat 註冊成不匯出：API 33 起是 RECEIVER_NOT_EXPORTED；更早的版本由 AndroidX Core
///   帶進 manifest 的 signature 權限（DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION）擋住別的 app，這個 app 自己的程序有這個權限。
/// - 註冊在 ApplicationContext，整個介面程序活著就一直在（MainActivity、SteamAccountActivity 的 OnCreate 都會叫，只註冊一次）。
///   介面程序不在就沒有人收：動態註冊的 receiver 不會讓系統啟動程序，這時介面也沒有在跑的 Steam 工作。
///   Android 14 起，快取（凍結）中的程序收到的 broadcast 會延到程序解凍才送到；凍結期間介面的工作也停著。
/// - 一收到就同步記下那一代（NoteRevokeReported，不拿鎖）：這一代的工作立刻不算數，開遊戲也不給 Steam 身分，
///   不用等背景撤回拿到鎖（Terra 第 8 輪第 2 條）。broadcast 送到之前已經開出去的遊戲，算已經送出的請求。
/// - 撤回在背景執行緒做（拿鎖最多等 5 秒，不能卡住主執行緒），GoAsync 讓 receiver 等它做完。
/// </summary>
public static class RevokeRelay
{
    private const string Action = "org.mt2mobile.launcher.STEAM_REVOKE_INCOMPLETE";
    private const string ExtraGeneration = "generation";
    private static int _registered;

    public static void Register(Context context)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;
        try
        {
            ContextCompat.RegisterReceiver(context.ApplicationContext!, new Receiver(), new IntentFilter(Action), ContextCompat.ReceiverNotExported);
        }
        catch (Exception e)
        {
            Interlocked.Exchange(ref _registered, 0);
            LauncherLog.Write($"steam revoke relay not registered: {e.GetType().Name}");
        }
    }

    /// <summary>:install 用。送不出去只寫 logcat，通知照樣發。</summary>
    public static void Send(Context context, string generation)
    {
        try
        {
            context.SendBroadcast(new Intent(Action).SetPackage(context.PackageName).PutExtra(ExtraGeneration, generation));
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("mt2launcher", $"steam revoke relay not sent: {e.GetType().Name}");
        }
    }

    private sealed class Receiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var generation = intent?.GetStringExtra(ExtraGeneration);
            var app = context?.ApplicationContext;
            if (app is null || string.IsNullOrEmpty(generation))
                return;
            SteamAccess.NoteRevokeReported(app, generation);
            var pending = GoAsync();
            _ = Task.Run(() =>
            {
                try
                {
                    LauncherLog.Write("install process reported an incomplete steam access revoke; revoking in the launcher process");
                    if (!SteamAccountStore.ClearIfGeneration(app, generation))
                        RevokeNotice.ShowWhenVisible();
                }
                catch (Exception e)
                {
                    LauncherLog.Write($"steam revoke relay failed: {e.GetType().Name}");
                    RevokeNotice.ShowWhenVisible();
                }
                finally { pending?.Finish(); }
            });
        }
    }
}

public static class SteamErrors
{
    /// <summary>登入失敗給人看的原因。只用例外類型與 EResult，不帶帳號。</summary>
    public static string Describe(Context c, Exception e) => e switch
    {
        AuthenticationException { Result: EResult.InvalidPassword } => c.GetString(Resource.String.steam_login_error_password),
        AuthenticationException { Result: EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle } => c.GetString(Resource.String.steam_login_error_rate_limited),
        AuthenticationException { Result: EResult.Expired or EResult.FileNotFound } => c.GetString(Resource.String.steam_login_error_expired),
        AuthenticationException ae => c.GetString(Resource.String.steam_login_error_rejected_format, ae.Result.ToString()),
        TimeoutException => c.GetString(Resource.String.steam_login_error_timeout),
        Mt2Mobile.Steam.SteamException se => Strings.Of(c, se),
        HttpRequestException => c.GetString(Resource.String.steam_login_error_network),
        _ => e.GetType().Name,
    };
}
