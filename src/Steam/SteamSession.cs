using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using Mt2Mobile.Steam.SteamCloud;

namespace Mt2Mobile.Steam;

/// <summary>MT2 在 Steam 上的 ID。DLC 清單取自商店 API 與 PICS（2026/10/22）。</summary>
public static class Mt2Steam
{
    public const uint AppId = 2742830;
    public const uint WindowsDepotId = 2742831;
}

/// <summary>授權檢查的結果：擁有本體與否、擁有哪些 DLC（app ID）。</summary>
public sealed record OwnershipResult(bool OwnsGame, IReadOnlyList<uint> OwnedDlc, IReadOnlyList<uint> KnownDlc, int LicenseCount);

/// <summary>Steam identity used by the game glue. Never write these values to a log.</summary>
public sealed record SteamIdentity(ulong SteamId, string PersonaName);

/// <summary>登入成功後要保存的東西。RefreshToken 交給呼叫端加密保存，這裡不寫檔、不寫 log。</summary>
public sealed record SteamLogin(string AccountName, string RefreshToken);

/// <summary>
/// 一條 Steam CM 連線。用法：ConnectAsync → （LoginWithQrAsync 或 LoginWithCredentialsAsync 拿 refresh token）→ LogOnAsync → CheckOwnershipAsync。
/// 已經有 refresh token 時直接 ConnectAsync → LogOnAsync。
/// HTTP 一律走 SocketsHttpHandler：Android 預設的 AndroidMessageHandler 不能給 ClientWebSocket 用（docs/steam-spike.md）。
/// log 只寫步驟與結果，不寫密碼、驗證碼、token、SteamID、QR 的 challenge URL。
/// </summary>
public sealed class SteamSession : IAsyncDisposable
{
    private readonly SteamClient _client;
    private readonly CallbackManager _manager;
    private readonly SteamUser _user;
    private readonly SteamApps _apps;
    private readonly SteamContent _content;
    private readonly SteamFriends _friends;
    private readonly Cloud _cloud;
    private readonly TaskCompletionSource<bool> _accountInfo = NewTcs<bool>();
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pump;
    private readonly Action<string> _log;
    private TaskCompletionSource<bool> _connected = NewTcs<bool>();
    private TaskCompletionSource<EResult> _loggedOn = NewTcs<EResult>();
    private readonly TaskCompletionSource<IReadOnlyList<SteamApps.LicenseListCallback.License>> _licenses = NewTcs<IReadOnlyList<SteamApps.LicenseListCallback.License>>();

