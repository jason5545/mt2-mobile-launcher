// 部分移植自 TypeTreeGeneratorAPI 0.0.10（https://github.com/UnityPy-Org/TypeTreeGeneratorAPI）：模板轉節點、MonoCecilResolver，MIT，Copyright (c) 2025-2025 K0lb3；授權全文見 THIRD_PARTY_NOTICES.md。
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mono.Cecil;

namespace Mt2Mobile.Importer.Serialized;

/// <summary>
/// 從 Managed/*.dll 產生 MonoBehaviour 的欄位結構，等同 Python 版用的 TypeTreeGeneratorAPI 0.0.10
///（UnityPy-Org/TypeTreeGeneratorAPI df79bd6a，MIT）預設的 AssetsTools 後端：
/// AssetsTools.NET.MonoCecil 1.0.1 的 MonoCecilTempGenerator（MIT）讀欄位，模板欄位深度優先轉成節點（IsAligned → 0x4000），
/// 再把 MonoBehaviour 的根節點（m_GameObject、m_Enabled、m_Script、m_Name）接在前面。
/// 生成器跟 Unity 實際序列化的三個差異不在這裡修，在 Supplement 照 supplement.py 的 _fix 修。
/// </summary>
public sealed class MonoTypeTrees : IDisposable
{
    private readonly MonoCecilTempGenerator _gen = new("");
    private readonly Resolver _resolver = new();
    private readonly UnityVersion _version;
    private readonly bool _pathIdIs64;

    public MonoTypeTrees(string unityVersion)
    {
        _version = new UnityVersion(unityVersion);
        _pathIdIs64 = AssetRipper.Primitives.UnityVersion.Parse(unityVersion).Major >= 5;
    }

    /// <summary>TypeTreeGenerator.load_dll</summary>
    public void LoadDll(byte[] dll)
    {
        var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(dll), _resolver.ReaderParameters);
        _resolver.Register(asm);
        _gen.loadedAssemblies.Add(asm.MainModule.Name, asm);
    }

    /// <summary>TypeTreeGenerator.get_nodes(assembly, fullname)：找不到型別時丟例外（Python 版是 AssertionError）</summary>
    public List<(string type, string name, int level, int metaFlag)> GetNodes(string assembly, string fullName)
    {
        if (!assembly.EndsWith(".dll", StringComparison.Ordinal)) assembly += ".dll";
        int lastDot = fullName.LastIndexOf('.');
        string ns = lastDot == -1 ? "" : fullName[..lastDot];
        string cls = lastDot == -1 ? fullName : fullName[(lastDot + 1)..];
        if (!_gen.loadedAssemblies.TryGetValue(assembly, out var asm))
            throw new KeyNotFoundException($"'{assembly}'");
        var fields = _gen.Read(asm, ns, cls, _version) ?? throw new InvalidOperationException("failed to dump nodes raw");
        var root = new AssetTypeTemplateField { Name = "Base", Type = cls, ValueType = AssetValueType.None, IsArray = false, IsAligned = false, HasValue = false, Children = [] };
        root.Children.AddRange(fields);

        var nodes = new List<(string, string, int, int)>();
        var stack = new Stack<(AssetTypeTemplateField field, int level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (field, level) = stack.Pop();
            nodes.Add((field.Type, field.Name, level, field.IsAligned ? 0x4000 : 0));
            for (int i = field.Children.Count - 1; i >= 0; i--) stack.Push((field.Children[i], level + 1));
        }

        // NativeAPI 的 GenerateTypeTreeNodes：MonoBehaviour 的根節點放前面，生成器的 head 換掉根節點的 head
        string pathIdType = _pathIdIs64 ? "SInt64" : "int";
        var withRoot = new List<(string, string, int, int)>
        {
            ("MonoBehaviour", "Base", 0, 0),
            ("PPtr<GameObject>", "m_GameObject", 1, 0x4000),
            ("int", "m_FileID", 2, 0),
            (pathIdType, "m_PathID", 2, 0),
            ("UInt8", "m_Enabled", 1, 0),
            ("PPtr<MonoScript>", "m_Script", 1, 0x4000),
            ("int", "m_FileID", 2, 0),
            (pathIdType, "m_PathID", 2, 0),
            ("string", "m_Name", 1, 0x4000),
        };
        if (nodes.Count == 0) return withRoot;
        if (nodes[0].Item3 == 0)
        {
            withRoot[0] = nodes[0];
            nodes.RemoveAt(0);
        }
        withRoot.AddRange(nodes);
        return withRoot;
    }

    public void Dispose() => _gen.Dispose();

    /// <summary>TypeTreeGeneratorAPI 的 MonoCecilResolver：先找已載入的組件（照名稱，不看完整名稱）</summary>
    private sealed class Resolver : BaseAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _cache = [];
        public ReaderParameters ReaderParameters { get; }

        public Resolver() => ReaderParameters = new ReaderParameters { InMemory = true, ReadWrite = false, AssemblyResolver = this };

        public void Register(AssemblyDefinition asm)
        {
            if (!_cache.ContainsKey(asm.Name.Name)) _cache[asm.Name.Name] = asm;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            if (_cache.TryGetValue(name.Name, out var asm) && asm is not null) return asm;
            asm = base.Resolve(name);
            _cache[name.FullName] = asm;
            return asm;
        }
    }
}
