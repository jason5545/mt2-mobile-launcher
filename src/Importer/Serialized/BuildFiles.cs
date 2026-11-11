// 部分移植自 UnityPy 1.25.4（https://github.com/K0lb3/UnityPy）：Environment 找 PPtr 的順序、預設的 get_ref_type_node，MIT，Copyright (c) 2019-2026 K0lb3；授權全文見 THIRD_PARTY_NOTICES.md。
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer.Serialized;

/// <summary>
/// 原版 build 的一個序列化檔（UnityPy 的 SerializedFile）：externals、檔案附的 type tree、物件。
/// 直接放在 build 資料夾的檔只讀檔頭與 metadata，物件位元組用到才從檔案讀（UnityPy 也是這樣；
/// AssetRipper 的 SerializedFile.Read 會一次讀進全部物件，Diceomancer 的 resources.assets 有 1.85 GB）。bundle 裡的檔照 AssetRipper 讀進記憶體。
/// </summary>
public sealed class BuildFile
{
    public string Name { get; }
    /// <summary>bundle 裡的檔（同一個 bundle 的其他檔，PPtr 先在這裡找）</summary>
    public IReadOnlyList<BuildFile>? Siblings { get; internal set; }
    /// <summary>UnityPy 的 externals[i].path</summary>
    public IReadOnlyList<string> ExternalPaths { get; }
    public List<BuildObject> Objects { get; } = [];
    public bool EnableTypeTree { get; }
    public UnityVersion Version { get; }
    public AssetRipper.IO.Files.SerializedFiles.Parser.SerializedTypeReference[] RefTypes { get; }
    private readonly Dictionary<long, BuildObject> _byPathId = [];
    private readonly string? _path;
    private readonly long _dataOffset;

    /// <summary>AssetRipper 已經整個讀進來的檔（bundle 裡的）</summary>
    public BuildFile(string name, SerializedFile file)
    {
        Name = name;
        EnableTypeTree = file.HasTypeTree;
        Version = file.Version;
        RefTypes = file.RefTypes.ToArray();
        ExternalPaths = file.Dependencies.ToArray().Select(d => d.PathNameOrigin).ToList();
        var types = file.Types.ToArray();
        foreach (var info in file.Objects)
            Add(new BuildObject(this, info.FileID, info.TypeID < 0 ? 114 : info.TypeID, info.ByteStart, info.ByteSize, info.ObjectData ?? [], info.GetSerializedType(types)));
    }

    /// <summary>只讀檔頭與 metadata 的檔（物件位元組用到才讀）</summary>
    public BuildFile(string name, string path, AssetRipper.IO.Files.SerializedFiles.Parser.SerializedFileHeader header,
        AssetRipper.IO.Files.SerializedFiles.Parser.SerializedFileMetadata metadata)
    {
        Name = name;
        _path = path;
        _dataOffset = header.DataOffset;
        EnableTypeTree = metadata.EnableTypeTree;
        Version = metadata.UnityVersion;
        RefTypes = metadata.RefTypes;
        ExternalPaths = metadata.Externals.Select(d => d.PathNameOrigin).ToList();
        foreach (var info in metadata.Object)
            Add(new BuildObject(this, info.FileID, info.TypeID < 0 ? 114 : info.TypeID, info.ByteStart, info.ByteSize, null, info.GetSerializedType(metadata.Types)));
    }

    private void Add(BuildObject o)
    {
        Objects.Add(o);
        _byPathId[o.PathId] = o;
    }

    internal byte[] ReadData(long byteStart, int byteSize)
    {
        var buf = new byte[byteSize];
        using var handle = File.OpenHandle(_path!);
        int read = 0;
        while (read < byteSize)
        {
            int n = RandomAccess.Read(handle, buf.AsSpan(read), _dataOffset + byteStart + read);
            if (n == 0) throw new EndOfStreamException($"{Name}：物件資料超出檔案");
            read += n;
        }
        return buf;
    }

    /// <summary>[e.path.split("/")[-1] for e in sf.externals]</summary>
    public List<string> Externals() => ExternalPaths.Select(p => p.Split('/')[^1]).ToList();

    public BuildObject? Get(long pathId) => _byPathId.GetValueOrDefault(pathId);

    public override string ToString() => Name;
}

