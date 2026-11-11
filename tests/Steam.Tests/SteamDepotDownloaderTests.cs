using System.Security.Cryptography;
using System.Text;
using Mt2Mobile.Steam;
using SteamKit2;
using Xunit;

namespace Mt2Mobile.Steam.Tests;

public sealed class SteamDepotDownloaderTests
{
    private static readonly SteamDepotSpec Depot = new(2742830, 2742831, 1234567890123456789);

    [Fact]
    public async Task Skips_an_existing_file_when_size_and_sha1_match()
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic managed payload");
        using var temp = new TempDirectory();
        var file = CreateFile("MonsterTrain2_Data/Managed/fake.dll", bytes);
        var transport = new FakeTransport(CreateManifest(file), (file.Chunks[0].ChunkID!, bytes));
        var destination = Path.Combine(temp.Path, "MonsterTrain2_Data", "Managed", "fake.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, bytes);

        using var downloader = new SteamDepotDownloader(transport);
        var plan = await downloader.PrepareAsync([Depot], "MonsterTrain2_Data", temp.Path);
        await downloader.DownloadAsync(plan);

        Assert.Equal(0, transport.ChunkCalls);
        Assert.Equal(0, plan.BytesToDownload);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Redownloads_an_existing_file_when_sha1_does_not_match()
    {
        var expected = Encoding.UTF8.GetBytes("synthetic correct data");
        var corrupted = Enumerable.Repeat((byte)'x', expected.Length).ToArray();
        using var temp = new TempDirectory();
        var file = CreateFile("MonsterTrain2_Data/Managed/fake.dll", expected);
        var transport = new FakeTransport(CreateManifest(file), (file.Chunks[0].ChunkID!, expected));
        var destination = Path.Combine(temp.Path, "MonsterTrain2_Data", "Managed", "fake.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, corrupted);

        using var downloader = new SteamDepotDownloader(transport);
        var plan = await downloader.PrepareAsync([Depot], "MonsterTrain2_Data", temp.Path);
        await downloader.DownloadAsync(plan);

        Assert.Equal(1, transport.ChunkCalls);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Reports_a_clear_error_when_the_pinned_manifest_is_denied()
    {
        using var temp = new TempDirectory();
        using var downloader = new SteamDepotDownloader(new FakeTransport(null));

        var error = await Assert.ThrowsAsync<SteamException>(() =>
            downloader.PrepareAsync([Depot], "MonsterTrain2_Data", temp.Path));

        Assert.Equal(SteamDepotErrors.ManifestUnavailableMessage, error.Message);
        Assert.Contains("不會改抓其他版本", error.Message);
    }

    [Fact]
    public async Task Downloads_only_files_under_the_target_Data_directory()
    {
        var dataBytes = Encoding.UTF8.GetBytes("synthetic data file");
        var outsideBytes = Encoding.UTF8.GetBytes("synthetic launcher file");
        using var temp = new TempDirectory();
        var inData = CreateFile("MonsterTrain2_Data/StreamingAssets/fake.bundle", dataBytes);
        var outsideData = CreateFile("MonoBleedingEdge/fake.dll", outsideBytes);
        var transport = new FakeTransport(CreateManifest(inData, outsideData),
            (inData.Chunks[0].ChunkID!, dataBytes), (outsideData.Chunks[0].ChunkID!, outsideBytes));
        using var downloader = new SteamDepotDownloader(transport);

        var plan = await downloader.PrepareAsync([Depot], "MonsterTrain2_Data", temp.Path);
        await downloader.DownloadAsync(plan);

        Assert.Single(plan.Files);
        Assert.Equal(1, transport.ChunkCalls);
        Assert.True(File.Exists(Path.Combine(temp.Path, "MonsterTrain2_Data", "StreamingAssets", "fake.bundle")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "MonoBleedingEdge", "fake.dll")));
    }

    [Theory]
    [InlineData("MonsterTrain2_Data/../outside.bin")]
    [InlineData("MonsterTrain2_Data/Managed/../../outside.bin")]
    [InlineData("/MonsterTrain2_Data/Managed/fake.dll")]
    [InlineData("C:/MonsterTrain2_Data/Managed/fake.dll")]
    public async Task Rejects_paths_that_are_absolute_or_escape_the_download_root(string path)
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("synthetic");
        var entry = CreateFile(path, payload);
        var transport = new FakeTransport(CreateManifest(entry), (entry.Chunks[0].ChunkID!, payload));
        using var downloader = new SteamDepotDownloader(transport);

        await Assert.ThrowsAsync<SteamException>(() =>
            downloader.PrepareAsync([Depot], "MonsterTrain2_Data", temp.Path));
        Assert.Empty(Directory.Exists(temp.Path) ? Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories) : []);
        Assert.Equal(0, transport.ChunkCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Concurrent_chunks_write_each_chunk_at_its_offset(int concurrency)
    {
        // 3 個檔，每個切成 5 個大小不同的 chunk；假傳輸層隨機延遲，讓完成順序跟排隊順序不同
        using var temp = new TempDirectory();
        var rng = new Random(20261023);
        var files = new List<(DepotManifest.FileData File, byte[] Bytes)>();
        var chunkData = new List<(byte[] Id, byte[] Data)>();
        for (int f = 0; f < 3; f++)
        {
            var sizes = Enumerable.Range(0, 5).Select(_ => rng.Next(1, 4096)).ToArray();
            var bytes = new byte[sizes.Sum()];
            rng.NextBytes(bytes);
            var file = CreateChunkedFile($"MonsterTrain2_Data/StreamingAssets/fake{f}.bundle", bytes, sizes);
            files.Add((file, bytes));
            int offset = 0;
            foreach (var size in sizes)
            {
                var piece = bytes.AsSpan(offset, size).ToArray();
                chunkData.Add((SHA1.HashData(piece), piece));
                offset += size;
            }
        }
        var transport = new FakeTransport(CreateManifest(files.Select(x => x.File).ToArray()), chunkData.ToArray()) { RandomDelay = true };
        using var downloader = new SteamDepotDownloader(transport);

        var plan = await downloader.PrepareAsync([Depot], "MonsterTrain2_Data", temp.Path);
        var reports = new List<SteamDepotDownloadProgress>();
        await downloader.DownloadAsync(plan, p => reports.Add(p), maxConcurrentChunks: concurrency);

        Assert.Equal(15, transport.ChunkCalls);
        Assert.True(transport.MaxInFlight <= concurrency);
        if (concurrency > 1) Assert.True(transport.MaxInFlight > 1);
        foreach (var (file, bytes) in files)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(temp.Path, file.FileName.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.steam-partial", SearchOption.AllDirectories));
        Assert.Equal(plan.TotalBytes, reports[^1].CompletedBytes);
        Assert.Equal(3, reports[^1].CompletedFiles);
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.CompletedBytes >= p.First.CompletedBytes));
    }

    private static DepotManifest.FileData CreateChunkedFile(string name, byte[] bytes, int[] sizes)
    {
        var file = CreateFile(name, bytes);
        file.Chunks.Clear();
        int offset = 0;
        foreach (var size in sizes)
        {
            var piece = bytes.AsSpan(offset, size).ToArray();
            file.Chunks.Add(new DepotManifest.ChunkData
            {
                ChunkID = SHA1.HashData(piece),
                Offset = (ulong)offset,
                CompressedLength = (uint)size,
                UncompressedLength = (uint)size,
            });
            offset += size;
        }
        return file;
    }

    private static DepotManifest CreateManifest(params DepotManifest.FileData[] files) => new()
    {
        DepotID = Depot.DepotId,
        ManifestGID = Depot.ManifestId,
        Files = files.ToList(),
    };

    private static DepotManifest.FileData CreateFile(string name, byte[] bytes)
    {
        var chunkId = SHA1.HashData(bytes);
        return new DepotManifest.FileData
        {
            FileName = name,
            FileNameHash = SHA1.HashData(Encoding.UTF8.GetBytes(name)),
            Flags = (EDepotFileFlag)0,
            TotalSize = (ulong)bytes.Length,
            FileHash = SHA1.HashData(bytes),
            LinkTarget = "",
            Chunks =
            [
                new DepotManifest.ChunkData
                {
                    ChunkID = chunkId,
                    Offset = 0,
                    CompressedLength = (uint)bytes.Length,
                    UncompressedLength = (uint)bytes.Length,
                },
            ],
        };
    }

    private sealed class FakeTransport : ISteamDepotTransport
    {
        private readonly DepotManifest? manifest;
        private readonly Dictionary<string, byte[]> chunks;

        public FakeTransport(DepotManifest? manifest, params (byte[] Id, byte[] Data)[] chunks)
        {
            this.manifest = manifest;
            this.chunks = chunks.ToDictionary(x => Convert.ToHexString(x.Id), x => x.Data);
        }

        private int chunkCalls, inFlight, maxInFlight;
        public int ChunkCalls => Volatile.Read(ref chunkCalls);
        public int MaxInFlight => Volatile.Read(ref maxInFlight);
        public bool RandomDelay { get; init; }

        public Task<DepotManifest?> DownloadManifestAsync(SteamDepotSpec depot, CancellationToken ct = default) =>
            Task.FromResult(manifest);

        public async Task<int> DownloadChunkAsync(SteamDepotSpec depot, DepotManifest.ChunkData chunk, byte[] destination, CancellationToken ct = default)
        {
            Interlocked.Increment(ref chunkCalls);
            int now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref maxInFlight)) && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
            try
            {
                if (RandomDelay) await Task.Delay(Random.Shared.Next(1, 25), ct);
                var data = chunks[Convert.ToHexString(chunk.ChunkID!)];
                data.CopyTo(destination, 0);
                return data.Length;
            }
            finally { Interlocked.Decrement(ref inFlight); }
        }

        public void Dispose() { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mt2-steam-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
