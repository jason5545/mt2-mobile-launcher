using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using K4os.Compression.LZ4;
using Mt2Mobile.Importer;
using Mt2Mobile.Importer.Serialized;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Shaders;

/// <summary>
/// shaders/convert.py：AssetRipper --shader-yaml 匯出的 DX11 bytecode → GLSL ES 3.00。
/// 每個 shader 一個資料夾（index.json、.dxbc、.asm、.spv、.glsl），內容與命令列版逐位元組相同（glslangValidator 的檢查除外，見 Convert）。
/// </summary>
public sealed class ShaderConvert(Action<string> log, int parallelism)
{
    private static readonly string[] CustomPrefixes = ["Shader Graphs_", "MoreMountains_"];

    private static readonly Dictionary<long, (string stage, string profile)> GpuProgramType = new()
    {
        [15] = ("vertex", "vs_4_0"), [16] = ("vertex", "vs_5_0"), [17] = ("fragment", "ps_4_0"), [18] = ("fragment", "ps_5_0"),
        [19] = ("geometry", "gs_4_0"), [20] = ("geometry", "gs_5_0"), [21] = ("hull", "hs_5_0"), [22] = ("domain", "ds_5_0"),
    };

    private static readonly (string field, string stage, string shortName)[] StageField =
    [
        ("progVertex", "vertex", "vert"), ("progFragment", "fragment", "frag"), ("progGeometry", "geometry", "geom"),
        ("progHull", "hull", "hull"), ("progDomain", "domain", "dom"), ("progRayTracing", "raytracing", "ray"),
    ];

    // ───────── Unity YAML（完整 YAML 1.1，同 CSafeLoader） ─────────

    private static readonly string[] HexScalarKeys = ["platforms", "compressedBlob", "m_SerializedKeywordStateMask", "m_KeywordFlags", "stageCounts", "Hash"];
    private static readonly string[] HexListKeys = ["offsets", "compressedLengths", "decompressedLengths", "m_ParameterBlobIndices"];
    private static readonly Regex HexScalarRe = PyRe.M(@"^(" + PyRe.S + @"*(?:" + string.Join("|", HexScalarKeys) + @"): )([0-9a-f]+)[ \t]*$");
    private static readonly Regex HexListRe = PyRe.M(@"^(" + PyRe.S + @"*(?:" + string.Join("|", HexListKeys) + @"):\n)((?:" + PyRe.S + @"+- [0-9a-f]+\n?)+)");
    private static readonly Regex HexItemRe = PyRe.M(@"^(" + PyRe.S + @"*- )([0-9a-f]+)[ \t]*$");
    private static readonly Regex PbiItem = PyRe.R(@"^(" + PyRe.S + @"*)- ([0-9a-f]+)" + PyRe.S + @"*$");

