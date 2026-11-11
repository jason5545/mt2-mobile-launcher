// 這台攝影機用哪一種 renderer 畫（決定 shader 的哪些 pass 會畫）。URP 的 C# 不在這裡跑，只照它挑 renderer 與 pass 的規則：
//   - render pipeline 資產：目前品質等級的 customRenderPipeline，沒有就用 GraphicsSettings 的 m_CustomRenderPipeline；都沒有是內建管線（MT2）。
//   - renderer：攝影機的 UniversalAdditionalCameraData.m_RendererIndex（−1 或沒有這個元件用資產的 m_DefaultRendererIndex）→ m_RendererDataList[i]。
//   - pass（ShaderTagId，照順序，每個標籤畫第一個符合的 pass；沒標 LightMode 的 pass 算 SRPDefaultUnlit）：
//       UniversalRenderer（前向）：SRPDefaultUnlit、UniversalForward、UniversalForwardOnly（URP 的 DrawObjectsPass）
//       Renderer2D：SRPDefaultUnlit、Universal2D（Render2DLightingPass 的 k_ShaderTags）
//     同一個標籤有好幾個 pass 時只畫第一個：未查證。
// Diceomancer（Windows 版，Standalone 預設品質 Ultra → URP2D 資產）：SplashScreen 與 Main 的攝影機是前向 renderer，Main Menu 是 2D renderer。
// 前向 renderer 不畫 Universal2D pass，所以 Sprite-Lit-Default 在 Main 場景走的是不吃 2D 光的 UniversalForward pass。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;

namespace UnityNative
{
    public static class RenderPipelines
    {
        public const int BuiltIn = 0, UrpForward = 1, Urp2D = 2;

        public static readonly string[][] Tags =
        {
            null, // 內建：ShaderLib 的舊規則
            new[] { "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly" },
            new[] { "SRPDefaultUnlit", "Universal2D" },
        };

        static bool loaded;
        static JsonObject asset;
        static readonly Dictionary<NCamera, int> byCamera = new Dictionary<NCamera, int>();

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            var project = Assets.ReadAssetJson("project.json");
            var q = project?["QualitySettings"]?["QualitySettings"];
            JsonNode pipe = null;
            if (q?["m_QualitySettings"] is JsonArray levels)
            {
                int cur = (int)Serializer.D(q["m_CurrentQuality"]);
                if (cur >= 0 && cur < levels.Count) pipe = levels[cur]?["customRenderPipeline"];
            }
            if (pipe?["guid"] == null) pipe = project?["GraphicsSettings"]?["GraphicsSettings"]?["m_CustomRenderPipeline"];
            var guid = pipe?["guid"]?.ToString();
            if (string.IsNullOrEmpty(guid)) return;
            asset = ReadMono(guid);
        }

        static JsonObject ReadMono(string guid)
        {
            var rec = Assets.Record("monobehaviours", guid);
            var file = rec?["file"]?.ToString();
            return file == null ? null : Assets.ReadAssetJson(file);
        }

        /// <summary>這台攝影機的 renderer 種類（BuiltIn／UrpForward／Urp2D）</summary>
        public static int ModeFor(NCamera cam)
        {
            Load();
            if (asset == null) return BuiltIn;
            if (cam != null && byCamera.TryGetValue(cam, out var m)) return m;
            int index = RendererIndex(cam);
            int mode = UrpForward;
            if (asset["m_RendererDataList"] is JsonArray list && index >= 0 && index < list.Count)
            {
                var g = list[index]?["guid"]?.ToString();
                var script = g == null ? null : Assets.Record("monobehaviours", g)?["script"]?.ToString();
                if (script == "Renderer2DData") mode = Urp2D;
            }
            if (cam != null) byCamera[cam] = mode;
            return mode;
        }

        static readonly Dictionary<NCamera, UnityEngine.Object> rendererData = new Dictionary<NCamera, UnityEngine.Object>();

        /// <summary>這台攝影機的 renderer 設定（ScriptableRendererData，原版的 managed 物件；Light2D 讀 Renderer2DData 的欄位）</summary>
        public static UnityEngine.Object RendererDataFor(NCamera cam)
        {
            Load();
            if (asset == null || cam == null) return null;
            if (rendererData.TryGetValue(cam, out var o)) return o;
            int index = RendererIndex(cam);
            if (asset["m_RendererDataList"] is JsonArray list && index >= 0 && index < list.Count && list[index]?["guid"]?.ToString() is string g)
                o = Assets.Load(g, list[index]?["fileID"]?.ToString() ?? "11400000");
            rendererData[cam] = o;
            return o;
        }

        static int RendererIndex(NCamera cam)
        {
            int index = -1;
            if (cam?.Go != null)
                foreach (var c in cam.Go.Components)
                {
                    var o = c.Managed;
                    if (o == null || o.GetType().Name != "UniversalAdditionalCameraData") continue;
                    var f = o.GetType().GetField("m_RendererIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) index = (int)f.GetValue(o);
                }
            if (index < 0) index = (int)Serializer.D(asset["m_DefaultRendererIndex"]);
            return index;
        }

        /// <summary>有 URP 資產時 Overlay Canvas 用前向的規則（沒標 LightMode 的 UI shader 兩種都畫）</summary>
        public static int OverlayMode() { Load(); return asset == null ? BuiltIn : UrpForward; }

        public static void Forget(NCamera cam) { byCamera.Remove(cam); rendererData.Remove(cam); }
    }
}
