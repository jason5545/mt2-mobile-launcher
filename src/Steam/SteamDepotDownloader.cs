using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.CDN;

namespace Mt2Mobile.Steam;

public interface ISteamDepotTransport : IDisposable
{
    Task<DepotManifest?> DownloadManifestAsync(SteamDepotSpec depot, CancellationToken ct = default);
    Task<int> DownloadChunkAsync(SteamDepotSpec depot, DepotManifest.ChunkData chunk, byte[] destination, CancellationToken ct = default);
}

public sealed record SteamDepotDownloadPlan(
    string Root,
    string DataDir,
    IReadOnlyList<SteamDepotDownloader.PlannedFile> Files,
    long TotalBytes,
    long BytesToDownload,
    long ExistingVerifiedBytes,
    int PendingFiles);

/// <summary>
/// Reads pinned manifests and writes only files under the target's _Data directory. Up to maxConcurrentChunks chunks
/// are downloaded at a time, each written at its own offset. A completed file is resumable only when both its size and SHA1 match.
/// </summary>
public sealed class SteamDepotDownloader(ISteamDepotTransport transport) : IDisposable
{
    public sealed record PlannedFile(
        string RelativePath,
        string Destination,
        long Size,
        byte[] Sha1,
        SteamDepotSpec Depot,
        IReadOnlyList<DepotManifest.ChunkData> Chunks,
        bool AlreadyValid);

