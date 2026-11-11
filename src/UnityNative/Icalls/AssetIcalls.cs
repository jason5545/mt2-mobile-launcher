// 貼圖、sprite、材質、MaterialPropertyBlock、shader、字型、TextAsset、AudioClip 的 internal call（資料面；繪製端另外接）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Texture
    {
        static NTexture T(Texture t) => Native.Self<NTexture>(t);
        public static int GetDataWidth(Texture self) => T(self).Width;
        public static int GetDataHeight(Texture self) => T(self).Height;
        public static TextureDimension GetDimension(Texture self) => T(self).Dimension;
        public static int get_mipmapCount(Texture self) => T(self).MipCount;
        public static bool get_isReadable(Texture self) => T(self).Readable;
        public static TextureWrapMode get_wrapMode(Texture self) => T(self).WrapU;
        public static void set_wrapMode(Texture self, TextureWrapMode value) { var t = T(self); bool changed = t.WrapU != value || t.WrapV != value; NTexture.TraceSet?.Invoke(t, "wrapMode", changed); t.WrapU = t.WrapV = t.WrapW = value; SamplingChanged(t, changed); }
        public static TextureWrapMode get_wrapModeU(Texture self) => T(self).WrapU;
        public static void set_wrapModeU(Texture self, TextureWrapMode value) { var t = T(self); bool changed = t.WrapU != value; NTexture.TraceSet?.Invoke(t, "wrapModeU", changed); t.WrapU = value; SamplingChanged(t, changed); }
        public static TextureWrapMode get_wrapModeV(Texture self) => T(self).WrapV;
        public static void set_wrapModeV(Texture self, TextureWrapMode value) { var t = T(self); bool changed = t.WrapV != value; NTexture.TraceSet?.Invoke(t, "wrapModeV", changed); t.WrapV = value; SamplingChanged(t, changed); }
        public static TextureWrapMode get_wrapModeW(Texture self) => T(self).WrapW;
        public static void set_wrapModeW(Texture self, TextureWrapMode value) => T(self).WrapW = value;
        public static FilterMode get_filterMode(Texture self) => T(self).Filter;
        public static void set_filterMode(Texture self, FilterMode value) { var t = T(self); bool changed = t.Filter != value; NTexture.TraceSet?.Invoke(t, "filterMode", changed); t.Filter = value; SamplingChanged(t, changed); }

        /// <summary>wrap／filter 的 setter：值沒變不做事；變了只標記取樣參數要重設，不重傳像素。RenderTexture 照舊設 GpuDirty（UploadRT 的行為不變）</summary>
        static void SamplingChanged(NTexture t, bool changed)
        {
            if (t is NRenderTexture) t.GpuDirty = true;
            else if (changed) t.ParamsDirty = true;
        }
        public static int get_anisoLevel(Texture self) => T(self).AnisoLevel;
        public static void set_anisoLevel(Texture self, int value) => T(self).AnisoLevel = value;
        public static float get_mipMapBias(Texture self) => T(self).MipBias;
        public static void set_mipMapBias(Texture self, float value) => T(self).MipBias = value;
        public static IntPtr GetNativeTexturePtr(Texture self) => IntPtr.Zero;
        public static uint get_updateCount(Texture self) => T(self).UpdateCount;
        public static void IncrementUpdateCount(Texture self) { NTexture.TraceSet?.Invoke(T(self), "IncrementUpdateCount", true); T(self).UpdateCount++; }
        public static int Internal_GetActiveTextureColorSpace(Texture self) => T(self).SRGB ? 1 : 0;
        public static TextureColorSpace Internal_GetStoredColorSpace(Texture self) => T(self).SRGB ? TextureColorSpace.sRGB : TextureColorSpace.Linear;
    }

    public static unsafe partial class UnityEngine_Texture2D
    {
        static NTexture T(Texture2D t) => Native.Self<NTexture>(t);
        static readonly Dictionary<string, Texture2D> solid = new Dictionary<string, Texture2D>();

        public static TextureFormat get_format(Texture2D self) => T(self).Format;
        public static bool get_isReadable(Texture2D self) => T(self).Readable;

        static Texture2D Solid(string name, byte r, byte g, byte b, byte a)
        {
            if (solid.TryGetValue(name, out var t) && t != null) return t;
            t = (Texture2D)Native.NewShell(typeof(Texture2D));
            var px = new byte[4 * 16];
            for (int i = 0; i < 16; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
            Native.Bind(new NTexture { Name = name, Width = 4, Height = 4, Pixels = px, Readable = true }, t, true);
            solid[name] = t;
            return t;
        }

        public static Texture2D get_whiteTexture() => Solid("UnityWhite", 255, 255, 255, 255);
        public static Texture2D get_blackTexture() => Solid("UnityBlack", 0, 0, 0, 255);
        public static Texture2D get_redTexture() => Solid("UnityRed", 255, 0, 0, 255);
        public static Texture2D get_grayTexture() => Solid("UnityGray", 128, 128, 128, 255);
        public static Texture2D get_linearGrayTexture() => Solid("UnityLinearGray", 188, 188, 188, 255);
        public static Texture2D get_normalTexture() => Solid("UnityNormal", 128, 128, 255, 255);

        public static bool Internal_CreateImpl(Texture2D mono, int w, int h, int mipCount, GraphicsFormat format, TextureColorSpace colorSpace, TextureCreationFlags flags, IntPtr nativeTex, string mipmapLimitGroupName)
        {
            Native.Bind(new NTexture { Width = w, Height = h, MipCount = Math.Max(1, mipCount), Pixels = new byte[w * h * 4], Readable = true, SRGB = colorSpace == TextureColorSpace.sRGB, Mips = mipCount > 1 }, mono);
            return true;
        }

        /// <summary>2022.3.3 的簽名（沒有 colorSpace；Diceomancer）：sRGB 照格式名判斷</summary>
        public static bool Internal_CreateImpl(Texture2D mono, int w, int h, int mipCount, GraphicsFormat format, TextureCreationFlags flags, IntPtr nativeTex, string mipmapLimitGroupName)
            => Internal_CreateImpl(mono, w, h, mipCount, format, format.ToString().Contains("SRGB") ? TextureColorSpace.sRGB : TextureColorSpace.Linear, flags, nativeTex, mipmapLimitGroupName);

        public static void ApplyImpl(Texture2D self, bool updateMipmaps, bool makeNoLongerReadable) { var t = T(self); NTexture.TraceSet?.Invoke(t, "Apply", true); t.GpuDirty = true; t.UpdateCount++; t.CpuWritten = true; if (makeNoLongerReadable) t.Readable = false; }
        public static bool ReinitializeImpl(Texture2D self, int width, int height) { var t = T(self); t.Width = width; t.Height = height; t.Pixels = new byte[width * height * 4]; t.CpuWritten = true; t.GpuDirty = true; return true; }
        public static bool ReinitializeWithFormatImpl(Texture2D self, int width, int height, GraphicsFormat format, bool hasMipMap) => ReinitializeImpl(self, width, height);
        public static bool ReinitializeWithTextureFormatImpl(Texture2D self, int width, int height, TextureFormat textureFormat, bool hasMipMap) => ReinitializeImpl(self, width, height);

        static byte[] Px(NTexture t)
        {
            // 保留原始壓縮資料的貼圖（.mt2t）：用 CPU 解第 0 層，log 記一行（docs/perf-battle.md「B 段」）
            if (t.Pixels == null) t.Pixels = (t.Compressed != 0 ? UnityNative.Gl.GpuResources.DecodeCompressedPixels(t) : Png.Decode(t.File, out _, out _)) ?? new byte[t.Width * t.Height * 4];
            return t.Pixels;
        }

        public static void SetPixelsImpl(Texture2D self, int x, int y, int w, int h, Color[] pixel, int miplevel, int frame)
        {
            var t = T(self); var px = Px(t);
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    var c = pixel[j * w + i];
                    int o = ((y + j) * t.Width + x + i) * 4;
                    if (o < 0 || o + 3 >= px.Length) continue;
                    px[o] = (byte)(Mathf.Clamp01(c.r) * 255f + 0.5f); px[o + 1] = (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f);
                    px[o + 2] = (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f); px[o + 3] = (byte)(Mathf.Clamp01(c.a) * 255f + 0.5f);
                }
        }

        public static void SetAllPixels32(Texture2D self, Color32[] colors, int miplevel)
        {
            var t = T(self); var px = Px(t);
            for (int i = 0; i < colors.Length && i * 4 + 3 < px.Length; i++) { px[i * 4] = colors[i].r; px[i * 4 + 1] = colors[i].g; px[i * 4 + 2] = colors[i].b; px[i * 4 + 3] = colors[i].a; }
        }

        public static bool LoadRawTextureDataImplArray(Texture2D self, byte[] data)
        {
            var t = T(self);
            t.Pixels = (byte[])data.Clone();
            t.CpuWritten = true;
            return true;
        }

        public static void GetPixelBilinearImpl_Injected(Texture2D self, int image, int mip, float u, float v, out Color ret)
        {
            var t = T(self); var px = Px(t);
            int x = Math.Clamp((int)(u * t.Width), 0, t.Width - 1), y = Math.Clamp((int)(v * t.Height), 0, t.Height - 1);
            int o = (y * t.Width + x) * 4;
            ret = o + 3 < px.Length ? new Color(px[o] / 255f, px[o + 1] / 255f, px[o + 2] / 255f, px[o + 3] / 255f) : Color.clear;
        }

        public static void GetPixelImpl_Injected(Texture2D self, int image, int mip, int x, int y, out Color ret)
        {
            var t = T(self); var px = Px(t);
            x = Math.Clamp(x, 0, t.Width - 1); y = Math.Clamp(y, 0, t.Height - 1);
            int o = (y * t.Width + x) * 4;
            ret = o + 3 < px.Length ? new Color(px[o] / 255f, px[o + 1] / 255f, px[o + 2] / 255f, px[o + 3] / 255f) : Color.clear;
        }

        public static void SetPixelImpl_Injected(Texture2D self, int image, int mip, int x, int y, ref Color color)
        {
            var t = T(self); var px = Px(t);
            int o = (y * t.Width + x) * 4;
            if (o < 0 || o + 3 >= px.Length) return;
            px[o] = (byte)(Mathf.Clamp01(color.r) * 255f + 0.5f); px[o + 1] = (byte)(Mathf.Clamp01(color.g) * 255f + 0.5f);
            px[o + 2] = (byte)(Mathf.Clamp01(color.b) * 255f + 0.5f); px[o + 3] = (byte)(Mathf.Clamp01(color.a) * 255f + 0.5f);
        }
    }

    public static unsafe partial class UnityEngine_Sprite
    {
        static NSprite S(Sprite s) => Native.Self<NSprite>(s);

        public static int GetPackingMode(Sprite self) => 0;
        public static int GetPackingRotation(Sprite self) => 0;
        public static int GetPacked(Sprite self) => S(self).Packed ? 1 : 0;
        public static Texture2D get_texture(Sprite self) => (Texture2D)S(self).Texture?.Managed;
        public static int GetSecondaryTextureCount(Sprite self) => 0;
        public static float get_pixelsPerUnit(Sprite self) => S(self).PixelsPerUnit;
        public static float get_spriteAtlasTextureScale(Sprite self) => 1f;
        public static Texture2D get_associatedAlphaSplitTexture(Sprite self) => null;
        public static Vector2[] get_vertices(Sprite self) { SpriteMesh.Ensure(S(self)); return (Vector2[])S(self).Vertices.Clone(); }
        public static ushort[] get_triangles(Sprite self) { SpriteMesh.Ensure(S(self)); return (ushort[])S(self).Triangles.Clone(); }
        public static Vector2[] get_uv(Sprite self) { SpriteMesh.Ensure(S(self)); return (Vector2[])S(self).UV.Clone(); }
        public static int GetPhysicsShapeCount(Sprite self) => 0;
        public static void OverrideGeometry(Sprite self, Vector2[] vertices, ushort[] triangles)
        {
            var s = S(self);
            SpriteMesh.Ensure(s);
            s.Vertices = (Vector2[])vertices.Clone();
            s.Triangles = (ushort[])triangles.Clone();
            s.UV = SpriteMesh.UVs(s, s.Vertices);
        }
        public static void GetTextureRect_Injected(Sprite self, out Rect ret) => ret = S(self).TextureRect;
        public static void GetTextureRectOffset_Injected(Sprite self, out Vector2 ret) => ret = S(self).TextureRectOffset;

        public static void GetInnerUVs_Injected(Sprite self, out Vector4 ret)
        {
            var s = S(self); var tr = s.TextureRect; var b = s.Border;
            float w = s.Texture?.Width ?? 1, h = s.Texture?.Height ?? 1;
            ret = new Vector4((tr.x + b.x) / w, (tr.y + b.y) / h, (tr.xMax - b.z) / w, (tr.yMax - b.w) / h);
        }

        public static void GetOuterUVs_Injected(Sprite self, out Vector4 ret)
        {
            var s = S(self); var tr = s.TextureRect;
            float w = s.Texture?.Width ?? 1, h = s.Texture?.Height ?? 1;
            ret = new Vector4(tr.x / w, tr.y / h, tr.xMax / w, tr.yMax / h);
        }

        public static void GetPadding_Injected(Sprite self, out Vector4 ret)
        {
            var s = S(self); var r = s.Rect; var tr = s.TextureRect; var o = s.TextureRectOffset;
            ret = new Vector4(o.x, o.y, r.width - tr.width - o.x, r.height - tr.height - o.y);
        }

        public static Sprite CreateSprite_Injected(Texture2D texture, ref Rect rect, ref Vector2 pivot, float pixelsPerUnit, uint extrude, SpriteMeshType meshType, ref Vector4 border, bool generateFallbackPhysicsShape, SecondarySpriteTexture[] secondaryTexture)
        {
            var s = (Sprite)Native.NewShell(typeof(Sprite));
            Native.Bind(new NSprite
            {
                Texture = Native.Of<NTexture>(texture), Rect = rect, TextureRect = rect, Pivot = new Vector2(pivot.x, pivot.y),
                PixelsPerUnit = pixelsPerUnit, Border = border,
            }, s);
            return s;
        }

        public static Sprite CreateSpriteWithoutTextureScripting_Injected(ref Rect rect, ref Vector2 pivot, float pixelsToUnits, Texture2D texture)
        {
            var b = Vector4.zero;
            return CreateSprite_Injected(texture, ref rect, ref pivot, pixelsToUnits, 0, SpriteMeshType.FullRect, ref b, false, null);
        }

        public static void get_bounds_Injected(Sprite self, out Bounds ret)
        {
            var s = S(self); var r = s.Rect; float ppu = s.PixelsPerUnit <= 0 ? 100 : s.PixelsPerUnit;
            var size = new Vector3(r.width / ppu, r.height / ppu, 0);
            var center = new Vector3((0.5f - s.Pivot.x) * size.x, (0.5f - s.Pivot.y) * size.y, 0);
            ret = new Bounds(center, size);
        }
        public static void get_rect_Injected(Sprite self, out Rect ret) => ret = S(self).Rect;
        public static void get_border_Injected(Sprite self, out Vector4 ret) => ret = S(self).Border;
        /// <summary>sprite.pivot 是像素（m_Pivot 是 0～1 的比例）</summary>
        public static void get_pivot_Injected(Sprite self, out Vector2 ret) { var s = S(self); ret = new Vector2(s.Pivot.x * s.Rect.width, s.Pivot.y * s.Rect.height); }
    }

    public static unsafe partial class UnityEngine_TextAsset
    {
        static NTextAsset T(TextAsset t) => Native.Self<NTextAsset>(t);
        public static byte[] get_bytes(TextAsset self) => T(self).GetBytes();
        public static byte[] GetPreviewBytes(TextAsset self, int maxByteCount) { var b = T(self).GetBytes(); return b.Length <= maxByteCount ? b : b[..maxByteCount]; }
        public static void Internal_CreateInstance(TextAsset self, string text) => Native.Bind(new NTextAsset { Text = text ?? "" }, self);
        public static IntPtr GetDataPtr(TextAsset self)
        {
            var b = T(self).GetBytes();
            var h = System.Runtime.InteropServices.GCHandle.Alloc(b, System.Runtime.InteropServices.GCHandleType.Pinned);
            return h.AddrOfPinnedObject();
        }
        public static long GetDataSize(TextAsset self) => T(self).GetBytes().Length;
    }

    public static unsafe partial class UnityEngine_Font
    {
        static NFont F(Font f) => Native.Self<NFont>(f);
        public static Material get_material(Font self) => F(self).Material;
        public static void set_material(Font self, Material value) => F(self).Material = value;
        public static string[] get_fontNames(Font self) => F(self).Names;
        public static void set_fontNames(Font self, string[] value) => F(self).Names = value ?? Array.Empty<string>();
        public static bool get_dynamic(Font self) => F(self).Dynamic;
        public static int get_ascent(Font self) => F(self).Ascent;
        public static int get_fontSize(Font self) => F(self).FontSize;
        public static CharacterInfo[] get_characterInfo(Font self) => F(self).Chars;
        public static void set_characterInfo(Font self, CharacterInfo[] value) => F(self).Chars = value ?? Array.Empty<CharacterInfo>();
        public static int get_lineHeight(Font self) => F(self).LineHeight;
        public static bool HasCharacter(Font self, int c) => true;
        public static string[] GetOSInstalledFontNames() => Array.Empty<string>();
        public static string[] GetPathsToOSFonts() => Array.Empty<string>();
        public static void Internal_CreateFont(Font self, string name) => Native.Bind(new NFont { Name = name ?? "" }, self);
        public static void Internal_CreateFontFromPath(Font self, string fontPath) => Native.Bind(new NFont { Name = Path.GetFileNameWithoutExtension(fontPath ?? ""), File = fontPath }, self);
        public static void Internal_CreateDynamicFont(Font self, string[] _names, int size) => Native.Bind(new NFont { Name = _names?.Length > 0 ? _names[0] : "", Names = _names ?? Array.Empty<string>(), FontSize = size }, self);
    }

    public static unsafe partial class UnityEngine_Shader
    {
        static NShader S(Shader s) => Native.Self<NShader>(s);
        static readonly Dictionary<int, float> globalFloats = new Dictionary<int, float>();
        static readonly Dictionary<int, int> globalInts = new Dictionary<int, int>();
        static readonly Dictionary<int, Vector4> globalVectors = new Dictionary<int, Vector4>();
        static readonly Dictionary<int, Texture> globalTextures = new Dictionary<int, Texture>();
        static readonly Dictionary<int, Matrix4x4> globalMatrices = new Dictionary<int, Matrix4x4>();
        public static readonly HashSet<string> GlobalKeywords = new HashSet<string>();
        static readonly Dictionary<string, int> tags = new Dictionary<string, int>();
        static readonly List<string> tagNames = new List<string> { "" };

        public static Shader FindBuiltin(string name) => Shaders.Find(name);
        public static int get_maximumLOD(Shader self) => S(self).MaximumLOD;
        public static void set_maximumLOD(Shader self, int value) => S(self).MaximumLOD = value;
        public static bool get_isSupported(Shader self) => true;
        public static void EnableKeyword(string keyword) => GlobalKeywords.Add(keyword);
        public static void DisableKeyword(string keyword) => GlobalKeywords.Remove(keyword);
        public static bool IsKeywordEnabled(string keyword) => GlobalKeywords.Contains(keyword);
        public static int get_renderQueue(Shader self) => S(self).RenderQueue;
        public static void WarmupAllShaders() { }
        public static int TagToID(string name)
        {
            if (!tags.TryGetValue(name, out var id)) { id = tagNames.Count; tagNames.Add(name); tags[name] = id; }
            return id;
        }
        public static string IDToTag(int name) => (uint)name < (uint)tagNames.Count ? tagNames[name] : "";
        public static int PropertyToID(string name) => PropertyIds.Of(name);
        public static int get_passCount(Shader self) => 1;
        public static int get_subshaderCount(Shader self) => 1;
        public static int GetPassCountInSubshader(Shader self, int subshaderIndex) => 1;

        /// <summary>全域 float／int／vector／matrix 改過就加一（繪製端的 uniform 快取靠它判斷要不要重算）</summary>
        public static int GlobalVersion;
        public static void SetGlobalIntImpl(int name, int value) { globalInts[name] = value; GlobalVersion++; }
        public static void SetGlobalFloatImpl(int name, float value) { globalFloats[name] = value; GlobalVersion++; }
        public static void SetGlobalTextureImpl(int name, Texture value) => globalTextures[name] = value;
        public static int GetGlobalIntImpl(int name) => globalInts.TryGetValue(name, out var v) ? v : 0;
        public static float GetGlobalFloatImpl(int name) => globalFloats.TryGetValue(name, out var v) ? v : 0;
        public static Texture GetGlobalTextureImpl(int name) => globalTextures.TryGetValue(name, out var v) ? v : null;
        public static void SetGlobalVectorImpl_Injected(int name, ref Vector4 value) { globalVectors[name] = value; GlobalVersion++; }
        public static void GetGlobalVectorImpl_Injected(int name, out Vector4 ret) => ret = globalVectors.TryGetValue(name, out var v) ? v : Vector4.zero;
        public static void SetGlobalMatrixImpl_Injected(int name, ref Matrix4x4 value) { globalMatrices[name] = value; GlobalVersion++; }
        public static void GetGlobalMatrixImpl_Injected(int name, out Matrix4x4 ret) => ret = globalMatrices.TryGetValue(name, out var v) ? v : Matrix4x4.identity;

        public static int GetPropertyCount(Shader self) => S(self).Props.Count;
        public static int FindPropertyIndex(Shader self, string propertyName) => S(self).Props.FindIndex(p => p.name == propertyName);
        public static string GetPropertyName(Shader shader, int propertyIndex) => S(shader).Props[propertyIndex].name;
        public static int GetPropertyNameId(Shader shader, int propertyIndex) => PropertyIds.Of(S(shader).Props[propertyIndex].name);
        public static ShaderPropertyType GetPropertyType(Shader shader, int propertyIndex) => (ShaderPropertyType)S(shader).Props[propertyIndex].type;

        public static bool TryGlobalVector(int id, out Vector4 v) => globalVectors.TryGetValue(id, out v);
        public static bool TryGlobalFloat(int id, out float v) => globalFloats.TryGetValue(id, out v);
        public static bool TryGlobalTexture(int id, out Texture v) => globalTextures.TryGetValue(id, out v);
        public static bool TryGlobalMatrix(int id, out Matrix4x4 v) => globalMatrices.TryGetValue(id, out v);
    }

    public static unsafe partial class UnityEngine_Material
    {
        static NMaterial M(Material m) => Native.Self<NMaterial>(m);

        public static void CreateWithShader(Material self, Shader shader)
        {
            var n = Native.Bind(new NMaterial { Name = "", Shader = Native.Of<NShader>(shader) }, self);
            n.Name = shader != null ? shader.name : "";
        }
        public static void CreateWithMaterial(Material self, Material source)
        {
            var n = Native.Bind(new NMaterial(), self);
            var s = Native.Of<NMaterial>(source);
            if (s != null) { n.CopyFrom(s); n.Name = s.Name; }
        }
        public static void CreateWithString(Material self) => Native.Bind(new NMaterial(), self);
        public static Material GetDefaultMaterial() => Defaults.Get("Sprites-Default", "Sprites/Default");
        public static Material GetDefaultParticleMaterial() => Defaults.Get("Default-Particle", "Legacy Shaders/Particles/Alpha Blended");
        public static Material GetDefaultLineMaterial() => Defaults.Get("Default-Line", "Legacy Shaders/Particles/Alpha Blended");
        public static Shader get_shader(Material self) => (Shader)M(self).Shader?.Managed;
        public static void set_shader(Material self, Shader value) { var m = M(self); m.Shader = Native.Of<NShader>(value); m.Version++; }
        public static bool HasProperty(Material self, int nameID) => M(self).Has(nameID);
        // mainTexture／color／mainTextureOffset…先問這個：回 -1 時 Unity 退回 "_MainTex"／"_Color"。替身回 0 會讓它們全部拿 id 0 的屬性（null）
        public static int GetFirstPropertyNameIdByAttribute(Material self, UnityEngine.Rendering.ShaderPropertyFlags flag)
        {
            var s = M(self).Shader;
            if (s == null) return -1;
            if (flag == UnityEngine.Rendering.ShaderPropertyFlags.MainTexture) return s.MainTextureId;
            if (flag == UnityEngine.Rendering.ShaderPropertyFlags.MainColor) return s.MainColorId;
            return -1;
        }
        public static bool HasFloatImpl(Material self, int name) => M(self).Has(name);
        public static bool HasIntImpl(Material self, int name) => M(self).Has(name);
        public static bool HasTextureImpl(Material self, int name) => M(self).Has(name);
        public static bool HasMatrixImpl(Material self, int name) => M(self).Props.Matrices.ContainsKey(name);
        public static bool HasVectorImpl(Material self, int name) => M(self).Has(name);
        public static bool HasBufferImpl(Material self, int name) => false;
        public static bool HasConstantBufferImpl(Material self, int name) => false;
        public static int get_renderQueue(Material self) => M(self).RenderQueue;
        public static void set_renderQueue(Material self, int value) { M(self).CustomRenderQueue = value; M(self).Version++; }
        public static int get_rawRenderQueue(Material self) => M(self).CustomRenderQueue;
        public static void EnableKeyword(Material self, string keyword) { M(self).Keywords.Add(keyword); M(self).Version++; }
        public static void DisableKeyword(Material self, string keyword) { M(self).Keywords.Remove(keyword); M(self).Version++; }
        public static bool IsKeywordEnabled(Material self, string keyword) => M(self).Keywords.Contains(keyword);
        public static void EnableLocalKeyword_Injected(Material self, ref LocalKeyword keyword) => EnableKeyword(self, keyword.m_Name);
        public static void DisableLocalKeyword_Injected(Material self, ref LocalKeyword keyword) => DisableKeyword(self, keyword.m_Name);
        public static void SetLocalKeyword_Injected(Material self, ref LocalKeyword keyword, bool value) { if (value) EnableKeyword(self, keyword.m_Name); else DisableKeyword(self, keyword.m_Name); }
        public static bool IsLocalKeywordEnabled_Injected(Material self, ref LocalKeyword keyword) => IsKeywordEnabled(self, keyword.m_Name);
        public static MaterialGlobalIlluminationFlags get_globalIlluminationFlags(Material self) => M(self).GIFlags;
        public static void set_globalIlluminationFlags(Material self, MaterialGlobalIlluminationFlags value) => M(self).GIFlags = value;
        public static bool get_doubleSidedGI(Material self) => M(self).DoubleSidedGI;
        public static void set_doubleSidedGI(Material self, bool value) => M(self).DoubleSidedGI = value;
        public static bool get_enableInstancing(Material self) => M(self).EnableInstancing;
        public static void set_enableInstancing(Material self, bool value) => M(self).EnableInstancing = value;
        public static int get_passCount(Material self) => 1;
        public static void SetShaderPassEnabled(Material self, string passName, bool enabled) { if (enabled) M(self).DisabledPasses.Remove(passName); else M(self).DisabledPasses.Add(passName); M(self).Version++; }
        public static bool GetShaderPassEnabled(Material self, string passName) => !M(self).DisabledPasses.Contains(passName);
        public static string GetPassName(Material self, int pass) => "";
        public static int FindPass(Material self, string passName) => -1;
        public static void SetOverrideTag(Material self, string tag, string val) => M(self).Tags[tag] = val;
        public static string GetTagImpl(Material self, string tag, bool currentSubShaderOnly, string defaultValue) => M(self).Tags.TryGetValue(tag, out var v) ? v : defaultValue;
        public static void Lerp(Material self, Material start, Material end, float t)
        {
            var m = M(self); var a = M(start); var b = M(end);
            foreach (var kv in a.Props.Floats) if (b.Props.Floats.TryGetValue(kv.Key, out var bv)) m.Props.SetFloat(kv.Key, Mathf.Lerp(kv.Value, bv, t));
            foreach (var kv in a.Props.Vectors) if (b.Props.Vectors.TryGetValue(kv.Key, out var bv)) m.Props.SetVector(kv.Key, Vector4.Lerp(kv.Value, bv, t));
        }
        public static bool SetPass(Material self, int pass) => true;
        public static void CopyPropertiesFromMaterial(Material self, Material mat) { var m = M(self); var s = M(mat); m.Props.CopyFrom(s.Props); m.Keywords.Clear(); m.Keywords.UnionWith(s.Keywords); m.Version++; }
        public static void CopyMatchingPropertiesFromMaterial(Material self, Material mat) => CopyPropertiesFromMaterial(self, mat);
        public static string[] GetShaderKeywords(Material self) => new List<string>(M(self).Keywords).ToArray();
        public static void SetShaderKeywords(Material self, string[] names) { var m = M(self); m.Keywords.Clear(); if (names != null) m.Keywords.UnionWith(names); m.Version++; }
        public static int ComputeCRC(Material self) => M(self).Props.Version ^ M(self).Keywords.Count;
        public static string[] GetTexturePropertyNames(Material self)
        {
            var list = new List<string>();
            foreach (var id in M(self).Props.Textures.Keys) list.Add(PropertyIds.Name(id));
            return list.ToArray();
        }
        public static int[] GetTexturePropertyNameIDs(Material self) => new List<int>(M(self).Props.Textures.Keys).ToArray();
        public static void GetTexturePropertyNamesInternal(Material self, object outNames) { var l = (IList)outNames; l.Clear(); foreach (var n in GetTexturePropertyNames(self)) l.Add(n); }
        public static void GetTexturePropertyNameIDsInternal(Material self, object outNames) { var l = (IList)outNames; l.Clear(); foreach (var n in GetTexturePropertyNameIDs(self)) l.Add(n); }
        public static void SetIntImpl(Material self, int name, int value) { M(self).Props.SetInt(name, value); M(self).Version++; }
        public static void SetFloatImpl(Material self, int name, float value) { M(self).Props.SetFloat(name, value); M(self).Version++; }
        public static void SetTextureImpl(Material self, int name, Texture value) { M(self).Props.SetTexture(name, value); M(self).Version++; }
        public static void SetRenderTextureImpl(Material self, int name, RenderTexture value, RenderTextureSubElement element) => SetTextureImpl(self, name, value);
        public static int GetIntImpl(Material self, int name) => (int)M(self).GetFloat(name);
        public static float GetFloatImpl(Material self, int name) => M(self).GetFloat(name);
        public static Texture GetTextureImpl(Material self, int name) => M(self).GetTexEnv(name).Texture;
        public static void SetFloatArrayImpl(Material self, int name, float[] values, int count) { M(self).Props.Arrays[name] = values[..count]; M(self).Version++; }
        public static void SetVectorArrayImpl(Material self, int name, Vector4[] values, int count) { M(self).Props.Arrays[name] = values[..count]; M(self).Version++; }
        public static void SetColorArrayImpl(Material self, int name, Color[] values, int count) { M(self).Props.Arrays[name] = values[..count]; M(self).Version++; }
        public static void SetMatrixArrayImpl(Material self, int name, Matrix4x4[] values, int count) { M(self).Props.Arrays[name] = values[..count]; M(self).Version++; }
        public static float[] GetFloatArrayImpl(Material self, int name) => M(self).Props.Arrays.TryGetValue(name, out var a) ? (float[])a : null;
        public static Vector4[] GetVectorArrayImpl(Material self, int name) => M(self).Props.Arrays.TryGetValue(name, out var a) ? (Vector4[])a : null;
        public static Color[] GetColorArrayImpl(Material self, int name) => M(self).Props.Arrays.TryGetValue(name, out var a) ? (Color[])a : null;
        public static Matrix4x4[] GetMatrixArrayImpl(Material self, int name) => M(self).Props.Arrays.TryGetValue(name, out var a) ? (Matrix4x4[])a : null;
        public static void SetColorImpl_Injected(Material self, int name, ref Color value) { M(self).Props.SetVector(name, value); M(self).Version++; }
        public static void SetMatrixImpl_Injected(Material self, int name, ref Matrix4x4 value) { M(self).Props.Matrices[name] = value; M(self).Version++; }
        public static void GetColorImpl_Injected(Material self, int name, out Color ret) => ret = M(self).GetVector(name);
        public static void GetMatrixImpl_Injected(Material self, int name, out Matrix4x4 ret) => ret = M(self).Props.Matrices.TryGetValue(name, out var m) ? m : Matrix4x4.identity;
        public static void GetTextureScaleAndOffsetImpl_Injected(Material self, int name, out Vector4 ret) { var e = M(self).GetTexEnv(name); ret = new Vector4(e.Scale.x, e.Scale.y, e.Offset.x, e.Offset.y); }
        public static void SetTextureOffsetImpl_Injected(Material self, int name, ref Vector2 offset) { M(self).Props.SetScaleOffset(name, null, offset); M(self).Version++; }
        public static void SetTextureScaleImpl_Injected(Material self, int name, ref Vector2 scale) { M(self).Props.SetScaleOffset(name, scale, null); M(self).Version++; }
        public static void SetVectorImpl_Injected(Material self, int name, ref Vector4 value) { M(self).Props.SetVector(name, value); M(self).Version++; }
        public static void GetVectorImpl_Injected(Material self, int name, out Vector4 ret) => ret = M(self).GetVector(name);
    }

    /// <summary>Unity 內建的預設材質</summary>
    public static class Defaults
    {
        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        public static Material Get(string name, string shader)
        {
            if (cache.TryGetValue(name, out var m) && m != null) return m;
            m = (Material)Native.NewShell(typeof(Material));
            Native.Bind(new NMaterial { Name = name, Shader = Native.Of<NShader>(Shaders.ByName(shader)) }, m, true);
            cache[name] = m;
            return m;
        }
    }

    public static unsafe partial class UnityEngine_MaterialPropertyBlock
    {
        static readonly Dictionary<IntPtr, PropertySheet> sheets = new Dictionary<IntPtr, PropertySheet>();
        static long next = 1;
        public static PropertySheet Of(MaterialPropertyBlock b) => b == null ? null : sheets.TryGetValue(b.m_Ptr, out var s) ? s : (sheets[b.m_Ptr] = new PropertySheet());
        static PropertySheet P(MaterialPropertyBlock b) => Of(b);

        public static IntPtr CreateImpl() { var p = (IntPtr)next++; sheets[p] = new PropertySheet(); return p; }
        public static void DestroyImpl(IntPtr mpb) => sheets.Remove(mpb);
        public static bool get_isEmpty(MaterialPropertyBlock self) => P(self).IsEmpty;
        public static void Clear(MaterialPropertyBlock self, bool keepMemory) => P(self).Clear();
        public static int GetIntImpl(MaterialPropertyBlock self, int name) => P(self).GetInt(name);
        public static float GetFloatImpl(MaterialPropertyBlock self, int name) => P(self).GetFloat(name);
        public static Texture GetTextureImpl(MaterialPropertyBlock self, int name) => P(self).Textures.TryGetValue(name, out var e) ? e.Texture : null;
        public static bool HasPropertyImpl(MaterialPropertyBlock self, int name) { var p = P(self); return p.Floats.ContainsKey(name) || p.Vectors.ContainsKey(name) || p.Textures.ContainsKey(name) || p.Matrices.ContainsKey(name); }
        public static bool HasFloatImpl(MaterialPropertyBlock self, int name) => P(self).Floats.ContainsKey(name);
        public static bool HasIntImpl(MaterialPropertyBlock self, int name) => P(self).Ints.ContainsKey(name);
        public static bool HasTextureImpl(MaterialPropertyBlock self, int name) => P(self).Textures.ContainsKey(name);
        public static bool HasMatrixImpl(MaterialPropertyBlock self, int name) => P(self).Matrices.ContainsKey(name);
        public static bool HasVectorImpl(MaterialPropertyBlock self, int name) => P(self).Vectors.ContainsKey(name);
        public static bool HasBufferImpl(MaterialPropertyBlock self, int name) => false;
        public static bool HasConstantBufferImpl(MaterialPropertyBlock self, int name) => false;
        public static void SetIntImpl(MaterialPropertyBlock self, int name, int value) => P(self).SetInt(name, value);
        public static void SetFloatImpl(MaterialPropertyBlock self, int name, float value) => P(self).SetFloat(name, value);
        public static void SetTextureImpl(MaterialPropertyBlock self, int name, Texture value) => P(self).SetTexture(name, value);
        public static void SetRenderTextureImpl(MaterialPropertyBlock self, int name, RenderTexture value, RenderTextureSubElement element) => P(self).SetTexture(name, value);
        public static void SetFloatArrayImpl(MaterialPropertyBlock self, int name, float[] values, int count) => P(self).Arrays[name] = values[..count];
        public static void SetVectorArrayImpl(MaterialPropertyBlock self, int name, Vector4[] values, int count) => P(self).Arrays[name] = values[..count];
        public static void SetMatrixArrayImpl(MaterialPropertyBlock self, int name, Matrix4x4[] values, int count) => P(self).Arrays[name] = values[..count];
        public static float[] GetFloatArrayImpl(MaterialPropertyBlock self, int name) => P(self).Arrays.TryGetValue(name, out var a) ? (float[])a : null;
        public static Vector4[] GetVectorArrayImpl(MaterialPropertyBlock self, int name) => P(self).Arrays.TryGetValue(name, out var a) ? (Vector4[])a : null;
        public static Matrix4x4[] GetMatrixArrayImpl(MaterialPropertyBlock self, int name) => P(self).Arrays.TryGetValue(name, out var a) ? (Matrix4x4[])a : null;
        public static void GetVectorImpl_Injected(MaterialPropertyBlock self, int name, out Vector4 ret) => ret = P(self).GetVector(name);
        public static void GetColorImpl_Injected(MaterialPropertyBlock self, int name, out Color ret) => ret = P(self).GetVector(name);
        public static void GetMatrixImpl_Injected(MaterialPropertyBlock self, int name, out Matrix4x4 ret) => ret = P(self).Matrices.TryGetValue(name, out var m) ? m : Matrix4x4.identity;
        public static void SetVectorImpl_Injected(MaterialPropertyBlock self, int name, ref Vector4 value) => P(self).SetVector(name, value);
        public static void SetColorImpl_Injected(MaterialPropertyBlock self, int name, ref Color value) => P(self).SetVector(name, value);
        public static void SetMatrixImpl_Injected(MaterialPropertyBlock self, int name, ref Matrix4x4 value) { P(self).Matrices[name] = value; P(self).Version++; }
    }

    public static unsafe partial class UnityEngine_AudioClip
    {
        static NAudioClip A(AudioClip a) => Native.Self<NAudioClip>(a);
        public static AudioClip Construct_Internal() { var a = (AudioClip)Native.NewShell(typeof(AudioClip)); Native.Bind(new NAudioClip(), a); return a; }
        public static string GetName(AudioClip self) => A(self).Name;
        public static void CreateUserSound(AudioClip self, string name, int lengthSamples, int channels, int frequency, bool stream)
        { var a = A(self); a.Name = name ?? ""; a.Samples = lengthSamples; a.Channels = channels; a.Frequency = frequency; a.Stream = stream; a.Data = new float[lengthSamples * channels]; a.LoadState = AudioDataLoadState.Loaded; }
        public static float get_length(AudioClip self) { var a = A(self); return a.Frequency > 0 ? (float)a.Samples / a.Frequency : 0; }
        public static int get_samples(AudioClip self) => A(self).Samples;
        public static int get_channels(AudioClip self) => A(self).Channels;
        public static int get_frequency(AudioClip self) => A(self).Frequency;
        public static bool get_isReadyToPlay(AudioClip self) => true;
        public static AudioClipLoadType get_loadType(AudioClip self) => A(self).LoadType;
        public static bool LoadAudioData(AudioClip self) => AudioEngine.Prepare(A(self));
        public static bool UnloadAudioData(AudioClip self) { A(self).LoadState = AudioDataLoadState.Unloaded; return true; }
        public static bool get_preloadAudioData(AudioClip self) => A(self).Preload;
        public static bool get_ambisonic(AudioClip self) => false;
        public static bool get_loadInBackground(AudioClip self) => A(self).LoadInBackground;
        public static AudioDataLoadState get_loadState(AudioClip self) => A(self).Preload ? AudioDataLoadState.Loaded : A(self).LoadState;
        public static bool GetData(AudioClip clip, float[] data, int numSamples, int samplesOffset)
        {
            var a = A(clip);
            if (a.Data == null && !AudioEngine.Decode(a)) return false;
            Array.Copy(a.Data, samplesOffset * a.Channels, data, 0, Math.Min(data.Length, a.Data.Length - samplesOffset * a.Channels));
            return true;
        }
        public static bool SetData(AudioClip clip, float[] data, int numsamples, int samplesOffset)
        {
            var a = A(clip);
            a.Data ??= new float[a.Samples * Math.Max(1, a.Channels)];
            Array.Copy(data, 0, a.Data, samplesOffset * a.Channels, Math.Min(data.Length, a.Data.Length - samplesOffset * a.Channels));
            return true;
        }
    }
}

