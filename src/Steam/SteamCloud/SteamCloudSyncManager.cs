using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mt2Mobile.Steam.SteamCloud;

/// <summary>
/// 照決策表同步手機存檔與 Steam Cloud。順序：
/// 讀 state（帳號對不上就丟掉整份基準與 Cloud 清單）→ 取清單 → 比對 → 有要問的就先回傳，什麼都不寫 →
/// 下載要用的 Cloud 檔、要被上傳蓋掉的 Cloud 版本（大小與 SHA1 要跟清單相同）→ 複核手機檔 → 寫備份並驗證 →
/// 逐檔檢查世代、複核後替換或移除手機檔 → 重新取清單確認 Cloud 沒變 → 上傳 → 檢查世代 → 寫 state。
///
/// 照實寫的限制：
/// - Steam 的上傳協定沒有「預期舊版本」之類的前置條件（SteamKit2 3.5.0 的 ClientBeginFileUpload、ClientCommitFileUpload）。
///   上傳前最後一次取清單到伺服器 commit 之間，別台裝置上傳的同名檔仍可能被蓋掉，而且沒有備份；這段空窗只能縮短，關不掉。
/// - 手機檔的複核只對遵守 cloud/game.lock 的寫入者有效（啟動器每次同步全程持有，遊戲開機時要拿到才開）。
///   不遵守這把鎖的程式在最後一次複核到 rename 之間寫入，仍然會被蓋掉。
/// - checkpoint（存取世代）在每次替換、移除、上傳與寫 state 之前檢查；已經送到 Steam 的請求，取消之後仍可能在伺服器端完成。
///   替換、移除手機檔、寫 state、刪舊備份這些本機寫入另外交給 localWrite 包起來（Terra 第 13 輪）：呼叫端在跨程序的鎖裡確認世代才執行，
///   換代（登出、撤回、存新登入）不會夾在檢查和寫入之間。寫入完成之後才換代，算換代之前完成的動作。
/// </summary>
public sealed class SteamCloudSyncManager
{
    public const int BackupsPerCategory = 10;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    // <建立順序>-<UTC 時間>-<亂數>：保留與刪除只看建立順序，時間只給人看（手機時鐘可能往回調）
    private static readonly Regex BackupDirectoryRegex = new("^(\\d{10})-\\d{8}T\\d{6}\\.\\d{3}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _persistentRoot;
    private readonly string _cloudRoot;
    private readonly TimeProvider _time;

    public SteamCloudSyncManager(string persistentRoot, string cloudRoot, TimeProvider? time = null)
    {
        _persistentRoot = Path.GetFullPath(persistentRoot);
        _cloudRoot = Path.GetFullPath(cloudRoot);
        _time = time ?? TimeProvider.System;
    }

    public string StatePath => Path.Combine(_cloudRoot, "state.json");
    public string BackupRoot => Path.Combine(_cloudRoot, "backup");
    /// <summary>帳號的第一次同步、使用者選過衝突時做的備份。</summary>
    public string ImportantBackupRoot => Path.Combine(BackupRoot, "important");
    /// <summary>日常的備份：上傳前的 Cloud 版本、Cloud 更新時被換掉的手機檔。</summary>
    public string GeneralBackupRoot => Path.Combine(BackupRoot, "general");

    public int GetCloudFileCount() => ReadState().Files.Values.Count(file => file.CloudPresent);

    /// <param name="accountKey">目前登入帳號的 <see cref="SteamCloudAccount.KeyFor"/>，要在取清單之前就確定。</param>
    /// <param name="approval">使用者對上一次回傳的衝突清單的選擇；沒有就只做不用問的動作。</param>
    /// <param name="checkpoint">每次替換、移除、上傳、寫 state 之前呼叫；登出或撤回時要丟例外。</param>
    /// <param name="localWrite">
    /// 替換、移除手機檔、寫 state、刪舊備份時，把那一步交給它執行：呼叫端拿跨程序的鎖、確認世代還是目前的才執行，不是就丟例外。
    /// 沒給就直接執行（checkpoint 照樣先檢查）。
    /// </param>
    public async Task<SteamCloudSyncResult> SynchronizeAsync(
        uint appId,
        string accountKey,
        ISteamCloudTransport transport,
        SteamCloudConflictApproval? approval = null,
        Action<string>? onHostObserved = null,
        Action? checkpoint = null,
        Action<Action>? localWrite = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
            throw new ArgumentException("A Steam account key is required.", nameof(accountKey));
        if (approval is not null && !string.Equals(approval.AccountKey, accountKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The conflict choice belongs to a different Steam account.");

        await Gate.WaitAsync(cancellationToken);
        try
        {
            void Checkpoint()
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
            }

            // 會改到手機存檔、state、備份的那一步：在呼叫端的鎖裡確認世代才做（Terra 第 13 輪）
            void LocalWrite(Action write)
            {
                if (localWrite is null)
                    write();
                else
                    localWrite(write);
            }

            Directory.CreateDirectory(_cloudRoot);
            Directory.CreateDirectory(_persistentRoot);

            var stored = ReadState();
            // 換帳號、第一次登入、舊版沒記帳號：整份基準與 Cloud 清單都不用，change number 從 0 問，delta 也不會混進別的帳號的清單
            bool firstSync = !string.Equals(stored.AccountKey, accountKey, StringComparison.Ordinal);
            var state = firstSync ? new SteamCloudSyncState() : stored;

            var manifest = await transport.GetManifestAsync(appId, state.ChangeNumber, cancellationToken);
            var remoteFiles = MergeManifest(state, manifest);
            var localFiles = ReadLocalFiles();
            var actions = DecideActions(state, localFiles, remoteFiles, firstSync);
            var conflicts = actions.Where(p => p.Value == SteamCloudSyncAction.Conflict).Select(p => p.Key).ToList();

            SteamCloudSyncResult Ask(IEnumerable<string> paths) => SteamCloudSyncResult.Conflict(
                paths.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => MakeConflict(path, state, localFiles, remoteFiles))
                    .OrderBy(c => c.LocalRelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                remoteFiles.Count, accountKey, firstSync);

            bool userChose = false;
            if (approval is null)
            {
                if (conflicts.Count > 0)
                    return Ask(conflicts);
            }
            else
            {
                if (approval.Choice == SteamCloudConflictChoice.Cancel)
                    return SteamCloudSyncResult.Cancel();

                // 顯示過的檔全部重新看：兩邊已經一樣的才從清單拿掉；其他變化（包括變成自動下載、自動上傳）都要重新問
                var review = new HashSet<string>(conflicts, StringComparer.OrdinalIgnoreCase);
                foreach (var shown in approval.Shown)
                    if (!IdenticalNow(shown.LocalRelativePath, localFiles, remoteFiles))
                        review.Add(shown.LocalRelativePath);
                bool sameAsShown = review
                    .Select(path => MakeConflict(path, state, localFiles, remoteFiles))
                    .All(current => approval.Shown.Any(shown => shown.SameVersions(current)));
                if (!sameAsShown)
                    return Ask(review);

                foreach (var path in review)
                    actions[path] = SteamCloudSyncDecision.ResolveConflict(
                        approval.Choice, localFiles.ContainsKey(path), remoteFiles.ContainsKey(path), firstSync);
                userChose = review.Count > 0;
            }

            var downloads = PathsFor(actions, SteamCloudSyncAction.Download);
            var uploads = PathsFor(actions, SteamCloudSyncAction.Upload);
            var moves = PathsFor(actions, SteamCloudSyncAction.MoveLocalToBackup);
            var localBackups = actions
                .Where(p => (p.Value is SteamCloudSyncAction.Download or SteamCloudSyncAction.KeepLocalAfterCloudDelete
                    or SteamCloudSyncAction.MoveLocalToBackup) && localFiles.ContainsKey(p.Key))
                .Select(p => p.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // 要被上傳蓋掉的 Cloud 版本一律先下載備份（Jason 10/27）
            var cloudBackups = uploads.Where(remoteFiles.ContainsKey).ToList();

            var stagedDownloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in downloads)
                stagedDownloads[path] = await DownloadVerifiedAsync(appId, transport, remoteFiles[path], path, onHostObserved, cancellationToken);

            // 下載時 Steam 回的大小與 SHA1 要跟清單相同，這也是上傳目標的第一次重新比對
            var stagedCloudBackups = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in cloudBackups)
                stagedCloudBackups[path] = await DownloadVerifiedAsync(appId, transport, remoteFiles[path], path, onHostObserved, cancellationToken);

            // 要備份、替換、移除、上傳的手機檔都要跟比對時一樣；當時不存在的下載目標現在也要不存在
            Checkpoint();
            foreach (var path in localBackups.Concat(downloads).Concat(uploads).Concat(moves).Distinct(StringComparer.OrdinalIgnoreCase))
                EnsureLocalMatches(path, localFiles.GetValueOrDefault(path));

            string? backupDirectory = null;
            string? backupCategory = null;
            if (localBackups.Count > 0 || stagedCloudBackups.Count > 0)
            {
                backupCategory = userChose || firstSync ? ImportantBackupRoot : GeneralBackupRoot;
                backupDirectory = await WriteBackupSetAsync(backupCategory, localBackups, localFiles, stagedCloudBackups, remoteFiles, cancellationToken);
            }
            if (moves.Count > 0 && backupDirectory is null)
                throw new InvalidOperationException("Device-only saves must be backed up before they are removed.");

            var prepared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (path, bytes) in stagedDownloads)
                {
                    var target = ResolveLocalPath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = target + ".cloud-" + Guid.NewGuid().ToString("N") + ".tmp";
                    prepared[path] = temporary;
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                }

                foreach (var path in downloads)
                {
                    Checkpoint();
                    LocalWrite(() =>
                    {
                        EnsureLocalMatches(path, localFiles.GetValueOrDefault(path));
                        ReplaceWithRename(path, prepared[path], remoteFiles[path].ModifiedAtUtc);
                    });
                }
            }
            finally
            {
                foreach (var temporary in prepared.Values)
                    TryDelete(temporary);
            }

            // 手機獨有的檔：上面已經備份並驗證過，這裡才移除
            foreach (var path in moves)
            {
                Checkpoint();
                LocalWrite(() =>
                {
                    EnsureLocalMatches(path, localFiles[path]);
                    File.Delete(ResolveLocalPath(path));
                });
            }

            if (uploads.Count > 0)
            {
                var uploadFiles = new List<SteamCloudUpload>(uploads.Count);
                foreach (var path in uploads)
                {
                    var local = ReadLocalFile(path);
                    if (!local.Sha1.Equals(localFiles[path].Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new SteamCloudLocalChangedException("A local save changed while Steam Cloud synchronization was running.");

                    var oldRemote = remoteFiles.GetValueOrDefault(path);
                    string rootPrefix = oldRemote?.RootPrefix ?? SteamCloudPathMapper.ExpectedRootPrefix;
                    var remoteFileName = oldRemote?.RemoteFileName ?? SteamCloudPathMapper.ToRemoteFileName(path, rootPrefix);
                    uploadFiles.Add(new SteamCloudUpload(remoteFileName, path, local.Bytes, local.Sha1, local.ModifiedAtUtc));
                }

                // 備份與驗證都做完了。上傳前最後一次取清單：每個檔（包括原本不存在的上傳目標）都要跟比對時一樣，
                // 不然就是別台裝置在途中上傳或新建了檔，先中止，下次同步重新比對、必要時重新問
                var latest = await transport.GetManifestAsync(appId, manifest.ChangeNumber, cancellationToken);
                var latestFiles = MergeManifest(new SteamCloudSyncState { ChangeNumber = manifest.ChangeNumber, CloudFiles = remoteFiles }, latest);
                EnsureRemoteUnchanged(remoteFiles, latestFiles);

                Checkpoint();
                await transport.UploadBatchAsync(appId, "MT2 Mobile Launcher", uploadFiles, onHostObserved, cancellationToken);
                var refreshed = await transport.GetManifestAsync(appId, latest.ChangeNumber, cancellationToken);
                remoteFiles = MergeManifest(new SteamCloudSyncState
                {
                    ChangeNumber = latest.ChangeNumber,
                    CloudFiles = latestFiles,
                }, refreshed);

                foreach (var upload in uploadFiles)
                {
                    if (!remoteFiles.TryGetValue(upload.LocalRelativePath, out var remote)
                        || !remote.Sha1.Equals(upload.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new SteamCloudProtocolException("Steam Cloud did not return the uploaded file hash.");
                }
                manifest = refreshed;
            }

            Checkpoint();
            var nextLocalFiles = ReadLocalFiles();
            var nextState = BuildNextState(state, accountKey, manifest.ChangeNumber, nextLocalFiles, remoteFiles, actions);
            LocalWrite(() =>
            {
                WriteState(nextState);
                if (backupDirectory is not null)
                    PruneBackups(backupCategory!, backupDirectory);
            });

            int unchanged = actions.Count(p => p.Value is SteamCloudSyncAction.None
                or SteamCloudSyncAction.UpdateState or SteamCloudSyncAction.KeepLocalAfterCloudDelete
                or SteamCloudSyncAction.ForgetState);
            return new SteamCloudSyncResult(SteamCloudSyncResultKind.Synced, downloads.Count, uploads.Count, unchanged,
                Array.Empty<SteamCloudConflictFile>()) { CloudFileCount = remoteFiles.Count, AccountKey = accountKey };
        }
        finally
        {
            Gate.Release();
        }
    }

    private static List<string> PathsFor(Dictionary<string, SteamCloudSyncAction> actions, SteamCloudSyncAction action) =>
        actions.Where(p => p.Value == action).Select(p => p.Key).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    private static async Task<byte[]> DownloadVerifiedAsync(
        uint appId,
        ISteamCloudTransport transport,
        SteamCloudFileRecord remote,
        string path,
        Action<string>? onHostObserved,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await transport.DownloadFileAsync(
            appId, remote.RemoteFileName, remote.Size, remote.Sha1, onHostObserved, cancellationToken);
        VerifyBytes(bytes, remote.Size, remote.Sha1, path);
        return bytes;
    }

    private Dictionary<string, SteamCloudSyncAction> DecideActions(
        SteamCloudSyncState state,
        Dictionary<string, LocalSnapshot> local,
        Dictionary<string, SteamCloudFileRecord> remote,
        bool firstSync)
    {
        var paths = new HashSet<string>(state.Files.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(local.Keys);
        paths.UnionWith(remote.Keys);
        var result = new Dictionary<string, SteamCloudSyncAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            state.Files.TryGetValue(path, out var previous);
            local.TryGetValue(path, out var device);
            remote.TryGetValue(path, out var cloud);
            result[path] = SteamCloudSyncDecision.Decide(previous, device?.Sha1, cloud?.Sha1, firstSync);
        }
        return result;
    }

    private static bool IdenticalNow(string path, Dictionary<string, LocalSnapshot> local, Dictionary<string, SteamCloudFileRecord> remote)
    {
        local.TryGetValue(path, out var device);
        remote.TryGetValue(path, out var cloud);
        return string.Equals(device?.Sha1, cloud?.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, SteamCloudFileRecord> MergeManifest(SteamCloudSyncState previous, SteamCloudManifest manifest)
    {
        var files = manifest.IsOnlyDelta
            ? new Dictionary<string, SteamCloudFileRecord>(previous.CloudFiles, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SteamCloudFileRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in manifest.Changes)
        {
            if (change.IsPresent)
                files[change.LocalRelativePath] = change;
            else
                files.Remove(change.LocalRelativePath);
        }
        return files;
    }

    private static void EnsureRemoteUnchanged(
        Dictionary<string, SteamCloudFileRecord> planned,
        Dictionary<string, SteamCloudFileRecord> latest)
    {
        foreach (var path in planned.Keys.Union(latest.Keys, StringComparer.OrdinalIgnoreCase))
        {
            planned.TryGetValue(path, out var before);
            latest.TryGetValue(path, out var now);
            if (!string.Equals(before?.Sha1, now?.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new SteamCloudRemoteChangedException("Steam Cloud changed after it was listed; no file was uploaded.");
        }
    }

    private Dictionary<string, LocalSnapshot> ReadLocalFiles()
    {
        var files = new Dictionary<string, LocalSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_persistentRoot))
            return files;

        foreach (var path in Directory.EnumerateFiles(_persistentRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_persistentRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!SteamCloudPathMapper.IsSupportedLocalPath(relative))
                continue;
            var item = ReadLocalFile(relative);
            if (!files.TryAdd(relative, item))
                throw new IOException("Two local save paths map to the same Steam Cloud name.");
        }
        return files;
    }

    private LocalSnapshot ReadLocalFile(string relative)
    {
        var path = ResolveLocalPath(relative);
        var bytes = File.ReadAllBytes(path);
        return new LocalSnapshot(relative, bytes, Sha1(bytes), new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
    }

    private SteamCloudSyncState ReadState()
    {
        if (!File.Exists(StatePath))
            return new SteamCloudSyncState();

        var state = JsonSerializer.Deserialize<SteamCloudSyncState>(File.ReadAllText(StatePath), JsonOptions)
            ?? throw new IOException("Steam Cloud state file is empty.");
        if (state.Schema != 1)
            throw new IOException("Steam Cloud state file uses an unsupported schema.");
        state.Files = new Dictionary<string, SteamCloudBaseline>(state.Files, StringComparer.OrdinalIgnoreCase);
        state.CloudFiles = new Dictionary<string, SteamCloudFileRecord>(state.CloudFiles, StringComparer.OrdinalIgnoreCase);
        return state;
    }

    private void WriteState(SteamCloudSyncState state)
    {
        Directory.CreateDirectory(_cloudRoot);
        var temporary = StatePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, StatePath, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private SteamCloudSyncState BuildNextState(
        SteamCloudSyncState previous,
        string accountKey,
        ulong changeNumber,
        Dictionary<string, LocalSnapshot> local,
        Dictionary<string, SteamCloudFileRecord> remote,
        Dictionary<string, SteamCloudSyncAction> actions)
    {
        var next = new SteamCloudSyncState
        {
            AccountKey = accountKey,
            ChangeNumber = changeNumber,
            UpdatedAtUtc = _time.GetUtcNow(),
        };
        var paths = new HashSet<string>(previous.Files.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(local.Keys);
        paths.UnionWith(remote.Keys);

        foreach (var path in paths)
        {
            local.TryGetValue(path, out var device);
            remote.TryGetValue(path, out var cloud);
            if (device is not null && cloud is not null)
            {
                if (!device.Sha1.Equals(cloud.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new SteamCloudProtocolException("Local and cloud copies differ after synchronization.");
                next.Files[path] = new SteamCloudBaseline(device.Sha1);
                next.CloudFiles[path] = Sanitized(cloud);
                continue;
            }

            if (device is not null && cloud is null)
            {
                actions.TryGetValue(path, out var action);
                previous.Files.TryGetValue(path, out var old);
                if (action != SteamCloudSyncAction.KeepLocalAfterCloudDelete && old?.CloudPresent != false)
                    throw new SteamCloudProtocolException("A local-only save was not resolved safely.");
                next.Files[path] = new SteamCloudBaseline(device.Sha1, CloudPresent: false);
                continue;
            }

            if (device is null && cloud is not null)
                throw new SteamCloudProtocolException("A cloud save was not downloaded to the device.");
        }
        return next;
    }

    private async Task WriteBackupAsync(
        string backupDirectory,
        HashSet<string> localBackupPaths,
        Dictionary<string, LocalSnapshot> localFiles,
        Dictionary<string, byte[]> cloudBackupBytes,
        Dictionary<string, SteamCloudFileRecord> remoteFiles,
        CancellationToken cancellationToken)
    {
        var manifest = new List<BackupEntry>();
        foreach (var path in localBackupPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = localFiles[path];
            EnsureLocalMatches(path, snapshot);
            var source = ResolveLocalPath(path);
            var destination = ResolveBackupPath(backupDirectory, "device", path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
            if (!Sha1(File.ReadAllBytes(destination)).Equals(snapshot.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A save backup did not match its source hash.");
            manifest.Add(new BackupEntry("device", path, snapshot.Sha1, snapshot.ModifiedAtUtc));
        }

        foreach (var (path, bytes) in cloudBackupBytes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var remote = remoteFiles[path];
            var destination = ResolveBackupPath(backupDirectory, "cloud", path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
            if (!Sha1(File.ReadAllBytes(destination)).Equals(remote.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A cloud backup did not match its source hash.");
            manifest.Add(new BackupEntry("cloud", path, remote.Sha1, remote.ModifiedAtUtc));
        }

        File.WriteAllText(Path.Combine(backupDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>
    /// 先寫在 &lt;類別&gt;/.partial-&lt;亂數&gt;，每個檔驗過 SHA1、寫完 manifest.json，才改名成正式名稱並給建立順序。
    /// 中途失敗或被中止，暫存資料夾刪掉（刪不掉的下次建立前再刪），不會留下符合正式命名的未完成備份。
    /// 未完成的備份裡只有還留在原處的檔（失敗就不會往下替換），所以可以直接刪。
    /// </summary>
    private async Task<string> WriteBackupSetAsync(
        string categoryRoot,
        HashSet<string> localBackupPaths,
        Dictionary<string, LocalSnapshot> localFiles,
        Dictionary<string, byte[]> cloudBackupBytes,
        Dictionary<string, SteamCloudFileRecord> remoteFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(categoryRoot);
        foreach (var leftover in Directory.GetDirectories(categoryRoot, PartialPrefix + "*"))
            TryDeleteDirectory(leftover);
        var staging = Path.Combine(categoryRoot, PartialPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await WriteBackupAsync(staging, localBackupPaths, localFiles, cloudBackupBytes, remoteFiles, cancellationToken);
            var final = Path.Combine(categoryRoot, NextBackupName());
            Directory.Move(staging, final);
            return final;
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private const string PartialPrefix = ".partial-";

    /// <summary>建立順序取兩類現有備份的最大號碼加一；跟手機時鐘無關，往回調也不會排到舊的後面。</summary>
    private string NextBackupName()
    {
        long next = new[] { ImportantBackupRoot, GeneralBackupRoot }
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(path => BackupSequence(Path.GetFileName(path)))
            .Where(sequence => sequence is not null)
            .Select(sequence => sequence!.Value)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return next.ToString("D10", CultureInfo.InvariantCulture) + "-"
            + _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture) + "-"
            + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// 每一類保留最近 10 組完成的備份（照建立順序），這次剛建的一定留。
    /// 只有符合正式命名、而且有 manifest.json 的才算完成、才佔名額；其他資料夾（舊版放在 backup/ 底下的、名稱不同的）不動。
    /// </summary>
    private static void PruneBackups(string categoryRoot, string keep)
    {
        var keepName = Path.GetFileName(keep);
        var others = Directory.GetDirectories(categoryRoot)
            .Select(path => (Path: path, Sequence: BackupSequence(Path.GetFileName(path))))
            .Where(item => item.Sequence is not null && Path.GetFileName(item.Path) != keepName
                && File.Exists(Path.Combine(item.Path, "manifest.json")))
            .OrderByDescending(item => item.Sequence)
            .Skip(BackupsPerCategory - 1);
        foreach (var old in others)
            Directory.Delete(old.Path, recursive: true);
    }

    private static long? BackupSequence(string name)
    {
        var match = BackupDirectoryRegex.Match(name);
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private string ResolveLocalPath(string relative) => ResolveUnder(_persistentRoot, relative);

    private static string ResolveBackupPath(string backupDirectory, string side, string relative) =>
        ResolveUnder(Path.Combine(backupDirectory, side), relative);

    private static string ResolveUnder(string root, string relative)
    {
        if (!SteamCloudPathMapper.IsSupportedLocalPath(relative))
            throw new IOException("Unsupported Steam Cloud relative path.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new IOException("Steam Cloud path escaped its private directory.");
        return fullPath;
    }

    private void ReplaceWithRename(string relative, string temporary, DateTimeOffset modifiedAtUtc)
    {
        var target = ResolveLocalPath(relative);
        File.Move(temporary, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, modifiedAtUtc.UtcDateTime);
    }

    /// <summary>手機檔要跟比對時一樣：當時有的，SHA1 要相同；當時沒有的，現在也要沒有。</summary>
    private void EnsureLocalMatches(string relative, LocalSnapshot? expected)
    {
        var path = ResolveLocalPath(relative);
        if (expected is null)
        {
            if (File.Exists(path))
                throw new SteamCloudLocalChangedException("A local save appeared while Steam Cloud synchronization was running.");
            return;
        }
        if (!File.Exists(path) || !ReadLocalFile(relative).Sha1.Equals(expected.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new SteamCloudLocalChangedException("A local save changed while Steam Cloud synchronization was running.");
    }

    private static void VerifyBytes(byte[] bytes, long expectedSize, string expectedSha1, string relative)
    {
        if (bytes.LongLength != expectedSize || !Sha1(bytes).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            throw new SteamCloudProtocolException($"Downloaded file failed size/SHA1 verification: {relative}");
    }

    private static SteamCloudConflictFile MakeConflict(
        string path,
        SteamCloudSyncState state,
        Dictionary<string, LocalSnapshot> localFiles,
        Dictionary<string, SteamCloudFileRecord> remoteFiles)
    {
        localFiles.TryGetValue(path, out var local);
        remoteFiles.TryGetValue(path, out var remote);
        bool cloudDeleted = remote is null && state.Files.ContainsKey(path);
        return new(path, local?.ModifiedAtUtc, remote?.ModifiedAtUtc, local?.Sha1, remote?.Sha1, cloudDeleted);
    }

    private static SteamCloudFileRecord Sanitized(SteamCloudFileRecord file) =>
        file with { MachineName = "", PathPrefixIndex = 0, MachineNameIndex = 0 };

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed record LocalSnapshot(string RelativePath, byte[] Bytes, string Sha1, DateTimeOffset ModifiedAtUtc);
    private sealed record BackupEntry(string Source, string RelativePath, string Sha1, DateTimeOffset ModifiedAtUtc);
}
