// resolver 的 regex 與純量建構規則移植自 PyYAML 6.0.3（https://github.com/yaml/pyyaml）的 resolver.py、constructor.py，MIT，Copyright (c) 2017-2021 Ingy döt Net、Copyright (c) 2006-2016 Kirill Simonov；授權全文見 THIRD_PARTY_NOTICES.md。
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Mt2Mobile.Importer.PyCompat;

/// <summary>
/// PyYAML 的 load_all：YamlDotNet 只負責事件（第 0 步逐檔對過 libyaml 的事件流，importer 會讀的檔全部相同），
/// 純量的型別判斷與建構照 PyYAML 的 resolver／SafeConstructor。兩種規則：
/// <see cref="Resolver.Unity"/> 是 unity.py 的 UnityLoader（只有 null、整數、浮點數），
/// <see cref="Resolver.Yaml11"/> 是 shaders/convert.py 用的 CSafeLoader（完整 YAML 1.1：bool、八進位、六十進位…）。
/// </summary>
public static class PyYaml
{
    public enum Resolver { Unity, Yaml11 }

    /// <summary>yaml.load_all(text)：每個文件一個值（空文件是 null）</summary>
    public static List<object?> LoadAll(string text, Resolver resolver)
    {
        var docs = new List<object?>();
        var parser = new Parser(new StringReader(text));
        var c = new Composer(parser, resolver);
        parser.Consume<StreamStart>();
        while (!parser.TryConsume<StreamEnd>(out _))
        {
            parser.Consume<DocumentStart>();
            object? doc = parser.Accept<DocumentEnd>(out _) ? null : c.Node();
            parser.Consume<DocumentEnd>();
            c.Anchors.Clear();
            docs.Add(doc);
        }
        return docs;
    }

    private sealed class Composer(IParser parser, Resolver resolver)
    {
        public readonly Dictionary<string, object?> Anchors = new();

