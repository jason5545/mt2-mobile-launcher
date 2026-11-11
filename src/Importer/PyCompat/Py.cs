using System.Globalization;
using System.Numerics;
using System.Text;

namespace Mt2Mobile.Importer.PyCompat;

/// <summary>Python 的基本語意：真假值、==、排序、str()／repr()。值的型別對應見 <see cref="PyDict"/>。</summary>
public static class Py
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool IsNumber(object? v) => v is bool or long or BigInteger or double or int;

    public static bool IsInt(object? v) => v is long or BigInteger or int;

    /// <summary>isinstance(v, int) and not isinstance(v, bool)</summary>
    public static bool IsIntNotBool(object? v) => v is long or BigInteger or int;

    public static BigInteger ToBig(object v) => v switch
    {
        bool b => b ? 1 : 0,
        int i => i,
        long l => l,
        BigInteger bi => bi,
        _ => throw new InvalidCastException($"不是整數：{Repr(v)}"),
    };

    public static double ToDouble(object v) => v switch
    {
        bool b => b ? 1.0 : 0.0,
        int i => i,
        long l => l,
        BigInteger bi => (double)bi,
        double d => d,
        _ => throw new InvalidCastException($"不是數字：{Repr(v)}"),
    };

    /// <summary>int(v)（整數或 bool）→ long；放不下就丟例外</summary>
    public static long ToLong(object? v) => v switch
    {
        bool b => b ? 1 : 0,
        int i => i,
        long l => l,
        BigInteger bi => (long)bi,
        double d => (long)Math.Truncate(d),
        string s => long.Parse(s.Trim(), NumberStyles.AllowLeadingSign, Inv),
        null => throw new InvalidCastException("int(None)"),
        _ => throw new InvalidCastException($"int({Repr(v)})"),
    };

    /// <summary>整數正規化：放得進 long 就用 long</summary>
    public static object Int(BigInteger v) => v >= long.MinValue && v <= long.MaxValue ? (object)(long)v : v;

    public static bool NumEquals(object a, object b)
    {
        if (a is double || b is double)
        {
            double x = ToDouble(a), y = ToDouble(b);
            if (a is BigInteger || b is BigInteger)
            {
                // 大整數跟浮點數：只有浮點數剛好是那個整數才相等
                var (i, f) = a is double ? (b, x) : (a, y);
                return !double.IsNaN(f) && !double.IsInfinity(f) && Math.Floor(f) == f && new BigInteger(f) == ToBig(i);
            }
            return x == y;
        }
        return ToBig(a) == ToBig(b);
    }

    /// <summary>Python 的真假值</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        BigInteger bi => !bi.IsZero,
        double d => d != 0.0,
        string s => s.Length > 0,
        PyDict d => d.Count > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary>Python 的 ==（dict 不看順序）</summary>
    public static bool Eq(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (IsNumber(a) && IsNumber(b)) return NumEquals(a, b);
        if (a is string sa) return b is string sb && sa == sb;
        if (a is List<object?> la)
        {
            if (b is not List<object?> lb || la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++)
                if (!Eq(la[i], lb[i])) return false;
            return true;
        }
        if (a is PyDict da)
        {
            if (b is not PyDict db || da.Count != db.Count) return false;
            foreach (var (k, v) in da)
                if (!db.TryGetValue(k, out var w) || !Eq(v, w)) return false;
            return true;
        }
        return a.Equals(b);
    }

    /// <summary>Python 的 &lt; 排序：數字照數值、字串照 code point、list 逐項</summary>
    public static int Compare(object? a, object? b)
    {
        if (IsNumber(a) && IsNumber(b))
        {
            if (a is double || b is double)
            {
                if (NumEquals(a!, b!)) return 0;
                return ToDouble(a!) < ToDouble(b!) ? -1 : 1;
            }
            return ToBig(a!).CompareTo(ToBig(b!));
        }
        if (a is string sa && b is string sb) return PyStr.Compare(sa, sb);
        if (a is List<object?> la && b is List<object?> lb)
        {
            int n = Math.Min(la.Count, lb.Count);
            for (int i = 0; i < n; i++)
            {
                if (Eq(la[i], lb[i])) continue;
                return Compare(la[i], lb[i]);
            }
            return la.Count.CompareTo(lb.Count);
        }
        throw new InvalidOperationException($"不能比較 {Repr(a)} 與 {Repr(b)}");
    }

    /// <summary>str(v)</summary>
    public static string Str(object? v) => v switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        int i => i.ToString(Inv),
        long l => l.ToString(Inv),
        BigInteger bi => bi.ToString(Inv),
        double d => PyFloat.ReprPython(d),
        _ => Repr(v),
    };

    /// <summary>repr(v)（錯誤訊息裡用到的寫法）</summary>
    public static string Repr(object? v)
    {
        var sb = new StringBuilder();
        AppendRepr(sb, v);
        return sb.ToString();
    }

    private static void AppendRepr(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case string s: AppendStrRepr(sb, s); break;
            case List<object?> list:
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    AppendRepr(sb, list[i]);
                }
                sb.Append(']');
                break;
            case PyTuple t:
                sb.Append('(');
                for (int i = 0; i < t.Items.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    AppendRepr(sb, t.Items[i]);
                }
                if (t.Items.Length == 1) sb.Append(',');
                sb.Append(')');
                break;
            case PyDict d:
                sb.Append('{');
                bool first = true;
                foreach (var (k, x) in d)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    AppendRepr(sb, k);
                    sb.Append(": ");
                    AppendRepr(sb, x);
                }
                sb.Append('}');
                break;
            default: sb.Append(Str(v)); break;
        }
    }

    private static void AppendStrRepr(StringBuilder sb, string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        sb.Append(quote);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == quote || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, s[i + 1]);
                if (IsPrintable(cp)) sb.Append(c).Append(s[i + 1]);
                else sb.Append("\\U").Append(cp.ToString("x8", Inv));
                i++;
            }
            else if (c < ' ' || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2", Inv));
            else if (c < 0x7f || IsPrintable(c)) sb.Append(c);
            else if (c <= 0xff) sb.Append("\\x").Append(((int)c).ToString("x2", Inv));
            else sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
        }
        sb.Append(quote);
    }

    private static bool IsPrintable(int cp)
    {
        if (cp == ' ') return true;
        if (cp >= 0xD800 && cp <= 0xDFFF) return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        return cat switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator => false,
            _ => true,
        };
    }

    public static List<object?> List(params object?[] items) => new(items);

    /// <summary>len(s)：code point 數（Python 的 str 長度）</summary>
    public static int Len(string s)
    {
        int n = s.Length;
        for (int i = 0; i < s.Length - 1; i++)
            if (char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1])) { n--; i++; }
        return n;
    }
}

/// <summary>Python 的 tuple（只在 repr 與排序時跟 list 不同）</summary>
public sealed record PyTuple(params object?[] Items);
