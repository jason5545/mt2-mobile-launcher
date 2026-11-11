// 移植自 UnityPy 1.25.4（https://github.com/K0lb3/UnityPy）的 TypeTreeHelper 與 UnityPyBoost，MIT，Copyright (c) 2019-2026 K0lb3；授權全文見 THIRD_PARTY_NOTICES.md。
using System.Buffers.Binary;
using System.Text;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer.Serialized;

/// <summary>type tree 的一個節點（UnityPy 的 TypeTreeNode）</summary>
public sealed class TtNode(string type, string name, int level, int metaFlag)
{
    public string Type { get; } = type;
    public string Name { get; } = name;
    public int Level { get; } = level;
    public int MetaFlag { get; } = metaFlag;
    public List<TtNode> Children { get; } = [];

    public const int AlignBytes = 0x4000;
    public bool Aligned => (MetaFlag & AlignBytes) != 0;

    /// <summary>TypeTreeNode.from_list：照 level 排好的扁平清單 → 樹（第一個是根）</summary>
    public static TtNode FromList(IReadOnlyList<(string type, string name, int level, int metaFlag)> nodes)
    {
        var fakeRoot = new TtNode("", "", -1, 0);
        var stack = new List<TtNode> { fakeRoot };
        TtNode parent = fakeRoot, prev = fakeRoot;
        foreach (var (type, name, level, flag) in nodes)
        {
            var node = new TtNode(type, name, level, flag);
            if (node.Level > prev.Level)
            {
                stack.Add(parent);
                parent = prev;
            }
            else if (node.Level < prev.Level)
            {
                while (node.Level <= parent.Level)
                {
                    parent = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            parent.Children.Add(node);
            prev = node;
        }
        return fakeRoot.Children[0];
    }

    /// <summary>深度優先的扁平清單（比對用）</summary>
    public IEnumerable<TtNode> Flatten()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var n in c.Flatten())
                yield return n;
    }

    /// <summary>get_name_peek_node：只到 m_Name（或 name）為止的部分節點；沒有就回 null</summary>
    public (TtNode node, string key)? NamePeekNode()
    {
        for (int i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            if (c.Name is "m_Name" or "name")
            {
                var peek = new TtNode(Type, Name, Level, 0);
                peek.Children.AddRange(Children.Take(i + 1));
                return (peek, c.Name);
            }
        }
        return null;
    }

    public override string ToString() => $"{Type} {Name} L{Level} F{MetaFlag:x}";
}

/// <summary>
/// UnityPy 1.25.4 的 TypeTreeHelper.read_typetree（as_dict=True）的 C# 版，值的形狀照 Python：
/// class → PyDict；pair → PyTuple；TypelessData → byte[]；字串用 surrogateescape 解碼。
/// UnityPy 有兩套實作，importer 兩套都用到：assets、scenes 階段走 C 加速版（UnityPyBoost），
/// supplement 把加速版關掉改走 Python 版。兩套的差別照抄（<paramref name="boost"/>）：
/// 加速版的基本型別陣列是 list、unsigned short 陣列無號、只要元素是數值型別就整批讀；
/// Python 版只有元素節點有對齊旗標時才整批讀，結果是 struct.unpack 的 tuple，unsigned short 陣列用 "h"（有號）讀。
/// </summary>
public sealed class TypeTreeReader
{
    /// <summary>SerializeReference 的型別 → 節點（ReferencedObject 的 type 欄位 {class, ns, asm}）；null 是空參照</summary>
    public delegate TtNode? RefTypeResolver(PyDict refObject);

    private readonly byte[] _data;
    private readonly int _end;
    private int _pos;
    private readonly int _start;
    private readonly RefTypeResolver? _refTypes;
    private readonly bool _boost;

    private TypeTreeReader(byte[] data, int offset, int length, RefTypeResolver? refTypes, bool boost)
    {
        _data = data;
        _start = _pos = offset;
        _end = offset + length;
        _refTypes = refTypes;
        _boost = boost;
    }

