using System.Text.Json.Nodes;
using Mt2Mobile.Steam;

namespace Mt2Mobile.Launcher.Install;

/// <summary>
/// 支援的遊戲版本（targets/*.json，docs/phase4.md「版本綁定」）。比對的是原版 &lt;名稱&gt;_Data/Managed 底下的全部檔案：
/// 檔案集合與每個 SHA256 都要一樣，多一個、少一個都不算。
/// </summary>
public sealed class Target
{
    public required string Id { get; init; }
    public required string DataDir { get; init; }
    public required SortedDictionary<string, string> Managed { get; init; }
    public required JsonObject Json { get; init; }
    public SteamTargetDefinition? Steam { get; init; }

    public string ManagedTotal => Util.ListTotal(Managed);

    public string AssetRipper(string key) => (string)Json["assetRipper"]![key]!;

    /// <summary>importer 設定的 import 段（路徑以外的部分）</summary>
    public JsonObject ImportConfig => (JsonObject)Json["import"]!.DeepClone();

    /// <summary>這一步上次（Phase 3 實測）花的秒數，進度用；沒有記就是 0</summary>
    public double StepSeconds(string step) => Json["steps_s"]?[step]?.GetValue<double>() ?? 0;

    /// <summary>從 step 開始、剩下的步驟還要寫多少（kB）：各步的 du × 檔案系統的額外開銷 + 餘裕</summary>
    public long NeedKb(IEnumerable<string> steps)
    {
        var space = Json["space"]!;
        double sum = steps.Sum(s => space["writes_kB"]![s]?.GetValue<double>() ?? 0);
        return (long)(sum * space["overhead"]!.GetValue<double>()) + space["margin_kB"]!.GetValue<long>();
    }

    public static List<Target> LoadAll(string dir) =>
        Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(Load).ToList();

    static Target Load(string path)
    {
        var j = Util.ReadJson(path) ?? throw new InvalidDataException("target 讀不了：" + path);
        var managed = new SortedDictionary<string, string>(Util.Utf8Order);
        foreach (var (k, v) in (JsonObject)j["managed"]!) managed[k] = (string)v!;
        return new Target
        {
            Id = (string)j["id"]!,
            DataDir = (string)j["dataDir"]!,
            Managed = managed,
            Json = j,
            Steam = ReadSteamDefinition(j),
        };
    }

    /// <summary>跟原版 Managed 的雜湊比：完全相同回 null，不然回第一個不同的檔（給使用者看）</summary>
    public string? FirstDifference(IReadOnlyDictionary<string, string> managed)
    {
        foreach (var k in Managed.Keys.Union(managed.Keys).Order(Util.Utf8Order))
        {
            if (!managed.TryGetValue(k, out var have)) return Strings.Get(Resource.String.install_diff_missing_format, k);
            if (!Managed.TryGetValue(k, out var want)) return Strings.Get(Resource.String.install_diff_extra_format, k);
            if (have != want) return Strings.Get(Resource.String.install_diff_changed_format, k);
        }
        return null;
    }

    public double SpaceOverhead => Json["space"]!["overhead"]!.GetValue<double>();

    public static SteamTargetDefinition? ReadSteamDefinition(JsonObject json)
    {
        if (json["steam"] is not JsonObject steam || steam["depotId"] is null || steam["manifestId"] is null)
            return null;
        var dlc = (steam["dlc"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(d => new SteamDlcDepotTarget(
                d["dlcAppId"]!.GetValue<uint>(),
                d["depotId"]!.GetValue<uint>(),
                d["manifestId"]!.GetValue<ulong>()))
            .ToArray();
        return new SteamTargetDefinition(
            steam["depotId"]!.GetValue<uint>(),
            steam["manifestId"]!.GetValue<ulong>(),
            (string)json["dataDir"]!,
            dlc);
    }
}
