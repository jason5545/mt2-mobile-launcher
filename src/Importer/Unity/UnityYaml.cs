using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer.Unity;

/// <summary>一份 Unity YAML 檔裡的一個物件（unity.load_unity_docs 回傳的 dict）</summary>
public sealed record UnityDoc(int ClassID, string FileID, bool Stripped, string Type, PyDict Data);

/// <summary>
/// unity.py：Unity YAML 的載入（十六進位字串先加引號、UnityLoader 的型別規則）、GUID 索引、PPtr 正規化、
/// MonoScript 的 fileID（MD4）、頂點資料解碼。遊戲自己的列舉陣列欄位（import.int32ArrayKeys）放在實例裡，不用全域變數。
/// </summary>
public sealed class UnityYaml
{
    public static readonly Regex DocHeader = PyRe.M(@"^--- !u!(\d+) &(-?\d+)( stripped)?[ \t]*$");

    private static readonly string[] HexKeys =
        ["_typelessdata", "m_IndexBuffer", "m_ComponentGlyphIDs", "m_OpeningTagArray", "m_ClosingTagArray", "WeightList", "ElementList", "cbVariantIndices"];
    private static readonly string[] RawHexArrayKeys = ["m_ActiveFontFeatures", "m_ApplyToSortingLayers", "m_VertexStreams"];
    private static readonly string[] RawHexAnyKeys = ["_keys", "_keyValues"];

    private static readonly Regex HexKeyLine = PyRe.M(@"^(" + PyRe.S + @"*(?:- )?(?:" + string.Join("|", HexKeys) + @")): ([0-9a-f]+)[ \t]*$");
    private static readonly Regex HexLongLine = PyRe.M(@"^(" + PyRe.S + @"*(?:- )?[^" + PyRe.SChars + @":{}\[\]#][^:{}\[\]#]*): ([0-9a-f]{16,})[ \t]*$");
    private static readonly Regex HexAnyLine = PyRe.M(@"^(" + PyRe.S + @"*(?:- )?(?:" + string.Join("|", RawHexAnyKeys) + @")): ((?:[0-9a-f]{2})+)[ \t]*$");
    private static readonly Regex SuspectHexLine = PyRe.M(@"^" + PyRe.S + @"*(?:- )?([A-Za-z_]" + PyRe.W + @"*): ([0-9a-f]{8,15})[ \t]*$");
    private static readonly Regex HasHexLetter = PyRe.R("[a-f]");

    // PPtr 的 guid 長得像科學記號時會被讀成數字；Unity 的字串欄位寫成數字時沒有引號
    private static readonly Regex GuidValue = PyRe.M(PyRe.BStart + @"guid: ([0-9a-f]{32})" + PyRe.BEnd);
    private static readonly Regex StringKeyNumber = PyRe.M(@"^(" + PyRe.S + @"*(?:- )?(?:m_Name|m_text|m_TagString)): ([-+.0-9eE]+)[ \t]*$");

    public IReadOnlyList<string> Int32ArrayKeys { get; }
    private readonly HashSet<string> _int32ArrayKeys;
    private readonly Regex _hexArrayLine;
    private readonly HashSet<string> _knownHexKeys;

    public UnityYaml(IEnumerable<string>? int32ArrayKeys = null)
    {
        Int32ArrayKeys = (int32ArrayKeys ?? []).ToList();
        _int32ArrayKeys = new HashSet<string>(Int32ArrayKeys, StringComparer.Ordinal);
        _hexArrayLine = PyRe.M(@"^(" + PyRe.S + @"*(?:- )?(?:" + string.Join("|", Int32ArrayKeys.Concat(RawHexArrayKeys).Select(Regex.Escape)) + @")): ((?:[0-9a-f]{8})+)[ \t]*$");
        _knownHexKeys = new HashSet<string>(HexKeys.Concat(Int32ArrayKeys).Concat(RawHexArrayKeys).Concat(RawHexAnyKeys), StringComparer.Ordinal);
    }

