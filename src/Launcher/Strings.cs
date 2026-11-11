using Android.Content;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 手上沒有 Context 的地方（安裝步驟、背景執行緒、靜態欄位）取介面字串。用 Application 的 Context，跟著 app 目前的語系；
/// 三個程序（介面、:install、:game）各自有自己的 Application。
/// </summary>
public static class Strings
{
    public static string Get(int id) => Android.App.Application.Context.GetString(id);

    /// <summary>格式參數只用 int 與字串（對應 %d、%s）；其他型別轉成字串。</summary>
    public static string Get(int id, params object?[] args) =>
        Android.App.Application.Context.GetString(id, args.Select(a => a switch
        {
            int i => new Java.Lang.Integer(i),
            Java.Lang.Object o => o,
            _ => (Java.Lang.Object)new Java.Lang.String(a?.ToString() ?? ""),
        }).ToArray());

    /// <summary>src/Steam 的錯誤：有 Key 就顯示 steam_error_&lt;Key&gt; 的當下語系版本，沒有對應的字串就退回原本的訊息。</summary>
    public static string Of(Context c, Mt2Mobile.Steam.SteamException e)
    {
        if (e.Key is null) return e.Message;
        int id = c.Resources!.GetIdentifier("steam_error_" + e.Key, "string", c.PackageName);
        return id == 0 ? e.Message : c.GetString(id);
    }

    /// <summary>GB，一位小數（空間不夠的訊息用）</summary>
    public static string Gb(long kb) => (kb / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
}
