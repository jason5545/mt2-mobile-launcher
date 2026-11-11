// 一次繪製：選 program、填 cbuffer、綁貼圖、設 render state、送頂點。
// cbuffer 的值依名稱找：Unity 內建的（矩陣、時間、螢幕參數）→ 這次繪製的覆寫（_ClipRect、_MainTex…）→ MaterialPropertyBlock
// → 材質 → 全域（Shader.SetGlobal*）→ shader 的預設值。專案是 linear 色彩空間：Color 型別的屬性轉成線性再送。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative.Gl
{
    public sealed class CameraState
    {
        public Matrix4x4 V = Matrix4x4.identity, P = Matrix4x4.identity, VP = Matrix4x4.identity, InvV = Matrix4x4.identity;
        public Vector3 Position;
        public float Near = 0.3f, Far = 1000f;
        public bool Ortho;
        public float OrthoW, OrthoH;
        public int Width, Height;
        public bool Overlay;
    }

    /// <summary>每次繪製可以覆寫的屬性（uGUI 的 _ClipRect、sprite 的貼圖與顏色）</summary>
    public sealed class Overrides
    {
        public readonly PropertySheet Sheet = new PropertySheet();
        public Texture MainTex;
        public GpuTexture MainGpu;
        public void Clear() { Sheet.Clear(); MainTex = null; MainGpu = null; }
    }

    public static unsafe class Draw
    {
        public static CameraState Cam = new CameraState();
        static readonly int idMainTex = PropertyIds.Of("_MainTex");
        static uint[] ubos = new uint[16];   // 每個 slot 目前的內容在哪個 UBO（從 uboPool 拿）
        static readonly UboContent[] uboLast = new UboContent[16];
        // uniform 緩衝池：內容變了就換池裡下一個 UBO（每格從頭輪），同一個緩衝一格只 glBufferData 一次。
        // 以前每個 slot 固定一個 UBO，一格重傳幾百次，手機（Adreno）每次都要等 GPU 或複製：Diceomancer 寬版戰鬥 564 個 draw 一格 5 ms
        static readonly List<uint> uboPool = new List<uint>();
        static int uboNext, uboFrame = -1;
        static uint NextUbo()
        {
            if (uboNext == uboPool.Count) uboPool.Add(GL.Gen(GL.glGenBuffers));
            return uboPool[uboNext++];
        }

        // 這一格傳過的 uniform 內容：雜湊 → 那份內容與它的 UBO。內容一樣就直接綁那個 UBO、不重傳（池裡的 UBO 一格內不會被覆寫）。
        // 繪製順序在幾個材質之間交替時（卡片的各個 sprite、UI 的批），同一份 $Globals 以前一格要重傳好幾次
        sealed class UboContent { public uint Ubo; public byte[] Data = Array.Empty<byte>(); public int Len; }
        static readonly Dictionary<ulong, UboContent> uboByContent = new Dictionary<ulong, UboContent>();
        static readonly List<UboContent> uboContents = new List<UboContent>();
        static int uboContentNext;

        static ulong Hash(byte[] d)
        {
            var u = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(d);
            ulong h = 14695981039346656037UL ^ (ulong)d.Length;
            foreach (var x in u) h = (h ^ x) * 1099511628211UL; // FNV-1a，一次 8 位元組（對到之後還會逐位元組比對）
            for (int i = u.Length * 8; i < d.Length; i++) h = (h ^ d[i]) * 1099511628211UL;
            return h;
        }

        static bool Same(UboContent c, byte[] d) => c != null && c.Len == d.Length && d.AsSpan().SequenceEqual(c.Data.AsSpan(0, c.Len));

        /// <summary>這份內容（block 的 Data）在這一格的 UBO：傳過就沿用，沒傳過就從池裡拿一個新的傳上去</summary>
        static UboContent UboFor(byte[] d)
        {
            ulong h = Hash(d);
            if (uboByContent.TryGetValue(h, out var c) && Same(c, d)) return c;
            if (uboContentNext == uboContents.Count) uboContents.Add(new UboContent());
            c = uboContents[uboContentNext++];
            if (c.Data.Length < d.Length) c.Data = new byte[d.Length];
            d.AsSpan().CopyTo(c.Data);
            c.Len = d.Length;
            c.Ubo = NextUbo();
            GL.glBindBuffer(GL.UNIFORM_BUFFER, c.Ubo);
            fixed (byte* p = d) GL.glBufferData(GL.UNIFORM_BUFFER, d.Length, p, GL.STREAM_DRAW);
            uboByContent[h] = c;
            return c;
        }
        static readonly uint[] boundUbo = new uint[64];
        public static int DrawCalls, StateChanges;
        static readonly Dictionary<string, (bool color, bool hdr)> colorProps = new Dictionary<string, (bool, bool)>();

        public static void Init()
        {
            for (int i = 0; i < ubos.Length; i++) ubos[i] = GL.Gen(GL.glGenBuffers);
        }

        // ───────── cbuffer ─────────

        /// <summary>攝影機設定（Cam）換一次加一：只看攝影機的 uniform block 靠它判斷能不能沿用</summary>
        public static int CamGen;
        // 只看時間、攝影機、model 的內建變數（Builtin／BuiltinMatrix 的 switch 裡、不讀材質與覆寫的那些）
        static readonly HashSet<string> CamVectors = new HashSet<string> { "_Time", "_SinTime", "_CosTime", "unity_DeltaTime", "_ScreenParams", "_ProjectionParams",
            "_ZBufferParams", "unity_OrthoParams", "_WorldSpaceCameraPos", "unity_WorldTransformParams", "unity_LODFade", "unity_ColorSpaceLuminance", "unity_SpriteProps",
            "_ScaledScreenParams", "_TimeParameters", "_GlobalMipBias" };
        static readonly HashSet<string> CamMatrices = new HashSet<string> { "unity_ObjectToWorld", "unity_WorldToObject", "unity_MatrixVP", "unity_MatrixV", "unity_MatrixInvV",
            "glstate_matrix_projection", "unity_MatrixP", "unity_CameraProjection", "unity_CameraInvProjection", "unity_MatrixMVP", "unity_MatrixMV", "unity_CameraToWorld", "unity_WorldToCamera" };

        /// <summary>覆寫（ov.Sheet）的內容摘要：uniform 快取的 key 之一（UI 每批的 _ClipRect、_UIMaskSoftness 之類）</summary>
        static int OvSignature(Overrides ov)
        {
            if (ov == null) return 0;
            int h = 17;
            foreach (var kv in ov.Sheet.Vectors) h = HashCode.Combine(h, kv.Key, kv.Value);
            foreach (var kv in ov.Sheet.Floats) h = HashCode.Combine(h, kv.Key, kv.Value);
            return h;
        }

        static bool IsColor(NMaterial m, string name)
        {
            var sh = m?.Shader;
            if (sh == null) return false;
            if (sh.IsColorCache.TryGetValue(name, out var r)) return r;
            r = name == "_Color" || name.EndsWith("Color");
            foreach (var p in sh.Props) if (p.name == name) { r = p.type == 0; break; }
            // 標了 [HDR] 的顏色不轉線性（同 TS 版 sceneRenderer.materialColorConstant：是推論，2022.3 的文件沒寫；依據是跟原版 Windows 2.2.1 的畫面對照，
            // 戰鬥房間的火盆 Shader Graphs/FireVertical 的 HDR 色轉了線性就到不了原版的橘黃）。火堆籠子的 FireBurst（_Color r＝13.8～30.2）轉了線性會亮到 Bloom 整片紅
            if (r && sh.HdrColors.Contains(PropertyIds.Of(name))) r = false;
            sh.IsColorCache[name] = r;
            return r;
        }

        static Vector4 Lin(Vector4 c) => new Vector4(Mathf.GammaToLinearSpace(c.x), Mathf.GammaToLinearSpace(c.y), Mathf.GammaToLinearSpace(c.z), c.w);

        /// <summary>一般屬性的值：這次繪製的覆寫 → MaterialPropertyBlock → 材質 → shader 預設 → 全域；「是不是顏色」記在欄位上（同一個 shader 只查一次）</summary>
        static bool TryVector(CbField f, NMaterial mat, PropertySheet block, Overrides ov, out Vector4 v)
        {
            int id = f.Id;
            if (ov != null && ov.Sheet.Vectors.TryGetValue(id, out v)) return true;
            if (ov != null && ov.Sheet.Floats.TryGetValue(id, out var f0)) { v = new Vector4(f0, 0, 0, 0); return true; }
            if (block != null)
            {
                if (block.Vectors.TryGetValue(id, out v)) { if (IsColor(mat, f)) v = Lin(v); return true; }
                if (block.Floats.TryGetValue(id, out var bf)) { v = new Vector4(bf, 0, 0, 0); return true; }
            }
            if (mat != null)
            {
                if (mat.Props.Vectors.TryGetValue(id, out v) || (mat.Shader != null && mat.Shader.Defaults.Vectors.TryGetValue(id, out v)))
                {
                    if (IsColor(mat, f)) v = Lin(v);
                    return true;
                }
                if (mat.Props.Floats.TryGetValue(id, out var mf) || (mat.Shader != null && mat.Shader.Defaults.Floats.TryGetValue(id, out mf))) { v = new Vector4(mf, 0, 0, 0); return true; }
            }
            if (Icalls.UnityEngine_Shader.TryGlobalVector(id, out v)) return true;
            if (Icalls.UnityEngine_Shader.TryGlobalFloat(id, out var gf)) { v = new Vector4(gf, 0, 0, 0); return true; }
            v = default;
            return false;
        }

        static bool IsColor(NMaterial m, CbField f)
        {
            var sh = m?.Shader;
            if (sh == null) return false;
            if (f.ColorShader != sh) { f.ColorIs = IsColor(m, f.Name); f.ColorShader = sh; }
            return f.ColorIs;
        }

        // 欄位種類（CbField.Kind）：第一次用到時照名字定下來（Resolve），之後每次 draw 直接照種類算，不再比對字串、切 _ST／_TexelSize 的名字。
        // 「是不是內建變數」本來就只看名字（以前 Builtin 的 found），算出來的值跟以前同一套
        const sbyte KGeneric = 0, KTime = 1, KSinTime = 2, KCosTime = 3, KDeltaTime = 4, KScreenParams = 5, KScaledScreenParams = 6, KTimeParameters = 7,
            KGlobalMipBias = 8, KProjectionParams = 9, KZBufferParams = 10, KOrthoParams = 11, KCameraPos = 12, KWorldTransformParams = 13, KLODFade = 14,
            KColorSpaceLuminance = 15, KRendererColor = 16, KSpriteProps = 17, KFlip = 18, KST = 19, KTexelSize = 20, KHDR = 21;
        const sbyte MGeneric = 0, MObjectToWorld = 1, MWorldToObject = 2, MVP = 3, MV = 4, MInvV = 5, MP = 6, MCameraProjection = 7, MCameraInvProjection = 8,
            MMVP = 9, MMV = 10, MCameraToWorld = 11, MWorldToCamera = 12;
        static readonly int idRendererColor = PropertyIds.Of("_RendererColor"), idFlip = PropertyIds.Of("_Flip");

        static void Resolve(CbField f)
        {
            var name = f.Name;
            if (f.Id == int.MinValue) f.Id = PropertyIds.Of(name);
            if (f.Matrix)
            {
                f.Kind = name switch
                {
                    "unity_ObjectToWorld" => MObjectToWorld, "unity_WorldToObject" => MWorldToObject, "unity_MatrixVP" => MVP, "unity_MatrixV" => MV,
                    "unity_MatrixInvV" => MInvV, "glstate_matrix_projection" => MP, "unity_MatrixP" => MP, "unity_CameraProjection" => MCameraProjection,
                    "unity_CameraInvProjection" => MCameraInvProjection, "unity_MatrixMVP" => MMVP, "unity_MatrixMV" => MMV, "unity_CameraToWorld" => MCameraToWorld,
                    "unity_WorldToCamera" => MWorldToCamera, _ => MGeneric,
                };
                return;
            }
            f.Kind = name switch
            {
                "_Time" => KTime, "_SinTime" => KSinTime, "_CosTime" => KCosTime, "unity_DeltaTime" => KDeltaTime, "_ScreenParams" => KScreenParams,
                "_ScaledScreenParams" => KScaledScreenParams, "_TimeParameters" => KTimeParameters, "_GlobalMipBias" => KGlobalMipBias,
                "_ProjectionParams" => KProjectionParams, "_ZBufferParams" => KZBufferParams, "unity_OrthoParams" => KOrthoParams,
                "_WorldSpaceCameraPos" => KCameraPos, "unity_WorldTransformParams" => KWorldTransformParams, "unity_LODFade" => KLODFade,
                "unity_ColorSpaceLuminance" => KColorSpaceLuminance, "unity_SpriteRendererColor" => KRendererColor, "_RendererColor" => KRendererColor,
                "unity_SpriteProps" => KSpriteProps, "_Flip" => KFlip, "unity_SpriteFlip" => KFlip, _ => KGeneric,
            };
            if (f.Kind == KGeneric)
            {
                if (name.EndsWith("_ST")) { f.Kind = KST; f.TexId = PropertyIds.Of(name.Substring(0, name.Length - 3)); }
                else if (name.EndsWith("_TexelSize")) { f.Kind = KTexelSize; f.TexName = name.Substring(0, name.Length - 10); }
                else if (name.EndsWith("_HDR")) f.Kind = KHDR;
            }
            f.NotBuiltin = f.Kind == KGeneric;
        }

        static Vector4 Builtin(CbField f, Matrix4x4 model, NMaterial mat, PropertySheet block, Overrides ov)
        {
            var t = (float)Engine.Time.Time;
            switch (f.Kind)
            {
                case KTime: return new Vector4(t / 20f, t, t * 2f, t * 3f);
                case KSinTime: return new Vector4(MathF.Sin(t / 8f), MathF.Sin(t / 4f), MathF.Sin(t / 2f), MathF.Sin(t));
                case KCosTime: return new Vector4(MathF.Cos(t / 8f), MathF.Cos(t / 4f), MathF.Cos(t / 2f), MathF.Cos(t));
                case KDeltaTime: { var d = Engine.Time.DeltaTime; return new Vector4(d, 1f / Math.Max(d, 1e-6f), Engine.Time.SmoothDeltaTime, 1f / Math.Max(Engine.Time.SmoothDeltaTime, 1e-6f)); }
                case KScreenParams: return new Vector4(Cam.Width, Cam.Height, 1f + 1f / Cam.Width, 1f + 1f / Cam.Height);
                // URP 每台攝影機設的全域值（UniversalRenderPipeline 的 SetupPerCameraShaderConstants、SetShaderTimeValues）：
                // render scale 1，_ScaledScreenParams 同 _ScreenParams；_TimeParameters ＝ (t, sin t, cos t, 0)；_GlobalMipBias ＝ (bias, 2^bias)，bias 0
                case KScaledScreenParams: return new Vector4(Cam.Width, Cam.Height, 1f + 1f / Cam.Width, 1f + 1f / Cam.Height);
                case KTimeParameters: return new Vector4(t, MathF.Sin(t), MathF.Cos(t), 0);
                case KGlobalMipBias: return new Vector4(0, 1, 0, 0);
                case KProjectionParams: return new Vector4(1f, Cam.Near, Cam.Far, 1f / Cam.Far);
                case KZBufferParams: { float x = 1f - Cam.Far / Cam.Near; return new Vector4(x, Cam.Far / Cam.Near, x / Cam.Far, (Cam.Far / Cam.Near) / Cam.Far); }
                case KOrthoParams: return new Vector4(Cam.OrthoW, Cam.OrthoH, 0, Cam.Ortho ? 1 : 0);
                case KCameraPos: return new Vector4(Cam.Position.x, Cam.Position.y, Cam.Position.z, 1);
                case KWorldTransformParams: return new Vector4(0, 0, 0, model.determinant < 0 ? -1 : 1);
                case KLODFade: return new Vector4(1, 1, 0, 0);
                case KColorSpaceLuminance: return new Vector4(0.0396819152f, 0.458021790f, 0.00609653955f, 1f);
                case KRendererColor:
                    if (ov != null && ov.Sheet.Vectors.TryGetValue(idRendererColor, out var rc)) return rc;
                    return Vector4.one;
                case KSpriteProps: return new Vector4(1, 1, 0, 0);
                case KFlip:
                    if (ov != null && ov.Sheet.Vectors.TryGetValue(idFlip, out var fl)) return fl;
                    return new Vector4(1, 1, 0, 0);
                case KST:
                {
                    if (TryVector(f, mat, block, ov, out var st)) return st;
                    TexEnv e = default;
                    if (block != null && block.Textures.TryGetValue(f.TexId, out var be)) e = be;
                    else if (mat != null) e = mat.GetTexEnv(f.TexId);
                    if (e.Scale == default) e.Scale = Vector2.one;
                    return new Vector4(e.Scale.x, e.Scale.y, e.Offset.x, e.Offset.y);
                }
                case KTexelSize:
                {
                    var g = TextureFor(f.TexName, mat, block, ov, null);
                    return g == null || g.Width == 0 ? new Vector4(1, 1, 1, 1) : new Vector4(1f / g.Width, 1f / g.Height, g.Width, g.Height);
                }
                case KHDR: return new Vector4(1, 1, 0, 0);
            }
            return default;
        }

        /// <summary>GL 慣例的（投影 × …）矩陣 → D3D 的 clip z（反向深度：near ＝ w、far ＝ 0）：z_d3d ＝ (w − z_gl) / 2，只換第三列。
        /// 轉出來的 shader 是 DX11 bytecode，拿 clip z 算東西的地方照這個慣例寫死（CharacterShader2.0 的霧：max((1 − z/near)·far, 0)）</summary>
        public static Matrix4x4 GpuProjection(Matrix4x4 m)
        {
            m.m20 = 0.5f * (m.m30 - m.m20); m.m21 = 0.5f * (m.m31 - m.m21); m.m22 = 0.5f * (m.m32 - m.m22); m.m23 = 0.5f * (m.m33 - m.m23);
            return m;
        }

        static Matrix4x4 BuiltinMatrix(CbField f, Matrix4x4 model, NMaterial mat, PropertySheet block)
        {
            switch (f.Kind)
            {
                case MObjectToWorld: return model;
                case MWorldToObject: { MathUtil.Inverse(model, out var inv); return inv; }
                // shader 拿到的投影是 D3D 的 GPU 投影（GL.GetGPUProjectionMatrix 的反向深度），vertex 最後換回 GL（ShaderSource.D3dClipToGl）；
                // unity_CameraProjection 是攝影機自己的投影（GL 慣例，Unity 也不換）。同 TS 版 sceneRenderer 的 d3dProjection
                case MVP: return GpuProjection(Cam.VP);
                case MV: return Cam.V;
                case MInvV: return Cam.InvV;
                case MP: return GpuProjection(Cam.P);
                case MCameraProjection: return Cam.P;
                case MCameraInvProjection: { MathUtil.Inverse(Cam.P, out var inv); return inv; }
                case MMVP: return GpuProjection(MathUtil.Mul(Cam.VP, model));
                case MMV: return MathUtil.Mul(Cam.V, model);
                case MCameraToWorld: return Cam.InvV;
                case MWorldToCamera: return Cam.V;
            }
            if (block != null && block.Matrices.TryGetValue(f.Id, out var bm)) return bm;
            if (mat != null && mat.Props.Matrices.TryGetValue(f.Id, out var mm)) return mm;
            if (Icalls.UnityEngine_Shader.TryGlobalMatrix(f.Id, out var gm)) return gm;
            return Matrix4x4.identity;
        }

        static void Fill(GpuBlock b, Matrix4x4 model, NMaterial mat, PropertySheet block, Overrides ov)
        {
            var d = b.Data;
            fixed (byte* p = d)
            {
                float* f = (float*)p;
                foreach (var fld in b.Fields)
                {
                    int o = fld.Offset / 4;
                    if (fld.Matrix)
                    {
                        if (fld.ArraySize > 0)
                        {
                            var arr = ArrayOf(fld.Name, mat, block) as Matrix4x4[];
                            for (int i = 0; i < fld.ArraySize && arr != null && i < arr.Length; i++) WriteMatrix(f, o + i * fld.Rows * 4, arr[i], fld.Rows, d.Length / 4);
                            continue;
                        }
                        if (fld.Kind < 0) Resolve(fld);
                        var m = BuiltinMatrix(fld, model, mat, block);
                        WriteMatrix(f, o, m, fld.Rows <= 0 ? 4 : fld.Rows, d.Length / 4);
                        continue;
                    }
                    if (fld.ArraySize > 0)
                    {
                        var arr = ArrayOf(fld.Name, mat, block);
                        if (arr is Vector4[] va) for (int i = 0; i < fld.ArraySize && i < va.Length; i++) Write(f, o + i * 4, va[i], 4, d.Length / 4);
                        else if (arr is float[] fa) for (int i = 0; i < fld.ArraySize && i < fa.Length; i++) Write(f, o + i * 4, new Vector4(fa[i], 0, 0, 0), 1, d.Length / 4);
                        else if (arr is Color[] ca) for (int i = 0; i < fld.ArraySize && i < ca.Length; i++) Write(f, o + i * 4, Lin(ca[i]), 4, d.Length / 4);
                        continue;
                    }
                    if (fld.Kind < 0) Resolve(fld);
                    Vector4 v;
                    if (fld.Kind != KGeneric) v = Builtin(fld, model, mat, block, ov);
                    else if (!TryVector(fld, mat, block, ov, out v)) v = default;
                    Write(f, o, v, fld.Dim <= 0 ? 4 : fld.Dim, d.Length / 4);
                }
            }
        }

        static Array ArrayOf(string name, NMaterial mat, PropertySheet block)
        {
            var id = PropertyIds.Of(name);
            if (block != null && block.Arrays.TryGetValue(id, out var a)) return a;
            if (mat != null && mat.Props.Arrays.TryGetValue(id, out a)) return a;
            return null;
        }

        static void Write(float* f, int o, Vector4 v, int dim, int max)
        {
            if (o + dim > max) return;
            f[o] = v.x;
            if (dim > 1) f[o + 1] = v.y;
            if (dim > 2) f[o + 2] = v.z;
            if (dim > 3) f[o + 3] = v.w;
        }

        /// <summary>D3D cbuffer 的矩陣：一欄一個 float4（column-major），rows 是欄數</summary>
        static void WriteMatrix(float* f, int o, Matrix4x4 m, int cols, int max)
        {
            for (int c = 0; c < cols; c++)
            {
                if (o + c * 4 + 4 > max) return;
                f[o + c * 4] = m[0, c]; f[o + c * 4 + 1] = m[1, c]; f[o + c * 4 + 2] = m[2, c]; f[o + c * 4 + 3] = m[3, c];
            }
        }

        // ───────── 貼圖 ─────────

        static GpuTexture TextureFor(string name, NMaterial mat, PropertySheet block, Overrides ov, string fallback)
        {
            var id = PropertyIds.Of(name);
            if (ov != null && id == idMainTex && (ov.MainGpu != null || (object)ov.MainTex != null)) return ov.MainGpu ?? GpuResources.Of(ov.MainTex);
            if (ov != null && ov.Sheet.Textures.TryGetValue(id, out var oe) && (object)oe.Texture != null) return GpuResources.Of(oe.Texture);
            if (block != null && block.Textures.TryGetValue(id, out var be) && (object)be.Texture != null) return GpuResources.Of(be.Texture);
            if (mat != null)
            {
                var e = mat.GetTexEnv(id);
                if ((object)e.Texture != null) return GpuResources.Of(e.Texture, DefaultName(mat, name) ?? fallback ?? "white");
            }
            if (Icalls.UnityEngine_Shader.TryGlobalTexture(id, out var gt) && (object)gt != null) return GpuResources.Of(gt);
            return GpuResources.Default(DefaultName(mat, name) ?? fallback ?? "white");
        }

        static string DefaultName(NMaterial mat, string tex)
        {
            var sh = mat?.Shader;
            if (sh == null) return null;
            if (sh.DefaultTexNames == null)
            {
                sh.DefaultTexNames = new Dictionary<string, string>();
                if (sh.Record?["m_PropInfo"] is JsonArray idx)
                    foreach (var p in idx)
                    {
                        var n = p?["m_Name"]?.ToString();
                        if (n != null) sh.DefaultTexNames.TryAdd(n, p["m_DefTexture"]?["m_DefaultName"]?.ToString());
                    }
            }
            return sh.DefaultTexNames.TryGetValue(tex, out var d) ? d : null;
        }

        // ───────── render state ─────────

        static readonly int idStencilComp = PropertyIds.Of("_StencilComp"), idStencilOp = PropertyIds.Of("_StencilOp");

        /// <summary>pass 的一個 render state 值，預先從 JSON 編好：常數，或綁屬性（prop）加備用值（val）。Get 的判斷跟 Val 一樣</summary>
        public sealed class StateVal
        {
            public readonly bool Present, IsConst, HasVal;
            readonly int constant, propId = int.MinValue, val;

            public StateVal(JsonNode n)
            {
                if (n == null) return;
                Present = true;
                if (n is JsonValue) { IsConst = true; constant = (int)Serializer.D(n); return; }
                var prop = n["prop"]?.ToString();
                if (!string.IsNullOrEmpty(prop) && prop != "<noninit>") propId = PropertyIds.Of(prop);
                if (n["val"] != null) { HasVal = true; val = (int)Serializer.D(n["val"]); }
            }

            public int Get(NMaterial mat, PropertySheet block, int def)
            {
                if (!Present) return def;
                if (IsConst) return constant;
                if (propId != int.MinValue)
                {
                    if (block != null && block.Floats.TryGetValue(propId, out var bf)) return (int)bf;
                    if (mat != null && (mat.Props.Floats.ContainsKey(propId) || mat.Props.Ints.ContainsKey(propId) || (mat.Shader?.Defaults.Floats.ContainsKey(propId) ?? false))) return (int)mat.GetFloat(propId);
                    if (Icalls.UnityEngine_Shader.TryGlobalFloat(propId, out var gf)) return (int)gf;
                }
                return HasVal ? val : def;
            }
        }

        public sealed class CompiledState
        {
            public static readonly CompiledState Empty = new CompiledState(null);
            public readonly StateVal Src, Dst, SrcA, DstA, Op, OpA, Cull, ColorMask, ZWrite, ZTest, StencilRef, StencilReadMask, StencilWriteMask, StencilComp, StencilPass;
            /// <summary>colorMask 是綁屬性的物件時那個屬性的 id（材質沒設這個屬性、值算出 0 時當 15）；不是就 int.MinValue</summary>
            public readonly int ColorMaskPropId = int.MinValue;

            public CompiledState(JsonObject s)
            {
                var bl = s?["blend"] as JsonObject;
                Src = new StateVal(bl?["src"]); Dst = new StateVal(bl?["dst"]); SrcA = new StateVal(bl?["srcAlpha"]); DstA = new StateVal(bl?["dstAlpha"]);
                Op = new StateVal(bl?["op"]); OpA = new StateVal(bl?["opAlpha"]);
                Cull = new StateVal(s?["culling"]); ColorMask = new StateVal(s?["colorMask"]);
                if (s?["colorMask"] is JsonObject cmo && cmo["prop"] != null) ColorMaskPropId = PropertyIds.Of(cmo["prop"].ToString());
                ZWrite = new StateVal(s?["zWrite"]); ZTest = new StateVal(s?["zTest"]);
                StencilRef = new StateVal(s?["stencilRef"]); StencilReadMask = new StateVal(s?["stencilReadMask"]); StencilWriteMask = new StateVal(s?["stencilWriteMask"]);
                StencilComp = new StateVal(s?["stencilComp"]); StencilPass = new StateVal(s?["stencilPass"]);
            }
        }

        static uint BlendF(int v) => v switch
        {
            0 => GL.ZERO, 1 => GL.ONE, 2 => GL.DST_COLOR, 3 => GL.SRC_COLOR, 4 => GL.ONE_MINUS_DST_COLOR, 5 => GL.SRC_ALPHA,
            6 => GL.ONE_MINUS_SRC_COLOR, 7 => GL.DST_ALPHA, 8 => GL.ONE_MINUS_DST_ALPHA, 9 => GL.SRC_ALPHA_SATURATE, 10 => GL.ONE_MINUS_SRC_ALPHA, _ => GL.ONE,
        };
        static uint BlendOp(int v) => v switch { 1 => GL.FUNC_SUBTRACT, 2 => GL.FUNC_REVERSE_SUBTRACT, 3 => GL.MIN, 4 => GL.MAX, _ => GL.FUNC_ADD };
        public static uint Cmp(int v) => v switch { 1 => GL.NEVER, 2 => GL.LESS, 3 => GL.EQUAL, 4 => GL.LEQUAL, 5 => GL.GREATER, 6 => GL.NOTEQUAL, 7 => GL.GEQUAL, _ => GL.ALWAYS };
        static uint StencilOp(int v) => v switch { 1 => GL.ZERO, 2 => GL.REPLACE, 3 => GL.INCR, 4 => GL.DECR, 5 => GL.INVERT, 6 => GL.INCR_WRAP, 7 => GL.DECR_WRAP, _ => GL.KEEP };

        public static bool DepthEnabled = true;

        /// <summary>SpriteMask（Graphics/SpriteMask.cs 檔頭的規則）：MaskPass 1 寫入、2 清除遮罩的 0x80 位元；
        /// MaskInteraction 是這次畫的 renderer 的 maskInteraction（1 只畫在遮罩裡、2 只畫在遮罩外）。呼叫端畫完設回 0</summary>
        public static int MaskPass, MaskInteraction;
        public const int SpriteMaskBit = 0x80;

        static void ApplyState(PassState ps, NMaterial mat, PropertySheet block, bool ui)
        {
            var c = ps == null ? CompiledState.Empty : (ps.Compiled ??= new CompiledState(ps.State));
            int src = c.Src.Get(mat, block, 1), dst = c.Dst.Get(mat, block, 0);
            int srcA = c.SrcA.Get(mat, block, src), dstA = c.DstA.Get(mat, block, dst);
            if (src == 1 && dst == 0 && srcA == 1 && dstA == 0) GL.Disable(GL.BLEND);
            else
            {
                GL.Enable(GL.BLEND);
                GL.BlendFuncSeparate(BlendF(src), BlendF(dst), BlendF(srcA), BlendF(dstA));
                GL.BlendEquationSeparate(BlendOp(c.Op.Get(mat, block, 0)), BlendOp(c.OpA.Get(mat, block, 0)));
            }
            int cull = c.Cull.Get(mat, block, 2);
            if (cull == 0) GL.Disable(GL.CULL_FACE);
            else { GL.Enable(GL.CULL_FACE); GL.CullFace(cull == 1 ? GL.FRONT : GL.BACK); }
            int cm = c.ColorMask.Get(mat, block, 15);
            if (cm == 0 && c.ColorMaskPropId != int.MinValue && (mat == null || !mat.Props.Floats.ContainsKey(c.ColorMaskPropId))) cm = 15;
            GL.ColorMask((byte)((cm & 8) != 0 ? 1 : 0), (byte)((cm & 4) != 0 ? 1 : 0), (byte)((cm & 2) != 0 ? 1 : 0), (byte)((cm & 1) != 0 ? 1 : 0));
            int zw = c.ZWrite.Get(mat, block, 1);
            int zt = c.ZTest.Get(mat, block, 4);
            // UI 的 ZTest [unity_GUIZTestMode]：Overlay Canvas 是 Always；畫面這邊 UI 一律不做深度測試（跟 Unity 的 2D／UI 一樣靠排序）
            if (ui || !DepthEnabled || zt == 8 || zt == 0) GL.Disable(GL.DEPTH_TEST);
            else { GL.Enable(GL.DEPTH_TEST); GL.DepthFunc(Cmp(zt)); }
            GL.DepthMask((byte)(zw != 0 && !ui && DepthEnabled ? 1 : 0));
            // stencil：Ref／ReadMask／WriteMask 從 pass state（可能綁屬性），Comp／Op 照 UI shader 的 _StencilComp／_StencilOp
            int sref = c.StencilRef.Get(mat, block, 0), srm = c.StencilReadMask.Get(mat, block, 255), swm = c.StencilWriteMask.Get(mat, block, 255);
            int comp = 8, op = 0;
            if (mat != null)
            {
                if (mat.Props.Floats.ContainsKey(idStencilComp)) comp = (int)mat.GetFloat(idStencilComp);
                if (mat.Props.Floats.ContainsKey(idStencilOp)) op = (int)mat.GetFloat(idStencilOp);
            }
            if (c.StencilComp.Present) comp = c.StencilComp.Get(mat, block, comp);
            if (c.StencilPass.Present) op = c.StencilPass.Get(mat, block, op);
            bool stencil = (comp != 8 && comp != 0) || op != 0;
            if (!stencil) GL.Disable(GL.STENCIL_TEST);
            else
            {
                GL.Enable(GL.STENCIL_TEST);
                GL.StencilFunc(Cmp(comp == 0 ? 8 : comp), sref, (uint)(srm & 0xff));
                GL.StencilOp(GL.KEEP, GL.KEEP, StencilOp(op));
                GL.StencilMask((uint)(swm & 0xff));
            }
            if (MaskPass != 0)
            {
                // Sprites/Mask：ColorMask 0、Blend Off，只動 0x80
                GL.ColorMask(0, 0, 0, 0);
                GL.Disable(GL.BLEND);
                GL.Enable(GL.STENCIL_TEST);
                GL.StencilFunc(GL.ALWAYS, SpriteMaskBit, 0xff);
                GL.StencilOp(GL.KEEP, GL.KEEP, MaskPass == 1 ? GL.REPLACE : GL.ZERO);
                GL.StencilMask(SpriteMaskBit);
            }
            else if ((MaskInteraction == 1 || MaskInteraction == 2) && !ui)
            {
                GL.Enable(GL.STENCIL_TEST);
                GL.StencilFunc(MaskInteraction == 1 ? GL.EQUAL : GL.NOTEQUAL, SpriteMaskBit, SpriteMaskBit);
                GL.StencilOp(GL.KEEP, GL.KEEP, GL.KEEP);
                GL.StencilMask(0);
            }
        }

        // ───────── 繪製 ─────────

        public static void Use(GpuProgram p, Matrix4x4 model, NMaterial mat, PropertySheet block, Overrides ov, bool ui)
        {
            GL.UseProgram(p.Prog);
            long u0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            // 新的一格池子從頭用：上一格傳過的 UBO 會被別的 slot 覆寫，「內容相同不重傳」的紀錄一起清掉
            if (uboFrame != Engine.Time.FrameCount) { uboFrame = Engine.Time.FrameCount; uboNext = 0; Array.Clear(uboLast); uboByContent.Clear(); uboContentNext = 0; }
            foreach (var b in p.Blocks)
            {
                long f0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (b.CamStatic == -1)
                    b.CamStatic = (sbyte)(b.Fields.TrueForAll(f => f.ArraySize == 0 && (f.Matrix ? CamMatrices.Contains(f.Name) : CamVectors.Contains(f.Name))) ? 1 : 0);
                bool camHit = b.CamStatic == 1 && b.CamFrame == Engine.Time.FrameCount && b.CamGenSeen == CamGen && b.CamModel.Equals(model);
                bool memo = !camHit && b.Memoable != 0 && block == null && mat != null;
                int ovSig = memo ? OvSignature(ov) : 0, gv = Icalls.UnityEngine_Shader.GlobalVersion;
                bool hit = camHit;
                if (memo && b.Memoable == 1 && b.Memos.TryGetValue(mat, out var mm) && mm.MatVer == mat.Version && mm.PropsVer == mat.Props.Version && mm.GlobalVer == gv && mm.Ov == ovSig)
                {
                    Buffer.BlockCopy(mm.Data, 0, b.Data, 0, b.Data.Length);
                    hit = true;
                }
                if (!hit)
                {
                    Array.Clear(b.Data);
                    Fill(b, model, mat, block, ov);
                    if (b.Memoable == -1) b.Memoable = (sbyte)(b.Fields.TrueForAll(f => f.NotBuiltin && !f.Matrix) ? 1 : 0);
                    if (b.CamStatic == 1) { b.CamFrame = Engine.Time.FrameCount; b.CamGenSeen = CamGen; b.CamModel = model; }
                    if (memo && b.Memoable == 1)
                    {
                        if (!b.Memos.TryGetValue(mat, out var e))
                        {
                            if (b.Memos.Count >= 4096) b.Memos.Clear(); // 材質一直新建（例如每格複製）時不要無限長
                            b.Memos[mat] = e = new BlockMemo { Data = new byte[b.Data.Length] };
                        }
                        Buffer.BlockCopy(b.Data, 0, e.Data, 0, b.Data.Length);
                        e.MatVer = mat.Version; e.PropsVer = mat.Props.Version; e.GlobalVer = gv; e.Ov = ovSig;
                    }
                }
                if (Engine.Profile)
                {
                    double fm = System.Diagnostics.Stopwatch.GetElapsedTime(f0).TotalMilliseconds;
                    Engine.Add("            uniform：Fill", fm);
                }
                int slot = (int)(b.Binding % ubos.Length);
                // 內容跟這個 binding 上次的一樣就照用（連續的 UI draw 大多只有 $Globals 不同），不一樣再找這一格傳過的（UboFor）
                if (!Same(uboLast[slot], b.Data))
                {
                    var c = UboFor(b.Data);
                    uboLast[slot] = c;
                    ubos[slot] = c.Ubo;
                }
                // binding 點綁這個 slot 目前的 ubo，換了才重綁
                var ubo = ubos[slot];
                if (b.Binding >= boundUbo.Length || boundUbo[b.Binding] != ubo)
                {
                    GL.glBindBufferBase(GL.UNIFORM_BUFFER, b.Binding, ubo);
                    if (b.Binding < boundUbo.Length) boundUbo[b.Binding] = ubo;
                }
            }
            long u1 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            foreach (var t in p.Textures)
            {
                var g = TextureFor(t.Name, mat, block, ov, null);
                GL.ActiveTexture(GL.TEXTURE0 + (uint)t.Unit);
                GL.BindTexture(t.Array ? GL.TEXTURE_2D_ARRAY : GL.TEXTURE_2D, g.Id);
            }
            long u2 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            ApplyState(p.Pass, mat, block, ui);
            if (Engine.Profile)
            {
                Engine.Add("          Use：uniform", System.Diagnostics.Stopwatch.GetElapsedTime(u0, u1).TotalMilliseconds);
                Engine.Add("          Use：貼圖", System.Diagnostics.Stopwatch.GetElapsedTime(u1, u2).TotalMilliseconds);
                Engine.Add("          Use：state", System.Diagnostics.Stopwatch.GetElapsedTime(u2).TotalMilliseconds);
            }
        }
    }
}
