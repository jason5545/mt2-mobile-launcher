using System.Text.RegularExpressions;

namespace Mt2Mobile.Steam.SteamCloud;

/// <summary>Maps MT2's Auto-Cloud names to the game's persistent data directory.</summary>
public static partial class SteamCloudPathMapper
{
    public const string ExpectedRootPrefix = "%WinAppDataLocalLow%";
    public const string GamePathPrefix = "Shiny Shoe/MonsterTrain2/";

    public static bool TryMap(string? rootPrefix, string? fileName, out string localRelativePath)
    {
        localRelativePath = "";
        if (!TryNormalizeRootPrefix(rootPrefix, out var normalizedRoot)
            || !TryNormalizeRelative(fileName, out var normalizedFile))
            return false;

        var combined = JoinRemotePath(normalizedRoot, normalizedFile);
        if (!combined.StartsWith(ExpectedRootPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var cloudRelative = combined[ExpectedRootPrefix.Length..].TrimStart('/');
        if (!TryNormalizeRelative(cloudRelative, out cloudRelative)
            || !cloudRelative.StartsWith(GamePathPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = cloudRelative[GamePathPrefix.Length..];
        if (!TryNormalizeRelative(relative, out relative) || !IsSupportedLocalPath(relative))
            return false;

        localRelativePath = relative;
        return true;
    }

    /// <summary>Returns the full Steam filename in the exact Auto-Cloud path namespace.</summary>
    public static string ToRemoteFileName(string localRelativePath, string rootPrefix = ExpectedRootPrefix)
    {
        if (!TryNormalizeRootPrefix(rootPrefix, out var normalizedRoot)
            || !TryNormalizeRelative(localRelativePath, out var relative)
            || !IsSupportedLocalPath(relative))
            throw new ArgumentException("Unsupported Steam Cloud path.", nameof(localRelativePath));

        var fullRemotePath = ExpectedRootPrefix + GamePathPrefix + relative;
        if (!IsRootPrefixOf(normalizedRoot, fullRemotePath))
            throw new ArgumentException("The Steam Cloud root prefix does not contain this MT2 file.", nameof(rootPrefix));
        return fullRemotePath;
    }

    public static bool IsSupportedLocalPath(string? path)
    {
        if (!TryNormalizeRelative(path, out var relative))
            return false;

        if (relative.Equals("preferences.json", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("metagameSave.json", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("metagameSave.json.backup", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("run-history/RunHistory.json", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!relative.StartsWith("saves/", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = relative["saves/".Length..];
        return name.Length > "save-.json".Length
            && name.StartsWith("save-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !name.Contains('/');
    }

    internal static string JoinRemotePath(string rootPrefix, string fileName)
    {
        var root = rootPrefix.Trim().Replace('\\', '/').TrimEnd('/');
        var file = fileName.Trim().Replace('\\', '/').TrimStart('/');
        return root.Equals(ExpectedRootPrefix, StringComparison.OrdinalIgnoreCase)
            ? root + file
            : root + "/" + file;
    }

    private static bool TryNormalizeRootPrefix(string? rootPrefix, out string normalized)
    {
        normalized = "";
        if (rootPrefix is null)
            return false;

        var value = rootPrefix.Trim().Replace('\\', '/');
        if (!value.StartsWith(ExpectedRootPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = value[ExpectedRootPrefix.Length..];
        if (suffix.StartsWith('/')) suffix = suffix[1..];
        if (suffix.EndsWith('/')) suffix = suffix[..^1];
        if (suffix.Length == 0)
        {
            normalized = ExpectedRootPrefix;
            return true;
        }

        if (!TryNormalizeRelative(suffix, out suffix))
            return false;
        normalized = ExpectedRootPrefix + suffix;
        return true;
    }

    private static bool IsRootPrefixOf(string rootPrefix, string fullPath)
    {
        if (rootPrefix.Equals(ExpectedRootPrefix, StringComparison.OrdinalIgnoreCase))
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);

        return fullPath.StartsWith(rootPrefix + "/", StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeRelative(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(path))
            return false;

        var value = path.Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains("//", StringComparison.Ordinal)
            || value.Contains(':') || value.Contains('\0') || DrivePrefixRegex().IsMatch(value))
            return false;

        var parts = value.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            return false;

        normalized = string.Join('/', parts);
        return true;
    }

    [GeneratedRegex("^[A-Za-z]:")]
    private static partial Regex DrivePrefixRegex();
}
