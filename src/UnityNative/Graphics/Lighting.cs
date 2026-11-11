// built-in 管線前向繪製（ForwardBase pass）的光照常數與 keyword：Unity 原生程式碼設的那些全域值（UnityShaderVariables.cginc），規則照 TS 版
// render/builtinLighting.ts。每個物件一組；照物件的圖層挑光源，所以實作上是每個圖層一組，每格重算。
//   - 主光源：啟用中的平行光、cullingMask 含物件圖層的，挑強度最大的。_WorldSpaceLightPos0 ＝ (−forward, 0)、_LightColor0 ＝ 光的顏色 × 強度。
//     沒有主光源時兩個都是 0。GraphicsSettings.m_LightsUseLinearIntensity 是 0（MT2）時強度乘在 gamma 色上：轉線性(顏色 × 強度)；1 時是 轉線性(顏色) × 強度。
//   - 環境光（ShadeSH9 讀 unity_SHAr…unity_SHC）：active 場景 LightingData 的 ambientProbe（m_BakedAmbientProbeInLinear，27 個係數，
//     [通道 × 9 + 係數]），乘基底常數再照 HDRP SphericalHarmonicMath.PackCoefficients 排（內建管線是不是同一套換算未查證，同 TS 版）。
//     沒有探針時只有常數項 ＝ 天空色轉線性 × 強度。
//   - 霧：RenderSettings 的 m_Fog 開著時 FOG_LINEAR／FOG_EXP／FOG_EXP2（m_FogMode 1／2／3），unity_FogColor（線性）、
//     unity_FogParams ＝ (density/√ln2, density/ln2, −1/(end−start), end/(end−start))。
//   - keyword（multi_compile_fwdbase）：一律 DIRECTIONAL；有光照圖的是 LIGHTMAP_ON，沒有的是 LIGHTPROBE_SH。
// 與原版的差異（同 TS 版）：陰影（SHADOWS_SCREEN）、ForwardAdd（其他逐像素光源）、頂點光源、光照探針群組（物件的 SH 一律用環境探針）。
// 反射探針：unity_SpecCube0 還沒綁 cubemap（取樣是黑的；TS 版用 RenderSettings 的 m_CustomReflection）。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public sealed class NLight : NBehaviour
    {
        public LightType Type = LightType.Point;
        public Color Color = Color.white;
        public float Intensity = 1f, Range = 10f, SpotAngle = 30f, InnerSpotAngle = 21.8f, BounceIntensity = 1f, ShadowStrength = 1f;
        public int CullingMask = -1;
        public LightShadows Shadows;
        public LightRenderMode RenderMode;
        /// <summary>載入過、還沒銷毀的光源（啟用與否畫的時候再看：Behaviour.enabled 不會通知原生元件）</summary>
        public static readonly List<NLight> All = new List<NLight>();

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Enabled = d["m_Enabled"] == null || Serializer.D(d["m_Enabled"]) != 0;
            if (d["m_Type"] != null) Type = (LightType)(int)Serializer.D(d["m_Type"]);
            if (d["m_Color"] != null) Color = (Color)Serializer.Convert(d["m_Color"], typeof(Color), null);
            if (d["m_Intensity"] != null) Intensity = (float)Serializer.D(d["m_Intensity"]);
            if (d["m_Range"] != null) Range = (float)Serializer.D(d["m_Range"]);
            if (d["m_SpotAngle"] != null) SpotAngle = (float)Serializer.D(d["m_SpotAngle"]);
            if (d["m_InnerSpotAngle"] != null) InnerSpotAngle = (float)Serializer.D(d["m_InnerSpotAngle"]);
            if (d["m_BounceIntensity"] != null) BounceIntensity = (float)Serializer.D(d["m_BounceIntensity"]);
            if (d["m_CullingMask"] is JsonObject cm) CullingMask = (int)(uint)Serializer.D(cm["m_Bits"]);
            if (d["m_Shadows"] is JsonObject sh)
            {
                Shadows = (LightShadows)(int)Serializer.D(sh["m_Type"]);
                if (sh["m_Strength"] != null) ShadowStrength = (float)Serializer.D(sh["m_Strength"]);
            }
            if (d["m_RenderMode"] != null) RenderMode = (LightRenderMode)(int)Serializer.D(d["m_RenderMode"]);
            Track();
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NLight)src;
            Type = s.Type; Color = s.Color; Intensity = s.Intensity; Range = s.Range; SpotAngle = s.SpotAngle; InnerSpotAngle = s.InnerSpotAngle;
            BounceIntensity = s.BounceIntensity; ShadowStrength = s.ShadowStrength; CullingMask = s.CullingMask; Shadows = s.Shadows; RenderMode = s.RenderMode;
            Track();
        }

        public override void OnActiveChanged() => Track();
        void Track() { if (Alive && !All.Contains(this)) All.Add(this); }
        public override void OnDestroyNative() => All.Remove(this);

        public static void Register() => Loader.Factories["Light"] = () => new NLight();
    }

    /// <summary>ForwardBase 的光照常數（見檔頭）</summary>
    public static class BuiltinLighting
    {
        /// <summary>HDRP SphericalHarmonicMath.PremultiplyCoefficients 的 ks（SH 基底的正規化常數）</summary>
        static readonly float[] ShKs = { 0.282095f, -0.325735f, 0.325735f, -0.325735f, 0.273137f, -0.273137f, 0.078848f, -0.273137f, 0.136569f };

        sealed class LayerSet { public int Frame = -1, SettingsGen = -1; public readonly List<(int id, Vector4 v)> Values = new List<(int, Vector4)>(); public string Fog; }
        static readonly LayerSet[] layers = new LayerSet[32];
        static int? linearIntensity;

        static readonly int idLightPos = PropertyIds.Of("_WorldSpaceLightPos0"), idLightColor = PropertyIds.Of("_LightColor0");
        static readonly int[] idSh = { PropertyIds.Of("unity_SHAr"), PropertyIds.Of("unity_SHAg"), PropertyIds.Of("unity_SHAb"),
            PropertyIds.Of("unity_SHBr"), PropertyIds.Of("unity_SHBg"), PropertyIds.Of("unity_SHBb"), PropertyIds.Of("unity_SHC") };

        static float Lin(float v) => Mathf.GammaToLinearSpace(v);
        static Vector4 LinColor(Color c) => new Vector4(Lin(c.r), Lin(c.g), Lin(c.b), c.a);

        static Color ColorOf(JsonNode n, Color def) => n != null ? (Color)Serializer.Convert(n, typeof(Color), null) : def;
        static float FloatOf(JsonNode n, float def) => n != null ? (float)Serializer.D(n) : def;

        /// <summary>active 場景的 RenderSettings 與 LightingData（Unity 用 active 場景的光照設定）</summary>
        static JsonObject SceneSettings => Scenes.Active != null && Render.Settings.TryGetValue(Scenes.Active, out var s) ? s : null;

        /// <summary>這次繪製的 keyword 加進 kw、常數寫進 sheet（覆寫：比材質、全域優先）</summary>
        public static void Apply(int layer, bool lightmapped, List<string> kw, PropertySheet sheet)
        {
            var set = Get(layer & 31);
            kw.Add("DIRECTIONAL");
            kw.Add(lightmapped ? "LIGHTMAP_ON" : "LIGHTPROBE_SH");
            if (set.Fog != null) kw.Add(set.Fog);
            foreach (var (id, v) in set.Values) sheet.Vectors[id] = v;
        }

        static LayerSet Get(int layer)
        {
            var set = layers[layer] ??= new LayerSet();
            int frame = Engine.Time.FrameCount;
            if (set.Frame == frame) return set;
            set.Frame = frame;
            Compute(layer, set);
            return set;
        }

        static void Compute(int layer, LayerSet set)
        {
            var v = set.Values;
            v.Clear();
            void Put(string name, Vector4 x) => v.Add((PropertyIds.Of(name), x));

            // 主光源
            NLight main = null;
            foreach (var l in NLight.All)
            {
                if (!l.Alive || !l.IsActiveAndEnabled || l.Type != LightType.Directional || (l.CullingMask & (1 << layer)) == 0) continue;
                if (main == null || l.Intensity > main.Intensity) main = l;
            }
            if (main != null)
            {
                var m = main.Tr.LocalToWorld;
                var f = new Vector3(m.m02, m.m12, m.m22).normalized;
                v.Add((idLightPos, new Vector4(-f.x, -f.y, -f.z, 0)));
                linearIntensity ??= LightsUseLinearIntensity();
                var c = main.Color;
                var col = linearIntensity == 1
                    ? new Vector4(Lin(c.r) * main.Intensity, Lin(c.g) * main.Intensity, Lin(c.b) * main.Intensity, 1)
                    : new Vector4(Lin(c.r * main.Intensity), Lin(c.g * main.Intensity), Lin(c.b * main.Intensity), 1);
                v.Add((idLightColor, col));
            }
            else
            {
                v.Add((idLightPos, Vector4.zero));
                v.Add((idLightColor, Vector4.zero));
            }

            var settings = SceneSettings;
            var rs = settings?["RenderSettings"] as JsonObject;
            var sky = ColorOf(rs?["m_AmbientSkyColor"], new Color(0, 0, 0, 1));
            float intensity = FloatOf(rs?["m_AmbientIntensity"], 1f);

            // 環境光 SH
            if (settings?["LightingData"]?["ambientProbe"] is JsonArray probe && probe.Count >= 27)
            {
                float K(int ch, int i) => (float)Serializer.D(probe[ch * 9 + i]) * ShKs[i];
                for (int ch = 0; ch < 3; ch++)
                {
                    v.Add((idSh[ch], new Vector4(K(ch, 3), K(ch, 1), K(ch, 2), K(ch, 0) - K(ch, 6))));
                    v.Add((idSh[3 + ch], new Vector4(K(ch, 4), K(ch, 5), K(ch, 6) * 3, K(ch, 7))));
                }
                v.Add((idSh[6], new Vector4(K(0, 8), K(1, 8), K(2, 8), 1)));
            }
            else
            {
                var s = LinColor(sky);
                v.Add((idSh[0], new Vector4(0, 0, 0, s.x * intensity)));
                v.Add((idSh[1], new Vector4(0, 0, 0, s.y * intensity)));
                v.Add((idSh[2], new Vector4(0, 0, 0, s.z * intensity)));
                for (int i = 3; i < 6; i++) v.Add((idSh[i], Vector4.zero));
                v.Add((idSh[6], new Vector4(0, 0, 0, 1)));
            }
            Vector4 Amb(JsonNode n) { var l = LinColor(ColorOf(n, new Color(0, 0, 0, 1))); l.w = 1; return l; }
            Put("unity_AmbientSky", Amb(rs?["m_AmbientSkyColor"]));
            Put("unity_AmbientEquator", Amb(rs?["m_AmbientEquatorColor"]));
            Put("unity_AmbientGround", Amb(rs?["m_AmbientGroundColor"]));
            Put("glstate_lightmodel_ambient", Amb(rs?["m_AmbientSkyColor"]));

            // 霧
            set.Fog = null;
            if (rs != null && FloatOf(rs["m_Fog"], 0) != 0)
                set.Fog = (int)FloatOf(rs["m_FogMode"], 3) switch { 1 => "FOG_LINEAR", 2 => "FOG_EXP", 3 => "FOG_EXP2", _ => null };
            Put("unity_FogColor", LinColor(ColorOf(rs?["m_FogColor"], new Color(0, 0, 0, 1))));
            float density = FloatOf(rs?["m_FogDensity"], 0), start = FloatOf(rs?["m_LinearFogStart"], 0), end = FloatOf(rs?["m_LinearFogEnd"], 300);
            float span = end - start; if (span == 0) span = 1;
            Put("unity_FogParams", new Vector4(density / MathF.Sqrt(MathF.Log(2)), density / MathF.Log(2), -1f / span, end / span));

            // 沒做的：光照探針群組、陰影、反射探針 → 給「沒有」的值
            Put("unity_SpecCube0_HDR", new Vector4(FloatOf(rs?["m_ReflectionIntensity"], 1f), 1, 0, 0));
            Put("unity_SpecCube1_HDR", new Vector4(1, 1, 0, 0));
            Put("unity_ProbeVolumeParams", Vector4.zero);
            Put("unity_ProbesOcclusion", Vector4.one);
            Put("unity_OcclusionMaskSelector", new Vector4(1, 0, 0, 0));
            Put("_LightShadowData", new Vector4(1, 0, 0, 0));
        }

        static int LightsUseLinearIntensity()
        {
            var g = Assets.Index("project")["GraphicsSettings"];
            g = g?["GraphicsSettings"] ?? g;
            return (int)FloatOf(g?["m_LightsUseLinearIntensity"], 0);
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Light
    {
        static NLight L(Light l) => Native.Self<NLight>(l);
        public static LightType get_type(Light self) => L(self).Type;
        public static void set_type(Light self, LightType value) => L(self).Type = value;
        public static float get_intensity(Light self) => L(self).Intensity;
        public static void set_intensity(Light self, float value) => L(self).Intensity = value;
        public static void get_color_Injected(Light self, out Color ret) => ret = L(self).Color;
        public static void set_color_Injected(Light self, ref Color value) => L(self).Color = value;
        public static int get_cullingMask(Light self) => L(self).CullingMask;
        public static void set_cullingMask(Light self, int value) => L(self).CullingMask = value;
        public static float get_range(Light self) => L(self).Range;
        public static void set_range(Light self, float value) => L(self).Range = value;
        public static float get_spotAngle(Light self) => L(self).SpotAngle;
        public static void set_spotAngle(Light self, float value) => L(self).SpotAngle = value;
        public static float get_innerSpotAngle(Light self) => L(self).InnerSpotAngle;
        public static void set_innerSpotAngle(Light self, float value) => L(self).InnerSpotAngle = value;
        public static float get_bounceIntensity(Light self) => L(self).BounceIntensity;
        public static void set_bounceIntensity(Light self, float value) => L(self).BounceIntensity = value;
        public static LightShadows get_shadows(Light self) => L(self).Shadows;
        public static void set_shadows(Light self, LightShadows value) => L(self).Shadows = value;
        public static float get_shadowStrength(Light self) => L(self).ShadowStrength;
        public static void set_shadowStrength(Light self, float value) => L(self).ShadowStrength = value;
        public static LightRenderMode get_renderMode(Light self) => L(self).RenderMode;
        public static void set_renderMode(Light self, LightRenderMode value) => L(self).RenderMode = value;
    }
}
