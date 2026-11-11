// Application.systemLanguage 的來源：宿主拿到系統語系字串（zh-Hant-TW、zh_TW.UTF-8、en-US…），這裡換成 SystemLanguage。
// 中文照 Unity 的分法：繁體（Hant、TW、HK、MO）→ ChineseTraditional，簡體（Hans、CN、SG）→ ChineseSimplified，看不出來 → Chinese。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public static class SystemLanguages
    {
        static readonly Dictionary<string, SystemLanguage> ByIso = new Dictionary<string, SystemLanguage>
        {
            ["af"] = SystemLanguage.Afrikaans, ["ar"] = SystemLanguage.Arabic, ["eu"] = SystemLanguage.Basque, ["be"] = SystemLanguage.Belarusian,
            ["bg"] = SystemLanguage.Bulgarian, ["ca"] = SystemLanguage.Catalan, ["cs"] = SystemLanguage.Czech, ["da"] = SystemLanguage.Danish,
            ["nl"] = SystemLanguage.Dutch, ["en"] = SystemLanguage.English, ["et"] = SystemLanguage.Estonian, ["fo"] = SystemLanguage.Faroese,
            ["fi"] = SystemLanguage.Finnish, ["fr"] = SystemLanguage.French, ["de"] = SystemLanguage.German, ["el"] = SystemLanguage.Greek,
            ["he"] = SystemLanguage.Hebrew, ["iw"] = SystemLanguage.Hebrew, ["hu"] = SystemLanguage.Hungarian, ["is"] = SystemLanguage.Icelandic,
            ["id"] = SystemLanguage.Indonesian, ["in"] = SystemLanguage.Indonesian, ["it"] = SystemLanguage.Italian, ["ja"] = SystemLanguage.Japanese,
            ["ko"] = SystemLanguage.Korean, ["lv"] = SystemLanguage.Latvian, ["lt"] = SystemLanguage.Lithuanian, ["no"] = SystemLanguage.Norwegian,
            ["nb"] = SystemLanguage.Norwegian, ["nn"] = SystemLanguage.Norwegian, ["pl"] = SystemLanguage.Polish, ["pt"] = SystemLanguage.Portuguese,
            ["ro"] = SystemLanguage.Romanian, ["ru"] = SystemLanguage.Russian, ["sh"] = SystemLanguage.SerboCroatian, ["sr"] = SystemLanguage.SerboCroatian,
            ["hr"] = SystemLanguage.SerboCroatian, ["bs"] = SystemLanguage.SerboCroatian, ["sk"] = SystemLanguage.Slovak, ["sl"] = SystemLanguage.Slovenian,
            ["es"] = SystemLanguage.Spanish, ["sv"] = SystemLanguage.Swedish, ["th"] = SystemLanguage.Thai, ["tr"] = SystemLanguage.Turkish,
            ["uk"] = SystemLanguage.Ukrainian, ["vi"] = SystemLanguage.Vietnamese, ["hi"] = SystemLanguage.Hindi,
        };

        /// <summary>語系字串（BCP 47 或 POSIX，例：zh-Hant-TW、zh_TW.UTF-8）→ SystemLanguage；空字串、C、POSIX 回 null（交給呼叫端決定）</summary>
        public static SystemLanguage? FromLocale(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return null;
            var s = locale.Trim();
            var cut = s.IndexOfAny(new[] { '.', '@' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (s.Length == 0 || s == "C" || s == "POSIX") return null;
            var parts = s.Split('-', '_');
            var lang = parts[0].ToLowerInvariant();
            if (lang == "zh")
            {
                for (int i = 1; i < parts.Length; i++)
                {
                    switch (parts[i].ToUpperInvariant())
                    {
                        case "HANT": case "TW": case "HK": case "MO": return SystemLanguage.ChineseTraditional;
                        case "HANS": case "CN": case "SG": return SystemLanguage.ChineseSimplified;
                    }
                }
                return SystemLanguage.Chinese;
            }
            return ByIso.TryGetValue(lang, out var l) ? l : SystemLanguage.Unknown;
        }
    }
}
