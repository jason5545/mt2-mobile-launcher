using System.Security.Cryptography;
using System.Text;

namespace Mt2Mobile.Steam;

/// <summary>
/// 啟動器存的 Steam 登入記著存的時候的存取世代（<see cref="SteamAccessGeneration"/>），遊戲身分記著是哪一次登入存的；這裡判斷還算不算數。
/// 從 Android 的 SteamAccountStore 抽出來，桌面測試測得到。current 一律是 <see cref="SteamAccessGeneration.Peek"/> 讀到的值，讀不到或空的是 null。
/// </summary>
public static class SteamAccountStamps
{
    /// <summary>存著的登入是不是 current 這一代存的。舊版存的沒有記世代（stamp 是 null），只在世代還是初始值時算數。</summary>
    public static bool LoginIsCurrent(string? current, string? loginStamp) =>
        current is not null && (loginStamp == current || (loginStamp is null && current == SteamAccessGeneration.Initial));

    /// <summary>
    /// 這一次登入的識別（Terra 第 11 輪）：存登入時 token 密文（Base64）的 SHA-256。第 12 輪起存登入會換代（一個世代最多一次登入），
    /// 識別留著當第二道：身分記的是哪一次登入，不靠「存登入一定換代」這件事。
    /// 每次存登入都重新加密，AES-GCM 每次用新的 IV，同一個帳號重新登入也不一樣；舊版存的登入也有密文，不用另外補。
    /// 識別和 token 從同一個欄位來，讀到的識別一定是解出那個 token 的那一次登入。
    /// </summary>
    public static string LoginKey(string tokenCiphertext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenCiphertext))).ToLowerInvariant();

    /// <summary>
    /// 跟登入綁在一起的寫入（WriteForLogin）能不能寫：存著的登入是 scope 那一代存的，而且就是這段工作開始時讀到、或自己存的那一次登入。
    /// 同一世代後來又存了別的登入（另一個登入頁），這段工作查到的身分與授權就不寫。
    /// </summary>
    public static bool IsSameLogin(string scopeGeneration, string? loginStamp, string? storedLoginKey, string loginKey) =>
        storedLoginKey == loginKey && LoginIsCurrent(scopeGeneration, loginStamp);

    /// <summary>
    /// 給遊戲用的身分與授權屬於目前的登入：登入是 current 這一代存的，身分是目前這一次登入存的（identityLoginKey 等於 loginKey），
    /// 授權是替這個身分的 Steam ID 查的。
    /// 身分只有在用這一次登入的 token 連上 Steam、寫入時存著的還是這一次登入，才會記上它的識別（WriteForLogin），
    /// 所以身分記的是目前這一次登入，就是目前登入的帳號；換了別的登入，識別就對不上（Terra 第 10、11 輪）。
    /// 別的程序手上的設定快取（:game）整份都是舊的，識別彼此對得上，擋它的是 current：存新登入會換代，舊登入記的世代不是目前的（Terra 第 12 輪）。
    /// 授權綁的是帳號：同一個帳號之前查的授權照舊算數（授權查詢失敗時本來就用這個帳號查的快取）。
    /// 沒有記登入識別的身分（這一版之前存的）、沒有記帳號的授權（更舊的版本存的）都不算，要等同步重寫。
    /// </summary>
    public static bool GameAuthorizationIsCurrent(string? current, string? loginStamp, string? loginKey,
        string? identityLoginKey, ulong identitySteamId, ulong ownershipSteamId) =>
        LoginIsCurrent(current, loginStamp) && loginKey is not null && identityLoginKey == loginKey
        && identitySteamId != 0 && ownershipSteamId == identitySteamId;
}
