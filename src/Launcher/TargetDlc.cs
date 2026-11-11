using System.Text.Json;
using Android.Content;
using Mt2Mobile.Launcher.SteamUi;
using Mt2Mobile.Steam;

namespace Mt2Mobile.Launcher;

/// <summary>target（APK 的 targets/*.json）的 steam.dlc：這個版本支援、會下載與傳給遊戲的 DLC。</summary>
public static class TargetDlc
{
    public static IReadOnlyList<SteamDlcDepotTarget> Read(Context context, string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return Array.Empty<SteamDlcDepotTarget>();
        try
        {
            using var stream = context.Assets!.Open($"targets/{targetId}.json");
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("steam", out var steam) || !steam.TryGetProperty("dlc", out var dlc))
                return Array.Empty<SteamDlcDepotTarget>();
            return dlc.EnumerateArray().Select(d => new SteamDlcDepotTarget(
                d.GetProperty("dlcAppId").GetUInt32(),
                d.GetProperty("depotId").GetUInt32(),
                d.GetProperty("manifestId").GetUInt64())).ToArray();
        }
        catch
        {
            return Array.Empty<SteamDlcDepotTarget>();
        }
    }

    /// <summary>
    /// 畫面上的「DLC 擁有 / 總數」：總數只算 targetId 支援的（Steam 的 DLC 清單還有原聲帶、數位畫冊這類不進遊戲的，不算），
    /// 擁有是其中帳號有的。舊的授權快取沒有 DLC ID 時，用舊的擁有數，最多到總數。
    /// 遊戲頁傳裝好的版本（遊戲實際拿得到的），Steam 下載頁傳 <see cref="SupportedTarget.Latest"/>（下載會抓的）。
    /// </summary>
    public static (int Owned, int Supported) Counts(Context context, SteamAccountStore.Ownership own, string targetId)
    {
        var supported = Read(context, targetId).Select(d => d.DlcAppId).ToHashSet();
        int owned = own.HasOwnedDlcIds ? own.OwnedDlcIds.Count(supported.Contains) : Math.Min(own.LegacyOwnedDlcCount, supported.Count);
        return (owned, supported.Count);
    }
}
