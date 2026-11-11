// 把 Unity 原版的 managed DLL 改成能脫離 Unity 原生引擎執行：
//   一、UnityEngine.* 裡的 internal call（extern + InternalCall、DllImport "__Internal"）改成一般方法，
//       方法本體只做一件事：呼叫 UnityNative.Icalls.<型別>::<同名方法>（實例方法把 this 當第一個參數）。
//       UnityNative.dll 沒有對應的方法時，第一次呼叫丟 MissingMethodException，訊息就是缺的那個方法。
//       帶 --hand（只有手寫部分的 UnityNative.dll）時，沒有手寫實作的，本體直接寫成替身（Stubs.cs），清單寫在 stubs.txt。
//   二、UnityEngine.* 的型別、欄位、非虛擬方法全部改成 public，原生層（UnityNative）才碰得到 m_CachedPtr 這類內部成員。
//       原本不是 public 的欄位記在 nonpublic-fields.txt，序列化照 Unity 的規則判斷時用得到。
//   三、redirects.txt 列的 managed 方法（遊戲或套件的），本體改成呼叫 UnityNative.Overrides.<型別>::<同名方法>。
// 這個做法不靠 Mono 的 embedding API（mono_add_internal_call），Mono、CoreCLR、NativeAOT 都能跑。
// 用法：dotnet run -- <Managed 資料夾> <輸出資料夾> [--hand UnityNative.dll] [redirects.txt…]
// 輸出的檔案內容沒變就不寫（時間戳記不動），每次 build 重新 patch，參考它們的專案也不會跟著重編。
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Text;

if (args.Length > 0 && args[0] == "inspect")
{
    var a = AssemblyDefinition.ReadAssembly(args[1]);
    foreach (var t in a.MainModule.GetTypes()) foreach (var mm in t.Methods)
        if (t.FullName == args[2] && mm.Name == args[3])
            Console.WriteLine($"{mm.FullName} attrs={mm.Attributes} impl={mm.ImplAttributes} rva={mm.RVA} hasBody={mm.HasBody} pinvoke={mm.IsPInvokeImpl} ins={(mm.HasBody ? mm.Body.Instructions.Count : -1)}");
    return;
}
var managed = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args[1]);
var rest = args.Skip(2).ToList();
Stubs stubs = null;
var handAt = rest.IndexOf("--hand");
if (handAt >= 0) { stubs = new Stubs(Path.GetFullPath(rest[handAt + 1])); rest.RemoveRange(handAt, 2); }
var redirectFiles = rest.ToArray();
Directory.CreateDirectory(outDir);

// BCL 由執行環境（.NET 的 Mono 或 CoreCLR）提供，不用 Unity 帶的那份
// Microsoft.Bcl.AsyncInterfaces 不算：.NET 的執行環境沒有這個組件（不在共用框架裡），原版那份要照帶，不然參考它的型別（AWSSDK 等）載不起來
static bool IsBcl(string n) => n is "mscorlib" or "netstandard" or "Accessibility" || n == "System" || n.StartsWith("System.") || n.StartsWith("Mono.");
static bool IsEngine(string n) => n == "UnityEngine" || (n.StartsWith("UnityEngine.") && n.EndsWith("Module"));

