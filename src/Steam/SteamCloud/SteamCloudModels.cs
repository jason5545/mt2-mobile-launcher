using System.Security.Cryptography;
using System.Text;

namespace Mt2Mobile.Steam.SteamCloud;

public sealed record SteamCloudFileRecord(
    string RemoteFileName,
    string LocalRelativePath,
    string RootPrefix,
    string FileName,
    string MachineName,
    uint PathPrefixIndex,
    uint MachineNameIndex,
    long Size,
    DateTimeOffset ModifiedAtUtc,
    string Sha1,
    bool IsPresent = true);

public sealed record SteamCloudManifest(
    ulong ChangeNumber,
    bool IsOnlyDelta,
    List<SteamCloudFileRecord> Changes);

public sealed class SteamCloudSyncState
{
    public int Schema { get; set; } = 1;
    /// <summary>這份基準屬於哪個 Steam 帳號（<see cref="SteamCloudAccount.KeyFor"/>）。沒有或對不上就當成這個帳號的第一次同步。</summary>
    public string? AccountKey { get; set; }
    public ulong ChangeNumber { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Dictionary<string, SteamCloudBaseline> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SteamCloudFileRecord> CloudFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 綁 Cloud 基準用的帳號鍵：SteamID 的 SHA-256。只在本機比對，不寫 log、不進版控；
/// 它還是可以拿來關聯同一個身分，不能當成匿名資料。
/// </summary>
public static class SteamCloudAccount
{
    public static string KeyFor(ulong steamId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "mt2-mobile-launcher/steam-cloud/" + steamId.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant();
}

public sealed record SteamCloudUpload(
    string RemoteFileName,
    string LocalRelativePath,
    byte[] Bytes,
    string Sha1,
    DateTimeOffset ModifiedAtUtc);

public interface ISteamCloudTransport
{
    Task<SteamCloudManifest> GetManifestAsync(uint appId, ulong syncedChangeNumber, CancellationToken cancellationToken = default);

    Task<byte[]> DownloadFileAsync(
        uint appId,
        string remoteFileName,
        long expectedRawSize,
        string expectedSha1,
        Action<string>? onHostObserved = null,
        CancellationToken cancellationToken = default);

    Task UploadBatchAsync(
        uint appId,
        string machineName,
        IReadOnlyList<SteamCloudUpload> files,
        Action<string>? onHostObserved = null,
        CancellationToken cancellationToken = default);
}

public enum SteamCloudSyncResultKind
{
    Synced,
    ConflictRequired,
    Cancelled,
}

/// <summary>
/// 要使用者選的一個檔。DeviceSha1／CloudSha1 是 null 表示那一邊沒有這個檔；
/// CloudDeleted 是同一個帳號的 Cloud 刪了原本有的檔（帳號的第一次同步沒有基準，不會是 true）。
/// </summary>
public sealed record SteamCloudConflictFile(
    string LocalRelativePath,
    DateTimeOffset? DeviceModifiedAtUtc,
    DateTimeOffset? CloudModifiedAtUtc,
    string? DeviceSha1,
    string? CloudSha1,
    bool CloudDeleted = false)
{
    public bool SameVersions(SteamCloudConflictFile other) =>
        string.Equals(LocalRelativePath, other.LocalRelativePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(DeviceSha1, other.DeviceSha1, StringComparison.OrdinalIgnoreCase)
        && string.Equals(CloudSha1, other.CloudSha1, StringComparison.OrdinalIgnoreCase)
        && CloudDeleted == other.CloudDeleted;
}

/// <summary>
/// 使用者對一份衝突清單的選擇。只套用到 Shown 裡、而且兩邊版本跟顯示時一模一樣的檔；
/// 帳號不同、多了沒顯示過的檔、或任何一個顯示過的檔變了，同步就不寫任何東西，回傳新的清單重新問。
/// </summary>
public sealed record SteamCloudConflictApproval(
    SteamCloudConflictChoice Choice,
    string AccountKey,
    IReadOnlyList<SteamCloudConflictFile> Shown)
{
    public static SteamCloudConflictApproval For(SteamCloudSyncResult shown, SteamCloudConflictChoice choice) =>
        new(choice, shown.AccountKey, shown.Conflicts);
}

public sealed record SteamCloudSyncResult(
    SteamCloudSyncResultKind Kind,
    int Downloaded,
    int Uploaded,
    int Unchanged,
    IReadOnlyList<SteamCloudConflictFile> Conflicts)
{
    public int CloudFileCount { get; init; }
    /// <summary>衝突清單是哪個帳號的；選擇要帶回同一個帳號鍵。</summary>
    public string AccountKey { get; init; } = "";
    /// <summary>這個帳號在這支手機上的第一次同步：選 Cloud 會把手機獨有的檔移到備份（對話框要講）。</summary>
    public bool FirstSyncForAccount { get; init; }
    /// <summary>選 Cloud 時會從手機移到備份的檔數（只有帳號的第一次同步會有）。</summary>
    public int DeviceOnlyCount => FirstSyncForAccount ? Conflicts.Count(c => c.DeviceSha1 is not null && c.CloudSha1 is null) : 0;

    public static SteamCloudSyncResult Conflict(IReadOnlyList<SteamCloudConflictFile> conflicts, int cloudFileCount,
        string accountKey = "", bool firstSyncForAccount = false) =>
        new(SteamCloudSyncResultKind.ConflictRequired, 0, 0, 0, conflicts)
        {
            CloudFileCount = cloudFileCount,
            AccountKey = accountKey,
            FirstSyncForAccount = firstSyncForAccount,
        };

    public static SteamCloudSyncResult Cancel() =>
        new(SteamCloudSyncResultKind.Cancelled, 0, 0, 0, Array.Empty<SteamCloudConflictFile>());
}

/// <summary>同步途中手機的存檔被別人改了（通常是遊戲）：已經中止，state 沒寫，下次同步重新比對。</summary>
public sealed class SteamCloudLocalChangedException(string message) : IOException(message);

/// <summary>讀清單之後 Cloud 又變了（通常是別台裝置上傳）：上傳前中止，state 沒寫，下次同步重新比對。</summary>
public sealed class SteamCloudRemoteChangedException(string message) : Exception(message);
