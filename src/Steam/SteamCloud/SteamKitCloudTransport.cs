using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamKit2.Internal;

namespace Mt2Mobile.Steam.SteamCloud;

/// <summary>Steam Cloud unified-message and HTTP transport for SteamKit2 3.5.0.</summary>
public sealed class SteamKitCloudTransport(Cloud cloud, SteamClient steamClient, Action<string>? log = null) : ISteamCloudTransport
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler());
    private static readonly Regex SteamIdInPath = new(@"\b\d{17}\b", RegexOptions.CultureInvariant);
    private static readonly Regex UserProfileInPath = new(@"(?i)([/\\](?:users|home)[/\\])[^/\\]+", RegexOptions.CultureInvariant);
    private static readonly Regex UncHostInPath = new(@"^(?:\\\\|//)[^/\\]+", RegexOptions.CultureInvariant);
    private static readonly Regex LongSecretInPath = new(@"(?<![A-Za-z0-9])[A-Za-z0-9_-]{32,}(?![A-Za-z0-9])", RegexOptions.CultureInvariant);
    private const int MaxDownloadBytes = 128 * 1024 * 1024;
    /// <summary>結束上傳批次、回報上傳失敗這兩個收尾請求的等待上限：取消或撤回時不跟著取消（要讓 Steam 知道這批失敗了），但也不無限等。</summary>
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<SteamCloudManifest> GetManifestAsync(uint appId, ulong syncedChangeNumber, CancellationToken cancellationToken = default)
    {
        var request = new CCloud_GetAppFileChangelist_Request
        {
            appid = appId,
            synced_change_number = syncedChangeNumber,
        };

        var response = await cloud.GetAppFileChangelist(request).ToTask().WaitAsync(cancellationToken);
        EnsureOk(response.Result, "GetAppFileChangelist");
        var body = response.Body;
        var files = new List<SteamCloudFileRecord>(body.files.Count);

        foreach (var file in body.files)
        {
            if (file.path_prefix_index >= body.path_prefixes.Count)
            {
                LogManifestPathDiagnostics(body.path_prefixes, file.path_prefix_index, file.file_name, file.persist_state, rootPrefix: null);
                throw new SteamCloudProtocolException("Steam Cloud returned an invalid path_prefix_index.");
            }

            var rootPrefix = body.path_prefixes[(int)file.path_prefix_index];
            var fileName = file.file_name;
            var present = file.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted;
            if (!SteamCloudPathMapper.TryMap(rootPrefix, fileName, out var localRelativePath))
            {
                LogManifestPathDiagnostics(body.path_prefixes, file.path_prefix_index, fileName, file.persist_state, rootPrefix);
                if (!present)
                {
                    _log("Steam Cloud skipped non-persisted file outside the MT2 Auto-Cloud allowlist.");
                    continue;
                }
                throw new SteamCloudProtocolException("Steam Cloud returned a path outside MT2's supported Auto-Cloud folder.");
            }

            var remoteFileName = SteamCloudPathMapper.JoinRemotePath(rootPrefix, fileName);
            var sha1 = present ? ToSha1(file.sha_file) : "";
            if (present && (file.raw_file_size == 0 && file.sha_file.Length == 0))
                throw new SteamCloudProtocolException("Steam Cloud returned an incomplete file record.");

            files.Add(new SteamCloudFileRecord(
                remoteFileName,
                localRelativePath,
                rootPrefix,
                fileName,
                file.machine_name_index < body.machine_names.Count ? body.machine_names[(int)file.machine_name_index] : "",
                file.path_prefix_index,
                file.machine_name_index,
                file.raw_file_size,
                FromUnixSeconds(file.time_stamp),
                sha1,
                present));
        }

        _log($"Steam Cloud manifest change={body.current_change_number} delta={body.is_only_delta} files={files.Count} pathPrefixes={body.path_prefixes.Count} machineNames={body.machine_names.Count}");
        foreach (var file in files)
            _log($"Steam Cloud file {file.LocalRelativePath} root={SafePathForLog(file.RootPrefix)} path_prefix_index={file.PathPrefixIndex} machine_name_index={file.MachineNameIndex} machine_name_present={!string.IsNullOrEmpty(file.MachineName)} bytes={file.Size} sha1={(file.Sha1.Length >= 8 ? file.Sha1[..8] : "-")} present={file.IsPresent}");

        return new SteamCloudManifest(body.current_change_number, body.is_only_delta, files);
    }

    private void LogManifestPathDiagnostics(
        IReadOnlyList<string> pathPrefixes,
        uint pathPrefixIndex,
        string? fileName,
        ECloudStoragePersistState persistState,
        string? rootPrefix)
    {
        var prefixes = string.Join(", ", pathPrefixes.Select((prefix, index) => $"{index}=\"{SafePathForLog(prefix)}\""));
        var selectedPrefix = rootPrefix is null ? "<invalid-index>" : $"\"{SafePathForLog(rootPrefix)}\"";
        _log($"Steam Cloud manifest path rejected: path_prefixes=[{prefixes}] path_prefix_index={pathPrefixIndex} selected={selectedPrefix} file_name=\"{SafePathForLog(fileName)}\" persist_state={persistState}");
    }

    private static string SafePathForLog(string? value)
    {
        var safe = value ?? "<null>";
        safe = UncHostInPath.Replace(safe, "//<redacted-host>");
        safe = UserProfileInPath.Replace(safe, "$1<redacted-user>");
        safe = SteamIdInPath.Replace(safe, "<redacted-id>");
        safe = LongSecretInPath.Replace(safe, "<redacted-secret>");
        return safe.Replace('\r', ' ').Replace('\n', ' ');
    }

    public async Task<byte[]> DownloadFileAsync(
        uint appId,
        string remoteFileName,
        long expectedRawSize,
        string expectedSha1,
        Action<string>? onHostObserved = null,
        CancellationToken cancellationToken = default)
    {
        var response = await cloud.ClientFileDownload(new CCloud_ClientFileDownload_Request
        {
            appid = appId,
            filename = remoteFileName,
            realm = 0,
            force_proxy = false,
        }).ToTask().WaitAsync(cancellationToken);
        EnsureOk(response.Result, "ClientFileDownload");

        var info = response.Body;
        if (info.is_explicit_delete)
            throw new SteamCloudProtocolException("Steam reports that this cloud file was deleted.");
        if (info.encrypted)
            throw new SteamCloudProtocolException("Steam returned an encrypted cloud file that this build cannot decode.");
        if (info.raw_file_size != expectedRawSize || !ToSha1(info.sha_file).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            throw new SteamCloudProtocolException("Steam Cloud file details changed between listing and download.");

        var uri = BuildUri(info.use_https, info.url_host, info.url_path);
        onHostObserved?.Invoke(uri.Host);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddHeaders(request, info.request_headers);
        using var httpResponse = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
            throw new SteamCloudHttpException("Download", (int)httpResponse.StatusCode);
        if (httpResponse.RequestMessage?.RequestUri is { } finalUri && !finalUri.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
            onHostObserved?.Invoke(finalUri.Host);

        var compressed = await ReadLimitedAsync(httpResponse.Content, MaxDownloadBytes, cancellationToken);
        var bytes = info.raw_file_size == info.file_size ? compressed : UnzipIfNeeded(compressed);
        if (bytes.LongLength != expectedRawSize)
            throw new SteamCloudProtocolException("Downloaded cloud file has the wrong uncompressed size.");
        if (!ComputeSha1(bytes).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            throw new SteamCloudProtocolException("Downloaded cloud file failed its SHA1 check.");
        return bytes;
    }

    public async Task UploadBatchAsync(
        uint appId,
        string machineName,
        IReadOnlyList<SteamCloudUpload> files,
        Action<string>? onHostObserved = null,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            return;

        var batchRequest = new CCloud_BeginAppUploadBatch_Request
        {
            appid = appId,
            machine_name = machineName,
            client_id = 0,
            app_build_id = 0,
        };
        batchRequest.files_to_upload.AddRange(files.Select(f => f.RemoteFileName));

        var batchResponse = await cloud.BeginAppUploadBatch(batchRequest).ToTask().WaitAsync(cancellationToken);
        EnsureOk(batchResponse.Result, "BeginAppUploadBatch");

        ulong batchId = batchResponse.Body.batch_id;
        if (batchId == 0)
            throw new SteamCloudProtocolException("Steam returned an invalid upload batch ID.");
        // app_change_number 的語意 proto 沒寫（未查證），只記下來，不拿來擋上傳
        _log($"Steam Cloud upload batch started files={files.Count} machineName={machineName} appChangeNumber={batchResponse.Body.app_change_number}");

        bool batchSucceeded = false;
        Exception? uploadError = null;
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UploadOneAsync(appId, batchId, file, onHostObserved, cancellationToken);
            }
            batchSucceeded = true;
        }
        catch (Exception e)
        {
            uploadError = e;
            throw;
        }
        finally
        {
            try
            {
                var complete = await cloud.CompleteAppUploadBatchBlocking(new CCloud_CompleteAppUploadBatch_Request
                {
                    appid = appId,
                    batch_id = batchId,
                    batch_eresult = (uint)(batchSucceeded ? EResult.OK : EResult.Fail),
                }).ToTask().WaitAsync(CleanupTimeout);
                EnsureOk(complete.Result, "CompleteAppUploadBatch");
            }
            catch when (uploadError is not null)
            {
                // Preserve the original transfer failure; the caller retries from the unchanged local state.
            }
        }
    }

    private async Task UploadOneAsync(
        uint appId,
        ulong batchId,
        SteamCloudUpload file,
        Action<string>? onHostObserved,
        CancellationToken cancellationToken)
    {
        byte[] sha1 = SHA1.HashData(file.Bytes);
        var begin = await cloud.ClientBeginFileUpload(new CCloud_ClientBeginFileUpload_Request
        {
            appid = appId,
            file_size = checked((uint)file.Bytes.Length),
            raw_file_size = checked((uint)file.Bytes.Length),
            file_sha = sha1,
            time_stamp = checked((ulong)Math.Max(0, file.ModifiedAtUtc.ToUnixTimeSeconds())),
            filename = file.RemoteFileName,
            platforms_to_sync = uint.MaxValue,
            cell_id = checked((uint)Math.Max(0, steamClient.CellID ?? 0)),
            can_encrypt = false,
            is_shared_file = false,
            upload_batch_id = batchId,
        }).ToTask().WaitAsync(cancellationToken);
        EnsureOk(begin.Result, "ClientBeginFileUpload");

        var body = begin.Body;
        if (body.encrypt_file)
            throw new SteamCloudProtocolException("Steam requested an encrypted cloud upload that this build cannot produce.");
        if (body.block_requests.Count == 0)
            throw new SteamCloudProtocolException("Steam returned no upload blocks for a changed file.");
        _log($"Steam Cloud upload file={file.LocalRelativePath} bytes={file.Bytes.LongLength} sha1={file.Sha1[..8]} blocks={body.block_requests.Count}");

        try
        {
            foreach (var block in body.block_requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = GetUploadBlock(file.Bytes, block);
                var uri = BuildUri(block.use_https, block.url_host, block.url_path);
                onHostObserved?.Invoke(uri.Host);

                using var request = new HttpRequestMessage(UploadMethod(block.http_method), uri)
                {
                    Content = new ByteArrayContent(bytes),
                };
                AddHeaders(request, block.request_headers);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new SteamCloudHttpException("Upload", (int)response.StatusCode);
                if (response.RequestMessage?.RequestUri is { } finalUri && !finalUri.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
                    onHostObserved?.Invoke(finalUri.Host);
            }

            var commit = await cloud.ClientCommitFileUpload(new CCloud_ClientCommitFileUpload_Request
            {
                transfer_succeeded = true,
                appid = appId,
                file_sha = sha1,
                filename = file.RemoteFileName,
            }).ToTask().WaitAsync(cancellationToken);
            EnsureOk(commit.Result, "ClientCommitFileUpload");
            if (!commit.Body.file_committed)
                throw new SteamCloudProtocolException("Steam did not commit an uploaded file.");
        }
        catch
        {
            try
            {
                await cloud.ClientCommitFileUpload(new CCloud_ClientCommitFileUpload_Request
                {
                    transfer_succeeded = false,
                    appid = appId,
                    file_sha = sha1,
                    filename = file.RemoteFileName,
                }).ToTask().WaitAsync(CleanupTimeout);
            }
            catch { }
            throw;
        }
    }

    private static byte[] GetUploadBlock(byte[] source, ClientCloudFileUploadBlockDetails block)
    {
        if (block.explicit_body_data is { Length: > 0 })
            return block.explicit_body_data;
        if (block.block_offset > (ulong)source.LongLength || block.block_length > source.LongLength - (long)block.block_offset)
            throw new SteamCloudProtocolException("Steam returned an upload block outside the source file.");
        return source.AsSpan(checked((int)block.block_offset), checked((int)block.block_length)).ToArray();
    }

    private static HttpMethod UploadMethod(int method) => method switch
    {
        3 => HttpMethod.Post,
        4 => HttpMethod.Put,
        _ => throw new SteamCloudProtocolException("Steam returned an unsupported upload HTTP method."),
    };

    private static Uri BuildUri(bool https, string host, string path)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains("@", StringComparison.Ordinal) || host.Contains("/", StringComparison.Ordinal))
            throw new SteamCloudProtocolException("Steam returned an invalid Cloud transfer host.");
        var scheme = https ? "https" : "http";
        var suffix = path.StartsWith('/') ? path : "/" + path;
        if (!Uri.TryCreate($"{scheme}://{host}{suffix}", UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Host.Length == 0)
            throw new SteamCloudProtocolException("Steam returned an invalid Cloud transfer URL.");
        return uri;
    }

    private static void AddHeaders<THeader>(HttpRequestMessage request, IEnumerable<THeader> headers)
    {
        foreach (var header in headers)
        {
            string name;
            string value;
            switch (header)
            {
                case CCloud_ClientFileDownload_Response.HTTPHeaders h:
                    name = h.name;
                    value = h.value;
                    break;
                case ClientCloudFileUploadBlockDetails.HTTPHeaders h:
                    name = h.name;
                    value = h.value;
                    break;
                default:
                    continue;
            }

            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw new SteamCloudProtocolException("Steam Cloud file exceeds the supported download size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new SteamCloudProtocolException("Steam Cloud file exceeds the supported download size.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] UnzipIfNeeded(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4b || bytes[2] != 0x03 || bytes[3] != 0x04)
            return bytes;
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0)
            throw new SteamCloudProtocolException("Steam returned an empty ZIP archive.");
        using var entry = archive.Entries[0].Open();
        using var output = new MemoryStream();
        entry.CopyTo(output);
        if (output.Length > MaxDownloadBytes)
            throw new SteamCloudProtocolException("Uncompressed Steam Cloud file exceeds the supported size.");
        return output.ToArray();
    }

    private static string ToSha1(byte[] bytes)
    {
        if (bytes is not { Length: 20 })
            throw new SteamCloudProtocolException("Steam returned a malformed SHA1 value.");
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeSha1(byte[] bytes) =>
        Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static DateTimeOffset FromUnixSeconds(ulong value)
    {
        if (value > long.MaxValue)
            throw new SteamCloudProtocolException("Steam returned an invalid cloud timestamp.");
        try { return DateTimeOffset.FromUnixTimeSeconds((long)value); }
        catch (ArgumentOutOfRangeException) { throw new SteamCloudProtocolException("Steam returned an invalid cloud timestamp."); }
    }

    private static void EnsureOk(EResult result, string operation)
    {
        if (result != EResult.OK)
            throw new SteamCloudProtocolException($"{operation} failed with Steam result {result}.");
    }
}

public sealed class SteamCloudProtocolException(string message) : Exception(message);

public sealed class SteamCloudHttpException(string operation, int statusCode)
    : Exception($"Steam Cloud {operation} HTTP status {statusCode}.");
