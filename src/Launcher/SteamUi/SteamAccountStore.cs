using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Mt2Mobile.Steam;

namespace Mt2Mobile.Launcher.SteamUi;

/// <summary>
/// 保存 Steam 登入：帳號名稱明文（畫面要顯示），refresh token 用 Android Keystore 的 AES-GCM 金鑰加密（金鑰不出 Keystore）。
/// 登出刪掉兩者與授權快取。密碼、Steam Guard 碼從不經過這裡。
/// 登入記著存的時候的存取世代（SteamAccess）：世代換了（登出、撤回、別的程序發現 token 失效）就不算登入，
/// 這樣別的程序清掉之後，這個程序的 SharedPreferences 快取還留著舊值也不會被當真。給遊戲用的身分記著是哪一次登入存的
/// （登入識別，Terra 第 10、11 輪），判斷在 <see cref="SteamAccountStamps"/>。
/// 寫入都要帶開始工作時的 scope，世代換了就不寫（OperationCanceledException）；身分與授權還要帶登入識別，存著的換成別次登入也不寫。
/// </summary>
public static class SteamAccountStore
{
    private const string Prefs = "steam_account";
    private const string KeyAlias = "mt2launcher_steam_refresh_token";

    /// <param name="SteamId">查這份授權時登入的帳號；舊版存的沒有，是 0。</param>
    public sealed record Ownership(
        bool OwnsGame,
        IReadOnlyList<uint> OwnedDlcIds,
        int KnownDlc,
        DateTimeOffset CheckedAt,
        int LegacyOwnedDlcCount,
        bool HasOwnedDlcIds,
        ulong SteamId)
    {
        public int OwnedDlc => HasOwnedDlcIds ? OwnedDlcIds.Count : LegacyOwnedDlcCount;
    }

    /// <param name="LoginKey">存的時候的登入識別（SteamAccountStamps.LoginKey）；這一版之前存的沒有，是 null（不給遊戲用，同步時重寫）。</param>
    public sealed record GameIdentity(ulong SteamId, string PersonaName, string? LoginKey);

    public static string? AccountName(Context context) =>
        Get(context).GetString("account_name", null);

    public static bool IsLoggedIn(Context context)
    {
        var prefs = Get(context);
        return prefs.GetString("account_name", null) != null && prefs.GetString("token_ct", null) != null
            && StampIsCurrent(context, prefs);
    }

