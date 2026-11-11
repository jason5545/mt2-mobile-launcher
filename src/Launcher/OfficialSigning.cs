using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;
using Mt2Mobile.Launcher.Install;
using Mt2Mobile.Launcher.SteamUi;
using Mt2Mobile.Steam;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 官方建置檢查（docs/phase4.md「官方建置的簽章檢查」）：讀自己 APK 的簽章憑證，跟官方指紋比。
/// 相同就直接開放 Steam 整合；不同就先跳風險提示，使用者同意才開。自行匯入與開遊戲不受影響。
/// 同意記到下一次 APK 安裝或更新為止（指紋、versionCode、lastUpdateTime 有一個不同就重問）；設定裡可以撤回，撤回就登出 Steam。
/// </summary>
public static class OfficialSigning
{
    /// <summary>
    /// 官方 release keystore 的憑證 SHA-256（小寫十六進位，同 apksigner verify --print-certs 的 SHA-256 digest），README 也公布同樣的值。
    /// 2026/10/27 建立（RSA 4096，CN=MT2 Mobile Launcher）。只有 tools/phase4/build.sh 帶 RELEASE=1 編的才是用它簽的；
    /// 其他建置用各台機器的 debug keystore，照舊算非官方，提示的流程開發時就會走到。換金鑰（apksigner rotate）時舊指紋不能拿掉。
    /// </summary>
    static readonly string[] Official = ["ea3c5c9dd9c64a811497a42118d71d2e41557c78fae45bf0b9fb4a293548f2f8"];

    const string Prefs = "signing_consent";

    /// <summary>顯示與同意記錄用的指紋：單一簽署者是憑證歷史的最後一張（目前的簽章）；多個簽署者取第一張，只給人看。</summary>
    public static string Fingerprint(Context c)
    {
        var (multiple, contents, history) = Signers(c);
        return OfficialSigningRule.DisplayFingerprint(multiple, contents, history);
    }

    /// <summary>單一簽署者、目前的憑證在官方清單；多個簽署者一律不是（判斷在 OfficialSigningRule，有桌面測試）</summary>
    public static bool IsOfficial(Context c)
    {
        var (multiple, contents, history) = Signers(c);
        return OfficialSigningRule.IsOfficial(multiple, contents, history, Official);
    }

    /// <summary>SigningInfo 的簽署者與輪替歷史，換成憑證 DER 的 SHA-256（同 apksigner verify --print-certs 的 SHA-256 digest）</summary>
    static (bool Multiple, string[] Contents, string[] History) Signers(Context c)
    {
        var si = c.PackageManager!.GetPackageInfo(c.PackageName!, PackageInfoFlags.SigningCertificates)!.SigningInfo!;
        static string[] Hash(Signature[]? certs) => (certs ?? []).Select(cert => Util.Hex(SHA256.HashData(cert.ToByteArray()!))).ToArray();
        return si.HasMultipleSigners
            ? (true, Hash(si.GetApkContentsSigners()), [])
            : (false, [], Hash(si.GetSigningCertificateHistory()));
    }

    /// <summary>官方指紋（顯示用）；還沒有就是 null</summary>
    public static string? OfficialFingerprint => Official.FirstOrDefault();

    public static bool Allowed(Context c) => IsOfficial(c) || Consented(c);

    public static bool Consented(Context c) => Get(c).GetString("key", null) == Key(c);

    /// <summary>同意過、但同意的是另一個 APK（安裝或更新過）：Steam 身分與 DLC 要重新同意才會載入。撤回過（Revoke 清掉）的不算</summary>
    public static bool NeedsReconsent(Context c) =>
        !IsOfficial(c) && Get(c).GetString("key", null) is { } key && key != Key(c);

    public static void Accept(Context c) =>
        Get(c).Edit()!.PutString("key", Key(c))!.PutString("at", DateTime.UtcNow.ToString("O"))!.Commit();

    /// <summary>
    /// 撤回同意：Steam 功能關掉、已登入的帳號登出（刪 token）。回傳 false：撤回只在這個程序生效（世代檔寫不了），
    /// 呼叫端要給使用者看 RevokeNotice。
    /// </summary>
    public static bool Revoke(Context c)
    {
        Get(c).Edit()!.Clear()!.Commit();
        return SteamAccountStore.Clear(c);
    }

    /// <summary>指紋、versionCode、lastUpdateTime：有一個不同就算另一個 APK，要重問</summary>
    static string Key(Context c)
    {
        var info = c.PackageManager!.GetPackageInfo(c.PackageName!, 0)!;
        return $"{Fingerprint(c)}|{info.LongVersionCode}|{info.LastUpdateTime}";
    }

    static ISharedPreferences Get(Context c) => c.GetSharedPreferences(Prefs, FileCreationMode.Private)!;
}
