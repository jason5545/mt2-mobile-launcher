using System.Globalization;
using System.Text;

namespace Mt2Mobile.Importer.PyCompat;

/// <summary>
/// Python 的浮點數格式：repr（json.dumps 用的就是它）與 unity.f32。
/// 第 0 步拿兩款遊戲 Python 輸出的 217.9 萬種浮點數字面值、480 萬個 float32 對過，全部相同。
/// </summary>
public static class PyFloat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>json.dumps 寫浮點數的字串：NaN、Infinity、-Infinity，其餘同 repr</summary>
    public static string Json(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        return ReprPython(d);
    }

    /// <summary>repr(float)：最短往返的數字；10 的指數 ≤ -5 或 ≥ 16 用科學記號（1e-05、1.6884289e-06、1e+16）</summary>
    public static string ReprPython(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        var (digits, decpt, neg) = Decompose(d.ToString("R", Inv));
        var sb = new StringBuilder(24);
        if (neg) sb.Append('-');
        if (decpt <= -4 || decpt > 16)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            int x = decpt - 1;
            sb.Append('e').Append(x < 0 ? '-' : '+').Append(Math.Abs(x).ToString("00", Inv));
        }
        else if (decpt <= 0)
        {
            sb.Append("0.").Append('0', -decpt).Append(digits);
        }
        else if (decpt >= digits.Length)
        {
            sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
        }
        else
        {
            sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }
        return sb.ToString();
    }

    /// <summary>數字字串 → 有效數字（去掉前後的 0）與小數點位置（值 = 0.digits × 10^decpt）</summary>
    private static (string digits, int decpt, bool neg) Decompose(string s)
    {
        bool neg = s[0] == '-';
        if (neg) s = s[1..];
        int exp = 0;
        int e = s.IndexOfAny(['E', 'e']);
        if (e >= 0) { exp = int.Parse(s[(e + 1)..], Inv); s = s[..e]; }
        int dot = s.IndexOf('.');
        string intPart = dot < 0 ? s : s[..dot];
        string frac = dot < 0 ? "" : s[(dot + 1)..];
        string digits = intPart + frac;
        int decpt = intPart.Length + exp;
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        decpt -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        return (digits, decpt, neg);
    }

    /// <summary>float(f"{x:.{p}g}")：精確十進位值四捨六入五成雙到 p 位有效數字（Python 的格式化是正確捨入）</summary>
    public static double RoundSig(double x, int p)
    {
        // 一般情況 .NET 的 "E" 格式化已經是正確捨入；只有剛好五進位平手時規則不同（.NET 遠離 0、Python 成雙）。
        // 平手代表精確值只有 p+1 位有效數字，這種短小數的最短往返字串就是精確值，用它判斷。
        var (rd, _, _) = Decompose(x.ToString("R", Inv));
        if (rd.Length == p + 1 && rd[^1] == '5')
            return RoundExact(x, p);
        return double.Parse(x.ToString("E" + (p - 1).ToString(Inv), Inv), NumberStyles.Float, Inv);
    }

    private static double RoundExact(double x, int p)
    {
        var (digits, decpt, neg) = Decompose(x.ToString("E200", Inv)); // float32 的值最多約 112 位有效數字
        if (digits.Length > p)
        {
            var kept = new StringBuilder(digits[..p]);
            char next = digits[p];
            bool rest = digits.Length > p + 1; // 已去掉結尾的 0，後面還有字就一定不是 0
            bool up = next > '5' || (next == '5' && (rest || (kept[p - 1] - '0') % 2 == 1));
            if (up)
            {
                int i = p - 1;
                while (i >= 0 && kept[i] == '9') { kept[i] = '0'; i--; }
                if (i < 0) { kept.Insert(0, '1'); decpt++; kept.Length = p; }
                else kept[i]++;
            }
            digits = kept.ToString();
        }
        string s = (neg ? "-" : "") + "0." + digits + "E" + decpt.ToString(Inv);
        return double.Parse(s, NumberStyles.Float, Inv);
    }

    /// <summary>unity.f32：float32 轉成能還原成同一個 float32 的最短十進位（試 6、7、8 位，最後 9 位）</summary>
    public static double F32(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return x;
        int packed = BitConverter.SingleToInt32Bits((float)x);
        for (int p = 6; p <= 8; p++)
        {
            double y = RoundSig(x, p);
            if (BitConverter.SingleToInt32Bits((float)y) == packed) return y;
        }
        return RoundSig(x, 9);
    }

    /// <summary>round(x, n)（計時用；Python 是正確捨入、五成雙）</summary>
    public static double Round(double x, int n) => Math.Round(x, n, MidpointRounding.ToEven);

    /// <summary>float(s)：Python 的 float() 解析（接受前後空白、底線、inf／nan）</summary>
    public static double Parse(string s)
    {
        string t = PyStr.Strip(s).Replace("_", "");
        string lower = t.ToLowerInvariant();
        string body = lower.TrimStart('+', '-');
        bool neg = lower.StartsWith('-');
        if (body is "inf" or "infinity") return neg ? double.NegativeInfinity : double.PositiveInfinity;
        if (body == "nan") return double.NaN;
        return double.Parse(t, NumberStyles.Float, Inv);
    }
}