    public static PyDict LoadUnityYaml(string path)
    {
        string text = PyStr.UniversalNewlines(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));
        text = HexScalarRe.Replace(text, "$1\"$2\"");
        text = HexListRe.Replace(text, m => m.Groups[1].Value + HexItemRe.Replace(m.Groups[2].Value, "$1\"$2\""));
        // m_ParameterBlobIndices 的項目混有空行（`- `）與 hex；逐行狀態處理
        var lines = PyStr.SplitLines(text, keepEnds: true);
        bool inPbi = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string ln = lines[i];
            string stripped = PyStr.Strip(ln);
            if (stripped.StartsWith("m_ParameterBlobIndices:", StringComparison.Ordinal))
            {
                inPbi = true;
                continue;
            }
            if (inPbi)
            {
                var m = PbiItem.Match(ln);
                if (m.Success) lines[i] = $"{m.Groups[1].Value}- \"{m.Groups[2].Value}\"\n";
                else if (stripped.StartsWith("- ", StringComparison.Ordinal) || stripped == "-") { }
                else inPbi = false;
            }
        }
        text = string.Concat(lines);
        foreach (var doc in PyYaml.LoadAll(text, PyYaml.Resolver.Yaml11))
            if (Py.Truthy(doc)) return (PyDict)doc!;
        throw new InvalidOperationException($"{path} 是空的");
    }

    /// <summary>Unity 把 uint32 陣列寫成 little-endian hex；一個元素可能含多個 uint32（讀成有號 int32，同 Python 的 "&lt;i"）</summary>
    public static List<long> HexU32s(object? value)
    {
        if (value is null) return [];
        var items = value is string s ? new List<object?> { s } : Assets.AsList(value);
        var o = new List<long>();
        foreach (var item in items)
        {
            if (item is not string hex) throw new InvalidCastException($"fromhex() argument must be str, not {item?.GetType().Name}");
            var raw = PyStr.FromHex(hex);
            for (int i = 0; i + 4 <= raw.Length; i += 4) o.Add(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i)));
            if (raw.Length % 4 != 0) throw new InvalidOperationException("unpack_from requires a buffer of at least 4 bytes");
        }
        return o;
    }

    // ───────── blob ─────────

    public static List<byte[]> DecompressChunks(PyDict shader)
    {
        var comp = PyStr.FromHex((string)shader["compressedBlob"]!);
        var offs = HexU32s(shader["offsets"]);
        var clens = HexU32s(shader["compressedLengths"]);
        var dlens = HexU32s(shader["decompressedLengths"]);
        var chunks = new List<byte[]>();
        int n = Math.Min(offs.Count, Math.Min(clens.Count, dlens.Count));
        for (int i = 0; i < n; i++)
        {
            int o = (int)offs[i], c = (int)clens[i], d = (int)dlens[i];
            var src = comp.AsSpan(Math.Min(o, comp.Length), Math.Max(0, Math.Min(c, comp.Length - Math.Min(o, comp.Length))));
            var dst = new byte[d];
            int got = LZ4Codec.Decode(src, dst);
            if (got != d) throw new InvalidOperationException($"Decompressor wrote {got} bytes, but {d} bytes expected from header");
            chunks.Add(dst);
        }
        return chunks;
    }

    public static List<(byte[] chunk, int off, int len)> EntryTable(List<byte[]> chunks)
    {
        var t = chunks[0];
        int n = BinaryPrimitives.ReadInt32LittleEndian(t);
        var o = new List<(byte[], int, int)>();
        for (int i = 0; i < n; i++)
        {
            int off = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(4 + i * 12));
            int ln = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(8 + i * 12));
            int ci = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(12 + i * 12));
            o.Add((chunks[ci], off, ln));
        }
        return o;
    }

    public sealed class SubProgram
    {
        public readonly List<string> Keywords = [];
        public readonly byte[] Dxbc;

        public SubProgram(byte[] chunk, int off, int elen)
        {
            int end = off + elen;
            if (end > chunk.Length) throw new InvalidOperationException("entry 超出 chunk");
            int version = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(off));
            if (version != 202012090) throw new InvalidOperationException($"未知 blob 版本 {version}");
            int pos = off + 24;
            int nkw = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(pos));
            pos += 4;
            for (int i = 0; i < nkw; i++)
            {
                int sl = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(pos));
                pos += 4;
                Keywords.Add(new UTF8Encoding(false, true).GetString(chunk, pos, Math.Min(sl, chunk.Length - pos)));
                pos = (pos + sl + 3) & ~3;
            }
            int clen = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(pos));
            pos += 4;
            var code = chunk.AsSpan(pos, Math.Max(0, Math.Min(clen, chunk.Length - pos)));
            if (code.Length != clen) throw new InvalidOperationException("code 長度不足");
            int dx = code.IndexOf("DXBC"u8);
            if (dx < 0) throw new InvalidOperationException("code 裡沒有 DXBC");
            uint dlen = BinaryPrimitives.ReadUInt32LittleEndian(code[(dx + 24)..]);
            if (dx + dlen > code.Length) throw new InvalidOperationException("DXBC 長度超出 code");
            Dxbc = code.Slice(dx, (int)dlen).ToArray();
        }
    }

    // ───────── DXBC 簽章 ─────────

    private static Dictionary<string, List<PyDict>> ParseDxbcSignatures(byte[] dxbc)
    {
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(28));
        var o = new Dictionary<string, List<PyDict>>();
        for (int k = 0; k < n; k++)
        {
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(32 + k * 4));
            string cc = Encoding.ASCII.GetString(dxbc, off, 4);
            if (cc is not ("ISGN" or "OSGN")) continue;
            var data = dxbc.AsSpan(off + 8);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var ents = new List<PyDict>();
            for (int i = 0; i < count; i++)
            {
                var e = data[(8 + i * 24)..];
                int nameOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(e);
                uint semIdx = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                uint sysval = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                uint reg = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
                uint maskrw = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
                int z = data[nameOff..].IndexOf((byte)0);
                if (z < 0) throw new InvalidOperationException("subsection not found");
                ents.Add(new PyDict
                {
                    ["semantic"] = new UTF8Encoding(false, true).GetString(data.Slice(nameOff, z)),
                    ["index"] = (long)semIdx, ["sysval"] = (long)sysval, ["register"] = (long)reg, ["mask"] = (long)(maskrw & 0xFF),
                });
            }
            o[cc] = ents;
        }
        return o;
    }

    /// <summary>VS 輸出（OSGN）與 FS 輸入（ISGN）用 D3D 語意名對接：v_&lt;語意&gt;&lt;index&gt;</summary>
    public static (Dictionary<(long, long, long), string> renames, List<object?> iface) IoRenameMap(byte[] dxbc, string stage)
    {
        var sigs = ParseDxbcSignatures(dxbc);
        var entries = sigs.GetValueOrDefault(stage == "vertex" ? "OSGN" : "ISGN") ?? [];
        long storage = stage == "vertex" ? 3 : 1;
        var renames = new Dictionary<(long, long, long), string>();
        var iface = new List<object?>();
        foreach (var e in entries)
        {
            if (Py.ToLong(e["sysval"]) != 0 || Py.ToLong(e["mask"]) == 0) continue;
            long comp = 0, m = Py.ToLong(e["mask"]);
            while ((m & 1) == 0)
            {
                m >>= 1;
                comp++;
            }
            string name = $"v_{e["semantic"]}{Py.Str(e["index"])}";
            renames[(storage, Py.ToLong(e["register"]), comp)] = name;
            iface.Add(new PyDict
            {
                ["name"] = name, ["semantic"] = e["semantic"], ["index"] = e["index"], ["register"] = e["register"],
                ["component"] = comp, ["mask"] = e["mask"],
            });
        }
        return (renames, iface);
    }

    // ───────── 參數 blob ─────────

    private sealed class BlobReader(byte[] buf)
    {
        public int P;

        public long U32()
        {
            if (P + 4 > buf.Length) throw new InvalidOperationException($"讀過頭 @{P}");
            long v = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(P));
            P += 4;
            return v;
        }

        private static bool Ident(ReadOnlySpan<byte> raw)
        {
            foreach (byte c in raw)
                if (!(c == 0x24 || c == 0x5F || (c >= 0x2D && c <= 0x7A))) return false;
            return true;
        }

        public string Str()
        {
            int start = P;
            long l = U32();
            if (!(1 <= l && l <= 128) || P + l > buf.Length) throw new InvalidOperationException($"@{start}: 字串長度 {l} 不合理");
            var raw = buf.AsSpan(P, (int)l);
            if (!Ident(raw)) throw new InvalidOperationException($"@{start}: 不是識別字");
            P = (int)((P + l + 3) & ~3);
            return Encoding.ASCII.GetString(raw);
        }

        public bool PeekString()
        {
            if (P + 4 > buf.Length) return false;
            int l = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(P));
            if (!(1 <= l && l <= 128) || P + 4 + l > buf.Length) return false;
            return Ident(buf.AsSpan(P + 4, l));
        }

        public bool Done => P >= buf.Length;
    }

    private static readonly Dictionary<long, long> SrvDimToUnity = new() { [4] = 2, [5] = 5, [6] = 2, [7] = 5, [8] = 3, [9] = 4, [10] = 6 };

    public static PyDict ParseParamBlob(byte[] blob)
    {
        var r = new BlobReader(blob);
        if (r.U32() != 202012090) throw new InvalidOperationException("未知版本");
        long cbCount = r.U32() - 1;
        if (cbCount < 0) throw new InvalidOperationException("CB 數為負");
        for (int i = 0; i < 4; i++)
            if (r.U32() != 0) throw new InvalidOperationException("header 非零");

        PyDict ReadMember()
        {
            string name = r.Str();
            var f = new List<long> { r.U32(), r.U32(), r.U32(), r.U32(), r.U32() };
            long offset = r.U32();
            return new PyDict { ["name"] = name, ["f"] = f.Cast<object?>().ToList(), ["offset"] = offset };
        }

        var cbs = new List<object?>();
        for (long c = 0; c < cbCount; c++)
        {
            string name = r.Str();
            long size = r.U32();
            long memberCount = r.U32();
            if (!(0 <= memberCount && memberCount <= 512)) throw new InvalidOperationException($"CB {name} 成員數 {memberCount} 不合理");
            var members = new List<object?>();
            for (long i = 0; i < memberCount; i++) members.Add(ReadMember());
            long structCount = r.U32();
            var structs = new List<object?>();
            for (long s = 0; s < structCount; s++)
            {
                string sname = r.Str();
                long soffset = r.U32(), sarray = r.U32(), ssize = r.U32(), scount = r.U32();
                var smembers = new List<object?>();
                for (long i = 0; i < scount; i++) smembers.Add(ReadMember());
                structs.Add(new PyDict { ["name"] = sname, ["offset"] = soffset, ["arraySize"] = sarray, ["structSize"] = ssize, ["members"] = smembers });
            }
            cbs.Add(new PyDict { ["name"] = name, ["size"] = size, ["members"] = members, ["structs"] = structs });
        }

        var textures = new List<object?>();
        var implicitSamplers = new List<object?>();
        var cbBindings = new List<object?>();
        var buffers = new List<object?>();
        if (!r.Done)
        {
            long count = r.U32();
            if (!(0 <= count && count <= 256)) throw new InvalidOperationException($"資源數 {count} 不合理");
            for (long i = 0; i < count; i++)
            {
                if (r.PeekString())
                {
                    string name = r.Str();
                    long tag = r.U32();
                    if (tag == 0)
                    {
                        long reg = r.U32(), samp = r.U32(), dim = r.U32();
                        textures.Add(new PyDict
                        {
                            ["name"] = name, ["register"] = reg, ["samplerRegister"] = samp >= 0 ? samp : null,
                            ["dim"] = SrvDimToUnity.TryGetValue(dim, out var u) ? u : dim, ["srvDim"] = dim, ["multiSampled"] = dim == 6,
                        });
                    }
                    else if (tag == 1)
                    {
                        long slot = r.U32(), arr = r.U32();
                        cbBindings.Add(new PyDict { ["name"] = name, ["slot"] = slot, ["arraySize"] = arr });
                    }
                    else if (tag == 2)
                    {
                        long reg = r.U32(), unk = r.U32();
                        buffers.Add(new PyDict { ["name"] = name, ["register"] = reg, ["raw"] = unk });
                    }
                    else throw new InvalidOperationException($"未知資源 tag {tag}");
                }
                else
                {
                    long zero = r.U32(), dim = r.U32(), reg = r.U32(), state = r.U32();
                    if (zero != 0) throw new InvalidOperationException("無名資源首欄非零");
                    implicitSamplers.Add(new PyDict { ["srvDim"] = dim, ["register"] = reg, ["samplerState"] = state });
                }
            }
        }
        while (!r.Done)
            if (r.U32() != 0) throw new InvalidOperationException("尾部非零");
        return new PyDict { ["cbs"] = cbs, ["textures"] = textures, ["implicitSamplers"] = implicitSamplers, ["cbBindings"] = cbBindings, ["buffers"] = buffers };
    }

    private static PyDict BlobMemberToParam(PyDict m)
    {
        var f = (List<object?>)m["f"]!;
        long a = Py.ToLong(f[0]), b = Py.ToLong(f[1]), c = Py.ToLong(f[2]), e = Py.ToLong(f[4]);
        if (b == 4) return new PyDict { ["name"] = m["name"], ["offset"] = m["offset"], ["rows"] = c, ["arraySize"] = e };
        return new PyDict { ["name"] = m["name"], ["offset"] = m["offset"], ["dim"] = c, ["arraySize"] = e != 0 ? e : a != 0 ? a : 0L };
    }

    /// <summary>變體參數 = stage 的 m_CommonParameters ∪ 參數 blob（blob 只存非共用部分）</summary>
    public static PyDict MergeParameters(PyDict common, PyDict blob)
    {
        var commonCb = new Dictionary<object, PyDict>(new PyEq());
        foreach (PyDict cb in Assets.ListOf(common.Get("constantBuffers")).Cast<PyDict>()) commonCb[cb["name"]!] = cb;
        var cbs = new List<object?>();
        int slot = 0;
        foreach (PyDict bcb in ((List<object?>)blob["cbs"]!).Cast<PyDict>())
        {
            commonCb.TryGetValue(bcb["name"]!, out var ccb);
            var members = ((List<object?>)bcb["members"]!).Cast<PyDict>().ToList();
            var vectors = members.Where(m => Py.ToLong(((List<object?>)m["f"]!)[1]) != 4).Select(m => (object?)BlobMemberToParam(m)).ToList();
            var matrices = members.Where(m => Py.ToLong(((List<object?>)m["f"]!)[1]) == 4).Select(m => (object?)BlobMemberToParam(m)).ToList();
            foreach (PyDict st in ((List<object?>)bcb["structs"]!).Cast<PyDict>())
            {
                foreach (PyDict m in ((List<object?>)st["members"]!).Cast<PyDict>())
                {
                    var p = BlobMemberToParam(m);
                    p["offset"] = Py.ToLong(p["offset"]) + Py.ToLong(st["offset"]);
                    p["struct"] = new PyDict { ["name"] = st["name"], ["arraySize"] = st["arraySize"], ["structSize"] = st["structSize"] };
                    (Py.ToLong(((List<object?>)m["f"]!)[1]) == 4 ? matrices : vectors).Add(p);
                }
            }
            var seen = new HashSet<object>(new PyEq());
            foreach (var (lst, key) in new[] { (vectors, "vectors"), (matrices, "matrices") })
            {
                foreach (PyDict p in (ccb is not null ? Assets.ListOf(ccb.Get(key)) : []).Cast<PyDict>())
                    if (!lst.Any(x => Py.Eq(((PyDict)x!)["name"], p["name"]))) lst.Add(p);
                var sorted = lst.OrderBy(x => (object?)new List<object?> { ((PyDict)x!)["offset"], ((PyDict)x)["name"] }, Comparer<object?>.Create(Py.Compare)).ToList();
                lst.Clear();
                lst.AddRange(sorted);
                foreach (PyDict p in lst.Cast<PyDict>())
                {
                    if (seen.Contains(p["name"]!)) throw new InvalidOperationException("AssertionError");
                    seen.Add(p["name"]!);
                }
            }
            cbs.Add(new PyDict
            {
                ["name"] = bcb["name"], ["size"] = bcb["size"], ["slot"] = (long)slot,
                ["partial"] = ccb?.Get("partial"), ["vectors"] = vectors, ["matrices"] = matrices,
            });
            slot++;
        }
        // 貼圖：共用 ∪ blob（同名以 blob 為準）
        var textures = new PyDict();
        foreach (PyDict t in Assets.ListOf(common.Get("textures")).Cast<PyDict>()) textures[t["name"]] = t.Copy();
        foreach (PyDict t in ((List<object?>)blob["textures"]!).Cast<PyDict>()) textures[t["name"]] = t;
        foreach (PyDict t in textures.Values.Cast<PyDict>())
            if (t.Get("samplerRegister") is { } sr && Py.Compare(sr, 0L) < 0) t["samplerRegister"] = null;
        var texList = textures.Values.OrderBy(t => (object?)new List<object?> { Py.Truthy(((PyDict)t!).Get("register")) ? ((PyDict)t).Get("register") : 0L, ((PyDict)t)["name"] },
            Comparer<object?>.Create(Py.Compare)).ToList();
        var bufs = new List<object?>(Assets.ListOf(common.Get("buffers")));
        bufs.AddRange((List<object?>)blob.Get("buffers", new List<object?>())!);
        return new PyDict
        {
            ["constantBuffers"] = cbs,
            ["textures"] = texList,
            ["buffers"] = bufs,
            ["samplers"] = Assets.ListOf(common.Get("samplers")),
            ["implicitSamplers"] = blob["implicitSamplers"],
            ["cbBindings"] = blob["cbBindings"],
        };
    }

    private sealed class PyEq : IEqualityComparer<object>
    {
        public new bool Equals(object? a, object? b) => Py.Eq(a, b);
        public int GetHashCode(object o) => o is string s ? s.GetHashCode() : Py.IsNumber(o) ? Py.ToDouble(o).GetHashCode() : 0;
    }

    // ───────── 外部工具（原生函式庫） ─────────

    private sealed record Converted(string Hash, string Stage, List<object?> PatchNotes, List<object?> Io);

    /// <summary>convert_one：反組譯 → dxbc_compiler → SpirvPatcher → spirv-cross（GLSL、reflection）。結果以 hash 存在工作資料夾</summary>
    private static Converted ConvertOne(string hash, byte[] dxbc, string stage, string workdir)
    {
        string b = Path.Combine(workdir, hash);
        File.WriteAllBytes(b + ".dxbc", dxbc);
        string asm = PyStr.UniversalNewlines(Native.Disassemble(dxbc));
        File.WriteAllBytes(b + ".asm", Encoding.UTF8.GetBytes(asm));
        var raw = Native.CompileCbvArray(dxbc);
        var (renames, iface) = IoRenameMap(dxbc, stage);
        var patcher = new SpirvPatcher(raw, renames);
        var spv = patcher.Patch();
        File.WriteAllBytes(b + ".spv", spv);
        string glsl = PyStr.UniversalNewlines(Native.ToGlslEs300(spv));
        PyStr.WriteText(b + ".glsl", glsl);
        try
        {
            PyStr.WriteText(b + ".reflect.json", PyStr.UniversalNewlines(Native.Reflect(spv)));
        }
        catch (Native.ToolError)
        {
            // 命令列版結束碼非 0 時不存 reflection
        }
        return new Converted(hash, stage, patcher.Notes.Distinct().Order(PyStr.Comparer).Cast<object?>().ToList(), iface);
    }

    // ───────── shader 解析 ─────────

    private static readonly Regex NonAlnum = PyRe.R("[^A-Za-z0-9]+");

    public static string Sanitize(string name)
    {
        string s = NonAlnum.Replace(name, "_").Trim('_');
        return s.Length > 0 ? s : "unnamed";
    }

    // ───────── build 裡的 shader（AssetRipper 沒匯出的內建 shader） ─────────

    private static string HexU32(IEnumerable<object?> values)
    {
        var sb = new StringBuilder();
        Span<byte> b = stackalloc byte[4];
        foreach (var v in values)
        {
            long x = Py.ToLong(v);
            if (x < 0 || x > uint.MaxValue) throw new InvalidOperationException("argument out of range");
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)x);
            sb.Append(System.Convert.ToHexStringLower(b));
        }
        return sb.ToString();
    }

    private static string HexU16(IEnumerable<object?> values)
    {
        var sb = new StringBuilder();
        Span<byte> b = stackalloc byte[2];
        foreach (var v in values)
        {
            long x = Py.ToLong(v);
            if (x < 0 || x > ushort.MaxValue) throw new InvalidOperationException("argument out of range");
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)x);
            sb.Append(System.Convert.ToHexStringLower(b));
        }
        return sb.ToString();
    }

    private static IEnumerable<object?> Items(object? v) => v switch
    {
        List<object?> l => l,
        PyTuple t => t.Items,
        byte[] bytes => bytes.Select(x => (object?)(long)x),
        _ => throw new InvalidCastException("not iterable"),
    };

    /// <summary>_ar_shape：UnityPy 的 typetree → AssetRipper YAML 讀進來的樣子</summary>
    public static object? ArShape(object? v, string? key = null)
    {
        switch (v)
        {
            case byte[] b: return PyStr.Hex(b);
            case string s:
                if (s == "") return null;
                if (s is "true" or "false") return s == "true";
                return s;
            case PyDict d:
            {
                var o = new PyDict(d.Count);
                foreach (var (k, x) in d) o[k] = ArShape(x, k as string);
                return o;
            }
            case List<object?> or PyTuple:
            {
                var items = Items(v).ToList();
                if (key == "m_ParameterBlobIndices")
                    return items.Select(x => Py.Truthy(x) ? (object?)HexU32(Items(x)) : null).ToList();
                if (key is "m_KeywordIndices" or "m_SerializedKeywordStateMask")
                    return items.Count > 0 ? HexU16(items) : null;
                if (key is "m_Platforms" or "m_KeywordFlags")
                    return PyStr.Hex(items.Select(x => checked((byte)Py.ToLong(x))).ToArray());
                if (items.Count > 0 && items.All(x => x is PyTuple { Items.Length: 2 } t && t.Items[0] is string))
                {
                    var o = new PyDict();
                    foreach (PyTuple t in items.Cast<PyTuple>()) o[t.Items[0]] = ArShape(t.Items[1], (string)t.Items[0]!);
                    return o;
                }
                return items.Select(x => ArShape(x)).ToList();
            }
            default: return v;
        }
    }

    /// <summary>一個 Shader 物件的 typetree → parse_shader 回傳的那種 dict</summary>
    public static PyDict BuildShaderDict(PyDict tt)
    {
        string[] blobKeys = ["platforms", "offsets", "compressedLengths", "decompressedLengths", "compressedBlob", "stageCounts"];
        var rest = new PyDict();
        foreach (var (k, v) in tt)
            if (!blobKeys.Contains((string)k!)) rest[k] = v;
        var sh = (PyDict)ArShape(rest)!;
        sh["m_Name"] = ((PyDict)tt["m_ParsedForm"]!)["m_Name"];
        sh["platforms"] = HexU32(Items(tt["platforms"]));
        foreach (var k in new[] { "offsets", "compressedLengths", "decompressedLengths" })
            sh[k] = Items(tt[k]).Select(x => (object?)HexU32(Items(x))).ToList();
        sh["compressedBlob"] = PyStr.Hex(Assets.Bytes(tt["compressedBlob"]));
        sh["stageCounts"] = HexU32(Py.Truthy(tt.Get("stageCounts")) ? Items(tt["stageCounts"]) : []);
        return sh;
    }

    /// <summary>build 的資產檔（level*、*.assets、Resources/unity_builtin_extra）依 m_ParsedForm.m_Name 找 Shader；同名多個記失敗</summary>
    public static (Dictionary<string, PyDict> found, List<object?> failures) LoadBuildShaders(string data, List<string> names)
    {
        var want = names.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, List<(string src, PyDict tt)>>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(data).Select(p => p.Replace('\\', '/'))
            .Where(p => (PyPath.Suffix(p) == ".assets" || PyPath.Name(p).StartsWith("level", StringComparison.Ordinal)) && !PyPath.Name(p).EndsWith(".resS", StringComparison.Ordinal))
            .Order(PyPath.Comparer).ToList();
        files.Add(PyPath.Join(data, "Resources/unity_builtin_extra"));
        foreach (var p in files)
        {
            if (!File.Exists(p)) continue;
            var env = new BuildEnv(data);
            foreach (var sf in env.Load(p))
                foreach (var o in sf.Objects)
                {
                    if (o.TypeName != "Shader") continue;
                    var tt = o.ReadTypeTree(boost: true);
                    if (Assets.Dict(tt.Get("m_ParsedForm")).Get("m_Name") is string n && want.Contains(n))
                    {
                        if (!found.TryGetValue(n, out var l)) found[n] = l = [];
                        l.Add(($"{PyPath.Name(p)}#{o.PathId}", tt));
                    }
                }
        }
        var outp = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        var failures = new List<object?>();
        foreach (var n in names)
        {
            var hits = found.GetValueOrDefault(n) ?? [];
            if (hits.Count != 1)
            {
                failures.Add(new PyDict { ["shader"] = n, ["step"] = "build", ["error"] = $"build 裡找到 {hits.Count} 個（{string.Join(", ", hits.Select(h => h.src))}）" });
                continue;
            }
            outp[n] = BuildShaderDict(hits[0].tt);
            outp[n]["_buildSource"] = hits[0].src;
        }
        return (outp, failures);
    }

    private static PyDict NameIndicesInv(PyDict pass)
    {
        var o = new PyDict();
        foreach (var (k, v) in Assets.Dict(pass.Get("m_NameIndices"))) o[v] = k;
        return o;
    }

    private static PyDict CommonParameters(PyDict prog, PyDict idx2name)
    {
        var cp = Assets.Dict(prog.Get("m_CommonParameters"));
        object? PName(PyDict p) => idx2name.TryGetValue(p.Get("m_NameIndex"), out var n) ? n : $"#{Py.Str(p.Get("m_NameIndex"))}";
        var cbs = new List<object?>();
        foreach (PyDict cb in Assets.ListOf(cp.Get("m_ConstantBuffers")).Cast<PyDict>())
        {
            cbs.Add(new PyDict
            {
                ["name"] = PName(cb),
                ["size"] = cb.Get("m_Size"),
                ["partial"] = Py.Truthy(cb.Get("m_IsPartialCB")),
                ["vectors"] = Assets.ListOf(cb.Get("m_VectorParams")).Cast<PyDict>().Select(v => (object?)new PyDict
                {
                    ["name"] = PName(v), ["offset"] = v.Get("m_Index"), ["dim"] = v.Get("m_Dim"), ["arraySize"] = v.Get("m_ArraySize"),
                }).ToList(),
                ["matrices"] = Assets.ListOf(cb.Get("m_MatrixParams")).Cast<PyDict>().Select(m => (object?)new PyDict
                {
                    ["name"] = PName(m), ["offset"] = m.Get("m_Index"), ["rows"] = m.Get("m_RowCount"), ["arraySize"] = m.Get("m_ArraySize"),
                }).ToList(),
            });
        }
        var bindings = Assets.ListOf(cp.Get("m_ConstantBufferBindings")).Cast<PyDict>()
            .Select(b => new PyDict { ["name"] = PName(b), ["slot"] = b.Get("m_Index"), ["arraySize"] = b.Get("m_ArraySize") }).ToList();
        var slotOf = new Dictionary<object, object?>(new PyEq());
        foreach (var b in bindings) slotOf[b["name"]!] = b["slot"];
        foreach (PyDict cb in cbs.Cast<PyDict>()) cb["slot"] = slotOf.TryGetValue(cb["name"]!, out var s) ? s : null;
        var textures = Assets.ListOf(cp.Get("m_TextureParams")).Cast<PyDict>().Select(t => (object?)new PyDict
        {
            ["name"] = PName(t), ["register"] = t.Get("m_Index"), ["samplerRegister"] = t.Get("m_SamplerIndex"),
            ["dim"] = t.Get("m_Dim"), ["multiSampled"] = Py.Truthy(t.Get("m_MultiSampled")),
        }).ToList();
        var buffers = Assets.ListOf(cp.Get("m_BufferParams")).Cast<PyDict>().Select(b => (object?)new PyDict { ["name"] = PName(b), ["register"] = b.Get("m_Index") }).ToList();
        var samplers = Assets.ListOf(cp.Get("m_Samplers")).Cast<PyDict>().Select(s => (object?)new PyDict { ["name"] = PName(s), ["register"] = s.Get("m_Index") }).ToList();
        return new PyDict { ["constantBuffers"] = cbs, ["textures"] = textures, ["buffers"] = buffers, ["samplers"] = samplers };
    }

    private static List<object?> PropInfo(PyDict pf)
    {
        var props = new List<object?>();
        foreach (PyDict p in Assets.ListOf(Assets.Dict(pf.Get("m_PropInfo")).Get("m_Props")).Cast<PyDict>())
        {
            props.Add(new PyDict
            {
                ["name"] = p.Get("m_Name"),
                ["description"] = p.Get("m_Description"),
                ["type"] = p.Get("m_Type"),
                ["flags"] = p.Get("m_Flags"),
                ["defaultValue"] = Enumerable.Range(0, 4).Select(i => p.Get($"m_DefValue[{i}]")).ToList(),
                ["defaultTexture"] = Assets.Dict(p.Get("m_DefTexture")).Get("m_DefaultName"),
                ["texDim"] = Assets.Dict(p.Get("m_DefTexture")).Get("m_TexDim"),
            });
        }
        return props;
    }

    /// <summary>SerializedShaderFloatValue {val, name}：綁材質屬性的輸出 {val, prop}，沒綁的（'&lt;noninit&gt;'）只輸出數字</summary>
    private static object? StateValue(object? v)
    {
        if (v is not PyDict d) return v;
        var name = d.Get("name");
        if (Py.Truthy(name) && !Py.Eq(name, "<noninit>")) return new PyDict { ["val"] = d.Get("val"), ["prop"] = name };
        return d.Get("val");
    }

    private static PyDict PassState(PyDict state)
    {
        var blend0 = Assets.Dict(state.Get("rtBlend0"));
        object? Val(string k) => StateValue(state.Get(k));
        object? ValOf(PyDict d, string k) => StateValue(d.Get(k));
        return new PyDict
        {
            ["zWrite"] = Val("zWrite"),
            ["zTest"] = Val("zTest"),
            ["culling"] = Val("culling"),
            ["blend"] = new PyDict
            {
                ["src"] = ValOf(blend0, "srcBlend"), ["dst"] = ValOf(blend0, "destBlend"),
                ["srcAlpha"] = ValOf(blend0, "srcBlendAlpha"), ["dstAlpha"] = ValOf(blend0, "destBlendAlpha"),
                ["op"] = ValOf(blend0, "blendOp"), ["opAlpha"] = ValOf(blend0, "blendOpAlpha"),
            },
            ["colorMask"] = ValOf(blend0, "colMask"),
            ["stencilRef"] = Val("stencilRef"),
            ["stencilReadMask"] = Val("stencilReadMask"),
            ["stencilWriteMask"] = Val("stencilWriteMask"),
        };
    }

    // ───────── 主流程 ─────────

    private sealed class ShaderRec
    {
        public required string Stem;
        public required PyDict Out;  // stem、name、fallback、keywordNames、properties、subShaders（index.json 的內容，不含 entries／custom）
        public required bool Custom;
        public required Dictionary<long, SubProgram> Entries;
    }

    private static bool IsShaderAsset(string f)
    {
        using var fs = File.OpenRead(f);
        var head = new byte[256];
        int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head.AsSpan(0, n).IndexOf("--- !u!48 "u8) >= 0;
    }

    private static readonly Regex MetaGuid = PyRe.M(@"^guid: ([0-9a-f]{32})$");

    /// <summary>
    /// convert.py 的 main（--src、--out、--build、--build-shaders）。跟 Python 版唯一不同：不跑 glslangValidator，
    /// glslValidated 寫成「有沒有產生 GLSL」（兩款遊戲的 glslangValidator 都是 0 失敗，所以兩者相同；見 docs/phase3-shader.md）。
    /// </summary>
    public PyDict Convert(string src, string outDir, string? buildData, List<string>? buildShaders)
    {
        double t0 = ImportContext.Now();
        string shaderDir = PyPath.Join(src, "Assets/Shader");
        string matDir = PyPath.Join(src, "Assets/Material");
        log($"來源: {shaderDir}");
        log($"輸出: {outDir}");

        var shaderPaths = new PyDict();
        foreach (var f in PyPath.Glob(shaderDir, "*.asset").Order(PyPath.Comparer)) shaderPaths[PyPath.Name(f)[..^".asset".Length]] = f;
        foreach (var f in PyPath.RGlob(PyPath.Join(src, "Assets"), "*.asset").Order(PyPath.Comparer))
        {
            if (PyPath.Parent(f) == shaderDir || !File.Exists(f) || !IsShaderAsset(f)) continue;
            shaderPaths[PyPath.RelativeTo(f, PyPath.Join(src, "Assets"))[..^".asset".Length].Replace("/", "_")] = f;
        }

        var guid2stem = new PyDict();
        foreach (var (stem, fObj) in shaderPaths)
        {
            string meta = (string)fObj! + ".meta";
            if (!File.Exists(meta)) continue;
            var m = MetaGuid.Match(PyStr.UniversalNewlines(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(meta))));
            if (m.Success) guid2stem[m.Groups[1].Value] = stem;
        }

        var materials = new List<PyDict>();
        foreach (var mp in PyPath.Glob(matDir, "*.mat").Order(PyPath.Comparer))
        {
            var doc = LoadUnityYaml(mp);
            var mat = Assets.Dict(doc.Get("Material"));
            var guid = Assets.Dict(mat.Get("m_Shader")).Get("guid");
            if (!Py.Truthy(guid)) guid = "";
            materials.Add(new PyDict
            {
                ["file"] = PyPath.Name(mp),
                ["name"] = Py.Truthy(mat.Get("m_Name")) ? mat["m_Name"] : PyPath.Stem(mp),
                ["shader"] = guid2stem.Get(guid),
                ["shaderGuid"] = guid,
                ["keywords"] = Assets.ListOf(mat.Get("m_ValidKeywords")).OrderBy(x => x, Comparer<object?>.Create(Py.Compare)).ToList(),
            });
        }

        var shaderFiles = shaderPaths.OrderBy(kv => kv.Key, Comparer<object?>.Create(Py.Compare)).ToList();
        var sources = shaderFiles.Select(kv => ((string)kv.Key!, (Func<PyDict>)(() => (PyDict)LoadUnityYaml((string)kv.Value!)["Shader"]!))).ToList();
        var buildFailures = new List<object?>();
        if (buildData is not null && buildShaders is not null)
        {
            var (loaded, fails) = LoadBuildShaders(buildData, buildShaders);
            buildFailures = fails;
            foreach (var n in buildShaders)
            {
                if (!loaded.TryGetValue(n, out var d)) continue;
                log($"  build 的 shader：{n}（{d["_buildSource"]}）");
                sources.Add(("Builtin_" + n.Replace("/", "_"), () => d));
            }
        }
        log($"shader 數: {sources.Count}；材質數: {materials.Count}");

        // phase 1：解析 YAML 與 blob，建立工作清單
        var shaders = new List<ShaderRec>();
        var jobs = new List<(string hash, byte[] dxbc, string stage)>();
        var jobIndex = new HashSet<string>(StringComparer.Ordinal);
        var totals = new Dictionary<string, long>();
        void Tot(string k, long n = 1) => totals[k] = totals.GetValueOrDefault(k) + n;
        var failures = new List<object?>(buildFailures);

        foreach (var (stem, load) in sources)
        {
            PyDict sh;
            try
            {
                sh = load();
            }
            catch (Exception e)
            {
                failures.Add(new PyDict { ["shader"] = stem, ["step"] = "yaml", ["error"] = Trunc(e.Message) });
                continue;
            }
            var pf = Assets.Dict(sh.Get("m_ParsedForm"));
            var chunks = DecompressChunks(sh);
            var entries = EntryTable(chunks);
            Tot("blobEntries", entries.Count);
            var parsedEntries = new Dictionary<long, SubProgram>();
            var paramCache = new Dictionary<long, PyDict>();
            var subshaders = new List<object?>();
            int ssi = 0;
            foreach (PyDict sub in Assets.ListOf(pf.Get("m_SubShaders")).Cast<PyDict>())
            {
                var passes = new List<object?>();
                int pi = 0;
                foreach (PyDict pass in Assets.ListOf(sub.Get("m_Passes")).Cast<PyDict>())
                {
                    var idx2name = NameIndicesInv(pass);
                    var state = Assets.Dict(pass.Get("m_State"));
                    object? pname = Py.Truthy(state.Get("m_Name")) ? state["m_Name"] : "";
                    var stages = new PyDict();
                    foreach (var (field, stageName, _) in StageField)
                    {
                        var prog = Assets.Dict(pass.Get(field));
                        var psp = Assets.ListOf(prog.Get("m_PlayerSubPrograms"));
                        var nonempty = psp.Where(Py.Truthy).ToList();
                        if (nonempty.Count == 0) continue;
                        if (nonempty.Count > 1)
                            failures.Add(new PyDict { ["shader"] = stem, ["pass"] = pname, ["stage"] = stageName, ["step"] = "parse", ["error"] = $"m_PlayerSubPrograms 有 {nonempty.Count} 個非空子列表" });
                        var variants = new List<object?>();
                        var stageCommon = CommonParameters(prog, idx2name);
                        var pbiList = new List<long>();
                        foreach (var item in Assets.ListOf(prog.Get("m_ParameterBlobIndices")))
                        {
                            if (item is string)
                            {
                                pbiList = HexU32s(item);
                                break;
                            }
                        }
                        var first = Assets.AsList(nonempty[0]);
                        for (int vi = 0; vi < first.Count; vi++)
                        {
                            var sp = (PyDict)first[vi]!;
                            var bi = sp.Get("m_BlobIndex");
                            var variant = new PyDict
                            {
                                ["index"] = (long)vi, ["blobIndex"] = bi,
                                ["gpuProgramType"] = sp.Get("m_GpuProgramType"), ["shaderRequirements"] = sp.Get("m_ShaderRequirements"),
                            };
                            variants.Add(variant);
                            Tot("programs");
                            long biL = Py.ToLong(bi);
                            if (!parsedEntries.ContainsKey(biL))
                            {
                                try
                                {
                                    var (chunk, off, elen) = entries[(int)biL];
                                    parsedEntries[biL] = new SubProgram(chunk, off, elen);
                                }
                                catch (Exception e)
                                {
                                    failures.Add(new PyDict { ["shader"] = stem, ["pass"] = pname, ["stage"] = stageName, ["blobIndex"] = bi, ["step"] = "blob", ["error"] = Trunc(e.Message) });
                                    continue;
                                }
                            }
                            var entry = parsedEntries[biL];
                            string h = System.Convert.ToHexStringLower(SHA1.HashData(entry.Dxbc));
                            if (jobIndex.Add(h)) jobs.Add((h, entry.Dxbc, stageName));
                            if (vi < pbiList.Count)
                            {
                                long pbi = pbiList[vi];
                                variant["parameterBlobIndex"] = pbi;
                                try
                                {
                                    if (!paramCache.ContainsKey(pbi))
                                    {
                                        var (chunk, off, elen) = entries[(int)pbi];
                                        paramCache[pbi] = ParseParamBlob(chunk.AsSpan(off, Math.Max(0, Math.Min(elen, chunk.Length - off))).ToArray());
                                    }
                                    var merged = MergeParameters(stageCommon, paramCache[pbi]);
                                    var bindSlot = new Dictionary<object, object?>(new PyEq());
                                    foreach (PyDict b in ((List<object?>)merged["cbBindings"]!).Cast<PyDict>()) bindSlot[b["name"]!] = b["slot"];
                                    var commonSlot = new Dictionary<object, object?>(new PyEq());
                                    foreach (PyDict cb in ((List<object?>)stageCommon["constantBuffers"]!).Cast<PyDict>()) commonSlot[cb["name"]!] = cb.Get("slot");
                                    int i = 0;
                                    foreach (PyDict cb in ((List<object?>)merged["constantBuffers"]!).Cast<PyDict>())
                                    {
                                        if (bindSlot.TryGetValue(cb["name"]!, out var bs) && bs is not null && !Py.Eq(bs, (long)i)) Tot("paramSlotMismatch");
                                        if (commonSlot.TryGetValue(cb["name"]!, out var cs) && cs is not null && !Py.Eq(cs, (long)i)) Tot("paramSlotVsCommon");
                                        i++;
                                    }
                                    variant["parameters"] = merged;
                                    Tot("paramBlobs");
                                }
                                catch (Exception e)
                                {
                                    failures.Add(new PyDict { ["shader"] = stem, ["pass"] = pname, ["stage"] = stageName, ["blobIndex"] = pbi, ["step"] = "paramBlob", ["error"] = Trunc(e.Message) });
                                }
                            }
                        }
                        stages[stageName] = new PyDict
                        {
                            ["gpuProgramType"] = first.Count > 0 ? ((PyDict)first[0]!).Get("m_GpuProgramType") : null,
                            ["keywordStateMask"] = Py.Truthy(prog.Get("m_SerializedKeywordStateMask")) ? prog["m_SerializedKeywordStateMask"] : "",
                            ["parameters"] = stageCommon,
                            ["variants"] = variants,
                        };
                    }
                    passes.Add(new PyDict
                    {
                        ["index"] = (long)pi,
                        ["name"] = pname,
                        ["type"] = Py.Truthy(pass.Get("m_Type")) ? pass["m_Type"] : 0L,
                        ["textureName"] = Py.Truthy(pass.Get("m_TextureName")) ? pass["m_TextureName"] : "",
                        ["tags"] = Py.Truthy(Assets.Dict(state.Get("m_Tags")).Get("tags")) ? Assets.Dict(state.Get("m_Tags"))["tags"] : new PyDict(),
                        ["lod"] = state.Get("m_LOD"),
                        ["state"] = PassState(state),
                        ["stages"] = stages,
                    });
                    pi++;
                }
                subshaders.Add(new PyDict
                {
                    ["index"] = (long)ssi,
                    ["tags"] = Py.Truthy(Assets.Dict(sub.Get("m_Tags")).Get("tags")) ? Assets.Dict(sub.Get("m_Tags"))["tags"] : new PyDict(),
                    ["lod"] = sub.Get("m_LOD"),
                    ["passes"] = passes,
                });
                ssi++;
            }
            shaders.Add(new ShaderRec
            {
                Stem = stem,
                Custom = CustomPrefixes.Any(p => stem.StartsWith(p, StringComparison.Ordinal)),
                Entries = parsedEntries,
                Out = new PyDict
                {
                    ["stem"] = stem,
                    ["name"] = sh.Get("m_Name"),
                    ["fallback"] = pf.Get("m_FallbackName"),
                    ["keywordNames"] = Py.Truthy(pf.Get("m_KeywordNames")) ? pf["m_KeywordNames"] : new List<object?>(),
                    ["properties"] = PropInfo(pf),
                    ["subShaders"] = subshaders,
                },
            });
        }
        log($"解析完成：blob 條目 {totals.GetValueOrDefault("blobEntries")}，program 引用 {totals.GetValueOrDefault("programs")}，唯一 DXBC {jobs.Count}（{ImportContext.Now() - t0:F1}s）");

        // phase 2：轉換（依內容 hash 去重），平行做，結果跟順序無關
        string workdir = Directory.CreateTempSubdirectory("mt2-shaders-").FullName;
        var converted = new Dictionary<string, Converted>(StringComparer.Ordinal);
        var failedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var results = new (Converted? ok, string? err)[jobs.Count];
        int done = 0;
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, i =>
        {
            try
            {
                results[i] = (ConvertOne(jobs[i].hash, jobs[i].dxbc, jobs[i].stage, workdir), null);
            }
            catch (Exception e)
            {
                results[i] = (null, Trunc(e.Message));
            }
            int d = Interlocked.Increment(ref done);
            if (d % 200 == 0 || d == jobs.Count) log($"  轉換進度 {d}/{jobs.Count}");
        });
        for (int i = 0; i < jobs.Count; i++)
        {
            if (results[i].ok is { } c) converted[jobs[i].hash] = c;
            else failedHashes[jobs[i].hash] = results[i].err!;
        }
        log($"轉換完成：成功 {converted.Count}，失敗 {failedHashes.Count}（{ImportContext.Now() - t0:F1}s）");

        // phase 3：寫出每個 shader 的檔案
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var variantRecords = new List<(ShaderRec srec, PyDict pass, string stage, PyDict variant, string hash)>();
        foreach (var srec in shaders)
        {
            string sdir = PyPath.Join(outDir, srec.Stem);
            Directory.CreateDirectory(sdir);
            var subShaders = (List<object?>)srec.Out["subShaders"]!;
            foreach (PyDict sub in subShaders.Cast<PyDict>())
            {
                foreach (PyDict pass in ((List<object?>)sub["passes"]!).Cast<PyDict>())
                {
                    string pfx = $"pass{Py.Str(pass["index"])}_{Sanitize(Py.Str(pass["name"]))}";
                    if (subShaders.Count > 1) pfx = $"sub{Py.Str(sub["index"])}_{pfx}";
                    foreach (var (stageName, stageObj) in (PyDict)pass["stages"]!)
                    {
                        string shortName = StageField.First(s => s.stage == (string)stageName!).shortName;
                        var stageInfo = (PyDict)stageObj!;
                        foreach (PyDict variant in ((List<object?>)stageInfo["variants"]!).Cast<PyDict>())
                        {
                            var bi = variant["blobIndex"];
                            if (!srec.Entries.TryGetValue(Py.ToLong(bi), out var entry)) continue;
                            string h = System.Convert.ToHexStringLower(SHA1.HashData(entry.Dxbc));
                            string b = $"{pfx}_{shortName}_{Py.Str(bi)}";
                            var files = new PyDict();
                            File.WriteAllBytes(PyPath.Join(sdir, b + ".dxbc"), entry.Dxbc);
                            files["dxbc"] = b + ".dxbc";
                            if (converted.TryGetValue(h, out var conv))
                            {
                                foreach (var ext in new[] { "asm", "spv", "glsl" })
                                {
                                    string srcF = Path.Combine(workdir, $"{h}.{ext}");
                                    if (!File.Exists(srcF)) continue;
                                    File.Copy(srcF, PyPath.Join(sdir, $"{b}.{ext}"), overwrite: true);
                                    files[ext] = $"{b}.{ext}";
                                }
                                string refl = Path.Combine(workdir, $"{h}.reflect.json");
                                if (File.Exists(refl)) variant["reflection"] = PyJson.Loads(PyStr.ReadText(refl));
                                if (conv.PatchNotes.Count > 0) variant["patchNotes"] = conv.PatchNotes;
                                if (conv.Io.Count > 0) variant["varyings"] = conv.Io;
                            }
                            string stgSuffix = (string)stageName! == "vertex" ? "vs" : "fs";
                            var cbSource = (List<object?>)((PyDict)(variant.Get("parameters") as PyDict is { Count: > 0 } vp ? vp : (PyDict)stageInfo["parameters"]!))["constantBuffers"]!;
                            var ub = new PyDict();
                            foreach (PyDict cb in cbSource.Cast<PyDict>())
                                if (cb.Get("slot") is { } slot)
                                    ub[cb["name"]] = new PyDict { ["block"] = $"cb{Py.Str(slot)}_{stgSuffix}_buf", ["instance"] = $"cb{Py.Str(slot)}_{stgSuffix}", ["slot"] = slot };
                            variant["uniformBlocks"] = ub;
                            variant["keywords"] = entry.Keywords.Cast<object?>().ToList();
                            variant["dxbcSize"] = (long)entry.Dxbc.Length;
                            variant["dxbcSha1"] = h;
                            variant["files"] = files;
                            variant["converted"] = converted.ContainsKey(h);
                            variantRecords.Add((srec, pass, (string)stageName!, variant, h));
                        }
                    }
                }
            }
        }

        // phase 4：Python 版這裡批次跑 glslangValidator；這裡不搬，glslValidated = 有沒有 GLSL
        var glslFiles = PyPath.RGlob(outDir, "*.glsl").Count();
        foreach (var (_, _, _, variant, _) in variantRecords)
            variant["glslValidated"] = Py.Truthy(((PyDict)variant["files"]!).Get("glsl"));

        // 材質 → 用到的變體
        var matByShader = new Dictionary<string, List<PyDict>>(StringComparer.Ordinal);
        foreach (var m in materials)
            if (m["shader"] is string s && Py.Truthy(s))
            {
                if (!matByShader.TryGetValue(s, out var l)) matByShader[s] = l = [];
                l.Add(m);
            }
        var usedMissing = new List<object?>();
        var usedVariants = new List<PyDict>();
        foreach (var srec in shaders)
        {
            if (!matByShader.TryGetValue(srec.Stem, out var mats) || mats.Count == 0) continue;
            var kwSpace = new HashSet<object>(Assets.ListOf(srec.Out["keywordNames"]).Where(x => x is not null).Cast<object>(), new PyEq());
            foreach (var (pass, stage) in Passes(srec))
                foreach (PyDict variant in ((List<object?>)stage["variants"]!).Cast<PyDict>())
                    foreach (var k in Assets.ListOf(variant.Get("keywords"))) kwSpace.Add(k!);
            foreach (var (pass, stage) in Passes(srec))
            {
                foreach (var m in mats)
                {
                    var mk = Assets.ListOf(m["keywords"]);
                    var effective = new HashSet<object>(mk.Where(k => kwSpace.Contains(k!)).Cast<object>(), new PyEq());
                    PyDict? match = null;
                    foreach (PyDict variant in ((List<object?>)stage["variants"]!).Cast<PyDict>())
                    {
                        var vk = new HashSet<object>(Assets.ListOf(variant.Get("keywords")).Cast<object>(), new PyEq());
                        if (vk.SetEquals(effective))
                        {
                            match = variant;
                            break;
                        }
                    }
                    bool fallback = false;
                    if (match is null)
                    {
                        int best = -1;
                        foreach (PyDict variant in ((List<object?>)stage["variants"]!).Cast<PyDict>())
                        {
                            var vk = new HashSet<object>(Assets.ListOf(variant.Get("keywords")).Cast<object>(), new PyEq());
                            if (vk.IsSubsetOf(effective) && vk.Count > best)
                            {
                                best = vk.Count;
                                match = variant;
                            }
                        }
                        fallback = match is not null;
                    }
                    if (match is null)
                    {
                        usedMissing.Add(new PyDict { ["material"] = m["file"], ["shader"] = srec.Stem, ["pass"] = pass["name"], ["stage"] = stage["__stage"], ["keywords"] = m["keywords"] });
                        continue;
                    }
                    if (match.Get("usedByMaterials") is not List<object?> ubm) match["usedByMaterials"] = ubm = [];
                    if (!ubm.Any(x => Py.Eq(x, m["name"]))) ubm.Add(m["name"]);
                    var mkSet = new HashSet<object>(mk.Cast<object>(), new PyEq());
                    if (fallback || !effective.SetEquals(mkSet))
                    {
                        if (match.Get("usedByMaterialsFallback") is not List<object?> fb) match["usedByMaterialsFallback"] = fb = [];
                        var rec = new PyDict { ["material"] = m["name"], ["materialKeywords"] = m["keywords"] };
                        if (!fb.Any(x => Py.Eq(x, rec))) fb.Add(rec);
                    }
                    if (!usedVariants.Any(v => ReferenceEquals(v, match))) usedVariants.Add(match);
                }
            }
        }

        // 寫 index.json（拿掉 entries、custom）。guid：同名的 shader 資產可能不只一個（各自只留下自己材質用到的變體），
        // 引擎照材質參照的 GUID 挑目錄，不能只靠名字
        var stem2guid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (g, s) in guid2stem) stem2guid[(string)s!] = (string)g!;
        foreach (var srec in shaders)
        {
            var o = srec.Out.Copy();
            foreach (var (pass, stage) in Passes(srec)) stage.Remove("__stage");
            o["file"] = srec.Stem + ".asset";
            if (stem2guid.TryGetValue(srec.Stem, out var sg)) o["guid"] = sg;
            o["materials"] = (matByShader.GetValueOrDefault(srec.Stem) ?? []).Select(m => m["name"]).ToList();
            PyStr.WriteText(PyPath.Join(PyPath.Join(outDir, srec.Stem), "index.json"), PyJson.Indented(o, 2, sortKeys: true) + "\n");
        }

        var report = new PyDict
        {
            ["source"] = shaderDir,
            ["totals"] = new PyDict
            {
                ["shaders"] = (long)shaders.Count,
                ["blobEntries"] = totals.GetValueOrDefault("blobEntries"),
                ["programReferences"] = totals.GetValueOrDefault("programs"),
                ["distinctPrograms"] = (long)shaders.Sum(s => s.Entries.Count),
                ["uniqueDxbc"] = (long)jobs.Count,
                ["converted"] = (long)converted.Count,
                ["convertFailed"] = (long)failedHashes.Count,
                ["glslFiles"] = (long)glslFiles,
                ["paramBlobsParsed"] = totals.GetValueOrDefault("paramBlobs"),
                ["paramSlotMismatch"] = totals.GetValueOrDefault("paramSlotMismatch"),
                ["paramSlotVsCommon"] = totals.GetValueOrDefault("paramSlotVsCommon"),
            },
            ["usedVariants"] = new PyDict { ["count"] = (long)usedVariants.Count, ["missing"] = usedMissing },
            ["materials"] = materials.Cast<object?>().ToList(),
            ["failures"] = failures.Concat(failedHashes.OrderBy(kv => kv.Key, PyStr.Comparer)
                .Select(kv => (object?)new PyDict { ["step"] = "convert", ["dxbcSha1"] = kv.Key, ["error"] = kv.Value })).ToList(),
        };
        PyStr.WriteText(PyPath.Join(outDir, "report.json"), PyJson.Indented(report, 2, sortKeys: true) + "\n");
        log($"shader: {shaders.Count}，唯一 DXBC: {jobs.Count}，轉換: {converted.Count}/{jobs.Count}，GLSL {glslFiles} 個（{ImportContext.Now() - t0:F1}s）");
        Directory.Delete(workdir, true);
        return report;
    }

    /// <summary>每個 pass 的每個 stage（stage 的 dict 暫時記住自己的名稱，寫 index.json 前拿掉）</summary>
    private static IEnumerable<(PyDict pass, PyDict stage)> Passes(ShaderRec srec)
    {
        foreach (PyDict sub in ((List<object?>)srec.Out["subShaders"]!).Cast<PyDict>())
            foreach (PyDict pass in ((List<object?>)sub["passes"]!).Cast<PyDict>())
                foreach (var (name, st) in (PyDict)pass["stages"]!)
                {
                    var stage = (PyDict)st!;
                    stage["__stage"] = name;
                    yield return (pass, stage);
                }
    }

    private static string Trunc(string s) => Py.Len(s) <= 300 ? s : string.Concat(s.EnumerateRunes().Take(300).Select(r => r.ToString()));
}
