using System.Text.Json;
using Android.Content;

namespace Mt2Mobile.Launcher.SteamUi;

public static class SteamCloudStatusStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public sealed record Status(
        string State,
        DateTimeOffset? LastSyncAtUtc,
        int Downloaded,
        int Uploaded,
        int CloudFiles,
        string Reason,
        IReadOnlyList<string> Hosts);

    public static Status? Load(Context context)
    {
        var path = Path.Combine(context.FilesDir!.AbsolutePath, "cloud", "status.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Status>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            LauncherLog.Write($"cloud status unreadable: {e.GetType().Name}");
            return null;
        }
    }

    public static void Save(Context context, Status status)
    {
        var directory = Path.Combine(context.FilesDir!.AbsolutePath, "cloud");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "status.json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(status, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}
