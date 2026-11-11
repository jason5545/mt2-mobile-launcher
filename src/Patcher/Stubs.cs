// 替身：patch 時帶 --hand（只有手寫部分的 UnityNative.dll），UnityEngine.* 的 internal call 沒有手寫實作的，方法本體直接寫成 IL，
// 不轉到 UnityNative、也不產生 C#（UnityNative.dll 只放手寫的部分，在開發機編好；替身跟著原版 DLL 在 patch 時產生）：
//   static 的 get_X／set_X：存在 StubStore 的字典；實例的 get_X／set_X（UnityEngine.Object 的子類別）：存在原生物件的屬性袋（NObject.Props）；
//   其他方法：什麼都不做、回預設值（out 參數給預設；一維陣列回空陣列）。
//   每個替身被呼叫時記到 Engine.Stub（報告列出實際碰到哪些）。
// 「有手寫實作」：UnityNative.Icalls.<型別> 有同名、同參數型別的 public static 方法（實例方法的第一個參數是 self），跟轉過去的目標一樣。
// 不寫替身、照舊轉過去（呼叫到時丟 MissingMethodException）的：泛型、簽名用到 [Obsolete(error: true)] 的型別、回傳或參數是指標／by-ref 而不能當泛型參數的屬性。
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text;

sealed class Stubs
{
    enum Kind { None, Default, GetStatic, GetSelf, SetStatic, SetSelf }

    // 同一個 key（Sanitize 後的型別名撞名）只照第一個寫：舊的 C# 產生器同名同參數只產生一個替身，後面的方法都呼叫它
    sealed record Spec(string Full, Kind Kind, string Store);

    readonly HashSet<string> have = new();
    readonly Dictionary<string, Spec> specs = new();
    readonly StringBuilder list = new();
    public int Count, Shared;

    public Stubs(string handDll)
    {
        using var hand = AssemblyDefinition.ReadAssembly(handDll);
        foreach (var t in hand.MainModule.GetTypes())
        {
            if (t.Namespace != "UnityNative.Icalls") continue;
            foreach (var m in t.Methods)
                if (m.IsStatic && m.IsPublic) have.Add(t.Name + "::" + m.Name + "(" + string.Join(",", m.Parameters.Select(p => Norm(p.ParameterType))) + ")");
        }
    }

    public string List => list.ToString();

    // sanitized：轉過去的目標型別名（Program.cs 的 Sanitize）。寫了替身回 true；false 就照舊 Redirect
    public bool TryWrite(ModuleDefinition module, TypeDefinition type, MethodDefinition m, string sanitized)
    {
        if (type.HasGenericParameters || m.HasGenericParameters) return false;
        var ps = new List<TypeReference>();
        if (m.HasThis) ps.Add(type.IsValueType ? new ByReferenceType(type) : type);
        ps.AddRange(m.Parameters.Select(p => p.ParameterType));
        var key = sanitized + "::" + m.Name + "(" + string.Join(",", ps.Select(Norm)) + ")";
        if (have.Contains(key)) return false;
        if (UsesErrorObsolete(m.ReturnType) || ps.Any(UsesErrorObsolete)) return false;
        if (m.ReturnType is ByReferenceType) return false;
        if (specs.TryGetValue(key, out var spec)) Shared++;
        else
        {
            spec = SpecOf(type, m, ps.Count);
            if (spec == null) return false;
            specs[key] = spec;
        }
        Emit(module, type, m, spec);
        list.Append(module.Assembly.Name.Name).Append('\t').Append(key).Append('\t').Append(spec.Kind);
        if (spec.Store != null) list.Append(' ').Append(spec.Store);
        list.Append('\t').AppendLine(spec.Full);
        Count++;
        return true;
    }

