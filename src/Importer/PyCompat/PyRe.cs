using System.Text.RegularExpressions;

namespace Mt2Mobile.Importer.PyCompat;

/// <summary>
/// Python re 模組跟 .NET Regex 不同的字元類別，照 Python 的定義寫出來，搬 regex 時用這些代替 \s、\w、\b。
/// （.NET 的 \s 不含 \x1c–\x1f，\w 多了 Mn、Pc。）
/// </summary>
public static class PyRe
{
    /// <summary>Python 的 \s（str.isspace()）</summary>
    public const string S = @"[\t\n\x0b\x0c\r\x1c-\x1f \x85\xa0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]";

    /// <summary>[^\s…] 裡用的部分（不含括號）</summary>
    public const string SChars = @"\t\n\x0b\x0c\r\x1c-\x1f \x85\xa0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000";

    /// <summary>Python 的 \w（str.isalnum() 或底線）</summary>
    public const string W = @"[\p{L}\p{N}_]";

    public const string WChars = @"\p{L}\p{N}_";

    /// <summary>\b 前面（左邊不是 \w）</summary>
    public const string BStart = @"(?<![\p{L}\p{N}_])";

    /// <summary>\b 後面（右邊不是 \w）</summary>
    public const string BEnd = @"(?![\p{L}\p{N}_])";

    // 不用 RegexOptions.Compiled：Sprite 的 YAML 有幾十萬字的十六進位長行，編譯版的 HexLongLine 在這種行上是平方時間
    //（Diceomancer 的 binarysword_0005.asset：編譯版 5.2 秒、直譯版 0 毫秒）
    public static Regex M(string pattern) => new(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant);
}