    public SteamSession(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _client = new SteamClient(CreateConfiguration());
        _manager = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _content = _client.GetHandler<SteamContent>()!;
        _friends = _client.GetHandler<SteamFriends>()!;
        _cloud = _client.GetHandler<SteamUnifiedMessages>()!.CreateService<Cloud>();
        Cloud = new SteamKitCloudTransport(_cloud, _client, _log);

        _manager.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult(true));
        _manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            IsLoggedOn = false;
            _log($"disconnected (user initiated: {cb.UserInitiated})");
            _connected.TrySetResult(false);
            _loggedOn.TrySetResult(EResult.NoConnection);
            Disconnected?.Invoke(cb.UserInitiated);
        });
        _manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            IsLoggedOn = cb.Result == EResult.OK;
            _loggedOn.TrySetResult(cb.Result);
        });
        _manager.Subscribe<SteamUser.AccountInfoCallback>(_ => _accountInfo.TrySetResult(true));
        _manager.Subscribe<SteamApps.LicenseListCallback>(cb =>
        {
            if (cb.Result == EResult.OK)
                _licenses.TrySetResult(cb.LicenseList);
            else
                _licenses.TrySetException(new SteamException($"license list: {cb.Result}"));
        });

        _pump = Task.Run(() =>
        {
            while (!_pumpCts.IsCancellationRequested)
                _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
        });
    }

    public bool IsConnected => _client.IsConnected;
    public bool IsLoggedOn { get; private set; }
    public ISteamCloudTransport Cloud { get; }

    /// <summary>Create a downloader tied to this logged-on session. Dispose it before disposing the session.</summary>
    public SteamDepotDownloader CreateDepotDownloader()
    {
        if (!IsLoggedOn)
            throw new SteamException("還沒登入 Steam", "not_logged_in");
        return new SteamDepotDownloader(new SteamKitDepotTransport(_client, _apps, _content, _log));
    }

    /// <summary>CM 斷線時（參數：是不是自己斷的）。</summary>
    public event Action<bool>? Disconnected;

    public static SteamConfiguration CreateConfiguration() => SteamConfiguration.Create(b => b
        .WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp)
        .WithHttpClientFactory(_ => new HttpClient(new SocketsHttpHandler())));

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected)
            return;
        _connected = NewTcs<bool>();
        _loggedOn = NewTcs<EResult>();
        _client.Connect();
        if (!await _connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct))
            throw new SteamException("無法連上 Steam 伺服器", "connect_failed");
        _log("connected");
    }

    /// <summary>QR 登入：challenge URL 交給 onChallenge 畫成 QR code（Steam 會換新的，換了再呼叫一次）；等 Steam App 掃描核准。</summary>
    public async Task<SteamLogin> LoginWithQrAsync(string deviceName, Action<string> onChallenge, CancellationToken ct = default)
    {
        var session = await _client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            DeviceFriendlyName = deviceName,
            IsPersistentSession = true,
        });
        onChallenge(session.ChallengeURL);
        session.ChallengeURLChanged = () => onChallenge(session.ChallengeURL);
        _log("qr session started");
        var result = await session.PollingWaitForResultAsync(ct);
        _log("qr approved");
        return new SteamLogin(result.AccountName, result.RefreshToken);
    }

    /// <summary>帳號密碼登入。Steam Guard 由 authenticator 處理（手機核准、手機驗證碼、Email 驗證碼）。</summary>
    public async Task<SteamLogin> LoginWithCredentialsAsync(string deviceName, string username, string password, IAuthenticator authenticator, CancellationToken ct = default)
    {
        var session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            DeviceFriendlyName = deviceName,
            Username = username,
            Password = password,
            IsPersistentSession = true,
            Authenticator = authenticator,
        });
        _log("credentials accepted, waiting for steam guard");
        var result = await session.PollingWaitForResultAsync(ct);
        _log("credentials approved");
        return new SteamLogin(result.AccountName, result.RefreshToken);
    }

    /// <summary>用 refresh token 登入 CM。token 過期或被撤銷時回傳非 OK（例如 AccessDenied、InvalidPassword）。</summary>
    public async Task<EResult> LogOnAsync(SteamLogin login, CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        _loggedOn = NewTcs<EResult>();
        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = login.AccountName,
            AccessToken = login.RefreshToken,
            ShouldRememberPassword = true,
        });
        var result = await _loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        _log($"logon {result}");
        return result;
    }

    public async Task<SteamIdentity> GetIdentityAsync(CancellationToken ct = default)
    {
        if (!IsLoggedOn)
            throw new SteamException("還沒登入 Steam", "not_logged_in");

        string? persona = _friends.GetPersonaName();
        if (string.IsNullOrWhiteSpace(persona))
        {
            await _accountInfo.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            persona = _friends.GetPersonaName();
        }

        ulong steamId = _client.SteamID?.ConvertToUInt64() ?? 0;
        if (steamId == 0 || string.IsNullOrWhiteSpace(persona))
            throw new SteamException("Steam 沒有回傳完整的玩家身分", "identity_incomplete");

        return new SteamIdentity(steamId, persona);
    }

    /// <summary>PICS 查詢的等待上限：逾時丟 TimeoutException（當成查詢失敗）；取消丟 OperationCanceledException（不當成查詢失敗）。</summary>
    private static readonly TimeSpan PicsTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 授權檢查：登入後 Steam 主動送授權清單（package），再用 PICS 把每個 package 展開成 app ID，看有沒有 MT2 與它的 DLC。
    /// DLC 清單從 MT2 的 PICS（extended/listofdlc，沒有就用 depot 的 dlcappid）讀，不寫死。每個查詢都可以取消，也有等待上限。
    /// </summary>
    public async Task<OwnershipResult> CheckOwnershipAsync(CancellationToken ct = default)
    {
        if (!IsLoggedOn)
            throw new SteamException("還沒登入 Steam", "not_logged_in");
        var licenses = await _licenses.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var owned = new HashSet<uint>();
        var requests = licenses.Select(l => new SteamApps.PICSRequest(l.PackageID, l.AccessToken)).ToList();
        foreach (var chunk in requests.Chunk(200))
        {
            var info = await _apps.PICSGetProductInfo(Array.Empty<SteamApps.PICSRequest>(), chunk).ToTask().WaitAsync(PicsTimeout, ct);
            foreach (var package in info.Results!.SelectMany(r => r.Packages.Values))
                foreach (var app in package.KeyValues["appids"].Children)
                    if (uint.TryParse(app.Value, out var id))
                        owned.Add(id);
        }

        var knownDlc = await GetDlcListAsync(ct);
        var ownedDlc = knownDlc.Where(owned.Contains).ToList();
        _log($"licenses {licenses.Count}, owns game {owned.Contains(Mt2Steam.AppId)}, dlc {ownedDlc.Count}/{knownDlc.Count}");
        return new OwnershipResult(owned.Contains(Mt2Steam.AppId), ownedDlc, knownDlc, licenses.Count);
    }

    private async Task<IReadOnlyList<uint>> GetDlcListAsync(CancellationToken ct)
    {
        var tokens = await _apps.PICSGetAccessTokens(Mt2Steam.AppId, null).ToTask().WaitAsync(PicsTimeout, ct);
        ulong token = tokens.AppTokens.TryGetValue(Mt2Steam.AppId, out var t) ? t : 0;
        var info = await _apps.PICSGetProductInfo(new SteamApps.PICSRequest(Mt2Steam.AppId, token), null).ToTask().WaitAsync(PicsTimeout, ct);
        var app = info.Results!.SelectMany(r => r.Apps).Where(a => a.Key == Mt2Steam.AppId).Select(a => a.Value).FirstOrDefault();
        if (app is { MissingToken: true })
            throw new SteamException("Steam 沒有回傳完整的 MT2 產品資訊（缺存取權杖）", "product_info_token");
        return DlcFromAppInfo(app?.KeyValues);
    }

    /// <summary>
    /// 從 MT2 的 PICS app 資訊讀 DLC 清單（extended/listofdlc，加上 depot 的 dlcappid）。
    /// 沒有 app 資訊、或連 depots 都沒有（MT2 一定有本體 depot），是查詢失敗，不是「沒有任何 DLC」（Terra 第 3 輪第 4 條）：
    /// 丟 SteamException，呼叫端照「只用同一個帳號的完整快取」處理，不會把空清單存成完整快取。
    /// </summary>
    public static IReadOnlyList<uint> DlcFromAppInfo(KeyValue? appInfo)
    {
        if (appInfo == null)
            throw new SteamException("Steam 沒有回傳 MT2 的產品資訊", "product_info_missing");
        if (appInfo["depots"].Children.Count == 0)
            throw new SteamException("Steam 回傳的 MT2 產品資訊不完整（沒有 depot）", "product_info_no_depot");
        var dlc = new SortedSet<uint>();
        foreach (var part in (appInfo["extended"]["listofdlc"].Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, out var id))
                dlc.Add(id);
        foreach (var depot in appInfo["depots"].Children)
            if (uint.TryParse(depot["dlcappid"].Value, out var id))
                dlc.Add(id);
        return dlc.ToList();
    }

    public void LogOff()
    {
        if (IsLoggedOn)
            _user.LogOff();
        IsLoggedOn = false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            LogOff();
            _client.Disconnect();
            await Task.Delay(300);
        }
        finally
        {
            _pumpCts.Cancel();
            await _pump;
            _pumpCts.Dispose();
        }
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Message 是臺灣正體中文（記錄檔、測試用）。Key 不是 null 時，啟動器改顯示 strings.xml 裡 steam_error_&lt;Key&gt; 的當下語系版本。</summary>
public sealed class SteamException(string message, string? key = null) : Exception(message)
{
    public string? Key { get; } = key;
}