    public async Task<SteamDepotDownloadPlan> PrepareAsync(
        IEnumerable<SteamDepotSpec> depots,
        string dataDir,
        string destinationRoot,
        CancellationToken ct = default)
    {
        try
        {
            ValidateDataDir(dataDir);
            var root = Path.GetFullPath(destinationRoot);
            var files = new Dictionary<string, (SteamDepotSpec Depot, long Size, byte[] Sha1, IReadOnlyList<DepotManifest.ChunkData> Chunks)>(StringComparer.Ordinal);

            foreach (var depot in depots)
            {
                ct.ThrowIfCancellationRequested();
                var manifest = await transport.DownloadManifestAsync(depot, ct).ConfigureAwait(false)
                    ?? throw SteamDepotErrors.ManifestUnavailable();
                if (manifest.DepotID != depot.DepotId || manifest.ManifestGID != depot.ManifestId)
                    throw new SteamException("Steam 回傳的 depot 或 manifest 與 target 不符；下載已停止。", "manifest_mismatch");

                foreach (var file in manifest.Files ?? [])
                {
                    var relative = DataRelativePath(file.FileName, dataDir);
                    if (relative is null || file.Flags.HasFlag(EDepotFileFlag.Directory))
                        continue;
                    if (!string.IsNullOrEmpty(file.LinkTarget))
                        throw new SteamException("Steam manifest 含有不支援的連結檔案；下載已停止。", "manifest_link");
                    if (file.TotalSize > long.MaxValue || file.FileHash.Length != 20)
                        throw new SteamException("Steam manifest 的檔案大小或 SHA1 格式無效；下載已停止。", "manifest_file_invalid");

                    if (files.TryGetValue(relative, out var prior))
                    {
                        if (prior.Size != (long)file.TotalSize || !prior.Sha1.AsSpan().SequenceEqual(file.FileHash))
                            throw new SteamException("多個 Steam depot 對同一個檔案提供不同內容；下載已停止。", "depot_conflict");
                        continue;
                    }

                    var destination = ResolveDestination(root, dataDir, relative);
                    files.Add(relative, (depot, (long)file.TotalSize, file.FileHash.ToArray(), file.Chunks.ToArray()));
                    _ = destination; // Resolve every manifest path before touching the destination tree.
                }
            }

            PruneUnlistedFiles(Path.Combine(root, dataDir), files.Keys.ToHashSet(StringComparer.Ordinal));

            var planned = new List<PlannedFile>(files.Count);
            long total = 0, pending = 0, verified = 0;
            int pendingFiles = 0;
            foreach (var (relative, item) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var destination = ResolveDestination(root, dataDir, relative);
                var partial = destination + ".steam-partial";
                if (Directory.Exists(destination) || Directory.Exists(partial))
                    throw new SteamException("Steam 下載目的地的檔案與資料夾衝突；請清除安裝暫存後重試。", "destination_conflict");
                bool valid = false;
                if (File.Exists(destination))
                {
                    var attributes = File.GetAttributes(destination);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new SteamException("Steam 下載目的地含有不支援的連結；下載已停止。", "destination_link");
                    var info = new FileInfo(destination);
                    if (info.Length == item.Size)
                    {
                        using var stream = File.OpenRead(destination);
                        valid = CryptographicOperations.FixedTimeEquals(SHA1.HashData(stream), item.Sha1);
                    }
                    if (!valid) File.Delete(destination);
                }
                if (File.Exists(partial)) File.Delete(partial);

                total = checked(total + item.Size);
                if (valid) verified = checked(verified + item.Size);
                else
                {
                    pending = checked(pending + item.Size);
                    pendingFiles++;
                }
                planned.Add(new PlannedFile(relative, destination, item.Size, item.Sha1, item.Depot, item.Chunks, valid));
            }

            return new SteamDepotDownloadPlan(root, dataDir, planned, total, pending, verified, pendingFiles);
        }
        catch (SteamException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch
        {
            throw new SteamException("無法讀取 Steam 固定版本的檔案清單或檢查下載暫存。", "manifest_read");
        }
    }

    /// <summary>
    /// 照 plan 下載。maxConcurrentChunks（1～8，照 STS2 的「本體分塊並行數」）是同時下載的 chunk 數：chunk 照檔案順序排隊，
    /// 每個 chunk 寫回自己的 offset（RandomAccess），所以完成順序不影響內容；一個檔的 chunk 都寫完才驗 SHA1、改名。
    /// 同時開著的暫存檔最多約 maxConcurrentChunks 個，記憶體是每個 worker 一個 chunk 的 buffer。
    /// </summary>
    public async Task DownloadAsync(
        SteamDepotDownloadPlan plan,
        Action<SteamDepotDownloadProgress>? progress = null,
        Func<bool>? isCancelled = null,
        CancellationToken ct = default,
        int maxConcurrentChunks = 1)
    {
        long completedBytes = plan.ExistingVerifiedBytes;
        int completedFiles = plan.Files.Count - plan.PendingFiles;
        var progressGate = new object();
        void Report()
        {
            if (progress == null) return;
            lock (progressGate)
                progress(new SteamDepotDownloadProgress(Interlocked.Read(ref completedBytes), plan.TotalBytes, Volatile.Read(ref completedFiles), plan.Files.Count));
        }
        Report();

        var open = new List<PendingFile>();
        try
        {
            var work = new List<(PendingFile File, DepotManifest.ChunkData Chunk)>();
            foreach (var file in plan.Files)
            {
                if (file.AlreadyValid) continue;
                foreach (var chunk in file.Chunks)
                    if (chunk.UncompressedLength > int.MaxValue || chunk.Offset > long.MaxValue ||
                        chunk.Offset + chunk.UncompressedLength > (ulong)file.Size)
                        throw new SteamException("Steam manifest 的 chunk 範圍無效；下載已停止。", "chunk_range");
                var pending = new PendingFile(file);
                open.Add(pending);
                if (file.Chunks.Count == 0)
                {
                    // 沒有 chunk 的檔（大小 0）：直接建、驗、改名
                    pending.Open();
                    Finish(pending);
                    Interlocked.Increment(ref completedFiles);
                    Report();
                    continue;
                }
                foreach (var chunk in file.Chunks)
                    work.Add((pending, chunk));
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(maxConcurrentChunks, 1, 8), CancellationToken = ct };
            await Parallel.ForEachAsync(work, options, async (item, token) =>
            {
                if (isCancelled?.Invoke() == true) throw new OperationCanceledException("download cancelled");
                var buffer = new byte[(int)item.Chunk.UncompressedLength];
                var written = await transport.DownloadChunkAsync(item.File.Plan.Depot, item.Chunk, buffer, token).ConfigureAwait(false);
                if (written != buffer.Length)
                    throw new SteamException("Steam chunk 解壓後的大小不符；下載已停止。", "chunk_size");
                var handle = item.File.Open();
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, written), (long)item.Chunk.Offset, token).ConfigureAwait(false);
                Interlocked.Add(ref completedBytes, written);
                if (item.File.ChunkDone())
                {
                    Finish(item.File);
                    Interlocked.Increment(ref completedFiles);
                }
                Report();
            }).ConfigureAwait(false);
        }
        catch (SteamException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch
        {
            throw new SteamException("Steam 下載或寫入暫存檔失敗。", "write_failed");
        }
        finally
        {
            foreach (var f in open) f.Close();
        }
    }