    static Spec SpecOf(TypeDefinition owner, MethodDefinition m, int n)
    {
        var full = owner.FullName + "::" + m.Name;
        bool isStatic = !m.HasThis, isVoid = m.ReturnType.FullName == "System.Void";
        var prop = m.Name.StartsWith("get_") || m.Name.StartsWith("set_") ? m.Name.Substring(4) : null;
        bool selfIsObject = !isStatic && IsUnityObject(owner);
        if (prop != null && m.Name.StartsWith("get_") && !isVoid && n == (isStatic ? 0 : 1))
        {
            if (!isStatic && !selfIsObject) return new Spec(full, Kind.Default, null);
            if (!CanBeTypeArg(m.ReturnType)) return null;
            return isStatic ? new Spec(full, Kind.GetStatic, owner.FullName + "." + prop) : new Spec(full, Kind.GetSelf, prop);
        }
        if (prop != null && m.Name.StartsWith("set_") && isVoid && n == (isStatic ? 1 : 2))
        {
            if (!isStatic && !selfIsObject) return new Spec(full, Kind.None, null);
            if (!CanBeTypeArg(ValueType(m))) return null;
            return isStatic ? new Spec(full, Kind.SetStatic, owner.FullName + "." + prop) : new Spec(full, Kind.SetSelf, prop);
        }
        return new Spec(full, isVoid ? Kind.None : Kind.Default, null);
    }