// redirects.txt 一行一個：<組件>|<型別>::<方法> => <目標組件>|<目標型別全名>（同名的多載全部轉；目標型別要有同名同參數的靜態方法）
var redirects = new Dictionary<string, (string asm, string type)>();
// after <組件>|<型別>::<方法> => <目標組件>|<目標型別全名>：原本的方法照跑，每個 return 之前先叫目標型別的同名靜態方法 (self)（實例方法才行；同名的多載都掛）
var afters = new Dictionary<string, (string asm, string type)>();
// uniquehash <組件>|<型別>：補一個 GetHashCode 覆寫，每個實例一個流水號（原版拿物件的預設雜湊當字典 key 的地方，CoreCLR 的雜湊會撞）
var uniqueHash = new HashSet<string>();
// native <函式庫>：這個原生函式庫手機與桌面都有對應的版本（例如 FMOD 的 fmodstudio），P/Invoke 照原樣留著、不換替身（宿主負責讓它載得到）
var keepNative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var redirectFile in redirectFiles)
    foreach (var line in File.ReadAllLines(redirectFile))
    {
        var l = line.Trim();
        if (l.Length == 0 || l.StartsWith("#")) continue;
        if (l.StartsWith("uniquehash ")) { uniqueHash.Add(l.Substring(11).Trim()); continue; }
        if (l.StartsWith("native ")) { keepNative.Add(l.Substring(7).Trim()); continue; }
        if (l.StartsWith("after "))
        {
            var ap = l.Substring(6).Split("=>", StringSplitOptions.TrimEntries);
            var at = ap[1].Split('|');
            afters[ap[0]] = (at[0], at[1]);
            continue;
        }
        var parts = l.Split("=>", StringSplitOptions.TrimEntries);
        var target = parts.Length > 1 ? parts[1].Split('|') : new[] { "UnityNative", "" };
        redirects[parts[0]] = (target[0], target.Length > 1 ? target[1] : "");
    }

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, ReadSymbols = false };

var nativeRef = new AssemblyNameReference("UnityNative", new Version(0, 0, 0, 0));
var icallList = new StringBuilder();
var nonPublic = new StringBuilder();
int nIcall = 0, nRedirect = 0, nAsm = 0, nPinvoke = 0;
var usedRedirects = new HashSet<string>();

foreach (var file in Directory.GetFiles(managed, "*.dll").OrderBy(f => f))
{
    AssemblyDefinition asm;
    try { asm = AssemblyDefinition.ReadAssembly(file, rp); }
    catch { continue; }
    var name = asm.Name.Name;
    if (IsBcl(name)) continue;
    bool engine = IsEngine(name);
    var module = asm.MainModule;
    bool touched = false;

    foreach (var type in module.GetTypes().ToList())
    {
        if (engine) touched |= Publicize(type, nonPublic);
        if (uniqueHash.Contains(name + "|" + type.FullName)) { AddUniqueHash(module, type); touched = true; usedRedirects.Add("uniquehash " + name + "|" + type.FullName); }
        foreach (var m in type.Methods.ToList())
        {
            bool icall = m.IsInternalCall || (m.IsPInvokeImpl && m.PInvokeInfo?.Module?.Name == "__Internal");
            if (icall && engine)
            {
                if (stubs == null || !stubs.TryWrite(module, type, m, Sanitize(type))) Redirect(module, type, m, "UnityNative.Icalls");
                icallList.AppendLine(name + "\t" + Signature(type, m));
                nIcall++; touched = true;
                continue;
            }
            // 遊戲與套件的原生外掛（user32、steam_api、XInput…）：手機上沒有，本體換成「記一筆、回預設值」
            if ((m.IsPInvokeImpl || m.IsInternalCall) && !engine && !m.IsRuntime)
            {
                var lib = m.IsInternalCall ? "icall" : m.PInvokeInfo?.Module?.Name;
                if (m.IsPInvokeImpl && lib != null && keepNative.Contains(Path.GetFileNameWithoutExtension(lib)))
                {
                    icallList.AppendLine(name + "\t[native] " + type.FullName + "::" + m.Name + " → " + lib);
                    continue;
                }
                StubBody(module, type, m, lib);
                icallList.AppendLine(name + "\t[pinvoke] " + type.FullName + "::" + m.Name + " → " + lib);
                nPinvoke++; touched = true;
                continue;
            }
            var key = name + "|" + type.FullName + "::" + m.Name;
            if (afters.TryGetValue(key, out var af) && m.HasBody && m.HasThis)
            {
                var dot = af.type.LastIndexOf('.');
                AppendAfter(module, type, m, af.type[..dot], af.asm, af.type[(dot + 1)..]);
                icallList.AppendLine(name + "\t[after] " + Signature(type, m));
                usedRedirects.Add("after " + key);
                nRedirect++; touched = true;
            }
            if (redirects.TryGetValue(key, out var rt) && m.HasBody)
            {
                var dot = rt.type.LastIndexOf('.');
                Redirect(module, type, m, dot < 0 ? "UnityNative.Overrides" : rt.type[..dot], rt.asm, dot < 0 ? null : rt.type[(dot + 1)..]);
                icallList.AppendLine(name + "\t[redirect] " + Signature(type, m));
                usedRedirects.Add(key);
                nRedirect++; touched = true;
            }
        }
    }
    var dst = Path.Combine(outDir, Path.GetFileName(file));
    if (touched)
    {
        using var ms = new MemoryStream();
        asm.Write(ms);
        WriteIfChanged(dst, ms.ToArray());
        nAsm++;
    }
    else WriteIfChanged(dst, File.ReadAllBytes(file));
}

