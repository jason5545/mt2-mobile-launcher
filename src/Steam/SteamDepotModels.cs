namespace Mt2Mobile.Steam;

/// <summary>A depot pinned to a manifest. AppId is the parent app that lists this depot in PICS.</summary>
public sealed record SteamDepotSpec(uint AppId, uint DepotId, ulong ManifestId);

public sealed record SteamDlcDepotTarget(uint DlcAppId, uint DepotId, ulong ManifestId)
{
    // This depot is listed in MT2's depots section. dlcAppId is the ownership entitlement, not the parent app for Steam content calls.
    public SteamDepotSpec ToDepotSpec() => new(Mt2Steam.AppId, DepotId, ManifestId);
}

public sealed record SteamTargetDefinition(
    uint DepotId,
    ulong ManifestId,
    string DataDir,
    IReadOnlyList<SteamDlcDepotTarget> Dlc)
{
    public SteamDepotSpec BaseDepot => new(Mt2Steam.AppId, DepotId, ManifestId);
}

public sealed record SteamDlcResolution(
    IReadOnlyList<SteamDlcDepotTarget> DepotsToDownload,
    IReadOnlyList<uint> SupportedOwnedDlcIds,
    IReadOnlyList<uint> UnsupportedOwnedDlcIds);

/// <summary>Steam DLC enablement is based on account ownership and target support, never on installation source or local file presence.</summary>
public static class SteamDlcPolicy
{
    public static SteamDlcResolution Resolve(
        IEnumerable<uint> ownedDlcIds,
        IEnumerable<SteamDlcDepotTarget> targetDlc)
    {
        var owned = ownedDlcIds.Distinct().Order().ToHashSet();
        var depots = targetDlc.OrderBy(x => x.DlcAppId).ThenBy(x => x.DepotId).ToArray();
        var supported = depots.Select(x => x.DlcAppId).Distinct().Order().ToArray();
        var supportedSet = supported.ToHashSet();
        return new SteamDlcResolution(
            depots.Where(x => owned.Contains(x.DlcAppId)).ToArray(),
            owned.Where(supportedSet.Contains).Order().ToArray(),
            owned.Where(x => !supportedSet.Contains(x)).Order().ToArray());
    }

    public static IReadOnlyList<uint> ForGame(
        bool hasSteamAuthorization,
        IEnumerable<uint> ownedDlcIds,
        IEnumerable<SteamDlcDepotTarget> targetDlc)
    {
        if (!hasSteamAuthorization) return Array.Empty<uint>();
        return Resolve(ownedDlcIds, targetDlc).SupportedOwnedDlcIds;
    }
}

public sealed record SteamDepotDownloadProgress(long CompletedBytes, long TotalBytes, int CompletedFiles, int TotalFiles);

public static class SteamDepotErrors
{
    public const string ManifestUnavailableMessage = "Steam 無法提供 target 指定的固定 manifest；下載已停止，不會改抓其他版本。";

    public static SteamException ManifestUnavailable() => new(ManifestUnavailableMessage, "manifest_unavailable");
}
