// URP 2D 光照（Renderer2D 的攝影機才有；Diceomancer 只有 Main Menu）。URP 的 C# 不在這裡跑，照它的 Render2DLightingPass、RendererLighting、
// LayerUtility 做（反編譯遊戲帶的 Unity.RenderPipelines.Universal.Runtime.dll）：
//   - 場景有沒有光：Light2DManager.lights（Light2D.OnEnable 自己登記）是空的 → 不受光，USE_SHAPE_LIGHT_TYPE_* 全關，sprite 照原色。
//   - 有光：全域值 _HDREmulationScale、_InverseHDREmulationScale、_UseSceneLighting 1、_RendererColor 白、每個混合樣式的
//     _ShapeLightBlendFactors／MaskFilter／InvertedFilter、_FalloffLookup。sorting layer 照「受同一組光影響」分批（LayerUtility.CalculateBatches）。
//   - 每批：用到的混合樣式各一張光照貼圖（畫面 × lightRenderTextureScale，只有全域光的是 4×4），清成全域光顏色（Light2DManager.GetGlobalColor，沒有就黑），
//     再把這個樣式、照到這一層的非全域光畫上去（RenderLightSet：點光源 TRS(位置, 旋轉, 外半徑)、其他用 localToWorld；材質照 CreateLightMaterial）。
//   - 畫這批的 renderer 之前：有光的批，用到的樣式綁 _ShapeLightTexture<n> 並開 keyword；沒光的批，四張都綁黑貼圖、只開樣式 0。
// 沒做：陰影（這個遊戲的光都沒開）、法線貼圖（NormalsRendering，都沒開）、光體積（volumeIntensity）、攝影機視錐剔除（全部的光都算看得到）。
// 光的查表貼圖由 URP 自己建（Light2DLookupTexture，R16G16B16A16_SFloat 的 SetPixel）；引擎的 Texture2D 像素是 8 位元，方向的 b、a 通道會被夾到 0～1，
// 只有法線貼圖用得到，這個遊戲沒用。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityNative.Icalls;

namespace UnityNative.Gl
{
    public sealed unsafe partial class GlGpu
    {
        const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly string[] blendStyleKeywords = { "USE_SHAPE_LIGHT_TYPE_0", "USE_SHAPE_LIGHT_TYPE_1", "USE_SHAPE_LIGHT_TYPE_2", "USE_SHAPE_LIGHT_TYPE_3" };
        static readonly int[] idShapeLightTex = { PropertyIds.Of("_ShapeLightTexture0"), PropertyIds.Of("_ShapeLightTexture1"), PropertyIds.Of("_ShapeLightTexture2"), PropertyIds.Of("_ShapeLightTexture3") };

        sealed class LightBatch
        {
            public int StartLayerId, Lower, Upper; // Lower、Upper：sorting layer 的順序值，含兩端
            public int TotalLights;
            public uint StylesUsed, StylesWithLights;
            public readonly RenderTexture[] Rt = new RenderTexture[4];
        }

        readonly List<LightBatch> lightBatches = new List<LightBatch>();
        readonly Dictionary<(int, int), RenderTexture> lightRts = new Dictionary<(int, int), RenderTexture>();
        readonly Dictionary<(object, uint), Material> lightMaterials = new Dictionary<(object, uint), Material>();
        LightBatch curLightBatch;
        bool lit2d;