WriteIfChanged(Path.Combine(outDir, "icalls.txt"), Encoding.UTF8.GetBytes(icallList.ToString()));
WriteIfChanged(Path.Combine(outDir, "nonpublic-fields.txt"), Encoding.UTF8.GetBytes(nonPublic.ToString()));
var stubsTxt = Path.Combine(outDir, "stubs.txt");
if (stubs != null) WriteIfChanged(stubsTxt, Encoding.UTF8.GetBytes(stubs.List));
else if (File.Exists(stubsTxt)) File.Delete(stubsTxt);
foreach (var r in uniqueHash.Select(u => "uniquehash " + u).Except(usedRedirects)) Console.Error.WriteLine("uniquehash 沒對到：" + r);
foreach (var r in redirects.Keys.Except(usedRedirects)) Console.Error.WriteLine("redirect 沒對到：" + r);
foreach (var r in afters.Keys.Select(k => "after " + k).Except(usedRedirects)) Console.Error.WriteLine("after 沒對到：" + r);
Console.WriteLine($"改寫 {nAsm} 個組件：internal call {nIcall} 個{(stubs != null ? $"（替身 {stubs.Count} 個，其中 {stubs.Shared} 個跟前面同名同參數）" : "")}，原生外掛 {nPinvoke} 個，redirect {nRedirect} 個 → {outDir}");

static void WriteIfChanged(string path, byte[] bytes)
{
    if (File.Exists(path) && new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
    File.WriteAllBytes(path, bytes);
}

// 型別、欄位、非虛擬方法改成 public。虛擬方法不動：子類別（遊戲碼）的 override 不能比基底的可見度低
static bool Publicize(TypeDefinition t, StringBuilder nonPublic)
{
    bool changed = false;
    if (t.IsNested) { if (!t.IsNestedPublic) { t.IsNestedPublic = true; changed = true; } }
    else if (!t.IsPublic) { t.IsPublic = true; changed = true; }
    foreach (var f in t.Fields)
        if (!f.IsPublic)
        {
            if (!f.IsStatic && !f.IsLiteral) nonPublic.Append(t.FullName).Append("::").AppendLine(f.Name);
            f.IsPublic = true; changed = true;
        }
    foreach (var m in t.Methods)
        if (!m.IsPublic && !m.IsVirtual) { m.IsPublic = true; changed = true; }
    return changed;
}

static string Sanitize(TypeDefinition t)
{
    var full = t.FullName.Replace('/', '+');
    var sb = new StringBuilder();
    foreach (var c in full) sb.Append(c switch { '.' => '_', '+' => '_', '`' => '_', _ => c });
    // 巢狀型別用雙底線分開，跟命名空間的點區分
    return t.IsNested ? Sanitize(t.DeclaringType) + "__" + Clean(t.Name) : sb.ToString();
    static string Clean(string s) => s.Replace('`', '_');
}

// 方法本體換成：把參數原樣交給 <ns>.<Sanitize(型別)>::<方法名> 的靜態方法
static void Redirect(ModuleDefinition module, TypeDefinition type, MethodDefinition m, string ns, string targetAsm = "UnityNative", string targetType = null)
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

    if (type.HasGenericParameters || m.HasGenericParameters)
    {
        // 泛型的 internal call 很少，先丟例外，用到再補
        var exCtor = module.ImportReference(typeof(MissingMethodException).GetConstructor(new[] { typeof(string) }));
        il.Emit(OpCodes.Ldstr, "UnityNative：泛型 internal call 沒實作：" + type.FullName + "::" + m.Name);
        il.Emit(OpCodes.Newobj, exCtor);
        il.Emit(OpCodes.Throw);
        return;
    }

    if (!module.AssemblyReferences.Any(r => r.Name == targetAsm))
        module.AssemblyReferences.Add(new AssemblyNameReference(targetAsm, new Version(0, 0, 0, 0)));
    var scope = module.AssemblyReferences.First(r => r.Name == targetAsm);
    var target = new TypeReference(ns, targetType ?? Sanitize(type), module, scope);
    var call = new MethodReference(m.Name, m.ReturnType, target) { HasThis = false };
    if (m.HasThis)
    {
        TypeReference self = type;
        if (type.IsValueType) self = new ByReferenceType(type);
        call.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, self));
    }
    foreach (var p in m.Parameters) call.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, p.ParameterType));

    int n = (m.HasThis ? 1 : 0) + m.Parameters.Count;
    for (int i = 0; i < n; i++)
    {
        if (i == 0) il.Emit(OpCodes.Ldarg_0);
        else if (i == 1) il.Emit(OpCodes.Ldarg_1);
        else if (i == 2) il.Emit(OpCodes.Ldarg_2);
        else if (i == 3) il.Emit(OpCodes.Ldarg_3);
        else il.Emit(OpCodes.Ldarg_S, m.Parameters[m.HasThis ? i - 1 : i]);
    }
    il.Emit(OpCodes.Call, call);
    il.Emit(OpCodes.Ret);
}