        public object? Node()
        {
            if (parser.TryConsume<AnchorAlias>(out var alias))
                return Anchors.TryGetValue(alias.Value.Value, out var target) ? target : throw new YamlException($"found undefined alias {alias.Value}");
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                var v = ConstructScalar(scalar);
                if (!scalar.Anchor.IsEmpty) Anchors[scalar.Anchor.Value] = v;
                return v;
            }
            if (parser.TryConsume<SequenceStart>(out var seq))
            {
                CheckCollectionTag(seq.Tag);
                var list = new List<object?>();
                if (!seq.Anchor.IsEmpty) Anchors[seq.Anchor.Value] = list;
                while (!parser.TryConsume<SequenceEnd>(out _))
                    list.Add(Node());
                return list;
            }
            if (parser.TryConsume<MappingStart>(out var map))
            {
                CheckCollectionTag(map.Tag);
                var d = new PyDict();
                if (!map.Anchor.IsEmpty) Anchors[map.Anchor.Value] = d;
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    bool merge = parser.Accept<Scalar>(out var ks) && ks.Style == ScalarStyle.Plain && ks.Tag.IsEmpty && ks.Value == "<<";
                    if (merge) throw new NotSupportedException("YAML 的 merge key（<<）沒有實作");
                    var key = Node();
                    if (key is List<object?> or PyDict) throw new YamlException("found unhashable key");
                    d[key] = Node();
                }
                return d;
            }
            throw new YamlException($"非預期的事件 {parser.Current}");
        }

        // shader 的 YAML 保留 `--- !u!48 &4800000` 標頭：convert.py 把 tag:unity3d.com,2011: 開頭的 tag 當一般 mapping
        private static void CheckCollectionTag(TagName tag)
        {
            if (tag.IsEmpty || tag.Value.StartsWith("tag:unity3d.com,2011:", StringComparison.Ordinal)
                || tag.Value is "tag:yaml.org,2002:map" or "tag:yaml.org,2002:seq")
                return;
            throw new NotSupportedException($"沒有處理的 tag {tag.Value}");
        }

        private object? ConstructScalar(Scalar s)
        {
            string v = s.Value;
            if (!s.Tag.IsEmpty && s.Tag.Value != "!")
            {
                return s.Tag.Value switch
                {
                    "tag:yaml.org,2002:str" => v,
                    "tag:yaml.org,2002:null" => null,
                    "tag:yaml.org,2002:int" => ConstructInt(v),
                    "tag:yaml.org,2002:float" => ConstructFloat(v),
                    "tag:yaml.org,2002:bool" => ConstructBool(v),
                    _ => throw new NotSupportedException($"沒有處理的 tag {s.Tag.Value}"),
                };
            }
            if (s.Style != ScalarStyle.Plain) return v;
            return Resolve(v, resolver) switch
            {
                Kind.Null => null,
                Kind.Int => ConstructInt(v),
                Kind.Float => ConstructFloat(v),
                Kind.Bool => ConstructBool(v),
                Kind.Str => v,
                Kind.Timestamp => throw new NotSupportedException($"YAML timestamp 沒有實作：{v}"),
                Kind.Merge => throw new NotSupportedException("YAML 的 merge key（<<）沒有實作"),
                _ => throw new YamlException($"could not determine a constructor for the tag 'tag:yaml.org,2002:value'（{v}）"),
            };
        }
    }

    public enum Kind { Str, Null, Bool, Int, Float, Timestamp, Merge, Value }

    // unity.py 的 UnityLoader：拿掉 bool／int／float／timestamp，加回這兩條
    private static readonly Regex UnityInt = new(@"^-?(?:0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex UnityFloat = new(@"^-?(?:[0-9]+\.[0-9]*|\.[0-9]+|[0-9]+)(?:[eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant);

    // PyYAML resolver.py 的 YAML 1.1 規則
    private static readonly Regex Y11Bool = new(@"^(?:yes|Yes|YES|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)$", RegexOptions.CultureInvariant);
    private static readonly Regex Y11Float = new(@"^(?:[-+]?(?:[0-9][0-9_]*)\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9][0-9_]*(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$", RegexOptions.CultureInvariant);
    private static readonly Regex Y11Int = new(@"^(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Y11Timestamp = new(@"^(?:[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]|[0-9][0-9][0-9][0-9]-[0-9][0-9]?-[0-9][0-9]?(?:[Tt]|[ \t]+)[0-9][0-9]?:[0-9][0-9]:[0-9][0-9](?:\.[0-9]*)?(?:[ \t]*(?:Z|[-+][0-9][0-9]?(?::[0-9][0-9])?))?)$", RegexOptions.CultureInvariant);

    private static bool IsNull(string v) => v is "" or "~" or "null" or "Null" or "NULL";

    // Python 的 regex 用 $ 時，字串結尾的一個 \n 也算結尾（re.match(r"^…$", "1\n") 對得到）；純量值不會有結尾換行以外的情況，照樣處理
    private static bool M(Regex r, string v) => r.IsMatch(v);

    /// <summary>plain 純量的型別（照 PyYAML 的 resolver 表與順序）</summary>
    public static Kind Resolve(string v, Resolver resolver)
    {
        if (resolver == Resolver.Unity)
        {
            if (IsNull(v)) return Kind.Null;
            if (v == "<<") return Kind.Merge;
            if (v == "=") return Kind.Value;
            if (M(UnityInt, v)) return Kind.Int;
            if (M(UnityFloat, v)) return Kind.Float;
            return Kind.Str;
        }
        if (M(Y11Bool, v)) return Kind.Bool;
        if (M(Y11Float, v)) return Kind.Float;
        if (M(Y11Int, v)) return Kind.Int;
        if (v == "<<") return Kind.Merge;
        if (IsNull(v)) return Kind.Null;
        if (M(Y11Timestamp, v)) return Kind.Timestamp;
        if (v == "=") return Kind.Value;
        return Kind.Str;
    }

    /// <summary>SafeConstructor.construct_yaml_bool</summary>
    public static bool ConstructBool(string v) => v.ToLowerInvariant() switch
    {
        "yes" or "true" or "on" => true,
        "no" or "false" or "off" => false,
        _ => throw new YamlException($"不是 bool：{v}"),
    };

    /// <summary>SafeConstructor.construct_yaml_int</summary>
    public static object ConstructInt(string value)
    {
        string v = value.Replace("_", "");
        int sign = 1;
        if (v.Length > 0 && v[0] == '-') sign = -1;
        if (v.Length > 0 && v[0] is '+' or '-') v = v[1..];
        if (v == "0") return 0L;
        BigInteger r;
        if (v.StartsWith("0b", StringComparison.Ordinal)) r = ParseBase(v[2..], 2);
        else if (v.StartsWith("0x", StringComparison.Ordinal)) r = BigInteger.Parse("0" + v[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        else if (v[0] == '0') r = ParseBase(v, 8);
        else if (v.Contains(':'))
        {
            var digits = v.Split(':').Select(p => BigInteger.Parse(p, CultureInfo.InvariantCulture)).Reverse();
            BigInteger b = 1;
            r = 0;
            foreach (var d in digits)
            {
                r += d * b;
                b *= 60;
            }
        }
        else r = BigInteger.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture);
        return Py.Int(sign * r);
    }

    private static BigInteger ParseBase(string s, int b)
    {
        BigInteger r = 0;
        foreach (char c in s) r = r * b + (c - '0');
        return r;
    }

    /// <summary>SafeConstructor.construct_yaml_float</summary>
    public static double ConstructFloat(string value)
    {
        string v = value.Replace("_", "").ToLowerInvariant();
        double sign = 1;
        if (v.Length > 0 && v[0] == '-') sign = -1;
        if (v.Length > 0 && v[0] is '+' or '-') v = v[1..];
        if (v == ".inf") return sign * double.PositiveInfinity;
        if (v == ".nan") return double.NaN;
        if (v.Contains(':'))
        {
            var digits = v.Split(':').Select(PyFloat.Parse).Reverse();
            double b = 1, r = 0.0;
            foreach (var d in digits)
            {
                r += d * b;
                b *= 60;
            }
            return sign * r;
        }
        return sign * PyFloat.Parse(v);
    }
}