/// <summary>序列化檔裡的一個物件（UnityPy 的 ObjectReader）</summary>
public sealed class BuildObject(BuildFile file, long pathId, int classId, long byteStart, int byteSize, byte[]? data,
    AssetRipper.IO.Files.SerializedFiles.Parser.SerializedType? serializedType)
{
    public BuildFile File { get; } = file;
    public long PathId { get; } = pathId;
    public int ClassId { get; } = classId;

    /// <summary>物件的位元組（沒有預先讀的話每次從檔案讀，不留在記憶體）</summary>
    public byte[] Data => data ?? File.ReadData(byteStart, byteSize);

    /// <summary>obj.type.name</summary>
    public string TypeName => Enum.IsDefined((ClassIDType)ClassId) ? ((ClassIDType)ClassId).ToString() : "UnknownType";

    /// <summary>檔案附的 type tree（bundle 才有）</summary>
    public TtNode? EmbeddedNode =>
        File.EnableTypeTree && serializedType is not null && serializedType.OldType.Nodes.Count > 0
            ? TypeTrees.FromAssetRipper(serializedType.OldType.Nodes)
            : null;

    /// <summary>_get_typetree_node()：檔案附的優先，沒有就用 TPK</summary>
    public TtNode Node => EmbeddedNode ?? TypeTrees.Tpk(ClassId, File.Version);

    /// <summary>obj.read_typetree(nodes, check_read)</summary>
    public PyDict ReadTypeTree(bool boost, TtNode? node = null, bool checkRead = true, TypeTreeReader.RefTypeResolver? refTypes = null)
        => (PyDict)TypeTreeReader.Read(node ?? Node, Data, boost, checkRead, refTypes ?? DefaultRefTypes)!;

    /// <summary>檔案附的 type tree 的根節點名稱，或 TPK 的：判斷要不要讀的時候不必建整棵樹</summary>
    public bool HasEmbeddedNode => File.EnableTypeTree && serializedType is not null && serializedType.OldType.Nodes.Count > 0;

    /// <summary>UnityPy 預設的 get_ref_type_node：只認檔案附的 ref_types</summary>
    private TtNode? DefaultRefTypes(PyDict refObject)
    {
        var t = (PyDict)refObject["type"]!;
        string cls = (string)t["class"]!, ns = (string)t["ns"]!, asm = (string)t["asm"]!;
        if (File.RefTypes.Length == 0) throw new InvalidOperationException("SerializedFile has no ref_types");
        if (cls == "") return null;
        foreach (var rt in File.RefTypes)
            if (rt.ClassName == cls && rt.Namespace == ns && rt.AsmName == asm)
                return TypeTrees.FromAssetRipper(rt.OldType.Nodes);
        throw new InvalidOperationException($"Referenced type not found: {cls} {ns} {asm}");
    }

    /// <summary>obj.peek_name()</summary>
    public string? PeekName(bool boost)
    {
        var peek = Node.NamePeekNode();
        if (peek is null) return null;
        var d = (PyDict)TypeTreeReader.Read(peek.Value.node, Data, boost, checkRead: false)!;
        return d.Get(peek.Value.key) as string;
    }

    /// <summary>obj.get_raw_data()</summary>
    public byte[] RawData => Data;

    public override string ToString() => $"{File.Name}#{PathId}";
}

/// <summary>type tree 的來源：AssetRipper 內附的 TPK（UnityPy 也是用 TPK）、檔案附的節點</summary>
public static class TypeTrees
{
    private static readonly Dictionary<(int, UnityVersion), TtNode> TpkCache = [];
    private static readonly Lock Gate = new();

    public static TtNode Tpk(int classId, UnityVersion version)
    {
        lock (Gate)
        {
            if (TpkCache.TryGetValue((classId, version), out var n)) return n;
            if (!TypeTreeNodeStruct.TryMakeFromTpk((ClassIDType)classId, version, out var release, out _))
                throw new InvalidOperationException($"Could not find class info for class id {classId}");
            var list = new List<(string, string, int, int)>();
            Flatten(release, 0, list);
            n = TtNode.FromList(list);
            TpkCache[(classId, version)] = n;
            return n;
        }
    }

    private static void Flatten(TypeTreeNodeStruct node, int level, List<(string, string, int, int)> list)
    {
        list.Add((node.TypeName, node.Name, level, (int)node.MetaFlag));
        foreach (var c in node.SubNodes) Flatten(c, level + 1, list);
    }

    public static TtNode FromAssetRipper(IReadOnlyList<AssetRipper.IO.Files.SerializedFiles.Parser.TypeTrees.TypeTreeNode> nodes)
        => TtNode.FromList(nodes.Select(n => (n.Type, n.Name, (int)n.Level, (int)n.MetaFlag)).ToList());
}

/// <summary>
/// 一組載入的 build 檔（UnityPy 的 Environment）。PPtr 指到別的檔時，照 UnityPy 的找法：
/// 先找同一個 bundle 裡的檔，再找已經載入的，最後到 build 資料夾底下照檔名找（unity default resources 在 Resources/ 裡）。
/// </summary>
public sealed class BuildEnv(string gameData)
{
    public string GameData { get; } = gameData;
    private readonly Dictionary<string, BuildFile> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _localFiles;