    /// <summary>
    /// 存登入，回傳這一次登入的識別（之後寫身分與授權要帶著它）。scope 是開始登入時記的世代；登入途中被撤回（世代換了）就不寫，token 不會被寫回去。
    /// 存登入會換代（SteamAccess.RenewForLogin，Terra 第 12 輪）：舊登入的 Cloud 同步、:install 的下載、另一個登入頁都停下來，
    /// :game 手上的設定快取記的是舊世代、不算登入；scope 改記新世代，登入流程接著用它寫身分與授權。
    /// 上一個登入留下的遊戲身分與授權一起清掉（Terra 第 10 輪的保險；擋住別的帳號身分的是登入的世代與身分記的登入識別）：
    /// 這時還不知道新登入的 Steam ID（SteamLogin 只有帳號名稱和 token），同一個帳號也清，接著的登入流程會重查。
    /// </summary>
    public static string Save(Context context, SteamLogin login, SteamAccessScope scope)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        var ct = Convert.ToBase64String(cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(login.RefreshToken))!);
        SteamAccess.RenewForLogin(context, scope, () =>
        {
            var edit = Get(context).Edit()!;
            foreach (var key in GameCacheKeys)
                edit.Remove(key);
            edit.PutString("account_name", login.AccountName);
            edit.PutString("token_iv", Convert.ToBase64String(cipher.GetIV()!));
            edit.PutString("token_ct", ct);
            edit.PutString(GenerationKey, scope.Generation);
            edit.Commit();
        }, rollback: () => ClearLocked(context));
        return SteamAccountStamps.LoginKey(ct);
    }

    /// <summary>跟登入的帳號綁在一起的遊戲身分與授權快取（SaveGameIdentity、SaveOwnership 寫的），存新登入時清掉。</summary>
    private static readonly string[] GameCacheKeys =
    [
        "game_steam_id", "game_persona_name", IdentityLoginKey,
        "owns_game", "owned_dlc", "owned_dlc_ids", "known_dlc", "checked_at", "owned_for_steam_id",
    ];

    /// <summary>
    /// 讀回這段工作那一代的登入：呼叫端先 Begin 拿 scope 再讀（Terra 第 13 輪），存著的登入要是 scope 那一代存的、而且那一代還是目前的世代。
    /// 不是就回 null（中途登出或換了登入，讀到的不是這段工作該用的 token）；Keystore 的金鑰不見了（例如系統重設）或解不開也回 null。
    /// 回 null 時要不要清，用 <see cref="HasLoginOf"/> 分辨。
    /// </summary>
    /// <param name="loginKey">這一次登入的識別，跟 token 從同一個欄位算出來；之後寫身分與授權要帶著它。沒有登入是 null。</param>
    public static SteamLogin? Load(Context context, SteamAccessScope scope, out string? loginKey)
    {
        loginKey = null;
        var prefs = Get(context);
        string? name = prefs.GetString("account_name", null), iv = prefs.GetString("token_iv", null), ct = prefs.GetString("token_ct", null);
        if (name == null || iv == null || ct == null || !StampIsCurrent(context, prefs)
            || !SteamAccountStamps.LoginIsCurrent(scope.Generation, prefs.GetString(GenerationKey, null)))
            return null;
        try
        {
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, Convert.FromBase64String(iv)));
            var token = System.Text.Encoding.UTF8.GetString(cipher.DoFinal(Convert.FromBase64String(ct))!);
            loginKey = SteamAccountStamps.LoginKey(ct);
            return new SteamLogin(name, token);
        }
        catch (Exception e)
        {
            LauncherLog.Write($"steam token unreadable: {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// 存著的登入是 scope 那一代存的（不管 token 解不解得開）。Load 回 null 時用：是，就是這一代的登入壞了（Keystore 的金鑰不見了），
    /// 用 <see cref="ClearIfCurrent"/> 清（換過代就不清）；不是，就不是這段工作的登入（中途登出或換了登入），什麼都不清（Terra 第 13 輪）。
    /// </summary>
    public static bool HasLoginOf(Context context, SteamAccessScope scope)
    {
        var prefs = Get(context);
        return prefs.GetString("account_name", null) != null && prefs.GetString("token_ct", null) != null
            && SteamAccountStamps.LoginIsCurrent(scope.Generation, prefs.GetString(GenerationKey, null));
    }

    /// <summary>存授權結果，記下是哪個帳號查的。scope 的世代換了，或存著的登入不是 loginKey 那一次，就不寫。</summary>
    public static void SaveOwnership(Context context, OwnershipResult result, ulong steamId, SteamAccessScope scope, string loginKey) =>
        WriteForLogin(context, scope, loginKey, prefs => prefs.Edit()!
            .PutBoolean("owns_game", result.OwnsGame)
            .PutInt("owned_dlc", result.OwnedDlc.Count)
            .PutString("owned_dlc_ids", System.Text.Json.JsonSerializer.Serialize(result.OwnedDlc.Distinct().Order().ToArray()))
            .PutInt("known_dlc", result.KnownDlc.Count)
            .PutLong("checked_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .PutString("owned_for_steam_id", steamId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Commit());

    /// <summary>存給遊戲用的身分，記下登入識別：遊戲只用目前這一次登入存的身分（Terra 第 10、11 輪）。</summary>
    public static void SaveGameIdentity(Context context, SteamIdentity identity, SteamAccessScope scope, string loginKey) =>
        WriteForLogin(context, scope, loginKey, prefs => prefs.Edit()!
            .PutString("game_steam_id", identity.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .PutString("game_persona_name", identity.PersonaName)
            .PutString(IdentityLoginKey, loginKey)
            .Commit());

    public static GameIdentity? LoadGameIdentity(Context context)
    {
        var prefs = Get(context);
        var id = prefs.GetString("game_steam_id", null);
        var name = prefs.GetString("game_persona_name", null);
        return ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var steamId)
            && steamId != 0 && !string.IsNullOrWhiteSpace(name)
            ? new GameIdentity(steamId, name, prefs.GetString(IdentityLoginKey, null))
            : null;
    }

    /// <summary>
    /// 給遊戲用的身分與授權都在、而且屬於目前的登入：登入是目前世代存的，身分是目前這一次登入存的，授權是替這個身分的 Steam ID 查的
    /// （判斷在 SteamAccountStamps.GameAuthorizationIsCurrent，Terra 第 10、11 輪）。世代只讀一次。
    /// 遊戲程序（GameActivity）開機時也用這個，從磁碟讀。
    /// </summary>
    public static bool HasCompleteGameAuthorization(Context context)
    {
        var prefs = Get(context);
        var ct = prefs.GetString("token_ct", null);
        return prefs.GetString("account_name", null) != null && ct != null
            && LoadGameIdentity(context) is { } identity
            && LoadOwnership(context) is { HasOwnedDlcIds: true } ownership
            && SteamAccountStamps.GameAuthorizationIsCurrent(TryPeek(context), prefs.GetString(GenerationKey, null),
                SteamAccountStamps.LoginKey(ct), identity.LoginKey, identity.SteamId, ownership.SteamId);
    }

    public static Ownership? LoadOwnership(Context context)
    {
        var prefs = Get(context);
        long at = prefs.GetLong("checked_at", 0);
        if (at == 0)
            return null;
        var encoded = prefs.GetString("owned_dlc_ids", null);
        uint[] ids = [];
        bool hasIds = encoded is not null;
        if (encoded is not null)
        {
            try { ids = System.Text.Json.JsonSerializer.Deserialize<uint[]>(encoded) ?? []; }
            catch (System.Text.Json.JsonException) { hasIds = false; }
        }
        ulong.TryParse(prefs.GetString("owned_for_steam_id", null), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var owner);
        return new Ownership(
            prefs.GetBoolean("owns_game", false),
            ids,
            prefs.GetInt("known_dlc", 0),
            DateTimeOffset.FromUnixTimeSeconds(at),
            prefs.GetInt("owned_dlc", ids.Length),
            hasIds,
            owner);
    }

    /// <summary>撤回沒有完成時給使用者看的話（Terra 第 4 輪：不能默默當成成功，也不宣稱有保證）。</summary>
    /// 每次取都照當下語系讀資源；MainActivity 用 == 比對時兩邊在同一個流程裡取，語系相同。
    public static string RevokeIncompleteMessage => Strings.Get(Resource.String.steam_revoke_incomplete_message);

    /// <summary>
    /// 使用者登出、撤回同意：換代（進行中的 Steam 工作都會停）並刪掉登入、身分與授權快取。
    /// 回傳 false：世代檔整個寫不了，或拿不到跨程序的鎖（Terra 第 5 輪），別的程序不保證看得到這次撤回。這個程序已經 fail closed（舊工作取消、新工作開不起來），
    /// 也盡力停掉了 :install，但不保證；呼叫端要把 <see cref="RevokeIncompleteMessage"/> 給使用者看。
    /// </summary>
    public static bool Clear(Context context)
    {
        if (SteamAccess.Revoke(context, () => ClearLocked(context)) == SteamAccessRevoke.Revoked)
            return true;
        RevokeIncomplete(context);
        return false;
    }

    /// <summary>
    /// 工作發現 token 失效時用：只有還是開始時的世代才清。使用者已經登出又登入別的帳號時什麼都不做，舊的工作不會清掉新的登入。
    /// 回傳 false 的意思同 <see cref="Clear"/>：換代寫不了或拿不到鎖，撤回只保證在這個程序生效。拿不到鎖時不清（Terra 第 6 輪：沒有鎖就可能清掉別的程序剛存的新登入）。
    /// </summary>
    public static bool ClearIfCurrent(Context context, SteamAccessScope scope)
    {
        switch (SteamAccess.RevokeIfCurrent(context, scope, () => ClearLocked(context)))
        {
            case SteamAccessRevoke.NotCurrent:
                LauncherLog.Write("steam login already changed; stale work did not clear it");
                return true;
            case SteamAccessRevoke.Revoked:
                return true;
            default:
                RevokeIncomplete(context);
                return false;
        }
    }

    /// <summary>
    /// :install 撤回沒有完成、把那一代傳過來時（RevokeRelay），介面程序替它再撤回一次：世代已經換過就什麼都不做（不清新的登入）。
    /// 回傳 false 的意思同 <see cref="Clear"/>：介面也撤回不了，這個程序已經 fail closed（Cloud 工作取消），呼叫端要給使用者看警告。
    /// </summary>
    public static bool ClearIfGeneration(Context context, string generation)
    {
        if (SteamAccess.RevokeIfGeneration(context, generation, () => ClearLocked(context)) != SteamAccessRevoke.OnlyInThisProcess)
            return true;
        RevokeIncomplete(context);
        return false;
    }

    /// <summary>
    /// 撤回只保證在這個程序生效（世代檔整個寫不了，或拿不到跨程序的鎖）：盡力停掉 :install，它是唯一會在別的程序用 Steam 的地方（下載），每一步都能從頭重跑。
    /// 先用 ActivityManager.getRunningAppProcesses 找同一個 uid、名稱是「套件名:install」的程序，Process.killProcess 直接結束
    /// （Android 文件：同一個 app 建立的程序可以互相 kill；getRunningAppProcesses 文件說只用於除錯或程序管理介面，沒有程序時回 null，
    /// 新版 Android 有沒有限制只回自己的程序，文件沒寫）。找不到再送 stop-now（要等 :install 收到 intent 才會結束）。
    /// 在 :install 自己裡面呼叫時不殺自己，只記 log：InstallService 另外發通知警告使用者，安裝結束時照樣結束自己的程序。
    /// </summary>
    private static void RevokeIncomplete(Context context)
    {
        LauncherLog.Write("steam access revoke only applies to this process (generation or lock not writable); stopping the install process");
        if (Android.App.Application.ProcessName?.EndsWith(":install", StringComparison.Ordinal) == true)
            return;
        bool killed = false;
        try
        {
            var target = context.PackageName + ":install";
            var manager = (Android.App.ActivityManager?)context.GetSystemService(Context.ActivityService);
            foreach (var process in manager?.RunningAppProcesses ?? [])
            {
                if (process.ProcessName != target || process.Uid != Android.OS.Process.MyUid()
                    || process.Pid <= 0 || process.Pid == Android.OS.Process.MyPid())
                    continue;
                Android.OS.Process.KillProcess(process.Pid);
                killed = true;
                LauncherLog.Write("install process killed after an incomplete steam access revoke");
            }
        }
        catch (Exception e)
        {
            LauncherLog.Write($"could not look up the install process: {e.GetType().Name}");
        }
        if (killed)
            return;
        try
        {
            context.StartService(new Intent(context, typeof(InstallService)).PutExtra("action", "stop-now"));
        }
        catch (Exception e)
        {
            LauncherLog.Write($"could not stop the install process: {e.GetType().Name}");
        }
    }

    private const string GenerationKey = "access_generation";
    private const string IdentityLoginKey = "game_login";

    /// <summary>存著的登入是不是目前世代的。舊版存的沒有記世代，只在世代還是初始值時算數。讀不到世代就當不是。</summary>
    private static bool StampIsCurrent(Context context, ISharedPreferences prefs) =>
        SteamAccountStamps.LoginIsCurrent(TryPeek(context), prefs.GetString(GenerationKey, null));

    /// <summary>目前的世代，只讀不補寫：空的（撤回後還沒有新世代）回 null，讀不到也回 null（記一筆 log）。</summary>
    private static string? TryPeek(Context context)
    {
        try { return SteamAccess.Peek(context); }
        catch (Exception e)
        {
            LauncherLog.Write($"steam access generation unreadable: {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// 跟登入綁在一起的寫入：scope 還是目前世代，存著的登入是這個世代的，而且就是 loginKey 那一次登入，才寫
    /// （Terra 第 11 輪：同一世代又存了別的登入，例如另一個登入頁，這段工作查到的身分與授權不寫）。
    /// 跟 Save 一樣在 WriteIfCurrent 的鎖裡，核對之後、寫完之前不會有別的 Save 插進來。
    /// </summary>
    private static void WriteForLogin(Context context, SteamAccessScope scope, string loginKey, Action<ISharedPreferences> write) =>
        SteamAccess.WriteIfCurrent(context, scope, () =>
        {
            var prefs = Get(context);
            var ct = prefs.GetString("token_ct", null);
            if (!SteamAccountStamps.IsSameLogin(scope.Generation, prefs.GetString(GenerationKey, null),
                    ct is null ? null : SteamAccountStamps.LoginKey(ct), loginKey))
                throw new System.OperationCanceledException("The Steam login changed while this work was running.");
            write(prefs);
        }, rollback: () => ClearLocked(context));

    private static void ClearLocked(Context context)
    {
        Get(context).Edit()!.Clear().Commit();
        try
        {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);
            if (ks.ContainsAlias(KeyAlias))
                ks.DeleteEntry(KeyAlias);
        }
        catch (Exception e)
        {
            LauncherLog.Write($"keystore delete failed: {e.GetType().Name}");
        }
    }

    private static ISharedPreferences Get(Context context) =>
        context.GetSharedPreferences(Prefs, FileCreationMode.Private)!;

    private static IKey GetOrCreateKey()
    {
        var ks = KeyStore.GetInstance("AndroidKeyStore")!;
        ks.Load(null);
        if (ks.GetKey(KeyAlias, null) is IKey existing)
            return existing;
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey()!;
    }
}
