using System.Globalization;
using System.Numerics;
using System.Text;

namespace Mt2Mobile.Importer.PyCompat;

/// <summary>
/// Python 的 json 模組：dumps 照 json.dumps(ensure_ascii=False) 的輸出（跳脫、浮點數 repr、indent 的排版、sort_keys），
/// loads 照 json.loads 的型別（整數 → long／BigInteger、帶小數點或指數 → double、NaN／Infinity）。
/// </summary>
public static class PyJson
{
    /// <summary>json.dumps(obj, ensure_ascii=False, separators=(",", ":"))</summary>
    public static string Compact(object? obj) => Dumps(obj, indent: null, itemSep: ",", keySep: ":", sortKeys: false);

    /// <summary>json.dumps(obj, ensure_ascii=False, indent=n[, sort_keys])（indent 時預設的分隔是 "," 與 ": "）</summary>
    public static string Indented(object? obj, int indent, bool sortKeys = false) => Dumps(obj, indent, ",", ": ", sortKeys);

    public static string Dumps(object? obj, int? indent, string itemSep, string keySep, bool sortKeys)
    {
        var sb = new StringBuilder();
        new Writer(sb, indent, itemSep, keySep, sortKeys).Write(obj, 0);
        return sb.ToString();
    }

    private readonly struct Writer(StringBuilder sb, int? indent, string itemSep, string keySep, bool sortKeys)
    {
        public void Write(object? v, int level)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: AppendString(sb, s); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case BigInteger bi: sb.Append(bi.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(PyFloat.Json(d)); break;
                case float f: sb.Append(PyFloat.Json(f)); break;
                case PyDict d: WriteDict(d, level); break;
                case List<object?> list: WriteList(list, level); break;
                case PyTuple t: WriteList(new List<object?>(t.Items), level); break;
                case RawJson raw: sb.Append(raw.Text); break;
                default: throw new InvalidOperationException($"Object of type {v.GetType().Name} is not JSON serializable");
            }
        }

        private void NewLine(int level)
        {
            sb.Append('\n').Append(' ', indent!.Value * level);
        }

