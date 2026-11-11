namespace Mt2Mobile.Steam;

/// <summary>
/// 官方建置的判斷（docs/phase4.md「官方建置的簽章檢查」），從 Android 的 SigningInfo 拿出來的指紋（憑證 DER 的 SHA-256，小寫十六進位）。
/// 不碰 Android，桌面測試也能跑；啟動器的 OfficialSigning 讀 SigningInfo 後交給這裡。
/// </summary>
public static class OfficialSigningRule
{
    /// <summary>顯示與同意記錄用：單一簽署者是憑證歷史的最後一張（目前的簽章）；多個簽署者取第一張，只給人看。</summary>
    public static string DisplayFingerprint(bool hasMultipleSigners, IReadOnlyList<string> contentSigners, IReadOnlyList<string> history) =>
        hasMultipleSigners ? contentSigners[0] : history[^1];

    /// <summary>
    /// 官方建置：只有一個簽署者，而且目前的憑證（輪替過也是歷史的最後一張）在官方清單裡。
    /// 多個簽署者一律當非官方，不管其中有沒有官方憑證（Terra 第 1 輪第 8 條）。
    /// </summary>
    public static bool IsOfficial(bool hasMultipleSigners, IReadOnlyList<string> contentSigners, IReadOnlyList<string> history,
        IReadOnlyCollection<string> official) =>
        !hasMultipleSigners && history.Count > 0 && official.Contains(history[^1]);
}
