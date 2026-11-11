namespace Mt2Mobile.Steam.SteamCloud;

public sealed record SteamCloudBaseline(string Sha1, bool CloudPresent = true);

public enum SteamCloudSyncAction
{
    None,
    Download,
    Upload,
    UpdateState,
    Conflict,
    KeepLocalAfterCloudDelete,
    ForgetState,
    Cancelled,
    /// <summary>帳號的第一次同步選了 Cloud：手機獨有的檔先備份、驗證，再從手機移除。</summary>
    MoveLocalToBackup,
}

public enum SteamCloudConflictChoice
{
    Cancel,
    UseCloud,
    UseDevice,
}

/// <summary>Pure decision table used by the synchronizer and desktop unit tests.</summary>
public static class SteamCloudSyncDecision
{
    /// <param name="firstSyncForAccount">
    /// 這個 Steam 帳號在這支手機上還沒有同步基準（第一次登入、換了帳號、舊版沒記帳號的 state）。
    /// 這時手機上的檔不知道屬於誰：只有 Cloud 有的檔可以直接下載，手機獨有的檔跟內容不同的檔一樣要問。
    /// </param>
    public static SteamCloudSyncAction Decide(
        SteamCloudBaseline? previous,
        string? localSha1,
        string? cloudSha1,
        bool firstSyncForAccount = false)
    {
        ValidateSha1(localSha1, nameof(localSha1));
        ValidateSha1(cloudSha1, nameof(cloudSha1));
        if (previous is not null)
            ValidateSha1(previous.Sha1, nameof(previous));

        bool localExists = localSha1 is not null;
        bool cloudExists = cloudSha1 is not null;

        if (previous is null)
        {
            if (!localExists && !cloudExists) return SteamCloudSyncAction.None;
            if (!localExists) return SteamCloudSyncAction.Download;
            if (!cloudExists) return firstSyncForAccount ? SteamCloudSyncAction.Conflict : SteamCloudSyncAction.Upload;
            return Same(localSha1, cloudSha1) ? SteamCloudSyncAction.UpdateState : SteamCloudSyncAction.Conflict;
        }

        if (!localExists && !cloudExists)
            return SteamCloudSyncAction.ForgetState;

        if (!localExists)
            return SteamCloudSyncAction.Download;

        if (!cloudExists)
        {
            if (!previous.CloudPresent)
                return Same(localSha1, previous.Sha1) ? SteamCloudSyncAction.None : SteamCloudSyncAction.Upload;

            return Same(localSha1, previous.Sha1)
                ? SteamCloudSyncAction.KeepLocalAfterCloudDelete
                : SteamCloudSyncAction.Conflict;
        }

        bool localChanged = !Same(localSha1, previous.Sha1);
        bool cloudChanged = !previous.CloudPresent || !Same(cloudSha1, previous.Sha1);

        if (!localChanged && !cloudChanged) return SteamCloudSyncAction.None;
        if (!localChanged) return SteamCloudSyncAction.Download;
        if (!cloudChanged) return SteamCloudSyncAction.Upload;
        return Same(localSha1, cloudSha1) ? SteamCloudSyncAction.UpdateState : SteamCloudSyncAction.Conflict;
    }

    /// <summary>
    /// 使用者的選擇套到一個檔。選 Cloud 而 Cloud 沒有這個檔時：同一個帳號是 Cloud 刪了檔，手機的照舊保留；
    /// 帳號的第一次同步是手機獨有的檔，先備份再從手機移除（Jason 10/27 定）。
    /// </summary>
    public static SteamCloudSyncAction ResolveConflict(
        SteamCloudConflictChoice choice,
        bool localExists,
        bool cloudExists,
        bool firstSyncForAccount = false)
    {
        if (choice == SteamCloudConflictChoice.Cancel)
            return SteamCloudSyncAction.Cancelled;

        if (choice == SteamCloudConflictChoice.UseCloud)
        {
            if (cloudExists) return SteamCloudSyncAction.Download;
            if (!localExists) return SteamCloudSyncAction.None;
            return firstSyncForAccount ? SteamCloudSyncAction.MoveLocalToBackup : SteamCloudSyncAction.KeepLocalAfterCloudDelete;
        }

        if (localExists) return SteamCloudSyncAction.Upload;
        return cloudExists ? SteamCloudSyncAction.Download : SteamCloudSyncAction.None;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void ValidateSha1(string? value, string name)
    {
        if (value is not null && (value.Length != 40 || value.Any(c => !Uri.IsHexDigit(c))))
            throw new ArgumentException("SHA1 must contain 40 hexadecimal characters.", name);
    }
}