    // Engine.Stub(名稱)；out 參數給預設；再照種類讀寫 StubStore 或回預設值
    static void Emit(ModuleDefinition module, TypeDefinition type, MethodDefinition m, Spec spec)
    {
        m.IsInternalCall = false;
        m.IsRuntime = false;
        m.IsManaged = true;
        m.IsIL = true;
        // Cecil：設 PInvokeInfo 會順手把 IsPInvokeImpl 設回 true，順序不能反
        if (m.IsPInvokeImpl) { m.PInvokeInfo = null; m.IsPInvokeImpl = false; }
        m.ImplAttributes &= ~(MethodImplAttributes.PreserveSig | MethodImplAttributes.InternalCall);
        var body = new MethodBody(m);
        m.Body = body;
        var il = body.GetILProcessor();
        var scope = NativeScope(module);

        var engine = new TypeReference("UnityNative", "Engine", module, scope);
        var stub = new MethodReference("Stub", module.TypeSystem.Void, engine) { HasThis = false };
        stub.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        il.Emit(OpCodes.Ldstr, spec.Full);
        il.Emit(OpCodes.Call, stub);
        foreach (var p in m.Parameters)
            if (p.IsOut && p.ParameterType is ByReferenceType br)
            {
                il.Emit(OpCodes.Ldarg, p);
                if (br.ElementType is PointerType or FunctionPointerType) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Stind_I); }
                else il.Emit(OpCodes.Initobj, br.ElementType);
            }

        switch (spec.Kind)
        {
            case Kind.GetStatic:
                il.Emit(OpCodes.Ldstr, spec.Store);
                il.Emit(OpCodes.Call, Store(module, type, scope, "Get", m.ReturnType, self: false, set: false));
                break;
            case Kind.GetSelf:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldstr, spec.Store);
                il.Emit(OpCodes.Call, Store(module, type, scope, "Get", m.ReturnType, self: true, set: false));
                break;
            case Kind.SetStatic:
                il.Emit(OpCodes.Ldstr, spec.Store);
                LoadValue(il, m);
                il.Emit(OpCodes.Call, Store(module, type, scope, "Set", ValueType(m), self: false, set: true));
                break;
            case Kind.SetSelf:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldstr, spec.Store);
                LoadValue(il, m);
                il.Emit(OpCodes.Call, Store(module, type, scope, "Set", ValueType(m), self: true, set: true));
                break;
            case Kind.Default:
                Default(module, body, il, m.ReturnType);
                break;
        }
        il.Emit(OpCodes.Ret);
    }

    // set_X 的值：ref 參數存它指到的值（C# 的 StubStore.Set(..., value) 遇到 ref 參數也是讀值）
    static TypeReference ValueType(MethodDefinition m) => m.Parameters[0].ParameterType is ByReferenceType br ? br.ElementType : m.Parameters[0].ParameterType;

    static void LoadValue(ILProcessor il, MethodDefinition m)
    {
        il.Emit(OpCodes.Ldarg, m.Parameters[0]);
        if (m.Parameters[0].ParameterType is ByReferenceType br) il.Emit(OpCodes.Ldobj, br.ElementType);
    }

    // 原生端回傳陣列的 API 幾乎不會回 null（查不到就回空陣列），替身照這個規則，免得呼叫端直接取 .Length 就 NRE
    static void Default(ModuleDefinition module, MethodBody body, ILProcessor il, TypeReference t)
    {
        if (t is ArrayType at && at.Rank == 1)
        {
            var array = new TypeReference("System", "Array", module, module.TypeSystem.CoreLibrary);
            var empty = new MethodReference("Empty", module.TypeSystem.Void, array) { HasThis = false };
            var g = new GenericParameter("T", empty);
            empty.GenericParameters.Add(g);
            empty.ReturnType = new ArrayType(g);
            var gi = new GenericInstanceMethod(empty);
            gi.GenericArguments.Add(at.ElementType);
            il.Emit(OpCodes.Call, gi);
            return;
        }
        if (t is PointerType or FunctionPointerType) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U); return; }
        var v = new VariableDefinition(t);
        body.Variables.Add(v);
        body.InitLocals = true;
        il.Emit(OpCodes.Ldloca, v);
        il.Emit(OpCodes.Initobj, t);
        il.Emit(OpCodes.Ldloc, v);
    }

    // UnityNative.StubStore 的 Get<T>(string)、Get<T>(UnityEngine.Object, string)、Set<T>(string, T)、Set<T>(UnityEngine.Object, string, T)
    static MethodReference Store(ModuleDefinition module, TypeDefinition owner, AssemblyNameReference scope, string name, TypeReference arg, bool self, bool set)
    {
        var store = new TypeReference("UnityNative", "StubStore", module, scope);
        var mr = new MethodReference(name, module.TypeSystem.Void, store) { HasThis = false };
        var t = new GenericParameter("T", mr);
        mr.GenericParameters.Add(t);
        if (!set) mr.ReturnType = t;
        if (self) mr.Parameters.Add(new ParameterDefinition(UnityObject(module, owner)));
        mr.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        if (set) mr.Parameters.Add(new ParameterDefinition(t));
        var gi = new GenericInstanceMethod(mr);
        gi.GenericArguments.Add(arg);
        return gi;
    }

    static AssemblyNameReference NativeScope(ModuleDefinition module)
    {
        if (!module.AssemblyReferences.Any(r => r.Name == "UnityNative"))
            module.AssemblyReferences.Add(new AssemblyNameReference("UnityNative", new Version(0, 0, 0, 0)));
        return module.AssemblyReferences.First(r => r.Name == "UnityNative");
    }

    static TypeReference UnityObject(ModuleDefinition module, TypeDefinition owner)
    {
        for (TypeDefinition c = owner; c != null; c = c.BaseType?.Resolve())
            if (c.FullName == "UnityEngine.Object") return c.Module == module ? c : module.ImportReference(c);
        throw new InvalidOperationException("不是 UnityEngine.Object 的子類別：" + owner.FullName);
    }

    // C# 的泛型參數不能是指標、by-ref、ref struct（舊的 C# 替身遇到會編不過，所以不會有）
    static bool CanBeTypeArg(TypeReference t)
    {
        if (t is PointerType or ByReferenceType or FunctionPointerType or PinnedType) return false;
        TypeDefinition d;
        try { d = t.Resolve(); } catch { return true; }
        return d == null || !d.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");
    }

    static string Norm(TypeReference t) => t.FullName.Replace('/', '+');

    static bool UsesErrorObsolete(TypeReference t)
    {
        while (t is TypeSpecification ts) t = ts.ElementType;
        TypeDefinition d;
        try { d = t.Resolve(); } catch { return false; }
        if (d == null) return false;
        foreach (var a in d.CustomAttributes)
            if (a.AttributeType.FullName == "System.ObsoleteAttribute" && a.ConstructorArguments.Count == 2 && a.ConstructorArguments[1].Value is bool b && b) return true;
        return false;
    }

    static bool IsUnityObject(TypeDefinition t)
    {
        for (var c = t; c != null; )
        {
            if (c.FullName == "UnityEngine.Object") return true;
            if (c.BaseType == null) return false;
            try { c = c.BaseType.Resolve(); } catch { return false; }
        }
        return false;
    }
}