    /// <summary>read_typetree(node, reader, byte_size=物件大小, check_read)</summary>
    public static object? Read(TtNode root, byte[] data, bool boost, bool checkRead = true, RefTypeResolver? refTypes = null)
        => Read(root, data, 0, data.Length, boost, checkRead, refTypes);

    public static object? Read(TtNode root, byte[] data, int offset, int length, bool boost, bool checkRead, RefTypeResolver? refTypes)
    {
        var r = new TypeTreeReader(data, offset, length, refTypes, boost);
        var v = r.ReadValue(root, false);
        int read = r._pos - r._start;
        if (checkRead && read != length)
            throw new InvalidOperationException($"Expected to read {length} bytes, but only read {read} bytes");
        return v;
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _end) throw new EndOfStreamException($"讀過頭：位置 {_pos - _start}，要 {n} bytes，剩 {_end - _pos}");
        var s = _data.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    private int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    private void Align() => _pos += (4 - (_pos - _start) % 4) % 4;

    private static bool Primitive(string type) => type switch
    {
        "SInt8" or "UInt8" or "char" or "short" or "SInt16" or "unsigned short" or "UInt16" or "int" or "SInt32"
            or "unsigned int" or "UInt32" or "Type*" or "long long" or "SInt64" or "unsigned long long" or "UInt64"
            or "FileSize" or "float" or "double" or "bool" or "string" or "TypelessData" => true,
        _ => false,
    };

    /// <summary>FUNCTION_READ_MAP</summary>
    private object? ReadPrimitive(string type) => type switch
    {
        "SInt8" => (long)(sbyte)Take(1)[0],
        "UInt8" or "char" => (long)Take(1)[0],
        "short" or "SInt16" => (long)BinaryPrimitives.ReadInt16LittleEndian(Take(2)),
        "unsigned short" or "UInt16" => (long)BinaryPrimitives.ReadUInt16LittleEndian(Take(2)),
        "int" or "SInt32" => (long)BinaryPrimitives.ReadInt32LittleEndian(Take(4)),
        "unsigned int" or "UInt32" or "Type*" => (long)BinaryPrimitives.ReadUInt32LittleEndian(Take(4)),
        "long long" or "SInt64" => BinaryPrimitives.ReadInt64LittleEndian(Take(8)),
        "unsigned long long" or "UInt64" or "FileSize" => Py.Int(BinaryPrimitives.ReadUInt64LittleEndian(Take(8))),
        "float" => (double)BinaryPrimitives.ReadSingleLittleEndian(Take(4)),
        "double" => BinaryPrimitives.ReadDoubleLittleEndian(Take(8)),
        "bool" => Take(1)[0] != 0,
        "string" => ReadAlignedString(),
        "TypelessData" => Take(ReadInt()).ToArray(),
        _ => throw new InvalidOperationException(type),
    };

    private string ReadAlignedString()
    {
        int length = ReadInt();
        if (0 < length && length <= _end - _pos)
        {
            string s = SurrogateEscape(Take(length));
            Align();
            return s;
        }
        return "";
    }

