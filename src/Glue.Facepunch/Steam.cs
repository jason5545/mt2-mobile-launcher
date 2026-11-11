// redirects.txt 把 Facepunch 的公開 API 接到這裡（方法名照 IL 的：屬性是 get_X、事件是 add_X／remove_X）。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace Glue.Facepunch
{
    public static class Settings
    {
        // Steam 的固定離線預設身分（不是任何使用者的帳號）。
        public static ulong SteamId = 0x0110000100000000UL;
        public static string Name = "Player";
        /// <summary>Steam 的語言名（english、tchinese…）；沒設就照 Application.systemLanguage（Steam 用戶端的語言多半跟系統一樣，同 TS 版照瀏覽器語言）</summary>
        public static string Language;
        public static readonly HashSet<uint> OwnedDlc = new HashSet<uint>();
        public static bool AllDlc;

        /// <summary>SystemLanguage → Steam 的語言名（Steamworks 文件 Languages 頁的 API language code）；
        /// 中文沒分繁簡的照 TS 版給 schinese，沒有對應的給 english</summary>
        public static string SteamLanguage(UnityEngine.SystemLanguage l)
        {
            switch (l)
            {
                case UnityEngine.SystemLanguage.ChineseTraditional: return "tchinese";
                case UnityEngine.SystemLanguage.Chinese: case UnityEngine.SystemLanguage.ChineseSimplified: return "schinese";
                case UnityEngine.SystemLanguage.Korean: return "koreana";
                case UnityEngine.SystemLanguage.Arabic: case UnityEngine.SystemLanguage.Bulgarian: case UnityEngine.SystemLanguage.Czech:
                case UnityEngine.SystemLanguage.Danish: case UnityEngine.SystemLanguage.Dutch: case UnityEngine.SystemLanguage.Finnish:
                case UnityEngine.SystemLanguage.French: case UnityEngine.SystemLanguage.German: case UnityEngine.SystemLanguage.Greek:
                case UnityEngine.SystemLanguage.Hungarian: case UnityEngine.SystemLanguage.Indonesian: case UnityEngine.SystemLanguage.Italian:
                case UnityEngine.SystemLanguage.Japanese: case UnityEngine.SystemLanguage.Norwegian: case UnityEngine.SystemLanguage.Polish:
                case UnityEngine.SystemLanguage.Portuguese: case UnityEngine.SystemLanguage.Romanian: case UnityEngine.SystemLanguage.Russian:
                case UnityEngine.SystemLanguage.Spanish: case UnityEngine.SystemLanguage.Swedish: case UnityEngine.SystemLanguage.Thai:
                case UnityEngine.SystemLanguage.Turkish: case UnityEngine.SystemLanguage.Ukrainian: case UnityEngine.SystemLanguage.Vietnamese:
                    return l.ToString().ToLowerInvariant();
                default: return "english";
            }
        }
    }

    public static class SteamClient
    {
        public static bool get_IsValid() => true;
        public static SteamId get_SteamId() => Settings.SteamId;
        public static string get_Name() => Settings.Name;
        /// <summary>不連 Steam：把 Facepunch 自己的狀態設成「已初始化」。SteamInput 的介面物件建一個空殼
        ///（MT2 的 HackFixInitSteamInput 用反射拿 SteamClientClass&lt;SteamInput&gt;.Interface 叫 Init，裡面的原生呼叫回 false）</summary>
        public static void Init(uint appid, bool asyncCallbacks)
        {
            const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            typeof(Steamworks.SteamClient).GetField("initialized", S)?.SetValue(null, true);
            typeof(Steamworks.SteamClient).GetProperty("AppId", S)?.SetValue(null, (AppId)appid);
            var asm = typeof(Steamworks.SteamClient).Assembly;
            var iface = asm.GetType("Steamworks.ISteamInput");
            if (iface != null)
                typeof(SteamClientClass<Steamworks.SteamInput>).GetField("Interface", S)?.SetValue(null, RuntimeHelpers.GetUninitializedObject(iface));
        }
        public static void Shutdown() { }
        public static void RunCallbacks() { }
        public static bool RestartAppIfNecessary(uint appid) => false;
    }

    public static class SteamUtils
    {
        static NotificationPosition position;
        public static NotificationPosition get_OverlayNotificationPosition() => position;
        public static void set_OverlayNotificationPosition(NotificationPosition value) => position = value;
        public static bool get_IsOverlayEnabled() => false;
        public static string get_SteamUILanguage() => Settings.Language ??= Settings.SteamLanguage(UnityEngine.Application.systemLanguage);
        public static bool get_IsSteamInBigPictureMode() => false;
        public static bool get_IsRunningOnSteamDeck() => false;
        public static void add_OnGamepadTextInputDismissed(Action<bool> value) { }
        public static void remove_OnGamepadTextInputDismissed(Action<bool> value) { }
        public static bool ShowGamepadTextInput(GamepadTextInputMode inputMode, GamepadTextInputLineMode lineInputMode, string description, int maxChars, string existingText) => false;
        public static string GetEnteredGamepadText() => "";
    }

    public static class SteamFriends
    {
        public static IEnumerable<Friend> GetFriends() => Array.Empty<Friend>();
        public static void OpenStoreOverlay(AppId id, OverlayToStoreFlag overlayToStoreFlag) { }
        public static void OpenWebOverlay(string url, bool modal) { }
        public static Task<Image?> GetSmallAvatarAsync(SteamId steamid) => Task.FromResult<Image?>(null);
        public static Task<Image?> GetMediumAvatarAsync(SteamId steamid) => Task.FromResult<Image?>(null);
        public static Task<Image?> GetLargeAvatarAsync(SteamId steamid) => Task.FromResult<Image?>(null);
        public static bool SetRichPresence(string key, string value) => true;
    }

    public static class SteamUserStats
    {
        public static bool get_StatsRecieved() => true;
        public static IEnumerable<Achievement> get_Achievements() => Array.Empty<Achievement>();
        public static bool StoreStats() => true;
        public static bool RequestCurrentStats() => true;
    }

    public static class SteamUser
    {
        public static Task<AuthTicket> GetAuthSessionTicketAsync(NetIdentity identity, double timeoutSeconds) => Task.FromResult<AuthTicket>(null);
    }

    public static class SteamInput
    {
        public static IEnumerable<Controller> get_Controllers() => Array.Empty<Controller>();
        public static string GetPngActionGlyph(Controller controller, string action, GlyphSize size) => null;
    }

    public static class SteamApps
    {
        public static bool IsDlcInstalled(AppId appid) => Settings.AllDlc || Settings.OwnedDlc.Contains(appid.Value);
    }
}