    /// <summary>一個 chunk 都寫完的檔：關掉暫存檔、驗大小與 SHA1、改成正式檔名</summary>
    static void Finish(PendingFile pending)
    {
        var file = pending.Plan;
        pending.Close(flush: true);
        using (var verify = File.OpenRead(pending.Partial))
        {
            if (verify.Length != file.Size || !CryptographicOperations.FixedTimeEquals(SHA1.HashData(verify), file.Sha1))
            {
                verify.Dispose();
                File.Delete(pending.Partial);
                throw new SteamException("Steam 下載檔案 SHA1 驗證失敗；請重新下載。", "sha1_failed");
            }
        }
        File.Move(pending.Partial, file.Destination, overwrite: true);
    }

    /// <summary>下載中的檔：第一個 chunk 到的時候才建 .steam-partial（先刪掉上次留下的），記還剩幾個 chunk</summary>
    sealed class PendingFile(PlannedFile plan)
    {
        readonly object gate = new();
        FileStream? stream;
        int remaining = plan.Chunks.Count;
        public PlannedFile Plan => plan;
        public string Partial => plan.Destination + ".steam-partial";

        public Microsoft.Win32.SafeHandles.SafeFileHandle Open()
        {
            lock (gate)
            {
                if (stream == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)!);
                    if (File.Exists(Partial)) File.Delete(Partial);
                    stream = new FileStream(Partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);
                    stream.SetLength(plan.Size);
                }
                return stream.SafeFileHandle;
            }
        }

        /// <summary>這個 chunk 寫完了；回傳 true 代表是最後一個</summary>
        public bool ChunkDone() => Interlocked.Decrement(ref remaining) == 0;

        public void Close(bool flush = false)
        {
            lock (gate)
            {
                if (stream == null) return;
                if (flush) stream.Flush(true);
                stream.Dispose();
                stream = null;
            }
        }
    }

    public static string? DataRelativePath(string manifestPath, string dataDir)
    {
        ValidateDataDir(dataDir);
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new SteamException("Steam manifest 含有空白路徑；下載已停止。", "path_empty");
        var path = manifestPath.Replace('\\', '/');
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.Contains(':'))
            throw new SteamException("Steam manifest 含有絕對路徑；下載已停止。", "path_absolute");
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".."))
            throw new SteamException("Steam manifest 含有不安全的路徑；下載已停止。", "path_unsafe");
        if (!path.StartsWith(dataDir + "/", StringComparison.Ordinal))
            return null;
        var relative = path[(dataDir.Length + 1)..];
        if (relative.Length == 0)
            throw new SteamException("Steam manifest 含有無效的 _Data 路徑；下載已停止。", "path_data");
        return relative;
    }

    private static string ResolveDestination(string root, string dataDir, string relative)
    {
        var dataRoot = Path.GetFullPath(Path.Combine(root, dataDir));
        var full = Path.GetFullPath(Path.Combine(dataRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var under = Path.GetRelativePath(dataRoot, full);
        if (Path.IsPathRooted(under) || under == ".." || under.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new SteamException("Steam manifest 路徑會跳出下載資料夾；下載已停止。", "path_escape");
        return full;
    }

    private static void PruneUnlistedFiles(string dataRoot, HashSet<string> expected)
    {
        if (!Directory.Exists(dataRoot)) return;
        foreach (var path in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dataRoot, path).Replace('\\', '/');
            if (relative.EndsWith(".steam-partial", StringComparison.Ordinal) || !expected.Contains(relative))
                File.Delete(path);
        }
    }

    private static void ValidateDataDir(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir) || dataDir.Contains('/') || dataDir.Contains('\\') ||
            dataDir is "." or ".." || !dataDir.EndsWith("_Data", StringComparison.Ordinal))
            throw new SteamException("target 的 _Data 資料夾名稱無效。", "target_data_dir");
    }

    public void Dispose() => transport.Dispose();
}