    /// <summary>UnityPy.load(path) 的檔：序列化檔一個，bundle 是裡面全部的序列化檔（照 bundle 目錄的順序）</summary>
    public List<BuildFile> Load(string path)
    {
        // 不用 SchemeReader.LoadFile：它先找 .split 分割檔，檔名有括號時（Addressables 的 bundle）會找不到檔
        var stream = AssetRipper.IO.Files.Streams.Smart.SmartStream.OpenRead(path, LocalFileSystem.Instance);
        if (SerializedFile.IsSerializedFile(stream))
        {
            stream.Position = 0;
            var header = new AssetRipper.IO.Files.SerializedFiles.Parser.SerializedFileHeader();
            using (var reader = new AssetRipper.IO.Endian.EndianReader(stream, AssetRipper.IO.Endian.EndianType.BigEndian))
                header.Read(reader);
            if (AssetRipper.IO.Files.SerializedFiles.Parser.SerializedFileMetadata.IsMetadataAtTheEnd(header.Version))
                stream.Position = header.FileSize - header.MetadataSize;
            var metadata = new AssetRipper.IO.Files.SerializedFiles.Parser.SerializedFileMetadata();
            metadata.Read(stream, header);
            AssetRipper.IO.Files.Converters.SerializedFileMetadataConverter.CombineFormats(header.Version, metadata);
            stream.Dispose();
            string name = PyPath.Name(path);
            if (!_loaded.TryGetValue(name, out var lazy)) _loaded[name] = lazy = new BuildFile(name, path, header, metadata);
            return [lazy];
        }
        stream.Position = 0;
        var fb = SchemeReader.ReadFile(stream, path, PyPath.Name(path));
        fb.ReadContentsRecursively();
        var result = new List<BuildFile>();
        switch (fb)
        {
            case SerializedFile sf:
                result.Add(Register(PyPath.Name(path), sf));
                break;
            case FileContainer container:
                foreach (var sf in AllSerialized(container))
                    result.Add(Register(sf.Name, sf));
                foreach (var f in result) f.Siblings = result;
                break;
            default:
                throw new InvalidOperationException($"不是 Unity 的序列化檔或 bundle：{path}");
        }
        return result;
    }

    private static IEnumerable<SerializedFile> AllSerialized(FileContainer c)
    {
        foreach (var sf in c.SerializedFiles) yield return sf;
        foreach (var sub in c.FileLists)
            foreach (var sf in AllSerialized(sub))
                yield return sf;
    }

    private BuildFile Register(string name, SerializedFile sf)
    {
        if (_loaded.TryGetValue(name, out var existing)) return existing;
        var bf = new BuildFile(name, sf);
        _loaded[name] = bf;
        return bf;
    }

    /// <summary>載入單一序列化檔（已載入就直接回傳）</summary>
    public BuildFile LoadOne(string path)
    {
        string name = PyPath.Name(path);
        if (_loaded.TryGetValue(name, out var f)) return f;
        var files = Load(path);
        if (files.Count != 1) throw new InvalidOperationException($"{path} 不是單一序列化檔");
        return files[0];
    }

    /// <summary>PPtr 的 m_FileID → 檔案（0 是自己）</summary>
    public BuildFile Target(BuildFile from, long fileId)
    {
        if (fileId == 0) return from;
        long i = fileId - 1;
        if (i < 0 || i >= from.ExternalPaths.Count) throw new FileNotFoundException("Failed to resolve pointer - invalid m_FileID!");
        string path = from.ExternalPaths[(int)i];
        string clean = path;
        if (clean.StartsWith("archive:/", StringComparison.Ordinal)) clean = clean[9..];
        if (clean.StartsWith("assets/", StringComparison.Ordinal)) clean = clean[7..];
        clean = clean.Split('/')[^1].ToLowerInvariant();
        if (from.Siblings is not null)
            foreach (var s in from.Siblings)
                if (s.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)) return s;
        if (_loaded.TryGetValue(clean, out var f)) return f;
        _localFiles ??= Directory.EnumerateFiles(GameData, "*", SearchOption.AllDirectories)
            .Select(p => p.Replace('\\', '/'))
            .GroupBy(p => PyPath.Name(p).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p, StringComparer.Ordinal).First());
        if (_localFiles.TryGetValue(clean, out var local)) return LoadOne(local);
        throw new FileNotFoundException($"Failed to resolve pointer - {path} not found!");
    }

    /// <summary>PPtr（{m_FileID, m_PathID}）→ 物件</summary>
    public BuildObject Deref(BuildFile from, PyDict pptr)
    {
        long fid = Py.ToLong(pptr["m_FileID"]), pid = Py.ToLong(pptr["m_PathID"]);
        if (pid == 0) throw new InvalidOperationException("PPtr can't deref with m_PathID == 0!");
        var target = Target(from, fid);
        return target.Get(pid) ?? throw new KeyNotFoundException($"{target.Name}#{pid}");
    }

    public void Clear() => _loaded.Clear();
}