    /// <summary>沒列在上面、但值看起來是十六進位陣列的欄位（報告用）</summary>
    public HashSet<string> SuspectHexKeys(string text)
    {
        var o = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in SuspectHexLine.Matches(text))
        {
            string key = m.Groups[1].Value, val = m.Groups[2].Value;
            if (!_knownHexKeys.Contains(key) && (val[0] == '0' || HasHexLetter.IsMatch(val)))
                o.Add(key);
        }
        return o;
    }

    /// <summary>把 int32ArrayKeys 的十六進位字串解成 int32 little-endian 陣列</summary>
    public object? DecodeInt32Arrays(object? v, string? key = null)
    {
        if (v is PyDict d)
        {
            var o = new PyDict(d.Count);
            foreach (var (k, x) in d) o[k] = DecodeInt32Arrays(x, k as string);
            return o;
        }
        if (v is List<object?> list) return list.Select(x => DecodeInt32Arrays(x, key)).ToList();
        if (key is not null && _int32ArrayKeys.Contains(key) && v is string s && s.Length > 0 && s.Length % 8 == 0)
        {
            var raw = Convert.FromHexString(s);
            var o = new List<object?>(raw.Length / 4);
            for (int i = 0; i < raw.Length; i += 4) o.Add((long)BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i)));
            return o;
        }
        return v;
    }

    /// <summary>load_yaml_text：guid 與字串欄位加引號後 load_all，去掉空的文件</summary>
    public static List<PyDict> LoadYamlText(string text)
    {
        text = GuidValue.Replace(text, "guid: \"$1\"");
        text = StringKeyNumber.Replace(text, "$1: \"$2\"");
        var docs = new List<PyDict>();
        foreach (var d in PyYaml.LoadAll(text, PyYaml.Resolver.Unity))
        {
            if (!Py.Truthy(d)) continue;
            docs.Add(d as PyDict ?? throw new InvalidOperationException($"文件不是 mapping：{Py.Repr(d)}"));
        }
        return docs;
    }

    public string QuoteHex(string text)
    {
        text = HexKeyLine.Replace(text, "$1: \"$2\"");
        text = _hexArrayLine.Replace(text, "$1: \"$2\"");
        text = HexAnyLine.Replace(text, "$1: \"$2\"");
        return HexLongLine.Replace(text, "$1: \"$2\"");
    }

    /// <summary>load_unity_docs：每個 `--- !u!&lt;classID&gt; &amp;&lt;fileID&gt;` 一個物件；標頭先換成單純的 ---</summary>
    public List<UnityDoc> LoadUnityDocs(string path, string? text = null)
    {
        text ??= PyStr.ReadText(path);
        var headers = DocHeader.Matches(text);
        var docs = LoadYamlText(QuoteHex(DocHeader.Replace(text, "---")));
        if (docs.Count != headers.Count)
            throw new InvalidOperationException($"{PyPath.Name(path)}：{headers.Count} 個物件標頭，解析出 {docs.Count} 個");
        var o = new List<UnityDoc>(docs.Count);
        for (int i = 0; i < docs.Count; i++)
        {
            var h = headers[i];
            var d = docs[i];
            if (d.Count != 1) throw new InvalidOperationException($"too many values to unpack (expected 1, got {d.Count})");
            var (typeName, data) = d.First();
            o.Add(new UnityDoc(
                int.Parse(h.Groups[1].Value, Py.Inv),
                h.Groups[2].Value,
                h.Groups[3].Success && h.Groups[3].Length > 0,
                typeName as string ?? Py.Str(typeName),
                data as PyDict ?? (data is null ? new PyDict() : throw new InvalidOperationException($"物件內容不是 mapping：{Py.Repr(data)}"))));
        }
        return o;
    }

    // ───────── GUID 索引 ─────────

    private static readonly Regex GuidRe = PyRe.M(@"^guid: ([0-9a-f]{32})");

    /// <summary>guid → 資產路徑（去掉 .meta）。照 os.walk 的順序（同一個 GUID 出現兩次時後面的蓋掉前面的）</summary>
    public static Dictionary<string, string> BuildGuidIndex(string assets)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in WalkFiles(assets))
        {
            if (!p.EndsWith(".meta", StringComparison.Ordinal)) continue;
            string head = ReadHead(p, 200);
            var m = GuidRe.Match(head);
            if (m.Success) index[m.Groups[1].Value] = PyPath.WithSuffix(p, "");
        }
        return index;
    }

    /// <summary>open(p, encoding="utf-8", errors="replace").read(n)：前 n 個字元（換行已轉換）</summary>
    public static string ReadHead(string path, int chars)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[chars * 4 + 4];
        int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        string s = PyStr.UniversalNewlines(new UTF8Encoding(false, false).GetString(buf, 0, n));
        // 只讀了一部分時，最後一個字可能被切斷，也可能是 \r（下一個位元組才是 \n）：多讀一些再截
        return s.Length > chars ? s[..chars] : s;
    }

    /// <summary>os.walk(top) 的檔案順序：每個資料夾先列自己的檔，再依列目錄的順序往下走</summary>
    public static IEnumerable<string> WalkFiles(string top)
    {
        var stack = new Stack<string>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            var subdirs = new List<string>();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                string p = e.Replace('\\', '/');
                var info = new FileInfo(p);
                bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                if (isDir && (info.Attributes & FileAttributes.ReparsePoint) == 0) subdirs.Add(p);
                else if (!isDir) yield return p;
            }
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
    }

    // ───────── PPtr ─────────

    public static bool IsPPtr(PyDict v)
    {
        if (v.Count == 0 || !v.ContainsKey("fileID")) return false;
        foreach (var k in v.Keys)
            if (k is not ("fileID" or "guid" or "type")) return false;
        return true;
    }

    public const long MaxSafeInt = (1L << 53) - 1;

    /// <summary>norm_refs：PPtr 換成 {guid, fileID}／{fileID}／null；超過 2^53 的整數轉字串</summary>
    public static object? NormRefs(object? v)
    {
        switch (v)
        {
            case PyDict d:
                if (IsPPtr(d))
                {
                    if (!Py.Truthy(d.Get("fileID")) && !Py.Truthy(d.Get("guid"))) return null;
                    if (d.ContainsKey("guid"))
                        return new PyDict { ["guid"] = Py.Str(d["guid"]), ["fileID"] = Py.Str(d["fileID"]) };
                    return new PyDict { ["fileID"] = Py.Str(d["fileID"]) };
                }
                var o = new PyDict(d.Count);
                foreach (var (k, x) in d) o[k] = NormRefs(x);
                return o;
            case List<object?> list:
                var l = new List<object?>(list.Count);
                foreach (var x in list) l.Add(NormRefs(x));
                return l;
            case long n when Math.Abs((decimal)n) > MaxSafeInt:
                return Py.Str(n);
            case BigInteger bi when BigInteger.Abs(bi) > MaxSafeInt:
                return Py.Str(bi);
            default:
                return v;
        }
    }

    private static readonly string[] MetaDrop = ["m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset"];

    public static PyDict StripMetaFields(PyDict d)
    {
        var o = new PyDict(d.Count);
        foreach (var (k, v) in d)
            if (!(k is string s && MetaDrop.Contains(s))) o[k] = v;
        return o;
    }

    // ───────── MonoScript 的 fileID ─────────

    /// <summary>MD4("s\0\0\0" + namespace + 類別名) 前 4 bytes，int32 little-endian</summary>
    public static int ScriptFileID(string ns, string cls)
    {
        var digest = Md4(Encoding.UTF8.GetBytes("s\0\0\0" + ns + cls));
        return BinaryPrimitives.ReadInt32LittleEndian(digest);
    }

    public static byte[] Md4(byte[] message)
    {
        long bitLen = (long)message.Length * 8;
        int padLen = (55 - message.Length) % 64;
        if (padLen < 0) padLen += 64;
        var msg = new byte[message.Length + 1 + padLen + 8];
        message.CopyTo(msg, 0);
        msg[message.Length] = 0x80;
        BinaryPrimitives.WriteInt64LittleEndian(msg.AsSpan(msg.Length - 8), bitLen);
        uint[] h = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];
        var x = new uint[16];
        int[] o2 = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
        int[] o3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
        for (int off = 0; off < msg.Length; off += 64)
        {
            for (int i = 0; i < 16; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(off + i * 4));
            uint a = h[0], b = h[1], c = h[2], d = h[3];
            void Round(Func<uint, uint, uint, uint> fn, uint k, int[] order, int[] shifts)
            {
                for (int i = 0; i < 16; i++)
                {
                    int s = shifts[i % 4];
                    uint xi = x[order[i]];
                    switch (i % 4)
                    {
                        case 0: a = BitOperations.RotateLeft(a + fn(b, c, d) + xi + k, s); break;
                        case 1: d = BitOperations.RotateLeft(d + fn(a, b, c) + xi + k, s); break;
                        case 2: c = BitOperations.RotateLeft(c + fn(d, a, b) + xi + k, s); break;
                        default: b = BitOperations.RotateLeft(b + fn(c, d, a) + xi + k, s); break;
                    }
                }
            }
            Round((p, q, r) => (p & q) | (~p & r), 0, Enumerable.Range(0, 16).ToArray(), [3, 7, 11, 19]);
            Round((p, q, r) => (p & q) | (p & r) | (q & r), 0x5A827999, o2, [3, 5, 9, 13]);
            Round((p, q, r) => p ^ q ^ r, 0x6ED9EBA1, o3, [3, 9, 11, 15]);
            h[0] += a; h[1] += b; h[2] += c; h[3] += d;
        }
        var outp = new byte[16];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(i * 4), h[i]);
        return outp;
    }

    // ───────── 頂點資料 ─────────

    private static readonly (char code, int size, double? norm)[] VertexFormats =
    [
        ('f', 4, null), ('e', 2, null), ('B', 1, 255.0), ('b', 1, 127.0), ('H', 2, 65535.0), ('h', 2, 32767.0),
        ('B', 1, null), ('b', 1, null), ('H', 2, null), ('h', 2, null), ('I', 4, null), ('i', 4, null),
    ];

    private static readonly string[] ChannelNames =
        ["position", "normal", "tangent", "color", "uv0", "uv1", "uv2", "uv3", "uv4", "uv5", "uv6", "uv7", "blendWeight", "blendIndices"];

    /// <summary>m_VertexData（serializedVersion 3）→ {channel: {dimension, values（攤平）[, rawDimension]}}</summary>
    public static PyDict DecodeVertexData(PyDict vd)
    {
        long count = Py.ToLong(Py.Truthy(vd.Get("m_VertexCount")) ? vd.Get("m_VertexCount") : 0L);
        object? rawObj = Py.Truthy(vd.Get("_typelessdata")) ? vd.Get("_typelessdata") : "";
        byte[] data = rawObj is string hex ? PyStr.FromHex(hex) : [];
        var channels = (Py.Truthy(vd.Get("m_Channels")) ? vd.Get("m_Channels") : new List<object?>()) as List<object?> ?? [];

        var strides = new SortedDictionary<long, long>();
        foreach (PyDict ch in channels.Cast<PyDict>())
        {
            long dim = Py.ToLong(ch["dimension"]) & 0xF;
            if (dim == 0) continue;
            long size = VertexFormats[Py.ToLong(ch["format"])].size * dim;
            long s = Py.ToLong(ch["stream"]);
            strides[s] = Math.Max(strides.GetValueOrDefault(s), Py.ToLong(ch["offset"]) + size);
        }
        var streamOffsets = new Dictionary<long, long>();
        long pos = 0;
        foreach (var (s, stride) in strides)
        {
            pos = (pos + 15) & ~15L;
            streamOffsets[s] = pos;
            pos += stride * count;
        }
        long expected = Py.ToLong(Py.Truthy(vd.Get("m_DataSize")) ? vd.Get("m_DataSize") : 0L);
        if (pos != expected || data.Length != expected)
            throw new InvalidOperationException($"頂點資料大小不符：算出 {pos}，m_DataSize {expected}，實際 {data.Length}");

        var outp = new PyDict();
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = (PyDict)channels[i]!;
            long rawDim = Py.ToLong(ch["dimension"]);
            int dim = (int)(rawDim & 0xF);
            if (dim == 0) continue;
            var (code, size, norm) = VertexFormats[Py.ToLong(ch["format"])];
            long s = Py.ToLong(ch["stream"]);
            long baseOff = streamOffsets[s] + Py.ToLong(ch["offset"]);
            long stride = strides[s];
            var values = new List<object?>((int)(count * dim));
            for (long v = 0; v < count; v++)
            {
                int p = (int)(baseOff + v * stride);
                for (int c = 0; c < dim; c++)
                {
                    var span = data.AsSpan(p + c * size);
                    if (p + (c + 1) * size > data.Length) throw new InvalidOperationException("unpack_from requires a buffer of more bytes");
                    object comp = code switch
                    {
                        'f' => (object)(double)BinaryPrimitives.ReadSingleLittleEndian(span),
                        'e' => (object)(double)BinaryPrimitives.ReadHalfLittleEndian(span),
                        'B' => (object)(long)span[0],
                        'b' => (long)(sbyte)span[0],
                        'H' => (long)BinaryPrimitives.ReadUInt16LittleEndian(span),
                        'h' => (long)BinaryPrimitives.ReadInt16LittleEndian(span),
                        'I' => (long)BinaryPrimitives.ReadUInt32LittleEndian(span),
                        _ => (long)BinaryPrimitives.ReadInt32LittleEndian(span),
                    };
                    if (norm is double nm) values.Add(PyFloat.F32((long)comp / nm));
                    else if (code is 'f' or 'e') values.Add(PyFloat.F32((double)comp));
                    else values.Add(comp);
                }
            }
            var entry = new PyDict { ["dimension"] = (long)dim, ["values"] = values };
            if (rawDim != dim) entry["rawDimension"] = rawDim;
            outp[i < ChannelNames.Length ? ChannelNames[i] : $"channel{i}"] = entry;
        }
        return outp;
    }

    /// <summary>m_IndexBuffer：0 是 UInt16、1 是 UInt32，little-endian</summary>
    public static List<object?> DecodeIndexBuffer(string? hex, long indexFormat = 0)
    {
        if (string.IsNullOrEmpty(hex)) return [];
        var data = PyStr.FromHex(hex);
        int size = indexFormat == 0 ? 2 : 4;
        var o = new List<object?>(data.Length / size);
        for (int i = 0; i + size <= data.Length; i += size)
            o.Add(size == 2 ? (long)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i)) : (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)));
        return o;
    }
}