internal sealed class SteamKitDepotTransport : ISteamDepotTransport
{
    private readonly SteamApps _apps;
    private readonly SteamContent _content;
    private readonly Client _cdn;
    private readonly Action<string> _log;
    private readonly Dictionary<SteamDepotSpec, byte[]> _keys = [];
    private readonly Dictionary<(SteamDepotSpec Depot, string Host), SteamContent.CDNAuthToken> _tokens = [];
    private readonly HashSet<string> _loggedHosts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyCollection<Server>? _servers;
    /// <summary>chunk 會並行下載：金鑰、伺服器清單第一次取得時排隊（只問 Steam 一次），快取的字典用 _cacheGate 保護</summary>
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly object _cacheGate = new();

    public SteamKitDepotTransport(SteamClient client, SteamApps apps, SteamContent content, Action<string> log)
    {
        _apps = apps;
        _content = content;
        _cdn = new Client(client);
        _log = log;
    }

    public async Task<DepotManifest?> DownloadManifestAsync(SteamDepotSpec depot, CancellationToken ct = default)
    {
        try
        {
            var requestCode = await _content.GetManifestRequestCode(depot.DepotId, depot.AppId, depot.ManifestId, "public", "").WaitAsync(ct).ConfigureAwait(false);
            if (requestCode == 0) return null;
            var key = await GetDepotKeyAsync(depot, ct).ConfigureAwait(false);
            bool denied = false;
            foreach (var server in await GetServersAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(server.Host)) continue;
                var host = server.Host;
                LogHost(host);
                try
                {
                    var token = await GetCachedTokenAsync(depot, host).ConfigureAwait(false);
                    return await _cdn.DownloadManifestAsync(depot.DepotId, depot.ManifestId, requestCode, server, key,
                        cdnAuthToken: token).WaitAsync(ct).ConfigureAwait(false);
                }
                catch (SteamKitWebRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    var token = await RequestTokenAsync(depot, host, ct).ConfigureAwait(false);
                    if (token is null)
                    {
                        denied = true;
                        continue;
                    }
                    try
                    {
                        return await _cdn.DownloadManifestAsync(depot.DepotId, depot.ManifestId, requestCode, server, key,
                            cdnAuthToken: token).WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (SteamKitWebRequestException denial) when (denial.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        denied = true;
                    }
                    catch (SteamKitWebRequestException) { }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { }
                }
                catch (SteamKitWebRequestException)
                {
                    // A manifest or server can be unavailable on one CDN; try the next server.
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { }
            }
            if (denied) throw new SteamException("Steam 拒絕存取 target 指定的固定 manifest。", "manifest_denied");
            return null;
        }
        catch (SteamException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SteamException("Steam 取得固定 manifest 等候逾時，沒有改抓其他版本。", "manifest_timeout"); }
        catch
        {
            throw new SteamException("無法從 Steam 取得 target 指定的固定 manifest。", "manifest_request_failed");
        }
    }

    public async Task<int> DownloadChunkAsync(SteamDepotSpec depot, DepotManifest.ChunkData chunk, byte[] destination, CancellationToken ct = default)
    {
        try
        {
            var key = await GetDepotKeyAsync(depot, ct).ConfigureAwait(false);
            bool denied = false;
            foreach (var server in await GetServersAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(server.Host)) continue;
                var host = server.Host;
                LogHost(host);
                try
                {
                    var token = await GetCachedTokenAsync(depot, host).ConfigureAwait(false);
                    return await _cdn.DownloadDepotChunkAsync(depot.DepotId, chunk, server, destination, key,
                        cdnAuthToken: token).WaitAsync(ct).ConfigureAwait(false);
                }
                catch (SteamKitWebRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    var token = await RequestTokenAsync(depot, host, ct).ConfigureAwait(false);
                    if (token is null)
                    {
                        denied = true;
                        continue;
                    }
                    try
                    {
                        return await _cdn.DownloadDepotChunkAsync(depot.DepotId, chunk, server, destination, key,
                            cdnAuthToken: token).WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (SteamKitWebRequestException denial) when (denial.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        denied = true;
                    }
                    catch (SteamKitWebRequestException) { }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { }
                }
                catch (SteamKitWebRequestException) { }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { }
            }
            if (denied) throw new SteamException("Steam 拒絕存取 target 固定版本的 depot 檔案。", "depot_denied");
            throw new SteamException("無法從 Steam CDN 下載固定版本的 chunk；請確認授權與網路後重試。", "cdn_failed");
        }
        catch (SteamException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            throw new SteamException("Steam CDN chunk 下載逾時；請重試固定版本。", "cdn_timeout");
        }
        catch
        {
            throw new SteamException("無法從 Steam CDN 下載固定版本的 chunk；請確認授權與網路後重試。", "cdn_failed");
        }
    }

    private async Task<byte[]> GetDepotKeyAsync(SteamDepotSpec depot, CancellationToken ct)
    {
        lock (_cacheGate)
            if (_keys.TryGetValue(depot, out var cached)) return cached;
        await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_cacheGate)
                if (_keys.TryGetValue(depot, out var cached)) return cached;
            var callback = await _apps.GetDepotDecryptionKey(depot.DepotId, depot.AppId).ToTask().WaitAsync(ct).ConfigureAwait(false);
            if (callback.Result != EResult.OK || callback.DepotKey is not { Length: > 0 })
                throw new SteamException("Steam 拒絕提供 target depot 的解密金鑰；請確認帳號授權。", "depot_key_denied");
            lock (_cacheGate) _keys[depot] = callback.DepotKey;
            return callback.DepotKey;
        }
        finally { _fetchGate.Release(); }
    }

    private async Task<IReadOnlyCollection<Server>> GetServersAsync(CancellationToken ct)
    {
        var servers = Volatile.Read(ref _servers);
        if (servers == null)
        {
            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                servers = _servers ??= await _content.GetServersForSteamPipe(maxNumServers: 20).WaitAsync(ct).ConfigureAwait(false);
            }
            finally { _fetchGate.Release(); }
        }
        if (servers.Count == 0) throw new SteamException("Steam 沒有回傳可用的 CDN 伺服器。", "no_cdn");
        return servers;
    }

    private Task<string?> GetCachedTokenAsync(SteamDepotSpec depot, string host)
    {
        lock (_cacheGate)
            if (_tokens.TryGetValue((depot, host), out var token) && token.Expiration > DateTime.UtcNow.AddMinutes(1))
                return Task.FromResult<string?>(token.Token);
        return Task.FromResult<string?>(null);
    }

    private async Task<string?> RequestTokenAsync(SteamDepotSpec depot, string host, CancellationToken ct)
    {
        try
        {
            var result = await _content.GetCDNAuthToken(depot.AppId, depot.DepotId, host).WaitAsync(ct).ConfigureAwait(false);
            if (result.Result != EResult.OK || string.IsNullOrEmpty(result.Token)) return null;
            lock (_cacheGate) _tokens[(depot, host)] = result;
            return result.Token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    private void LogHost(string host)
    {
        bool added;
        lock (_cacheGate) added = _loggedHosts.Add(host);
        if (added) _log($"CDN host={host}");
    }

    public void Dispose()
    {
        _cdn.Dispose();
        lock (_cacheGate)
        {
            foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
            _keys.Clear();
            _tokens.Clear();
        }
        _fetchGate.Dispose();
    }
}
