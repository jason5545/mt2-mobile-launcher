using System.Text.Json.Nodes;
using Mt2Mobile.Shared;

namespace Mt2Mobile.TextureExport;

/// <summary>
/// 這台裝置哪些貼圖格式保留原始壓縮資料（docs/perf-battle.md「B 段」第 4 題）：安裝跑在要玩的那台手機上，匯出前查 GPU 的擴充。
/// DXT1、DXT5 要 s3tc 加 s3tc_srgb（sRGB 的有 5,187 張）；BC7、BC6H 要 bptc。不支援的格式照舊解成 PNG。
/// Key 進匯出的指紋：換了 GPU（或強制不支援的診斷開關）就重匯出。
/// </summary>
public sealed class TexturePlan
{
    /// <summary>LZ4 HC 第 3 級：手機上原生 lz4 量到壓縮 170 MB/s、解壓約 4.5 GB/s，比 fast 小約一成五，比 HC 9 快 2.6 倍（docs/perf-battle.md）</summary>
    public const int Lz4Level = 3;

    public bool S3tc { get; init; }
    public bool S3tcSrgb { get; init; }
    public bool Bptc { get; init; }
    /// <summary>診斷開關強制的模式（png：當成全部不支援）；沒有是 null</summary>
    public string? Forced { get; init; }

    public IReadOnlyList<int> RawFormats
    {
        get
        {
            if (Forced == "png") return [];
            var l = new List<int>();
            if (S3tc && S3tcSrgb) l.AddRange([RawTexture.DXT1, RawTexture.DXT5]);
            if (Bptc) l.AddRange([RawTexture.BC6H, RawTexture.BC7]);
            return l;
        }
    }

    public bool KeepsRaw(int format) => RawFormats.Contains(format);

    /// <summary>進匯出指紋的字串：保留哪些格式、LZ4 等級</summary>
    public string Key => $"raw={string.Join(",", RawFormats.Select(RawTexture.FormatName))};lz4={Lz4Level}";

    public static TexturePlan FromExtensions(IEnumerable<string> extensions, string? forced = null)
    {
        var e = new HashSet<string>(extensions, StringComparer.Ordinal);
        return new TexturePlan
        {
            S3tc = e.Contains("GL_EXT_texture_compression_s3tc"),
            S3tcSrgb = e.Contains("GL_EXT_texture_compression_s3tc_srgb"),
            Bptc = e.Contains("GL_EXT_texture_compression_bptc") || e.Contains("GL_ARB_texture_compression_bptc"),
            Forced = forced,
        };
    }

    /// <summary>每種格式都留 PNG（桌面比對工具、沒有 GPU 資訊時）</summary>
    public static TexturePlan None(string? forced = null) => new() { Forced = forced };

    public static TexturePlan All() => new() { S3tc = true, S3tcSrgb = true, Bptc = true };

    /// <summary>寫進 install.json 的決定</summary>
    public JsonObject Describe() => new()
    {
        ["s3tc"] = S3tc,
        ["s3tcSrgb"] = S3tcSrgb,
        ["bptc"] = Bptc,
        ["raw"] = new JsonArray(RawFormats.Select(f => (JsonNode)RawTexture.FormatName(f)).ToArray()),
        ["lz4Level"] = Lz4Level,
        ["forced"] = Forced,
    };
}

/// <summary>匯出的統計：存成原始資料的張數與位元組、留 PNG 的原因</summary>
public sealed class TextureExportStats
{
    public readonly SortedDictionary<string, int> Raw = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, int> KeptPng = new(StringComparer.Ordinal);
    public long RawBytes, StoredBytes;
    public int Png;
    public double CompressSeconds;

    internal void AddRaw(int format, long raw, long stored, double seconds)
    {
        var k = RawTexture.FormatName(format);
        Raw[k] = Raw.GetValueOrDefault(k) + 1;
        RawBytes += raw;
        StoredBytes += stored;
        CompressSeconds += seconds;
    }

    internal void AddPng(string? keptBecause)
    {
        Png++;
        if (keptBecause != null) KeptPng[keptBecause] = KeptPng.GetValueOrDefault(keptBecause) + 1;
    }

    public JsonObject Describe() => new()
    {
        ["raw"] = new JsonObject(Raw.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["rawBytes"] = RawBytes,
        ["storedBytes"] = StoredBytes,
        ["compressSeconds"] = Math.Round(CompressSeconds, 2),
        ["png"] = Png,
        ["keptPng"] = new JsonObject(KeptPng.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
    };
}
