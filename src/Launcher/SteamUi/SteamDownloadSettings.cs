using Android.Content;

namespace Mt2Mobile.Launcher.SteamUi;

/// <summary>
/// Steam 下載的「本體分塊並行數」（照 STS2：1 相容優先、2 推薦、4 高速網路，預設 2）。
/// 存在介面程序的 SharedPreferences，按下載時用 intent 的 extra 帶給 :install 服務。
/// </summary>
public static class SteamDownloadSettings
{
    public const string Extra = "concurrent-chunks";
    public const int Default = 2;
    public static readonly int[] Options = [1, 2, 4];
    private const string Prefs = "steam_download";
    private const string Key = "concurrent_chunks";

    public static int Normalize(int value) => value is 1 or 4 ? value : Default;

    public static int ConcurrentChunks(Context context) =>
        Normalize(context.GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetInt(Key, Default));

    public static void SetConcurrentChunks(Context context, int value) =>
        context.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!.PutInt(Key, Normalize(value)).Apply();
}
