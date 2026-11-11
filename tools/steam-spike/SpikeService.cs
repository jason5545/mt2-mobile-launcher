using Stopwatch = System.Diagnostics.Stopwatch;
using Android.App;
using Android.Content;
using Android.OS;
using SteamKit2;
using SteamKit2.Authentication;

namespace Mt2Mobile.SteamSpike;

/// <summary>
/// SteamKit2 在手機上的煙霧測試，不用帳號。依序：連 CM → 開 QR 登入的工作階段（只確認拿得到 challenge，不等掃描）
/// → 匿名登入 → MT2 的 PICS（depot、branch、public manifest）→ SteamPipe 的 CDN 清單 → 登出。
/// 前景服務，不開畫面（手機在用的時候不搶到最前面）：am start-foreground-service -n org.mt2mobile.steamspike/.SpikeService。
/// 每一步寫 logcat（tag steamspike）和 files/spike.log；結束寫 files/spike-done.txt（PASS 或 FAIL 加原因），然後停掉服務。
/// log 不寫 challenge URL 本身（任何人掃了都能核准這個登入），只寫長度。
/// </summary>
[Service(Name = "org.mt2mobile.steamspike.SpikeService", Exported = true, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class SpikeService : Service
{
    private const uint Mt2AppId = 2742830;
    private const string ChannelId = "steam-spike";
    private static readonly object LogGate = new();
    private string _logPath = "";
    private int _running;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Steam spike", NotificationImportance.Low));
        StartForeground(1, new Notification.Builder(this, ChannelId)
            .SetContentTitle("MT2 Steam spike")
            .SetContentText("Testing SteamKit2")
            .SetSmallIcon(Android.Resource.Drawable.IcMenuInfoDetails)
            .SetOngoing(true)
            .Build());
        if (Interlocked.Exchange(ref _running, 1) == 0)
        {
            _logPath = Path.Combine(FilesDir!.AbsolutePath, "spike.log");
            File.Delete(_logPath);
            File.Delete(Path.Combine(FilesDir!.AbsolutePath, "spike-done.txt"));
            _ = Task.Run(async () =>
            {
                await RunAsync();
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            });
        }
        return StartCommandResult.NotSticky;
    }

    private void Log(string line)
    {
        line = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        Android.Util.Log.Info("steamspike", line);
        lock (LogGate)
            File.AppendAllText(_logPath, line + "\n");
    }

    private async Task RunAsync()
    {
        var total = Stopwatch.StartNew();
        string verdict;
        try
        {
            verdict = await SmokeAsync();
        }
        catch (Exception e)
        {
            Log("EXCEPTION " + e);
            verdict = "FAIL " + e.GetType().Name + ": " + e.Message;
        }
        Log($"RESULT {verdict} ({total.Elapsed.TotalSeconds:F1} s)");
        File.WriteAllText(Path.Combine(FilesDir!.AbsolutePath, "spike-done.txt"), verdict + "\n");
    }

    private async Task<string> SmokeAsync()
    {
        Log($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
            $"SteamKit2 {typeof(SteamClient).Assembly.GetName().Version}");

        DebugLog.AddListener((category, msg) => Log($"  [sk2 {category}] {msg}"));
        DebugLog.Enabled = true;
        // Android 上 HttpClient 預設走 AndroidMessageHandler（Java 的 HTTP），ClientWebSocket 不能用它連線，
        // SteamKit2 的 WebSocket 一連就失敗（Unable to connect to the remote server）：一律改用 .NET 自己的 SocketsHttpHandler
        var config = SteamConfiguration.Create(b => b
            .WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp)
            .WithHttpClientFactory(_ => new HttpClient(new SocketsHttpHandler())));
        var client = new SteamClient(config);
        var manager = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;
        var apps = client.GetHandler<SteamApps>()!;
        var content = client.GetHandler<SteamContent>()!;

        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggedOn = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult(true));
        manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            Log($"disconnected (user initiated: {cb.UserInitiated})");
            connected.TrySetResult(false);
            loggedOn.TrySetResult(EResult.NoConnection);
        });
        manager.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedOn.TrySetResult(cb.Result));

        using var cts = new CancellationTokenSource();
        var pump = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
                manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
        });

        try
        {
            var sw = Stopwatch.StartNew();
            client.Connect();
            if (!await connected.Task.WaitAsync(TimeSpan.FromSeconds(30)))
                return "FAIL connect";
            Log($"1 connected to CM {client.CurrentEndPoint} ({sw.ElapsedMilliseconds} ms)");

            sw.Restart();
            var qr = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = "mt2-mobile-launcher spike",
            });
            Log($"2 QR auth session ok: challenge URL {qr.ChallengeURL.Length} chars, " +
                $"starts with {qr.ChallengeURL.Split('/')[2]} ({sw.ElapsedMilliseconds} ms)");

            sw.Restart();
            user.LogOnAnonymous();
            var logon = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (logon != EResult.OK)
                return "FAIL anonymous logon " + logon;
            Log($"3 anonymous logon OK ({sw.ElapsedMilliseconds} ms)");

            sw.Restart();
            var tokens = await apps.PICSGetAccessTokens(Mt2AppId, null);
            ulong token = tokens.AppTokens.TryGetValue(Mt2AppId, out var t) ? t : 0;
            Log($"4 PICS access token: {(tokens.AppTokensDenied.Contains(Mt2AppId) ? "denied" : token == 0 ? "none (public)" : "granted")}");
            var info = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(Mt2AppId, token), null);
            var app = info.Results!.SelectMany(r => r.Apps).Where(a => a.Key == Mt2AppId).Select(a => a.Value).FirstOrDefault();
            if (app == null)
                return "FAIL PICS: no app info";
            var kv = app.KeyValues;
            Log($"  name={kv["common"]["name"].Value} type={kv["common"]["type"].Value} changenumber={app.ChangeNumber} ({sw.ElapsedMilliseconds} ms)");
            var depots = kv["depots"];
            foreach (var branch in depots["branches"].Children)
                Log($"  branch {branch.Name}: buildid={branch["buildid"].Value} timeupdated={branch["timeupdated"].Value} pwd={(branch["pwdrequired"].Value ?? "0")}");
            int depotCount = 0;
            foreach (var depot in depots.Children.Where(d => uint.TryParse(d.Name, out _)))
            {
                depotCount++;
                var cfg = depot["config"];
                var pub = depot["manifests"]["public"]["gid"].Value ?? depot["manifests"]["public"].Value;
                Log($"  depot {depot.Name}: name={depot["name"].Value} os={cfg["oslist"].Value} lang={cfg["language"].Value} " +
                    $"dlcappid={depot["dlcappid"].Value} public manifest={pub ?? "(hidden)"} size={depot["manifests"]["public"]["size"].Value}");
            }
            if (depotCount == 0)
                return "FAIL PICS: no depots";

            sw.Restart();
            var servers = await content.GetServersForSteamPipe();
            Log($"5 SteamPipe CDN servers: {servers.Count} ({sw.ElapsedMilliseconds} ms); first: " +
                string.Join(", ", servers.Take(3).Select(s => $"{s.Type} {s.Host}")));
            if (servers.Count == 0)
                return "FAIL no CDN servers";

            user.LogOff();
            return $"PASS (depots {depotCount}, CDN servers {servers.Count})";
        }
        finally
        {
            client.Disconnect();
            await Task.Delay(500);
            cts.Cancel();
            await pump;
        }
    }
}
