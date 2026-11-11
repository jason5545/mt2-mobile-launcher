// ToGlslEs300 設選項與呼叫的順序照 SPIRV-Cross vulkan-sdk-1.4.363.0（https://github.com/KhronosGroup/SPIRV-Cross）main.cpp 的 compile_iteration，Apache-2.0；授權全文見 THIRD_PARTY_NOTICES.md。
using System.Runtime.InteropServices;
using System.Text;

namespace Mt2Mobile.Shaders;

/// <summary>
/// 原生工具：dxbc-spirv（native/dxbc-spirv 的 C 介面 libdxbcspv）與 SPIRV-Cross 的 C API（libspirv-cross-c-shared）。
/// 呼叫的順序與選項照命令列工具（docs/phase3-shader.md 第 0 步第 3 項），結果跟命令列版逐位元組相同。
/// <para>
/// 不用 DllImport／LibraryImport，改成函式指標（docs/phase4.md「shaders 偶發崩潰：P/Invoke 第一次綁定」）。
/// DllImport 的進入點在第一次呼叫時才綁定，.NET for Android 10 的 CoreCLR 宿主綁定時寫的快取（PinvokeOverride 的 tsl::robin_map）沒有上鎖；
/// Release 的 ReadyToRun 預編了這些 blittable 的 P/Invoke，Marshal.Prelink 也不會先綁。ShaderConvert 平行轉換一開始，
/// 好幾個執行緒同時第一次呼叫，同時改那張表，弄壞 heap。函式指標在型別初始化時一次拿齊（CLR 保證靜態建構式只有一個執行緒跑，
/// 其他執行緒等它做完），之後每次都是直接呼叫，執行期不再經過綁定，也不寫任何共用的狀態。
/// </para>
/// </summary>
internal static unsafe class Native
{
    private const string Dxbc = "dxbcspv";
    private const string Spvc = "spirv-cross-c-shared";

    /// <summary>原生函式的位址。函式庫的名稱照 DllImport 的找法（NativeLibrary.Load 帶組件）：Android 是 lib&lt;名稱&gt;.so，macOS 是 lib&lt;名稱&gt;.dylib</summary>
    private static class Fn
    {
        public static readonly nint DxbcCompile, DxbcDisasm, DxbcFree;
        public static readonly nint ContextCreate, ContextDestroy, ContextGetLastError, ParseSpirv, CreateCompiler, CreateOptions, OptionsSetBool, OptionsSetUint,
            InstallOptions, BuildDummySampler, SetDecoration, CreateShaderResources, BuildCombinedImageSamplers, GetCombinedImageSamplers, GetName, SetName, Compile;

        static Fn()
        {
            var asm = typeof(Native).Assembly;
            nint d = NativeLibrary.Load(Dxbc, asm, null), s = NativeLibrary.Load(Spvc, asm, null);
            DxbcCompile = NativeLibrary.GetExport(d, "dxbcspv_compile");
            DxbcDisasm = NativeLibrary.GetExport(d, "dxbcspv_disasm");
            DxbcFree = NativeLibrary.GetExport(d, "dxbcspv_free");
            ContextCreate = NativeLibrary.GetExport(s, "spvc_context_create");
            ContextDestroy = NativeLibrary.GetExport(s, "spvc_context_destroy");
            ContextGetLastError = NativeLibrary.GetExport(s, "spvc_context_get_last_error_string");
            ParseSpirv = NativeLibrary.GetExport(s, "spvc_context_parse_spirv");
            CreateCompiler = NativeLibrary.GetExport(s, "spvc_context_create_compiler");
            CreateOptions = NativeLibrary.GetExport(s, "spvc_compiler_create_compiler_options");
            OptionsSetBool = NativeLibrary.GetExport(s, "spvc_compiler_options_set_bool");
            OptionsSetUint = NativeLibrary.GetExport(s, "spvc_compiler_options_set_uint");
            InstallOptions = NativeLibrary.GetExport(s, "spvc_compiler_install_compiler_options");
            BuildDummySampler = NativeLibrary.GetExport(s, "spvc_compiler_build_dummy_sampler_for_combined_images");
            SetDecoration = NativeLibrary.GetExport(s, "spvc_compiler_set_decoration");
            CreateShaderResources = NativeLibrary.GetExport(s, "spvc_compiler_create_shader_resources");
            BuildCombinedImageSamplers = NativeLibrary.GetExport(s, "spvc_compiler_build_combined_image_samplers");
            GetCombinedImageSamplers = NativeLibrary.GetExport(s, "spvc_compiler_get_combined_image_samplers");
            GetName = NativeLibrary.GetExport(s, "spvc_compiler_get_name");
            SetName = NativeLibrary.GetExport(s, "spvc_compiler_set_name");
            Compile = NativeLibrary.GetExport(s, "spvc_compiler_compile");
        }
    }