namespace UnityNative
{
    /// <summary>sprite 的網格：匯入器的 sprite-meshes/&lt;guid&gt;.json（只有 position），UV 照 textureRect 推</summary>
    public static class SpriteMesh
    {
        public static void Ensure(NSprite s)
        {
            if (s.MeshLoaded) return;
            s.MeshLoaded = true;
            JsonObject m = s.InlineMesh;
            if (m == null && s.MeshFile != null && File.Exists(s.MeshFile)) m = Assets.ParseFile(s.MeshFile);
            if (m?["position"] is JsonArray pos && m["indices"] is JsonArray idx)
            {
                int dim = (int)(m["dimensions"]?["position"] != null ? Serializer.D(m["dimensions"]["position"]) : 3);
                int n = pos.Count / dim;
                s.Vertices = new Vector2[n];
                for (int i = 0; i < n; i++) s.Vertices[i] = new Vector2((float)Serializer.D(pos[i * dim]), (float)Serializer.D(pos[i * dim + 1]));
                s.Triangles = new ushort[idx.Count];
                for (int i = 0; i < idx.Count; i++) s.Triangles[i] = (ushort)Serializer.D(idx[i]);
            }
            else
            {
                // 沒有網格：整個 rect 一個四邊形（FullRect）
                var r = s.Rect; float ppu = s.PixelsPerUnit <= 0 ? 100 : s.PixelsPerUnit;
                float x0 = -s.Pivot.x * r.width / ppu, y0 = -s.Pivot.y * r.height / ppu, x1 = x0 + r.width / ppu, y1 = y0 + r.height / ppu;
                s.Vertices = new[] { new Vector2(x0, y1), new Vector2(x1, y1), new Vector2(x0, y0), new Vector2(x1, y0) };
                s.Triangles = new ushort[] { 0, 1, 2, 2, 1, 3 };
            }
            s.UV = UVs(s, s.Vertices);
        }

        public static Vector2[] UVs(NSprite s, Vector2[] verts)
        {
            float tw = s.Texture?.Width ?? 1, th = s.Texture?.Height ?? 1, ppu = s.PixelsPerUnit <= 0 ? 100 : s.PixelsPerUnit;
            var uv = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                float px = verts[i].x * ppu + s.Pivot.x * s.Rect.width, py = verts[i].y * ppu + s.Pivot.y * s.Rect.height;
                uv[i] = new Vector2((s.TextureRect.x + px - s.TextureRectOffset.x) / tw, (s.TextureRect.y + py - s.TextureRectOffset.y) / th);
            }
            return uv;
        }
    }

    /// <summary>PNG 解碼（腳本要讀像素時才用；繪製端自己讀檔）</summary>
    public static class Png
    {
        public static Func<string, (byte[] rgba, int w, int h)> Decoder;
        public static byte[] Decode(string file, out int w, out int h)
        {
            w = h = 0;
            if (file == null || Decoder == null) return null;
            var r = Decoder(file);
            w = r.w; h = r.h;
            return r.rgba;
        }
    }
}