// 每個 ret 改成「ldarg.0；call 目標(self)；ret」：原本的 ret 指令物件換成 ldarg.0（跳到 ret 的分支、例外區塊的邊界都還指著它），
// 回傳值留在堆疊底下不動
static void AppendAfter(ModuleDefinition module, TypeDefinition type, MethodDefinition m, string ns, string targetAsm, string targetType)
{
    if (!module.AssemblyReferences.Any(r => r.Name == targetAsm))
        module.AssemblyReferences.Add(new AssemblyNameReference(targetAsm, new Version(0, 0, 0, 0)));
    var scope = module.AssemblyReferences.First(r => r.Name == targetAsm);
    var target = new TypeReference(ns, targetType, module, scope);
    var call = new MethodReference(m.Name, module.TypeSystem.Void, target) { HasThis = false };
    TypeReference self = type;
    if (type.IsValueType) self = new ByReferenceType(type);
    call.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, self));
    m.Body.SimplifyMacros(); // 短跳躍先展開，插了指令之後才不會超出範圍
    var il = m.Body.GetILProcessor();
    foreach (var ret in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
    {
        ret.OpCode = OpCodes.Ldarg_0;
        ret.Operand = null;
        var c = il.Create(OpCodes.Call, call);
        il.InsertAfter(ret, c);
        il.InsertAfter(c, il.Create(OpCodes.Ret));
    }
    m.Body.OptimizeMacros();
}

// GetHashCode 覆寫：第一次被要的時候從全域計數器拿一個號碼，存在新加的欄位
static void AddUniqueHash(ModuleDefinition module, TypeDefinition type)
{
    var i32 = module.TypeSystem.Int32;
    var field = new FieldDefinition("__unityNativeHash", FieldAttributes.Private, i32);
    type.Fields.Add(field);
    var counter = new FieldDefinition("__unityNativeHashNext", FieldAttributes.Private | FieldAttributes.Static, i32);
    type.Fields.Add(counter);
    var m = new MethodDefinition("GetHashCode", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, i32);
    var il = m.Body.GetILProcessor();
    var done = il.Create(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldfld, field);
    il.Emit(OpCodes.Brtrue_S, done);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldsflda, counter);
    var inc = module.ImportReference(typeof(System.Threading.Interlocked).GetMethod("Increment", new[] { typeof(int).MakeByRefType() }));
    il.Emit(OpCodes.Call, inc);
    il.Emit(OpCodes.Stfld, field);
    il.Append(done);
    il.Emit(OpCodes.Ldfld, field);
    il.Emit(OpCodes.Ret);
    type.Methods.Add(m);
}

