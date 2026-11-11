using Android.Content;
using Mt2Mobile.Steam;

namespace Mt2Mobile.Launcher.SteamUi;

/// <summary>
/// 啟動器各程序（介面、:install、:game）共用的 Steam 存取世代，放在 files/steam/。規則與限制見 <see cref="SteamAccessGeneration"/>：
/// 登出、撤回同意、token 失效都換代；進行中的 Steam 工作（Cloud 同步、登入、授權檢查、Steam 下載）在換代後停下，寫入也會被擋下。
/// </summary>
public static class SteamAccess
{
    public const string Initial = SteamAccessGeneration.Initial;

    private static SteamAccessGeneration For(Context c) =>
        new(Path.Combine(c.FilesDir!.AbsolutePath, "steam"), LauncherLog.Write);

    public static string Current(Context c) => For(c).Current();

    /// <summary>只讀、不補寫；空的（撤回後還沒有新世代）回 null。</summary>
    public static string? Peek(Context c) => For(c).Peek();

    public static SteamAccessScope Begin(Context c) => For(c).Begin();

    /// <summary>OnlyInThisProcess：世代檔整個寫不了或拿不到跨程序的鎖，別的程序不保證看得到這次撤回（見 SteamAccessGeneration.Revoke）。</summary>
    public static SteamAccessRevoke Revoke(Context c, Action clear) => For(c).Revoke(clear);

    public static SteamAccessRevoke RevokeIfCurrent(Context c, SteamAccessScope scope, Action clear) => For(c).RevokeIfCurrent(scope, clear);

    /// <summary>這個程序撤回過、別的程序還不保證看得到（見 SteamAccessGeneration.RevokePending）。</summary>
    public static bool RevokePending(Context c) => For(c).RevokePending;

    /// <summary>別的程序回報這一代撤回沒有完成：同步記下，不拿鎖（見 SteamAccessGeneration.NoteRevokeReported）。</summary>
    public static void NoteRevokeReported(Context c, string generation) => For(c).NoteRevokeReported(generation);

    public static SteamAccessRevoke RevokeIfGeneration(Context c, string generation, Action clear) => For(c).RevokeIfGeneration(generation, clear);

    public static void WriteIfCurrent(Context c, SteamAccessScope scope, Action write, Action rollback) =>
        For(c).WriteIfCurrent(scope, write, rollback);

    /// <summary>存新登入：換代，scope 改記新世代（見 SteamAccessGeneration.RenewForLogin）。</summary>
    public static void RenewForLogin(Context c, SteamAccessScope scope, Action write, Action rollback) =>
        For(c).RenewForLogin(scope, write, rollback);
}