    /// <summary>bytes.decode("utf8", "surrogateescape")</summary>
    public static string SurrogateEscape(ReadOnlySpan<byte> raw)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            var sb = new StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                int n = Utf8SequenceLength(raw[i..]);
                if (n > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(raw.Slice(i, n)));
                    i += n;
                }
                else
                {
                    sb.Append((char)(0xDC00 + raw[i]));
                    i++;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>開頭是一個合法 UTF-8 序列時回傳它的長度，否則 0</summary>
    private static int Utf8SequenceLength(ReadOnlySpan<byte> s)
    {
        byte b = s[0];
        int n; int min;
        if (b < 0x80) return 1;
        if (b >= 0xC2 && b <= 0xDF) { n = 2; min = 0x80; }
        else if (b >= 0xE0 && b <= 0xEF) { n = 3; min = 0x800; }
        else if (b >= 0xF0 && b <= 0xF4) { n = 4; min = 0x10000; }
        else return 0;
        if (s.Length < n) return 0;
        int cp = b & (0xFF >> (n + 1));
        for (int i = 1; i < n; i++)
        {
            if ((s[i] & 0xC0) != 0x80) return 0;
            cp = (cp << 6) | (s[i] & 0x3F);
        }
        if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return 0;
        return n;
    }

    private object? ReadValue(TtNode node, bool hasRegistry)
    {
        bool align = node.Aligned;
        object? value;
        if (Primitive(node.Type))
        {
            value = ReadPrimitive(node.Type);
        }
        else if (node.Type == "pair")
        {
            var first = ReadValue(node.Children[0], hasRegistry);
            var second = ReadValue(node.Children[1], hasRegistry);
            value = new PyTuple(first, second);
        }
        else if (node.Type == "ReferencedObject")
        {
            var d = new PyDict();
            foreach (var child in node.Children)
            {
                if (child.Type == "ReferencedObjectData")
                {
                    var refNode = ResolveRef(d);
                    if (refNode is null) continue;
                    d[child.Name] = ReadValue(refNode, hasRegistry);
                }
                else d[child.Name] = ReadValue(child, hasRegistry);
            }
            value = d;
        }
        else if (node.Children.Count > 0 && node.Children[0].Type == "Array")
        {
            if (node.Children[0].Aligned) align = true;
            int size = ReadInt();
            if (size < 0) throw new InvalidOperationException("Negative length read from TypeTree");
            var subtype = node.Children[0].Children[1];
            if (_boost ? BoostArrayType(subtype.Type) : subtype.Aligned) value = ReadValueArray(subtype, size, hasRegistry);
            else
            {
                var list = new List<object?>(Math.Min(size, 1 << 16));
                for (int i = 0; i < size; i++) list.Add(ReadValue(subtype, hasRegistry));
                value = list;
            }
        }
        else
        {
            var d = new PyDict(node.Children.Count);
            bool reg = hasRegistry;
            foreach (var child in node.Children)
            {
                if (child.Type == "ManagedReferencesRegistry")
                {
                    if (reg) continue;
                    reg = true;
                }
                d[child.Name] = ReadValue(child, reg);
            }
            value = d;
        }
        if (align) Align();
        return value;
    }

    /// <summary>UnityPyBoost 的 SUPPORTED_VALUE_ARRAY_READ_TYPES</summary>
    private static bool BoostArrayType(string type) => type switch
    {
        "SInt8" or "UInt8" or "char" or "short" or "SInt16" or "unsigned short" or "UInt16" or "int" or "SInt32"
            or "unsigned int" or "UInt32" or "Type*" or "long long" or "SInt64" or "unsigned long long" or "UInt64"
            or "FileSize" or "float" or "double" or "bool" or "pair" => true,
        _ => false,
    };

    private TtNode? ResolveRef(PyDict item)
    {
        if (_refTypes is null) throw new InvalidOperationException("SerializedFile has no ref_types");
        return _refTypes(item);
    }

    private object? ReadValueArray(TtNode node, int size, bool hasRegistry)
    {
        bool align = node.Aligned;
        object? value;
        switch (node.Type)
        {
            // FUNCTION_READ_MAP_ARRAY：struct.unpack → tuple
            case "SInt8": value = Tuple(size, 1, s => (long)(sbyte)s[0]); break;
            case "UInt8" or "char": value = Tuple(size, 1, s => (long)s[0]); break;
            case "short" or "SInt16": value = Tuple(size, 2, s => (long)BinaryPrimitives.ReadInt16LittleEndian(s)); break;
            // Python 版的 read_u_short_array 用的是 "h"（有號），照抄；加速版是無號
            case "unsigned short" or "UInt16":
                value = _boost ? Tuple(size, 2, s => (long)BinaryPrimitives.ReadUInt16LittleEndian(s)) : Tuple(size, 2, s => (long)BinaryPrimitives.ReadInt16LittleEndian(s));
                break;
            case "int" or "SInt32": value = Tuple(size, 4, s => (long)BinaryPrimitives.ReadInt32LittleEndian(s)); break;
            case "unsigned int" or "UInt32" or "Type*": value = Tuple(size, 4, s => (long)BinaryPrimitives.ReadUInt32LittleEndian(s)); break;
            case "long long" or "SInt64": value = Tuple(size, 8, s => BinaryPrimitives.ReadInt64LittleEndian(s)); break;
            case "unsigned long long" or "UInt64" or "FileSize": value = Tuple(size, 8, s => Py.Int(BinaryPrimitives.ReadUInt64LittleEndian(s))); break;
            case "float": value = Tuple(size, 4, s => (double)BinaryPrimitives.ReadSingleLittleEndian(s)); break;
            case "double": value = Tuple(size, 8, s => BinaryPrimitives.ReadDoubleLittleEndian(s)); break;
            case "bool": value = Tuple(size, 1, s => s[0] != 0); break;
            case "string":
            {
                var list = new List<object?>(size);
                for (int i = 0; i < size; i++) list.Add(ReadAlignedString());
                value = list;
                break;
            }
            case "TypelessData":
            {
                var list = new List<object?>(size);
                for (int i = 0; i < size; i++) list.Add(Take(ReadInt()).ToArray());
                value = list;
                break;
            }
            case "pair":
            {
                var keyNode = node.Children[0];
                var valueNode = node.Children[1];
                var list = new List<object?>(size);
                for (int i = 0; i < size; i++)
                {
                    // Python 版的 key_func／value_func：基本型別直接讀（不管對齊），其他走 read_value；加速版一律 read_value
                    var k = !_boost && Primitive(keyNode.Type) ? ReadPrimitive(keyNode.Type) : ReadValue(keyNode, hasRegistry);
                    var v = !_boost && Primitive(valueNode.Type) ? ReadPrimitive(valueNode.Type) : ReadValue(valueNode, hasRegistry);
                    list.Add(new PyTuple(k, v));
                }
                value = list;
                break;
            }
            case "ReferencedObject":
            {
                var list = new List<object?>(size);
                for (int i = 0; i < size; i++)
                {
                    var item = new PyDict();
                    foreach (var child in node.Children)
                    {
                        if (child.Type == "ReferencedObjectData")
                        {
                            var refNode = ResolveRef(item) ?? throw new NullReferenceException("'NoneType' object has no attribute 'm_MetaFlag'");
                            item[child.Name] = ReadValue(refNode, hasRegistry);
                        }
                        else item[child.Name] = ReadValue(child, hasRegistry);
                    }
                    list.Add(item);
                }
                value = list;
                break;
            }
            default:
                if (node.Children.Count > 0 && node.Children[0].Type == "Array")
                {
                    if (node.Children[0].Aligned) align = true;
                    var subtype = node.Children[0].Children[1];
                    var list = new List<object?>(size);
                    for (int i = 0; i < size; i++)
                    {
                        if (subtype.Aligned) list.Add(ReadValueArray(subtype, ReadInt(), hasRegistry));
                        else
                        {
                            int n = ReadInt();
                            var inner = new List<object?>(n);
                            for (int j = 0; j < n; j++) inner.Add(ReadValue(subtype, hasRegistry));
                            list.Add(inner);
                        }
                    }
                    value = list;
                }
                else
                {
                    // as_dict：每個元素是 {child.m_Name: read_value(child)}（這裡不處理 ManagedReferencesRegistry，同 UnityPy）
                    var list = new List<object?>(size);
                    for (int i = 0; i < size; i++)
                    {
                        var d = new PyDict(node.Children.Count);
                        foreach (var child in node.Children) d[child.Name] = ReadValue(child, hasRegistry);
                        list.Add(d);
                    }
                    value = list;
                }
                break;
        }
        if (align) Align();
        return value;
    }

    /// <summary>基本型別陣列：Python 版是 tuple，加速版是 list</summary>
    private object Tuple(int size, int width, Func<ReadOnlySpan<byte>, object> read)
    {
        var span = Take(checked(size * width));
        var items = new object?[size];
        for (int i = 0; i < size; i++) items[i] = read(span.Slice(i * width, width));
        return _boost ? new List<object?>(items) : new PyTuple(items);
    }
}