        private void WriteList(List<object?> list, int level)
        {
            if (list.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            if (indent is not null) NewLine(level + 1);
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(itemSep);
                    if (indent is not null) NewLine(level + 1);
                }
                Write(list[i], level + 1);
            }
            if (indent is not null) NewLine(level);
            sb.Append(']');
        }

        private void WriteDict(PyDict d, int level)
        {
            if (d.Count == 0) { sb.Append("{}"); return; }
            IEnumerable<KeyValuePair<object?, object?>> items = d;
            if (sortKeys)
            {
                var sorted = d.ToList();
                sorted.Sort((a, b) => Py.Compare(a.Key, b.Key));
                items = sorted;
            }
            sb.Append('{');
            if (indent is not null) NewLine(level + 1);
            bool first = true;
            foreach (var (k, v) in items)
            {
                if (!first)
                {
                    sb.Append(itemSep);
                    if (indent is not null) NewLine(level + 1);
                }
                first = false;
                AppendString(sb, KeyString(k));
                sb.Append(keySep);
                Write(v, level + 1);
            }
            if (indent is not null) NewLine(level);
            sb.Append('}');
        }
    }

    /// <summary>json.dumps 把非字串的鍵轉成字串的規則</summary>
    private static string KeyString(object? k) => k switch
    {
        string s => s,
        null => "null",
        bool b => b ? "true" : "false",
        double d => PyFloat.Json(d),
        long or int or BigInteger => Py.Str(k),
        _ => throw new InvalidOperationException($"keys must be str, int, float, bool or None, not {k.GetType().Name}"),
    };

    /// <summary>ensure_ascii=False 的字串：只跳脫 " \ 與控制字元</summary>
    public static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 0x20 && c != '"' && c != '\\') continue;
            sb.Append(s, start, i - start);
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default: sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
            }
            start = i + 1;
        }
        sb.Append(s, start, s.Length - start);
        sb.Append('"');
    }

    public static string QuoteString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        AppendString(sb, s);
        return sb.ToString();
    }

    /// <summary>已經是 JSON 的文字，原樣接上（packs 用）</summary>
    public sealed record RawJson(string Text);

    // ───────── loads ─────────

    public static object? Loads(string text)
    {
        var p = new Parser(text);
        p.SkipWs();
        var v = p.Value();
        p.SkipWs();
        if (p.Pos != text.Length) throw new FormatException($"Extra data at {p.Pos}");
        return v;
    }

    /// <summary>json.loads(path.read_text(encoding="utf-8"))</summary>
    public static object? LoadFile(string path) => Loads(PyStr.ReadText(path));

    public static PyDict LoadDict(string path) => (PyDict)LoadFile(path)!;

    private sealed class Parser(string s)
    {
        public int Pos;

        public void SkipWs()
        {
            while (Pos < s.Length && s[Pos] is ' ' or '\t' or '\n' or '\r') Pos++;
        }

        public object? Value()
        {
            if (Pos >= s.Length) throw new FormatException("Expecting value");
            char c = s[Pos];
            switch (c)
            {
                case '{': return Object();
                case '[': return Array();
                case '"': return String();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                case 'N': Expect("NaN"); return double.NaN;
                case 'I': Expect("Infinity"); return double.PositiveInfinity;
                case '-' when Pos + 1 < s.Length && s[Pos + 1] == 'I': Expect("-Infinity"); return double.NegativeInfinity;
                default: return Number();
            }
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(s, Pos, word, 0, word.Length) != 0) throw new FormatException($"Expecting value at {Pos}");
            Pos += word.Length;
        }

        private PyDict Object()
        {
            var d = new PyDict();
            Pos++;
            SkipWs();
            if (s[Pos] == '}') { Pos++; return d; }
            while (true)
            {
                SkipWs();
                if (s[Pos] != '"') throw new FormatException($"Expecting property name at {Pos}");
                string key = String();
                SkipWs();
                if (s[Pos] != ':') throw new FormatException($"Expecting ':' at {Pos}");
                Pos++;
                SkipWs();
                d[key] = Value();
                SkipWs();
                if (s[Pos] == ',') { Pos++; continue; }
                if (s[Pos] == '}') { Pos++; return d; }
                throw new FormatException($"Expecting ',' delimiter at {Pos}");
            }
        }

        private List<object?> Array()
        {
            var list = new List<object?>();
            Pos++;
            SkipWs();
            if (s[Pos] == ']') { Pos++; return list; }
            while (true)
            {
                SkipWs();
                list.Add(Value());
                SkipWs();
                if (s[Pos] == ',') { Pos++; continue; }
                if (s[Pos] == ']') { Pos++; return list; }
                throw new FormatException($"Expecting ',' delimiter at {Pos}");
            }
        }

        private string String()
        {
            Pos++;
            int start = Pos;
            StringBuilder? sb = null;
            while (true)
            {
                char c = s[Pos];
                if (c == '"')
                {
                    string r = sb is null ? s[start..Pos] : sb.Append(s, start, Pos - start).ToString();
                    Pos++;
                    return r;
                }
                if (c == '\\')
                {
                    sb ??= new StringBuilder();
                    sb.Append(s, start, Pos - start);
                    char e = s[Pos + 1];
                    Pos += 2;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)int.Parse(s.AsSpan(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException($"Invalid \\escape at {Pos}");
                    }
                    start = Pos;
                    continue;
                }
                Pos++;
            }
        }

        private object Number()
        {
            int start = Pos;
            if (s[Pos] == '-') Pos++;
            bool isFloat = false;
            while (Pos < s.Length)
            {
                char c = s[Pos];
                if (c is >= '0' and <= '9') { Pos++; continue; }
                if (c is '.' or 'e' or 'E' or '+' or '-') { isFloat = true; Pos++; continue; }
                break;
            }
            var span = s.AsSpan(start, Pos - start);
            if (span.Length == 0) throw new FormatException($"Expecting value at {start}");
            if (isFloat) return double.Parse(span, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (long.TryParse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
            return BigInteger.Parse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
    }
}
