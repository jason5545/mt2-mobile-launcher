// 繪製模組的入口：場景的 RenderSettings、shader 名稱表。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public static class Render
    {
        public static readonly Dictionary<NScene, JsonObject> Settings = new Dictionary<NScene, JsonObject>();
        public static void SceneSettings(NScene scene, JsonObject settings)
        {
            if (settings != null) Settings[scene] = settings;
        }
    }

    /// <summary>shader：匯入器的 shaders.json（專案的 shader）＋ Unity 內建的（只有名字，繪製端照名字認）</summary>
    public static class Shaders
    {
        static readonly Dictionary<string, Shader> byName = new Dictionary<string, Shader>();
        static Dictionary<string, string> guidByName;

        public static Shader Lookup(string name) => name != null && byName.TryGetValue(name, out var s) ? s : null;

        public static Shader Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var s = Lookup(name);
            if ((object)s != null) return s;
            if (guidByName == null)
            {
                guidByName = new Dictionary<string, string>();
                foreach (var kv in Assets.Index("shaders")) if (kv.Value?["name"] != null) guidByName[kv.Value["name"].ToString()] = kv.Key;
            }
            if (guidByName.TryGetValue(name, out var g)) return Assets.Load(g, "4800000", typeof(Shader)) as Shader;
            if (Builtin.Contains(name)) return ByName(name);
            return null;
        }

        /// <summary>Unity 內建 shader 的名字（Shader.Find 找得到、材質參照得到）</summary>
        static readonly HashSet<string> Builtin = new HashSet<string>
        {
            "UI/Default", "UI/DefaultETC1", "UI/Default Font", "Sprites/Default", "Sprites/Mask", "Standard", "Unlit/Texture", "Unlit/Color", "Unlit/Transparent",
            "Unlit/Transparent Cutout", "Hidden/InternalErrorShader", "Legacy Shaders/Particles/Additive", "Legacy Shaders/Particles/Alpha Blended",
            "Legacy Shaders/Particles/Alpha Blended Premultiply", "Particles/Standard Unlit", "Skybox/Procedural", "GUI/Text Shader", "Hidden/Internal-Colored",
        };

        /// <summary>照名字拿 shader；專案沒有、也不是已知內建的，建一個只有名字的（繪製端照名字處理或畫成錯誤色）</summary>
        public static Shader ByName(string name)
        {
            var s = Find2(name);
            if ((object)s != null) return s;
            s = (Shader)Native.NewShell(typeof(Shader));
            var n = Native.Bind(new NShader { Name = name, Source = "builtin:" + name }, s, true);
            n.RenderQueue = name.StartsWith("UI/") || name.StartsWith("Sprites/") || name.Contains("Particles") || name.Contains("Transparent") ? 3000 : 2000;
            BuiltinDefaults(n);
            byName[name] = s;
            return s;
        }

        static Shader Find2(string name)
        {
            var s = Lookup(name);
            if ((object)s != null) return s;
            if (guidByName == null) Find("\0");
            return guidByName != null && guidByName.TryGetValue(name, out var g) ? Assets.Load(g, "4800000", typeof(Shader)) as Shader : null;
        }

        /// <summary>內建 shader 的屬性與預設值（Unity 2022.3 的 UI-Default.shader、Sprites-Default.shader）</summary>
        static void BuiltinDefaults(NShader n)
        {
            void V(string k, Vector4 v, int type = 0) { n.Props.Add((k, type)); n.Defaults.Vectors[PropertyIds.Of(k)] = v; }
            void F(string k, float v) { n.Props.Add((k, 2)); n.Defaults.Floats[PropertyIds.Of(k)] = v; }
            void T(string k) { n.Props.Add((k, 4)); n.Defaults.Textures[PropertyIds.Of(k)] = new TexEnv { Scale = Vector2.one }; }
            T("_MainTex");
            V("_Color", Vector4.one);
            if (n.Name.StartsWith("UI/") || n.Name == "GUI/Text Shader")
            {
                F("_StencilComp", 8); F("_Stencil", 0); F("_StencilOp", 0); F("_StencilWriteMask", 255); F("_StencilReadMask", 255);
                F("_ColorMask", 15); F("_UseUIAlphaClip", 0);
            }
            if (n.Name.StartsWith("Sprites/"))
            {
                V("_RendererColor", Vector4.one); V("_Flip", new Vector4(1, 1, 0, 0), 1); F("PixelSnap", 0); F("_EnableExternalAlpha", 0);
            }
        }

        public static void Init(NShader n, JsonObject r)
        {
            byName.TryAdd(n.Name, (Shader)n.Managed); // 同名的 shader 資產：Shader.Find 拿先載入的那個
            if (r["m_PropInfo"] is JsonArray props)
                foreach (var p in props)
                {
                    var name = p?["m_Name"]?.ToString();
                    if (name == null) continue;
                    int type = (int)Serializer.D(p["m_Type"]);
                    n.Props.Add((name, type));
                    var id = PropertyIds.Of(name);
                    int flags = (int)Serializer.D(p["m_Flags"]);
                    if ((flags & 0x80) != 0 && n.MainTextureId < 0) n.MainTextureId = id;
                    if ((flags & 0x100) != 0 && n.MainColorId < 0) n.MainColorId = id;
                    if (type == 0 && (flags & 0x10) != 0) n.HdrColors.Add(id);
                    var dv = p["m_DefValue"] as JsonArray;
                    float d0 = dv != null && dv.Count > 0 ? (float)Serializer.D(dv[0]) : 0;
                    switch (type)
                    {
                        case 0: case 1: n.Defaults.Vectors[id] = dv != null && dv.Count >= 4 ? new Vector4(d0, (float)Serializer.D(dv[1]), (float)Serializer.D(dv[2]), (float)Serializer.D(dv[3])) : Vector4.zero; break;
                        case 2: case 3: n.Defaults.Floats[id] = d0; break;
                        case 5: n.Defaults.Ints[id] = (int)d0; n.Defaults.Floats[id] = d0; break;
                        case 4: n.Defaults.Textures[id] = new TexEnv { Scale = Vector2.one, Texture = DefaultTexture(p["m_DefTexture"]?["m_DefaultName"]?.ToString()) }; break;
                    }
                }
            var tags = (r["m_SubShaders"] as JsonArray)?[0]?["m_Tags"]?["tags"] as JsonObject;
            var q = tags?["QUEUE"]?.ToString() ?? tags?["Queue"]?.ToString();
            if (q != null) n.RenderQueue = ParseQueue(q);
        }

        public static int ParseQueue(string q)
        {
            int baseQ = 2000, off = 0;
            var s = q.Trim();
            int plus = s.IndexOfAny(new[] { '+', '-' });
            if (plus > 0) { int.TryParse(s.Substring(plus), out off); s = s.Substring(0, plus); }
            switch (s.ToLowerInvariant())
            {
                case "background": baseQ = 1000; break;
                case "geometry": baseQ = 2000; break;
                case "alphatest": baseQ = 2450; break;
                case "transparent": baseQ = 3000; break;
                case "overlay": baseQ = 4000; break;
                default: if (int.TryParse(s, out var n)) return n; break;
            }
            return baseQ + off;
        }

        public static Func<string, Texture> DefaultTextureResolver;
        static Texture DefaultTexture(string name) => DefaultTextureResolver?.Invoke(name);
    }
}