// 原生外掛的替身：out 參數清成預設、呼叫 UnityNative.Engine.Stub(名稱)、回傳預設值（直接寫 IL，參數用到 internal 型別也沒關係）
static void StubBody(ModuleDefinition module, TypeDefinition type, MethodDefinition m, string lib)
{
    m.PInvokeInfo = null;
    m.IsPInvokeImpl = false;
    m.IsInternalCall = false;
    m.IsManaged = true;
    m.IsIL = true;
    m.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
    var body = new MethodBody(m);
    m.Body = body;
    var il = body.GetILProcessor();
    foreach (var p in m.Parameters)
        if (p.IsOut && p.ParameterType is ByReferenceType br)
        {
            il.Emit(OpCodes.Ldarg, p);
            il.Emit(OpCodes.Initobj, br.ElementType);
        }
    if (!module.AssemblyReferences.Any(r => r.Name == "UnityNative"))
        module.AssemblyReferences.Add(new AssemblyNameReference("UnityNative", new Version(0, 0, 0, 0)));
    var scope = module.AssemblyReferences.First(r => r.Name == "UnityNative");
    var engine = new TypeReference("UnityNative", "Engine", module, scope);
    var stub = new MethodReference("Stub", module.TypeSystem.Void, engine) { HasThis = false };
    stub.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
    il.Emit(OpCodes.Ldstr, "[pinvoke " + lib + "] " + type.FullName + "::" + m.Name);
    il.Emit(OpCodes.Call, stub);
    if (m.ReturnType.FullName != "System.Void")
    {
        var v = new VariableDefinition(m.ReturnType);
        body.Variables.Add(v);
        body.InitLocals = true;
        il.Emit(OpCodes.Ldloca, v);
        il.Emit(OpCodes.Initobj, m.ReturnType);
        il.Emit(OpCodes.Ldloc, v);
    }
    il.Emit(OpCodes.Ret);
}

// C# 寫實作時照抄用的簽名
static string Signature(TypeDefinition type, MethodDefinition m)
{
    var ps = new List<string>();
    if (m.HasThis) ps.Add((type.IsValueType ? "ref " : "") + Cs(type) + " self");
    foreach (var p in m.Parameters)
    {
        var t = p.ParameterType;
        string mod = "";
        if (t is ByReferenceType br) { mod = p.IsOut ? "out " : "ref "; t = br.ElementType; }
        ps.Add(mod + Cs(t) + " " + (p.Name.Length > 0 ? p.Name : "a" + p.Index));
    }
    return Sanitize(type) + "\tpublic static " + Cs(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", ps) + ")";
}

static string Cs(TypeReference t)
{
    switch (t.FullName)
    {
        case "System.Void": return "void";
        case "System.Boolean": return "bool";
        case "System.Int32": return "int";
        case "System.UInt32": return "uint";
        case "System.Int64": return "long";
        case "System.UInt64": return "ulong";
        case "System.Int16": return "short";
        case "System.UInt16": return "ushort";
        case "System.Byte": return "byte";
        case "System.SByte": return "sbyte";
        case "System.Single": return "float";
        case "System.Double": return "double";
        case "System.String": return "string";
        case "System.Object": return "object";
        case "System.Char": return "char";
        case "System.IntPtr": return "IntPtr";
        case "System.UIntPtr": return "UIntPtr";
    }
    if (t is ArrayType at) return Cs(at.ElementType) + "[]";
    if (t is PointerType pt) return Cs(pt.ElementType) + "*";
    if (t is ByReferenceType bt) return "ref " + Cs(bt.ElementType);
    if (t is GenericInstanceType gi)
    {
        var baseName = Cs(gi.ElementType);
        var tick = baseName.IndexOf('`');
        if (tick >= 0) baseName = baseName[..tick];
        return baseName + "<" + string.Join(", ", gi.GenericArguments.Select(Cs)) + ">";
    }
    return t.FullName.Replace('/', '.');
}
