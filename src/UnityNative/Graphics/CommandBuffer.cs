// CommandBuffer（UnityEngine.Rendering）：腳本錄下來的繪製指令，攝影機在掛上的 CameraEvent 執行（GPU 端，GlGpu 的 CommandExec）。
// Post Processing v2 的 PostProcessLayer 就是這樣做後製：OnPreCull 裡把 Bloom、Lut2DBaker、Uber 錄進 BeforeImageEffects 的 buffer。
// 錄的時候照 Unity 的規則：DrawMesh 的 MaterialPropertyBlock 錄下當時的內容（之後改 block 不影響已錄的指令）；
// 材質、網格、RenderTexture 是參照，執行時讀當下的狀態。
// 沒做：compute、ray tracing、GPU fence、非同步讀回、多重 render target、scissor、立體成像（這些指令照舊是替身）。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityNative
{
    public enum CmdKind
    {
        GetTemp, ReleaseTemp, GlobalFloat, GlobalVector, GlobalMatrix, GlobalTexture, Keyword,
        SetTarget, ClearTarget, Viewport, DrawMesh, Blit, ViewProjection,
    }

    public sealed class Cmd
    {
        public CmdKind Kind;
        public int Id;
        public float F;
        public Vector4 V;
        public Matrix4x4 M, M2;
        public RenderTargetIdentifier A, B;
        public Texture Tex;
        public Material Mat;
        public int Pass, Sub;
        public Mesh Mesh;
        public PropertySheet Props;
        public string Keyword;
        public bool On;
        public RenderTextureDescriptor Desc;
        public FilterMode Filter;
        public RTClearFlags ClearFlags;
        public Color Color;
        public uint Stencil;
        public Rect Rect;
        public Vector2 Scale, Offset;
    }

    public sealed class NCommandBuffer
    {
        public string Name = "";
        public readonly List<Cmd> Cmds = new List<Cmd>();
    }

    /// <summary>GPU 端掛進來的執行器：攝影機事件與 Graphics.ExecuteCommandBuffer 用（沒有 GPU 時不執行）</summary>
    public static class CommandBuffers
    {
        public static Action<NCommandBuffer> Execute;
        /// <summary>Graphics.Blit(source, dest, material, pass)：dest 是 null 時畫到目前攝影機的最終目標（OnRenderImage 裡）或畫面</summary>
        public static Action<Texture, RenderTexture, Material, int, Vector2, Vector2> Blit;
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Rendering_CommandBuffer
    {
        static readonly Dictionary<IntPtr, NCommandBuffer> buffers = new Dictionary<IntPtr, NCommandBuffer>();
        static long next = 1;

        public static NCommandBuffer Of(CommandBuffer b) => b == null ? null : buffers.TryGetValue(b.m_Ptr, out var n) ? n : (buffers[b.m_Ptr] = new NCommandBuffer());
        static List<Cmd> L(CommandBuffer b) => Of(b).Cmds;
        static void Add(CommandBuffer b, Cmd c) => L(b).Add(c);

        public static IntPtr InitBuffer() { var p = (IntPtr)next++; buffers[p] = new NCommandBuffer(); return p; }
        public static void ReleaseBuffer(CommandBuffer self) => buffers.Remove(self.m_Ptr);
        public static string get_name(CommandBuffer self) => Of(self).Name;
        public static void set_name(CommandBuffer self, string value) => Of(self).Name = value ?? "";
        public static int get_sizeInBytes(CommandBuffer self) => L(self).Count * 64;
        public static void Clear(CommandBuffer self) => L(self).Clear();
        public static bool ValidateAgainstExecutionFlags(CommandBuffer self, CommandBufferExecutionFlags requiredFlags, CommandBufferExecutionFlags invalidFlags) => true;
        public static void BeginSample(CommandBuffer self, string name) { }
        public static void EndSample(CommandBuffer self, string name) { }
        public static void SetInvertCulling(CommandBuffer self, bool invert) { }
        public static void Internal_SetSinglePassStereo(CommandBuffer self, SinglePassStereoMode mode) { }

        // ───────── 暫存 RT（GetTemporaryRT 也把它設成同名的全域貼圖，Unity 文件這樣寫） ─────────

        public static void GetTemporaryRT(CommandBuffer self, int nameID, int width, int height, int depthBuffer, FilterMode filter, GraphicsFormat format,
            int antiAliasing, bool enableRandomWrite, RenderTextureMemoryless memorylessMode, bool useDynamicScale)
        {
            var desc = new RenderTextureDescriptor(width, height, format, depthBuffer) { msaaSamples = Math.Max(1, antiAliasing), enableRandomWrite = enableRandomWrite };
            Add(self, new Cmd { Kind = CmdKind.GetTemp, Id = nameID, Desc = desc, Filter = filter });
        }

        public static void GetTemporaryRTWithDescriptor_Injected(CommandBuffer self, int nameID, ref RenderTextureDescriptor desc, FilterMode filter) =>
            Add(self, new Cmd { Kind = CmdKind.GetTemp, Id = nameID, Desc = desc, Filter = filter });

        public static void ReleaseTemporaryRT(CommandBuffer self, int nameID) => Add(self, new Cmd { Kind = CmdKind.ReleaseTemp, Id = nameID });

        // ───────── 全域屬性與 keyword ─────────

        public static void SetGlobalFloat(CommandBuffer self, int nameID, float value) => Add(self, new Cmd { Kind = CmdKind.GlobalFloat, Id = nameID, F = value });
        public static void SetGlobalInt(CommandBuffer self, int nameID, int value) => Add(self, new Cmd { Kind = CmdKind.GlobalFloat, Id = nameID, F = value });
        public static void SetGlobalInteger(CommandBuffer self, int nameID, int value) => Add(self, new Cmd { Kind = CmdKind.GlobalFloat, Id = nameID, F = value });
        public static void SetGlobalVector_Injected(CommandBuffer self, int nameID, ref Vector4 value) => Add(self, new Cmd { Kind = CmdKind.GlobalVector, Id = nameID, V = value });
        // 線性色彩空間：Color 轉線性再存（同 Shader.SetGlobalColor）
        public static void SetGlobalColor_Injected(CommandBuffer self, int nameID, ref Color value) => Add(self, new Cmd { Kind = CmdKind.GlobalVector, Id = nameID, V = value.linear });
        public static void SetGlobalMatrix_Injected(CommandBuffer self, int nameID, ref Matrix4x4 value) => Add(self, new Cmd { Kind = CmdKind.GlobalMatrix, Id = nameID, M = value });
        public static void SetGlobalTexture_Impl(CommandBuffer self, int nameID, ref RenderTargetIdentifier rt, RenderTextureSubElement element) =>
            Add(self, new Cmd { Kind = CmdKind.GlobalTexture, Id = nameID, A = rt });
        public static void EnableShaderKeyword(CommandBuffer self, string keyword) => Add(self, new Cmd { Kind = CmdKind.Keyword, Keyword = keyword, On = true });
        public static void DisableShaderKeyword(CommandBuffer self, string keyword) => Add(self, new Cmd { Kind = CmdKind.Keyword, Keyword = keyword, On = false });
        public static void SetViewProjectionMatrices_Injected(CommandBuffer self, ref Matrix4x4 view, ref Matrix4x4 proj) => Add(self, new Cmd { Kind = CmdKind.ViewProjection, M = view, M2 = proj });

        // ───────── render target、清除、viewport ─────────

        public static void SetRenderTargetSingle_Internal_Injected(CommandBuffer self, ref RenderTargetIdentifier rt, RenderBufferLoadAction colorLoad, RenderBufferStoreAction colorStore,
            RenderBufferLoadAction depthLoad, RenderBufferStoreAction depthStore) => Add(self, new Cmd { Kind = CmdKind.SetTarget, A = rt, B = rt });

        public static void SetRenderTargetColorDepth_Internal_Injected(CommandBuffer self, ref RenderTargetIdentifier color, ref RenderTargetIdentifier depth, RenderBufferLoadAction colorLoad,
            RenderBufferStoreAction colorStore, RenderBufferLoadAction depthLoad, RenderBufferStoreAction depthStore, RenderTargetFlags flags) =>
            Add(self, new Cmd { Kind = CmdKind.SetTarget, A = color, B = depth });

        public static void ClearRenderTarget_Injected(CommandBuffer self, RTClearFlags flags, ref Color color, float depth, uint stencil) =>
            Add(self, new Cmd { Kind = CmdKind.ClearTarget, ClearFlags = flags, Color = color, F = depth, Stencil = stencil });

        public static void SetViewport_Injected(CommandBuffer self, ref Rect rect) => Add(self, new Cmd { Kind = CmdKind.Viewport, Rect = rect });

        // ───────── 繪製 ─────────

        public static void Internal_DrawMesh_Injected(CommandBuffer self, Mesh mesh, ref Matrix4x4 matrix, Material material, int submeshIndex, int shaderPass, MaterialPropertyBlock properties)
        {
            PropertySheet snap = null;
            var src = UnityEngine_MaterialPropertyBlock.Of(properties);
            if (src != null && !src.IsEmpty) { snap = new PropertySheet(); snap.CopyFrom(src); }
            Add(self, new Cmd { Kind = CmdKind.DrawMesh, Mesh = mesh, M = matrix, Mat = material, Sub = submeshIndex, Pass = shaderPass, Props = snap });
        }

        public static void Blit_Texture_Injected(CommandBuffer self, Texture source, ref RenderTargetIdentifier dest, Material mat, int pass, ref Vector2 scale, ref Vector2 offset, int sourceDepthSlice, int destDepthSlice) =>
            Add(self, new Cmd { Kind = CmdKind.Blit, Tex = source, A = new RenderTargetIdentifier(BuiltinRenderTextureType.None), B = dest, Mat = mat, Pass = pass, Scale = scale, Offset = offset });

        public static void Blit_Identifier_Injected(CommandBuffer self, ref RenderTargetIdentifier source, ref RenderTargetIdentifier dest, Material mat, int pass, ref Vector2 scale, ref Vector2 offset, int sourceDepthSlice, int destDepthSlice) =>
            Add(self, new Cmd { Kind = CmdKind.Blit, A = source, B = dest, Mat = mat, Pass = pass, Scale = scale, Offset = offset });
    }

    public static unsafe partial class UnityEngine_Graphics
    {
        public static void ExecuteCommandBuffer(CommandBuffer buffer) { var n = UnityEngine_Rendering_CommandBuffer.Of(buffer); if (n != null) CommandBuffers.Execute?.Invoke(n); }

        public static void Blit2(Texture source, RenderTexture dest) => CommandBuffers.Blit?.Invoke(source, dest, null, -1, Vector2.one, Vector2.zero);
        public static void Blit4_Injected(Texture source, RenderTexture dest, ref Vector2 scale, ref Vector2 offset) => CommandBuffers.Blit?.Invoke(source, dest, null, -1, scale, offset);
        public static void Internal_BlitMaterial5(Texture source, RenderTexture dest, Material mat, int pass, bool setRT) => CommandBuffers.Blit?.Invoke(source, dest, mat, pass, Vector2.one, Vector2.zero);
        public static void Internal_BlitMaterial6(Texture source, RenderTexture dest, Material mat, int pass, bool setRT, int destDepthSlice) => CommandBuffers.Blit?.Invoke(source, dest, mat, pass, Vector2.one, Vector2.zero);
    }

    public static unsafe partial class UnityEngine_Display
    {
        /// <summary>畫面的 RenderBuffer：m_BufferPtr 給固定的記號（CommandExec 認得，當成畫面）</summary>
        public static readonly IntPtr ColorBufferPtr = (IntPtr)0x7d150001, DepthBufferPtr = (IntPtr)0x7d150002;
        public static void GetRenderingBuffersImpl(IntPtr nativeDisplay, out RenderBuffer color, out RenderBuffer depth)
        {
            color = new RenderBuffer { m_BufferPtr = ColorBufferPtr };
            depth = new RenderBuffer { m_BufferPtr = DepthBufferPtr };
        }
    }
}