    private static int DxbcCompile(byte* data, nuint size, uint flags, out IntPtr spirv, out nuint spirvSize, out IntPtr error)
    {
        IntPtr o, e; nuint n;
        int rc = ((delegate* unmanaged<byte*, nuint, uint, IntPtr*, nuint*, IntPtr*, int>)Fn.DxbcCompile)(data, size, flags, &o, &n, &e);
        (spirv, spirvSize, error) = (o, n, e);
        return rc;
    }

    private static int DxbcDisasm(byte* data, nuint size, out IntPtr text, out IntPtr error)
    {
        IntPtr t, e;
        int rc = ((delegate* unmanaged<byte*, nuint, IntPtr*, IntPtr*, int>)Fn.DxbcDisasm)(data, size, &t, &e);
        (text, error) = (t, e);
        return rc;
    }

    private static void DxbcFree(IntPtr ptr) => ((delegate* unmanaged<IntPtr, void>)Fn.DxbcFree)(ptr);

    public sealed class ToolError(string message) : Exception(message);

    private static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        string s = Marshal.PtrToStringUTF8(p) ?? "";
        DxbcFree(p);
        return s;
    }

    /// <summary>dxbc_disasm &lt;dxbc&gt; 的 stdout；失敗丟 ToolError</summary>
    public static string Disassemble(byte[] dxbc)
    {
        fixed (byte* p = dxbc)
        {
            int rc = DxbcDisasm(p, (nuint)dxbc.Length, out var text, out var error);
            if (rc != 0) throw new ToolError($"dxbc_disasm: {TakeString(error).Trim()}");
            return TakeString(text);
        }
    }

    /// <summary>dxbc_compiler &lt;dxbc&gt; --spv &lt;out&gt; --cbv-array</summary>
    public static byte[] CompileCbvArray(byte[] dxbc)
    {
        fixed (byte* p = dxbc)
        {
            int rc = DxbcCompile(p, (nuint)dxbc.Length, 1u, out var spirv, out var size, out var error);
            if (rc != 0) throw new ToolError($"dxbc_compiler: {TakeString(error).Trim()}");
            var o = new byte[(int)size];
            Marshal.Copy(spirv, o, 0, o.Length);
            DxbcFree(spirv);
            return o;
        }
    }

    // ───────── SPIRV-Cross C API（spirv_cross_c.h） ─────────

    private const int SpvcSuccess = 0;
    private const int BackendGlsl = 1;
    private const int BackendJson = 5;
    private const int CaptureTakeOwnership = 1;
    private const int DecorationBinding = 33;
    private const int DecorationDescriptorSet = 34;

    // SPVC_COMPILER_OPTION_*（值 = 編號 | 語言旗標：COMMON 0x1000000、GLSL 0x2000000）
    private const int OptCommonBit = 0x1000000;
    private const int OptGlslBit = 0x2000000;
    private const int OptForceTemporary = 1 | OptCommonBit;
    private const int OptFlattenMultidimensionalArrays = 2 | OptCommonBit;
    private const int OptFixupDepthConvention = 3 | OptCommonBit;
    private const int OptFlipVertexY = 4 | OptCommonBit;
    private const int OptGlslSupportNonzeroBaseInstance = 5 | OptGlslBit;
    private const int OptGlslSeparateShaderObjects = 6 | OptGlslBit;
    private const int OptGlslEnable420PackExtension = 7 | OptGlslBit;
    private const int OptGlslVersion = 8 | OptGlslBit;
    private const int OptGlslEs = 9 | OptGlslBit;
    private const int OptGlslVulkanSemantics = 10 | OptGlslBit;
    private const int OptEmitLineDirectives = 37 | OptCommonBit;
    private const int OptGlslEmitPushConstantAsUniformBuffer = 33 | OptGlslBit;
    private const int OptGlslEmitUniformBufferAsPlainUniforms = 35 | OptGlslBit;
    private const int OptGlslForceFlattenedIoBlocks = 66 | OptGlslBit;
    private const int OptGlslOvrMultiviewViewCount = 77 | OptGlslBit;
    private const int OptEnableStorageImageQualifierDeduction = 52 | OptCommonBit;
    private const int OptForceZeroInitializedVariables = 54 | OptCommonBit;
    private const int OptRelaxNanChecks = 78 | OptCommonBit;

    private static int ContextCreate(out IntPtr ctx) { IntPtr c; int rc = ((delegate* unmanaged<IntPtr*, int>)Fn.ContextCreate)(&c); ctx = c; return rc; }
    private static void ContextDestroy(IntPtr ctx) => ((delegate* unmanaged<IntPtr, void>)Fn.ContextDestroy)(ctx);
    private static IntPtr ContextGetLastError(IntPtr ctx) => ((delegate* unmanaged<IntPtr, IntPtr>)Fn.ContextGetLastError)(ctx);
    private static int ParseSpirv(IntPtr ctx, uint* spirv, nuint wordCount, out IntPtr ir) { IntPtr r; int rc = ((delegate* unmanaged<IntPtr, uint*, nuint, IntPtr*, int>)Fn.ParseSpirv)(ctx, spirv, wordCount, &r); ir = r; return rc; }
    private static int CreateCompiler(IntPtr ctx, int backend, IntPtr ir, int mode, out IntPtr compiler) { IntPtr c; int rc = ((delegate* unmanaged<IntPtr, int, IntPtr, int, IntPtr*, int>)Fn.CreateCompiler)(ctx, backend, ir, mode, &c); compiler = c; return rc; }
    private static int CreateOptions(IntPtr compiler, out IntPtr options) { IntPtr o; int rc = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn.CreateOptions)(compiler, &o); options = o; return rc; }
    private static int OptionsSetBool(IntPtr options, int option, byte value) => ((delegate* unmanaged<IntPtr, int, byte, int>)Fn.OptionsSetBool)(options, option, value);
    private static int OptionsSetUint(IntPtr options, int option, uint value) => ((delegate* unmanaged<IntPtr, int, uint, int>)Fn.OptionsSetUint)(options, option, value);
    private static int InstallOptions(IntPtr compiler, IntPtr options) => ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn.InstallOptions)(compiler, options);
    private static int BuildDummySampler(IntPtr compiler, out uint id) { uint i; int rc = ((delegate* unmanaged<IntPtr, uint*, int>)Fn.BuildDummySampler)(compiler, &i); id = i; return rc; }
    private static void SetDecoration(IntPtr compiler, uint id, int decoration, uint argument) => ((delegate* unmanaged<IntPtr, uint, int, uint, void>)Fn.SetDecoration)(compiler, id, decoration, argument);
    private static int CreateShaderResources(IntPtr compiler, out IntPtr resources) { IntPtr r; int rc = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn.CreateShaderResources)(compiler, &r); resources = r; return rc; }
    private static int BuildCombinedImageSamplers(IntPtr compiler) => ((delegate* unmanaged<IntPtr, int>)Fn.BuildCombinedImageSamplers)(compiler);
    private static int GetCombinedImageSamplers(IntPtr compiler, out CombinedImageSampler* samplers, out nuint count) { CombinedImageSampler* s; nuint n; int rc = ((delegate* unmanaged<IntPtr, CombinedImageSampler**, nuint*, int>)Fn.GetCombinedImageSamplers)(compiler, &s, &n); samplers = s; count = n; return rc; }
    private static IntPtr GetName(IntPtr compiler, uint id) => ((delegate* unmanaged<IntPtr, uint, IntPtr>)Fn.GetName)(compiler, id);
    private static void SetName(IntPtr compiler, uint id, byte* name) => ((delegate* unmanaged<IntPtr, uint, byte*, void>)Fn.SetName)(compiler, id, name);
    private static int Compile(IntPtr compiler, out IntPtr source) { IntPtr s; int rc = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn.Compile)(compiler, &s); source = s; return rc; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CombinedImageSampler
    {
        public uint CombinedId, ImageId, SamplerId;
    }

    private static void Ok(IntPtr ctx, int rc, string what)
    {
        if (rc != SpvcSuccess)
            throw new ToolError($"spirv-cross: {what}: {Marshal.PtrToStringUTF8(ContextGetLastError(ctx))}");
    }

    private static uint[] Words(byte[] spv)
    {
        var w = new uint[spv.Length / 4];
        Buffer.BlockCopy(spv, 0, w, 0, w.Length * 4);
        return w;
    }

    /// <summary>spirv-cross &lt;spv&gt; --es --version 300 --flatten-multidimensional-arrays（main.cpp 的 compile_iteration）</summary>
    public static string ToGlslEs300(byte[] spv)
    {
        var words = Words(spv);
        Ok(IntPtr.Zero, ContextCreate(out var ctx), "context");
        try
        {
            IntPtr ir, compiler;
            fixed (uint* w = words) Ok(ctx, ParseSpirv(ctx, w, (nuint)words.Length, out ir), "parse");
            Ok(ctx, CreateCompiler(ctx, BackendGlsl, ir, CaptureTakeOwnership, out compiler), "compiler");

            // 命令列：opts = get_common_options()，再設它有給的選項（沒給的就是 CLIArguments 的預設，跟 CompilerGLSL::Options 的預設相同）。
            // force_recompile_max_debug_iterations（預設 3）C API 沒有開放，用預設值
            Ok(ctx, CreateOptions(compiler, out var options), "options");
            Ok(ctx, OptionsSetUint(options, OptGlslVersion, 300), "version");
            Ok(ctx, OptionsSetBool(options, OptGlslEs, 1), "es");
            Ok(ctx, OptionsSetBool(options, OptForceTemporary, 0), "force_temporary");
            Ok(ctx, OptionsSetBool(options, OptGlslSeparateShaderObjects, 0), "sso");
            Ok(ctx, OptionsSetBool(options, OptFlattenMultidimensionalArrays, 1), "flatten");
            Ok(ctx, OptionsSetBool(options, OptGlslEnable420PackExtension, 1), "420pack");
            Ok(ctx, OptionsSetBool(options, OptGlslVulkanSemantics, 0), "vulkan");
            Ok(ctx, OptionsSetBool(options, OptFixupDepthConvention, 0), "fixup");
            Ok(ctx, OptionsSetBool(options, OptFlipVertexY, 0), "yflip");
            Ok(ctx, OptionsSetBool(options, OptGlslSupportNonzeroBaseInstance, 1), "nonzero_base_instance");
            Ok(ctx, OptionsSetBool(options, OptGlslEmitPushConstantAsUniformBuffer, 0), "push_constant_as_ubo");
            Ok(ctx, OptionsSetBool(options, OptGlslEmitUniformBufferAsPlainUniforms, 0), "ubo_as_plain");
            Ok(ctx, OptionsSetBool(options, OptGlslForceFlattenedIoBlocks, 0), "flattened_io");
            Ok(ctx, OptionsSetUint(options, OptGlslOvrMultiviewViewCount, 0), "ovr_multiview");
            Ok(ctx, OptionsSetBool(options, OptEmitLineDirectives, 0), "line_directives");
            Ok(ctx, OptionsSetBool(options, OptEnableStorageImageQualifierDeduction, 1), "storage_image_deduction");
            Ok(ctx, OptionsSetBool(options, OptForceZeroInitializedVariables, 0), "zero_init");
            Ok(ctx, OptionsSetBool(options, OptRelaxNanChecks, 0), "relax_nan");
            Ok(ctx, InstallOptions(compiler, options), "install options");

            Ok(ctx, BuildDummySampler(compiler, out uint sampler), "dummy sampler");
            if (sampler != 0)
            {
                SetDecoration(compiler, sampler, DecorationDescriptorSet, 0);
                SetDecoration(compiler, sampler, DecorationBinding, 0);
            }
            Ok(ctx, CreateShaderResources(compiler, out _), "resources");
            Ok(ctx, BuildCombinedImageSamplers(compiler), "combined samplers");
            Ok(ctx, GetCombinedImageSamplers(compiler, out var samplers, out var count), "get combined samplers");
            for (nuint i = 0; i < count; i++)
            {
                var s = samplers[i];
                string name = "SPIRV_Cross_Combined" + Marshal.PtrToStringUTF8(GetName(compiler, s.ImageId)) + Marshal.PtrToStringUTF8(GetName(compiler, s.SamplerId));
                var bytes = Encoding.UTF8.GetBytes(name + "\0");
                fixed (byte* b = bytes) SetName(compiler, s.CombinedId, b);
            }
            Ok(ctx, Compile(compiler, out var source), "compile");
            return Marshal.PtrToStringUTF8(source) ?? "";
        }
        finally
        {
            ContextDestroy(ctx);
        }
    }

    /// <summary>spirv-cross &lt;spv&gt; --flatten-multidimensional-arrays --reflect（CompilerReflection，format json）</summary>
    public static string Reflect(byte[] spv)
    {
        var words = Words(spv);
        Ok(IntPtr.Zero, ContextCreate(out var ctx), "context");
        try
        {
            IntPtr ir;
            fixed (uint* w = words) Ok(ctx, ParseSpirv(ctx, w, (nuint)words.Length, out ir), "parse");
            Ok(ctx, CreateCompiler(ctx, BackendJson, ir, CaptureTakeOwnership, out var compiler), "compiler");
            Ok(ctx, Compile(compiler, out var source), "compile");
            return Marshal.PtrToStringUTF8(source) ?? "";
        }
        finally
        {
            ContextDestroy(ctx);
        }
    }
}
