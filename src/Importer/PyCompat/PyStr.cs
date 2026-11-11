using System.Text;

namespace Mt2Mobile.Importer.PyCompat;

/// <summary>Python 的字串規則：排序照 code point、讀檔的換行與錯誤處理、splitlines。</summary>
public static class PyStr
{
    /// <summary>sorted() 的字串順序（code point；UTF-16 的代理對要排在 U+E000–U+FFFF 後面）</summary>
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            char x = a[i], y = b[i];
            if (x != y) return Fix(x) - Fix(y);
        }
        return a.Length - b.Length;
    }

    private static int Fix(char c) => c >= 0xD800 ? (c >= 0xE000 ? c - 0x800 : c + 0x2000) : c;

    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    private static readonly UTF8Encoding Utf8Replace = new(false, false);

    /// <summary>Path.read_text(encoding="utf-8", errors="replace")：無效位元組換 U+FFFD，換行轉成 \n（universal newlines）</summary>
    public static string ReadText(string path) => UniversalNewlines(Utf8Replace.GetString(File.ReadAllBytes(path)));

    /// <summary>bytes.decode("utf-8")：嚴格，無效就丟例外；不轉換行</summary>
    public static bool TryDecodeUtf8(byte[] data, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>read_text(encoding="utf-8-sig")：開頭的 BOM 拿掉</summary>
    public static string ReadTextSig(string path)
    {
        var s = ReadText(path);
        return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
    }

    public static string UniversalNewlines(string s)
    {
        if (s.IndexOf('\r') < 0) return s;
        return s.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Path.write_text(text, encoding="utf-8")（macOS 不轉換行）</summary>
    public static void WriteText(string path, string text) => File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    /// <summary>str.splitlines(keepends)</summary>
    public static List<string> SplitLines(string s, bool keepEnds)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            int eol = 0;
            if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') eol = 2;
            else if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029') eol = 1;
            if (eol == 0) { i++; continue; }
            lines.Add(s.Substring(start, (keepEnds ? i + eol : i) - start));
            i += eol;
            start = i;
        }
        if (start < s.Length) lines.Add(s[start..]);
        return lines;
    }

    /// <summary>str.strip()（Python 的空白定義）</summary>
    public static string Strip(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    public static string LStrip(string s)
    {
        int a = 0;
        while (a < s.Length && IsSpace(s[a])) a++;
        return s[a..];
    }

    /// <summary>str.isspace() 的單一字元版</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '\x1c' or '\x1d' or '\x1e' or '\x1f'
        or '\x85' or '\xa0' or '\u1680' or (>= '\u2000' and <= '\u200a') or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000';

    /// <summary>bytes.hex()</summary>
    public static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(data);

    /// <summary>bytes.fromhex(s)（中間可以有空白）</summary>
    public static byte[] FromHex(string s)
    {
        if (s.Contains(' ')) s = s.Replace(" ", "");
        return Convert.FromHexString(s);
    }
}
