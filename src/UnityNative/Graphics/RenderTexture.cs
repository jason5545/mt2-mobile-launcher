// RenderTexture：原生物件跟 Texture 共用（NTexture），GPU 那邊是一張貼圖加一個 framebuffer。
// 攝影機的 targetTexture、RawImage 顯示的 RT 都走這裡；還沒畫過的 RT 是透明的（Unity 新建的 RT 內容未定義，實際上多半是清空的）。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityNative
{
    public sealed class NRenderTexture : NTexture
    {
        public int DepthBits;
        public GraphicsFormat ColorFormat = GraphicsFormat.R8G8B8A8_SRGB;
        public GraphicsFormat DepthStencilFormat;
        public int AntiAliasing = 1;
        public bool Created, UseMipMap, AutoGenerateMips = true, RandomWrite, Temporary;
        public RenderTextureDescriptor Desc;
        /// <summary>GPU 端的 framebuffer（GlGpu 管）</summary>
        public object Fbo;
        public static RenderTexture Active;
        public static readonly List<NRenderTexture> TempPool = new List<NRenderTexture>();
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_RenderTexture
    {
        static NRenderTexture R(RenderTexture r) => Native.Self<NRenderTexture>(r);

        public static void Internal_Create(RenderTexture rt) => Native.Bind(new NRenderTexture { Width = 256, Height = 256, Readable = false, GpuDirty = true }, rt);
        public static int get_width(RenderTexture self) => R(self).Width;
        public static void set_width(RenderTexture self, int value) { var r = R(self); r.Width = value; r.GpuDirty = true; }
        public static int get_height(RenderTexture self) => R(self).Height;
        public static void set_height(RenderTexture self, int value) { var r = R(self); r.Height = value; r.GpuDirty = true; }
        public static TextureDimension get_dimension(RenderTexture self) => R(self).Dimension;
        public static void set_dimension(RenderTexture self, TextureDimension value) => R(self).Dimension = value;
        public static GraphicsFormat GetColorFormat(RenderTexture self, bool suppressWarnings) => R(self).ColorFormat;
        public static void SetColorFormat(RenderTexture self, GraphicsFormat format) { var r = R(self); r.ColorFormat = format; r.SRGB = GraphicsFormatUtility.IsSRGBFormat(format); r.GpuDirty = true; }
        public static bool get_useMipMap(RenderTexture self) => R(self).UseMipMap;
        public static void set_useMipMap(RenderTexture self, bool value) => R(self).UseMipMap = value;
        public static bool get_sRGB(RenderTexture self) => R(self).SRGB;
        public static GraphicsFormat get_stencilFormat(RenderTexture self) => GraphicsFormat.None;
        public static void set_stencilFormat(RenderTexture self, GraphicsFormat value) { }
        public static GraphicsFormat get_depthStencilFormat(RenderTexture self) => R(self).DepthStencilFormat;
        public static void set_depthStencilFormat(RenderTexture self, GraphicsFormat value) { R(self).DepthStencilFormat = value; R(self).DepthBits = value == GraphicsFormat.None ? 0 : 24; }
        public static bool get_autoGenerateMips(RenderTexture self) => R(self).AutoGenerateMips;
        public static void set_autoGenerateMips(RenderTexture self, bool value) => R(self).AutoGenerateMips = value;
        public static int get_volumeDepth(RenderTexture self) => 1;
        public static void set_volumeDepth(RenderTexture self, int value) { }
        public static int get_antiAliasing(RenderTexture self) => R(self).AntiAliasing;
        public static void set_antiAliasing(RenderTexture self, int value) => R(self).AntiAliasing = value;
        public static bool get_enableRandomWrite(RenderTexture self) => R(self).RandomWrite;
        public static void set_enableRandomWrite(RenderTexture self, bool value) => R(self).RandomWrite = value;
        public static bool GetIsPowerOfTwo(RenderTexture self) { var r = R(self); return (r.Width & (r.Width - 1)) == 0 && (r.Height & (r.Height - 1)) == 0; }
        public static RenderTexture GetActive() => NRenderTexture.Active;
        public static void SetActive(RenderTexture rt) => NRenderTexture.Active = rt;
        public static void SetMipMapCount(RenderTexture self, int count) => R(self).MipCount = Math.Max(1, count);
        public static void DiscardContents(RenderTexture self, bool discardColor, bool discardDepth) { }
        public static void MarkRestoreExpected(RenderTexture self) { }
        public static void SetGlobalShaderProperty(RenderTexture self, string propertyName) => UnityEngine_Shader.SetGlobalTextureImpl(PropertyIds.Of(propertyName), self);
        public static bool Create(RenderTexture self) { R(self).Created = true; return true; }
        public static void Release(RenderTexture self) { var r = R(self); r.Created = false; NTexture.GpuRelease?.Invoke(r); r.Gpu = null; }
        public static bool IsCreated(RenderTexture self) => R(self).Created;
        public static void GenerateMips(RenderTexture self) { }
        public static void SetSRGBReadWrite(RenderTexture self, bool srgb) { R(self).SRGB = srgb; R(self).GpuDirty = true; }
        public static bool SupportsStencil(RenderTexture rt) => true;
        public static int get_depth(RenderTexture self) => R(self).DepthBits;
        public static void set_depth(RenderTexture self, int value) => R(self).DepthBits = value;

        public static void SetRenderTextureDescriptor_Injected(RenderTexture self, ref RenderTextureDescriptor desc)
        {
            var r = R(self);
            r.Desc = desc;
            r.Width = desc.width; r.Height = desc.height;
            r.ColorFormat = desc.graphicsFormat;
            r.SRGB = GraphicsFormatUtility.IsSRGBFormat(desc.graphicsFormat) || desc.sRGB;
            r.DepthBits = desc.depthBufferBits;
            r.UseMipMap = desc.useMipMap;
            r.GpuDirty = true;
        }

        public static void GetDescriptor_Injected(RenderTexture self, out RenderTextureDescriptor ret)
        {
            var r = R(self);
            ret = r.Desc;
            ret.width = r.Width; ret.height = r.Height; ret.graphicsFormat = r.ColorFormat; ret.depthBufferBits = r.DepthBits;
            ret.msaaSamples = Math.Max(1, r.AntiAliasing); ret.volumeDepth = 1; ret.dimension = TextureDimension.Tex2D;
        }

        public static RenderTexture GetTemporary_Internal_Injected(ref RenderTextureDescriptor desc)
        {
            foreach (var t in NRenderTexture.TempPool)
                if (!t.Created && t.Width == desc.width && t.Height == desc.height && t.ColorFormat == desc.graphicsFormat)
                {
                    t.Created = true;
                    NRenderTexture.TempPool.Remove(t);
                    return (RenderTexture)t.Managed;
                }
            var rt = (RenderTexture)Native.NewShell(typeof(RenderTexture));
            Internal_Create(rt);
            var n = R(rt);
            n.Temporary = true;
            SetRenderTextureDescriptor_Injected(rt, ref desc);
            n.Created = true;
            return rt;
        }

        public static void ReleaseTemporary(RenderTexture temp)
        {
            var n = Native.Of<NRenderTexture>(temp);
            if (n == null) return;
            n.Created = false;
            if (!NRenderTexture.TempPool.Contains(n)) NRenderTexture.TempPool.Add(n);
        }

        public static void GetColorBuffer_Injected(RenderTexture self, out RenderBuffer ret) => ret = new RenderBuffer { m_RenderTextureInstanceID = R(self).InstanceId };
        public static void GetDepthBuffer_Injected(RenderTexture self, out RenderBuffer ret) => ret = new RenderBuffer { m_RenderTextureInstanceID = R(self).InstanceId };
    }

    public static unsafe partial class UnityEngine_Experimental_Rendering_GraphicsFormatUtility
    {
        public static bool IsSRGBFormat(GraphicsFormat format) => format.ToString().EndsWith("SRGB") || format.ToString().Contains("_SRGB");
        public static bool IsCrunchFormat(TextureFormat format) => format == TextureFormat.DXT1Crunched || format == TextureFormat.DXT5Crunched || format == TextureFormat.ETC_RGB4Crunched || format == TextureFormat.ETC2_RGBA8Crunched;
        // DefaultHDR：桌面與手機都給 R16G16B16A16_SFloat（同 SystemInfo.GetGraphicsFormat(DefaultFormat.HDR)；Unity 在不支援半精度的裝置才換別的，未查證）。
        // 浮點格式不分 sRGB
        public static GraphicsFormat GetGraphicsFormat_Native_RenderTextureFormat(RenderTextureFormat format, bool isSRGB) => format switch
        {
            RenderTextureFormat.ARGBHalf or RenderTextureFormat.DefaultHDR => GraphicsFormat.R16G16B16A16_SFloat,
            RenderTextureFormat.ARGBFloat => GraphicsFormat.R32G32B32A32_SFloat,
            RenderTextureFormat.RGB111110Float => GraphicsFormat.B10G11R11_UFloatPack32,
            RenderTextureFormat.Depth => GraphicsFormat.None,
            _ => isSRGB ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm,
        };
        public static GraphicsFormat GetGraphicsFormat_Native_TextureFormat(TextureFormat format, bool isSRGB) => isSRGB ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
        public static GraphicsFormat GetDepthStencilFormatFromBitsLegacy_Native(int minimumDepthBits) => minimumDepthBits <= 0 ? GraphicsFormat.None : minimumDepthBits <= 16 ? GraphicsFormat.D16_UNorm : GraphicsFormat.D24_UNorm_S8_UInt;
        public static bool IsSRGBFormat_Injected(ref GraphicsFormat format) => IsSRGBFormat(format);

        // sRGB／線性互換照列舉名稱：R8G8B8A8_SRGB ↔ R8G8B8A8_UNorm；沒有對應的就原樣回傳（Unity 也是這樣）
        static GraphicsFormat Swap(GraphicsFormat f, string from, string to)
        {
            var n = f.ToString();
            return n.EndsWith(from) && Enum.TryParse<GraphicsFormat>(n.Substring(0, n.Length - from.Length) + to, out var r) ? r : f;
        }
        public static GraphicsFormat GetSRGBFormat(GraphicsFormat format) => Swap(format, "_UNorm", "_SRGB");
        public static GraphicsFormat GetLinearFormat(GraphicsFormat format) => Swap(format, "_SRGB", "_UNorm");
        public static bool IsDepthFormat(GraphicsFormat format) => format.ToString().StartsWith("D") && char.IsDigit(format.ToString()[1]);
        public static bool IsStencilFormat(GraphicsFormat format) => format.ToString().StartsWith("S8") || format.ToString().EndsWith("_S8_UInt");
        public static bool IsDepthStencilFormat(GraphicsFormat format) => IsDepthFormat(format) || IsStencilFormat(format);
        public static int GetDepthBits(GraphicsFormat format) => format switch
        {
            GraphicsFormat.D16_UNorm => 16,
            GraphicsFormat.D24_UNorm or GraphicsFormat.D24_UNorm_S8_UInt => 24,
            GraphicsFormat.D32_SFloat or GraphicsFormat.D32_SFloat_S8_UInt => 32,
            _ => 0,
        };
        public static RenderTextureFormat GetRenderTextureFormat(GraphicsFormat format) => format switch
        {
            GraphicsFormat.R16G16B16A16_SFloat => RenderTextureFormat.ARGBHalf,
            GraphicsFormat.R32G32B32A32_SFloat => RenderTextureFormat.ARGBFloat,
            GraphicsFormat.B10G11R11_UFloatPack32 => RenderTextureFormat.RGB111110Float,
            GraphicsFormat.R8_UNorm => RenderTextureFormat.R8,
            _ when IsDepthFormat(format) => RenderTextureFormat.Depth,
            _ => RenderTextureFormat.ARGB32,
        };
    }

    public static unsafe partial class UnityEngine_SystemInfo
    {
        public static GraphicsFormat GetCompatibleFormat(GraphicsFormat format, FormatUsage usage) => format;
        public static bool IsFormatSupported(GraphicsFormat format, FormatUsage usage) => true;
        public static GraphicsFormat GetGraphicsFormat(DefaultFormat format) => format == DefaultFormat.LDR ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R16G16B16A16_SFloat;
        public static bool SupportsRenderTextureFormat(RenderTextureFormat format) => true;
        public static bool SupportsTextureFormatNative(TextureFormat format) => true;
        public static int GetMaxTextureSize() => 8192;
        public static int GetMaxRenderTextureSize() => 8192;
        public static string GetOperatingSystem() => Engine.Config.Platform == RuntimePlatform.Android ? "Android OS" : "Mac OS X";
        public static OperatingSystemFamily GetOperatingSystemFamily() => OperatingSystemFamily.Other;
        public static string GetProcessorType() => "ARM64";
        public static int GetProcessorCount() => Environment.ProcessorCount;
        public static int GetProcessorFrequencyMHz() => 3000;
        public static int GetPhysicalMemoryMB() => 8192;
        public static int GetGraphicsMemorySize() => 4096;
        public static string GetGraphicsDeviceName() => "OpenGL ES 3";
        public static string GetGraphicsDeviceVendor() => "unity-native";
        public static string GetGraphicsDeviceVersion() => "OpenGL ES 3.0";
        public static int GetGraphicsShaderLevel() => 35;
        public static GraphicsDeviceType GetGraphicsDeviceType() => GraphicsDeviceType.OpenGLES3;
        // 跑的是 D3D 的 shader bytecode（UNITY_UV_STARTS_AT_TOP 寫死是 1），CommandBuffer 的全螢幕繪製也照 D3D 的列序畫（CommandExec.cs 檔頭）：
        // 照 D3D 回 true，PPv2 的 Uber 才會給 _UVTransform (1, −1, 0, 1)（同 TS 版）。MT2 的腳本、uGUI、TMP 都沒有讀這個值
        public static bool GetGraphicsUVStartsAtTop() => true;
        public static DeviceType GetDeviceType() => Engine.Config.Platform == RuntimePlatform.Android ? DeviceType.Handheld : DeviceType.Desktop;
        public static string GetDeviceModel() => "unity-native";
        public static string GetDeviceName() => "unity-native";
        public static string GetDeviceUniqueIdentifier() => "unity-native-device";
        public static bool SupportsInstancing() => false;
        public static bool SupportsComputeShaders() => false;
        public static bool Supports2DArrayTextures() => true;
        public static int SupportedRenderTargetCount() => 4;
        public static bool SupportsAudio() => true;
        public static float GetBatteryLevel() => 1f;
        public static BatteryStatus GetBatteryStatus() => BatteryStatus.Unknown;
    }

    public static unsafe partial class UnityEngine_QualitySettings
    {
        static int vsync = 1, antiAliasing, masterTextureLimit;
        public static int get_vSyncCount() => vsync;
        public static void set_vSyncCount(int value) => vsync = value;
        public static int get_antiAliasing() => antiAliasing;
        public static void set_antiAliasing(int value) => antiAliasing = value;
        public static ColorSpace get_activeColorSpace() => ColorSpace.Linear;
        public static ColorSpace get_desiredColorSpace() => ColorSpace.Linear;
        public static int get_masterTextureLimit() => masterTextureLimit;
        public static void set_masterTextureLimit(int value) => masterTextureLimit = value;
    }
}