        static object Get(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, AnyMember | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
                var f = t.GetField(name, AnyMember | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        static float F(object o, string name) => Convert.ToSingle(Get(o, name));
        static int I(object o, string name) => Convert.ToInt32(Get(o, name));
        static bool IsLitLayer(object light, int layerId) => (bool)light.GetType().GetMethod("IsLitLayer", AnyMember).Invoke(light, new object[] { layerId });

        static Type light2DManager;
        static IList Lights()
        {
            light2DManager ??= Type.GetType("UnityEngine.Rendering.Universal.Light2DManager, Unity.RenderPipelines.Universal.Runtime");
            return light2DManager?.GetProperty("lights", AnyMember)?.GetValue(null) as IList;
        }

        static void SetGlobalFloat(string name, float v) => UnityEngine_Shader.SetGlobalFloatImpl(PropertyIds.Of(name), v);
        static void SetGlobalVector(string name, Vector4 v) => UnityEngine_Shader.SetGlobalVectorImpl_Injected(PropertyIds.Of(name), ref v);
        static void SetGlobalMatrix(string name, Matrix4x4 m) => UnityEngine_Shader.SetGlobalMatrixImpl_Injected(PropertyIds.Of(name), ref m);
        static void SetGlobalTexture(string name, Texture t) => UnityEngine_Shader.SetGlobalTextureImpl(PropertyIds.Of(name), t);
        static void EnableBlendStyle(int i, bool on) { if (on) UnityEngine_Shader.GlobalKeywords.Add(blendStyleKeywords[i]); else UnityEngine_Shader.GlobalKeywords.Remove(blendStyleKeywords[i]); }

        /// <summary>攝影機開始畫之前：2D renderer 而且場景有光，就畫好每批的光照貼圖（回 true）</summary>
        bool Lights2DBegin(NCamera cam, int w, int h)
        {
            lit2d = false;
            curLightBatch = null;
            if (ShaderLib.Mode != RenderPipelines.Urp2D) return false;
            var rd = RenderPipelines.RendererDataFor(cam);
            var all = Lights();
            if (rd == null || all == null || all.Count == 0)
            {
                for (int i = 0; i < 4; i++) EnableBlendStyle(i, false);
                return false;
            }
            try
            {
                float hdr = F(rd, "hdrEmulationScale");
                SetGlobalFloat("_HDREmulationScale", hdr);
                SetGlobalFloat("_InverseHDREmulationScale", 1f / hdr);
                SetGlobalFloat("_UseSceneLighting", 1);
                SetGlobalVector("_RendererColor", Color.white);
                var styles = Get(rd, "lightBlendStyles") as Array;
                for (int i = 0; styles != null && i < styles.Length && i < 4; i++)
                {
                    var st = styles.GetValue(i);
                    var bf = (Vector2)Get(st, "blendFactors");
                    SetGlobalVector("_ShapeLightBlendFactors" + i, new Vector4(bf.x, bf.y, 0, 0));
                    var filter = Get(st, "maskTextureChannelFilter");
                    SetGlobalVector("_ShapeLightMaskFilter" + i, (Vector4)Get(filter, "mask"));
                    SetGlobalVector("_ShapeLightInvertedFilter" + i, (Vector4)Get(filter, "inverted"));
                }
                var falloff = Get(rd, "fallOffLookup") as Texture;
                SetGlobalTexture("_FalloffLookup", falloff);

                // 看得到的光（Light2DCullResult.SetupCulling：攝影機的 cullingMask；視錐剔除沒做）
                var visible = new List<object>();
                foreach (var l in all)
                {
                    var c = l as Component;
                    if (c == null || (cam.CullingMask & (1 << c.gameObject.layer)) == 0) continue;
                    l.GetType().GetMethod("CacheValues", AnyMember)?.Invoke(l, null);
                    visible.Add(l);
                }

                // LayerUtility.CalculateBatches
                lightBatches.Clear();
                int n = SortingLayers.Count;
                for (int i = 0; i < n;)
                {
                    int id = SortingLayers.At(i).id;
                    int j = i;
                    while (j + 1 < n)
                    {
                        int id2 = SortingLayers.At(j + 1).id;
                        bool same = true;
                        foreach (var l in visible) if (IsLitLayer(l, id) != IsLitLayer(l, id2)) { same = false; break; }
                        if (!same) break;
                        j++;
                    }
                    var b = new LightBatch { StartLayerId = id, Lower = i == 0 ? int.MinValue : i, Upper = j == n - 1 ? int.MaxValue : j };
                    foreach (var l in visible)
                    {
                        if (!IsLitLayer(l, id)) continue;
                        b.TotalLights++;
                        int style = I(l, "blendStyleIndex");
                        b.StylesUsed |= 1u << style;
                        if (I(l, "lightType") != 4) b.StylesWithLights |= 1u << style;
                    }
                    lightBatches.Add(b);
                    i = j + 1;
                }

                // 每批的光照貼圖
                int prevFbo, scissor;
                int* vp = stackalloc int[4];
                GL.glGetIntegerv(GL.FRAMEBUFFER_BINDING, &prevFbo);
                GL.glGetIntegerv(GL.VIEWPORT, vp);
                GL.glGetIntegerv(GL.SCISSOR_TEST, &scissor);
                if (scissor != 0) GL.Disable(GL.SCISSOR_TEST);
                float scale = Mathf.Clamp(F(rd, "lightRenderTextureScale"), 0.01f, 1f);
                int rw = Math.Max(1, (int)(w * scale)), rh = Math.Max(1, (int)(h * scale));
                for (int bi = 0; bi < lightBatches.Count; bi++)
                {
                    var b = lightBatches[bi];
                    for (int s = 0; s < 4 && styles != null && s < styles.Length; s++)
                    {
                        if ((b.StylesUsed & (1u << s)) == 0) continue;
                        bool withLights = (b.StylesWithLights & (1u << s)) != 0;
                        var rt = LightRt(bi, s, withLights ? rw : 4, withLights ? rh : 4);
                        b.Rt[s] = rt;
                        var tgt = RtTgt(Native.Of<NRenderTexture>(rt));
                        Bind(tgt);
                        var clear = GlobalColor(visible, b.StartLayerId, s);
                        GL.ColorMask(1, 1, 1, 1);
                        GL.glClearColor(clear.r, clear.g, clear.b, clear.a);
                        GL.glClear(GL.COLOR_BUFFER_BIT);
                        if (withLights) RenderLightSet(rd, visible, s, b.StartLayerId, tgt.W, tgt.H, w, h);
                    }
                }
                GL.glBindFramebuffer(GL.FRAMEBUFFER, (uint)prevFbo);
                GL.glViewport(vp[0], vp[1], vp[2], vp[3]);
                if (scissor != 0) GL.Enable(GL.SCISSOR_TEST);
                lit2d = true;
                if (DumpNext) Dump.Add($"  2D 光照：{visible.Count} 盞光、{lightBatches.Count} 批，光照貼圖 {rw}x{rh}");
            }
            catch (Exception e) { Engine.LogException(e); }
            return lit2d;
        }

        RenderTexture LightRt(int batch, int style, int w, int h)
        {
            if (!lightRts.TryGetValue((batch, style), out var rt) || !rt)
            {
                var desc = new RenderTextureDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat, 0);
                rt = UnityEngine_RenderTexture.GetTemporary_Internal_Injected(ref desc);
                rt.name = $"2D 光照 {batch}/{style}";
                lightRts[(batch, style)] = rt;
            }
            var n = Native.Of<NRenderTexture>(rt);
            if (n.Width != w || n.Height != h) { n.Width = w; n.Height = h; n.GpuDirty = true; }
            n.Filter = FilterMode.Bilinear;
            return rt;
        }

        /// <summary>Light2DManager.GetGlobalColor：這一層、這個樣式的第一盞全域光的 color × intensity，沒有就黑</summary>
        static Color GlobalColor(List<object> lights, int layerId, int style)
        {
            foreach (var l in lights)
                if (I(l, "lightType") == 4 && I(l, "blendStyleIndex") == style && IsLitLayer(l, layerId))
                    return (Color)Get(l, "color") * F(l, "intensity");
            return Color.black;
        }

        /// <summary>RendererLighting.RenderLightSet（陰影的部分不做）</summary>
        void RenderLightSet(object rd, List<object> lights, int style, int layerId, int rw, int rh, int camW, int camH)
        {
            var c = Draw.Cam;
            int sw = c.Width, sh = c.Height;
            c.Width = rw; c.Height = rh; // 光照貼圖上的 _ScreenParams
            Draw.CamGen++;
            try
            {
                foreach (var l in lights)
                {
                    int type = I(l, "lightType");
                    if (type == 4 || I(l, "blendStyleIndex") != style || !IsLitLayer(l, layerId)) continue;
                    var mesh = Get(l, "lightMesh") as Mesh;
                    var nmesh = Native.Of<NMesh>(mesh);
                    if (nmesh == null || nmesh.VertexCount == 0) continue;
                    var mat = LightMaterial(rd, l);
                    if (mat == null) continue;
                    var tr = ((Component)l).transform;
                    // SetGeneralLightShaderGlobals
                    var color = (Color)Get(l, "color");
                    var lc = F(l, "intensity") * color.a * color;
                    lc.a = 1f;
                    SetGlobalFloat("_FalloffIntensity", F(l, "falloffIntensity"));
                    SetGlobalFloat("_FalloffDistance", F(l, "shapeLightFalloffSize"));
                    SetGlobalVector("_LightColor", lc);
                    SetGlobalFloat("_VolumeOpacity", F(l, "volumeIntensity"));
                    bool point = type == 3;
                    var sprite = Get(l, "lightCookieSprite") as Sprite;
                    if (type == 2 && sprite != null && sprite.texture != null) SetGlobalTexture("_CookieTex", sprite.texture);
                    if (I(l, "normalMapQuality") != 2 || point)
                    {
                        // SetPointLightShaderGlobals
                        float outer = F(l, "pointLightOuterRadius");
                        var inv = Matrix4x4.Inverse(Matrix4x4.TRS(tr.position, tr.rotation, new Vector3(outer, outer, outer)));
                        float innerN = F(l, "pointLightInnerRadius") / outer;
                        float inA = F(l, "pointLightInnerAngle") / 360f, outA = F(l, "pointLightOuterAngle") / 360f;
                        SetGlobalVector("_LightPosition", tr.position);
                        SetGlobalMatrix("_LightInvMatrix", inv);
                        SetGlobalFloat("_InnerRadiusMult", 1f / (1f - innerN));
                        SetGlobalFloat("_OuterAngle", outA);
                        SetGlobalFloat("_InnerAngleMult", 1f / (outA - inA));
                        var lookupType = Type.GetType("UnityEngine.Rendering.Universal.Light2DLookupTexture, Unity.RenderPipelines.Universal.Runtime");
                        SetGlobalTexture("_LightLookup", lookupType?.GetMethod("GetLightLookupTexture", AnyMember)?.Invoke(null, null) as Texture);
                        SetGlobalTexture("_FalloffLookup", Get(rd, "fallOffLookup") as Texture);
                        SetGlobalFloat("_IsFullSpotlight", inA == 1f ? 1f : 0f);
                        SetGlobalFloat("_LightZDistance", F(l, "normalMapDistance"));
                        if (sprite != null && sprite.texture != null) SetGlobalTexture("_PointLightCookieTex", sprite.texture);
                    }
                    // 陰影沒做：ShadowRendering.DisableGlobalShadowTexture
                    SetGlobalFloat("_ShadowIntensity", 1f);
                    SetGlobalFloat("_ShadowVolumeIntensity", 1f);
                    float r = F(l, "pointLightOuterRadius");
                    var model = point ? Matrix4x4.TRS(tr.position, tr.rotation, new Vector3(r, r, r)) : tr.localToWorldMatrix;
                    var nmat = Native.Of<NMaterial>(mat);
                    var g = GpuMesh.Of(nmesh);
                    ov.Clear();
                    foreach (var p in ProgramsFor(nmat, null))
                    {
                        if (p.Prog == 0 || p.Pass?.Type == 2) continue;
                        Draw.Use(p, model, nmat, null, ov, false);
                        stream.DrawMesh(p, g, 0);
                        Draw.DrawCalls++;
                    }
                }
            }
            finally { c.Width = sw; c.Height = sh; Draw.CamGen++; }
        }

        /// <summary>RendererLighting.CreateLightMaterial（照 GetLightMaterialIndex 快取）</summary>
        Material LightMaterial(object rd, object l)
        {
            int type = I(l, "lightType");
            bool point = type == 3;
            bool additive = I(l, "overlapOperation") == 0;
            int quality = I(l, "normalMapQuality");
            var sprite = Get(l, "lightCookieSprite") as Sprite;
            bool cookie = point && sprite != null && sprite.texture != null;
            uint key = (point ? 0 : 2u) | (additive ? 4u : 0) | (type == 2 ? 8u : 0) | (cookie ? 16u : 0) | (quality == 1 ? 32u : 0) | (quality != 2 ? 64u : 0);
            if (lightMaterials.TryGetValue((rd, key), out var m) && m) return m;
            var shader = Get(rd, point ? "pointLightShader" : "shapeLightShader") as Shader;
            if (shader == null) return null;
            m = new Material(shader);
            if (additive) { m.SetFloat("_SrcBlend", 1); m.SetFloat("_DstBlend", 1); m.EnableKeyword("USE_ADDITIVE_BLENDING"); }
            else { m.SetFloat("_SrcBlend", 5); m.SetFloat("_DstBlend", 10); }
            if (type == 2) m.EnableKeyword("SPRITE_LIGHT");
            if (cookie) m.EnableKeyword("USE_POINT_LIGHT_COOKIES");
            if (quality == 1) m.EnableKeyword("LIGHT_QUALITY_FAST");
            if (quality != 2) m.EnableKeyword("USE_NORMAL_MAP");
            lightMaterials[(rd, key)] = m;
            return m;
        }

        /// <summary>畫一個 renderer 之前：換到它所在 sorting layer 的那批（綁光照貼圖、開關 keyword）</summary>
        void Lights2DLayer(int layerValue)
        {
            if (!lit2d) return;
            LightBatch b = null;
            foreach (var x in lightBatches) if (layerValue >= x.Lower && layerValue <= x.Upper) { b = x; break; }
            if (b == null || b == curLightBatch) return;
            curLightBatch = b;
            if (b.TotalLights > 0)
                for (int k = 0; k < 4; k++)
                {
                    bool used = (b.StylesUsed & (1u << k)) != 0;
                    if (used && b.Rt[k]) UnityEngine_Shader.SetGlobalTextureImpl(idShapeLightTex[k], b.Rt[k]);
                    EnableBlendStyle(k, used);
                }
            else
                for (int k = 0; k < 4; k++)
                {
                    UnityEngine_Shader.SetGlobalTextureImpl(idShapeLightTex[k], Texture2D.blackTexture);
                    EnableBlendStyle(k, k == 0);
                }
        }

        /// <summary>攝影機畫完：DisableAllKeywords</summary>
        void Lights2DEnd()
        {
            if (!lit2d) return;
            for (int i = 0; i < 4; i++) EnableBlendStyle(i, false);
            lit2d = false;
            curLightBatch = null;
        }
    }
}
