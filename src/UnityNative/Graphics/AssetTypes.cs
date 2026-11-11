// 繪製類素材的原生物件：貼圖、sprite、材質（含 MaterialPropertyBlock 共用的屬性表）、shader、字型、文字、聲音片段。
// 載入函式登記到 Assets.Loaders；GPU 那邊（上傳貼圖、編譯 shader）由繪製端在用到時才做。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public static class PropertyIds
    {
        static readonly Dictionary<string, int> byName = new Dictionary<string, int>();
        static readonly List<string> names = new List<string> { "" };
        public static int Of(string name)
        {
            if (name == null) return 0;
            if (byName.TryGetValue(name, out var id)) return id;
            id = names.Count;
            names.Add(name);
            byName[name] = id;
            return id;
        }
        public static string Name(int id) => (uint)id < (uint)names.Count ? names[id] : "";
    }

    public struct TexEnv { public Texture Texture; public Vector2 Scale; public Vector2 Offset; }

    /// <summary>材質與 MaterialPropertyBlock 的屬性</summary>
    public sealed class PropertySheet
    {
        public readonly Dictionary<int, float> Floats = new Dictionary<int, float>();
        public readonly Dictionary<int, int> Ints = new Dictionary<int, int>();
        public readonly Dictionary<int, Vector4> Vectors = new Dictionary<int, Vector4>();
        public readonly Dictionary<int, TexEnv> Textures = new Dictionary<int, TexEnv>();
        public readonly Dictionary<int, Matrix4x4> Matrices = new Dictionary<int, Matrix4x4>();
        public readonly Dictionary<int, Array> Arrays = new Dictionary<int, Array>();
        public int Version;

        public bool IsEmpty => Floats.Count + Ints.Count + Vectors.Count + Textures.Count + Matrices.Count + Arrays.Count == 0;
        public void Clear() { Floats.Clear(); Ints.Clear(); Vectors.Clear(); Textures.Clear(); Matrices.Clear(); Arrays.Clear(); Version++; }

        public void CopyFrom(PropertySheet o)
        {
            Clear();
            foreach (var kv in o.Floats) Floats[kv.Key] = kv.Value;
            foreach (var kv in o.Ints) Ints[kv.Key] = kv.Value;
            foreach (var kv in o.Vectors) Vectors[kv.Key] = kv.Value;
            foreach (var kv in o.Textures) Textures[kv.Key] = kv.Value;
            foreach (var kv in o.Matrices) Matrices[kv.Key] = kv.Value;
            foreach (var kv in o.Arrays) Arrays[kv.Key] = (Array)kv.Value.Clone();
        }

        public float GetFloat(int id) => Floats.TryGetValue(id, out var f) ? f : Ints.TryGetValue(id, out var i) ? i : 0f;
        public int GetInt(int id) => Ints.TryGetValue(id, out var i) ? i : Floats.TryGetValue(id, out var f) ? (int)f : 0;
        public Vector4 GetVector(int id) => Vectors.TryGetValue(id, out var v) ? v : Vector4.zero;
        public void SetFloat(int id, float v) { Floats[id] = v; Version++; }
        public void SetInt(int id, int v) { Ints[id] = v; Floats[id] = v; Version++; }
        public void SetVector(int id, Vector4 v) { Vectors[id] = v; Version++; }
        public void SetTexture(int id, Texture t)
        {
            Textures.TryGetValue(id, out var e);
            if (e.Scale == default) e.Scale = Vector2.one;
            e.Texture = t;
            Textures[id] = e;
            Version++;
        }
        public void SetScaleOffset(int id, Vector2? scale, Vector2? offset)
        {
            Textures.TryGetValue(id, out var e);
            if (e.Scale == default && scale == null) e.Scale = Vector2.one;
            if (scale.HasValue) e.Scale = scale.Value;
            if (offset.HasValue) e.Offset = offset.Value;
            Textures[id] = e;
            Version++;
        }
    }

    public class NTexture : NObject, IDestroyable
    {
        public int Width, Height, Depth = 1, MipCount = 1;
        public string File;
        public FilterMode Filter = FilterMode.Bilinear;
        public TextureWrapMode WrapU = TextureWrapMode.Clamp, WrapV = TextureWrapMode.Clamp, WrapW = TextureWrapMode.Clamp;
        public bool SRGB = true, Mips, Readable;
        public int AnisoLevel = 1;
        public float MipBias;
        public TextureFormat Format = TextureFormat.RGBA32;
        public TextureDimension Dimension = TextureDimension.Tex2D;
        /// <summary>File 是保留原始壓縮資料的容器（.mt2t，docs/perf-battle.md「B 段」）：原版的格式（Unity TextureFormat 的 DXT1、DXT5、BC6H、BC7）；PNG 是 0。
        /// Format 照舊是 RGBA32（以前從 PNG 讀也是，腳本看到的不變）</summary>
        public int Compressed;
        /// <summary>RGBA8 像素（腳本讀寫、或程式產生的貼圖才有；從檔案載入的由繪製端自己讀檔）</summary>
        public byte[] Pixels;
        /// <summary>像素被腳本或引擎改過（Apply、字型加字…）：上傳 GPU 之後也要留著，不能再從檔案重讀。
        /// Readable 只是原版的 Read/Write 旗標：Diceomancer 開了的有 1485 張、全留著要 7 GB，沒改過的照樣丟掉、要讀時再從檔案解</summary>
        public bool CpuWritten;
        public uint UpdateCount;
        /// <summary>繪製端的貼圖物件（GL texture）</summary>
        public object Gpu;
        public bool GpuDirty = true;
        /// <summary>只有取樣參數（wrap、filter）變了：繪製端只重設 texture 參數，不重傳像素（RenderTexture 不用這個，照舊設 GpuDirty）</summary>
        public bool ParamsDirty;
        public virtual void OnDestroyNative() { GpuRelease?.Invoke(this); }
        public static Action<NTexture> GpuRelease;
        /// <summary>量測（ctl「tex」）：誰在設會讓貼圖重傳的值（設定名、值有沒有變）；沒在追蹤時是 null</summary>
        public static Action<NTexture, string, bool> TraceSet;
    }

    public class NSprite : NObject
    {
        public NTexture Texture;
        public Rect Rect, TextureRect;
        public Vector2 Pivot, TextureRectOffset;
        public Vector4 Border;
        public float PixelsPerUnit = 100;
        public bool Packed;
        public Vector2[] Vertices, UV;
        public ushort[] Triangles;
        public string MeshFile;
        public JsonObject InlineMesh;
        public bool MeshLoaded;
    }

    public class NShader : NObject
    {
        public JsonObject Record;
        public int RenderQueue = 2000;
        public int MaximumLOD = -1;
        /// <summary>m_PropInfo 的預設值（材質沒設的屬性用這個）</summary>
        public readonly PropertySheet Defaults = new PropertySheet();
        public readonly List<(string name, int type)> Props = new List<(string, int)>();
        /// <summary>標了 [MainTexture]／[MainColor] 的屬性（m_Flags 0x80／0x100），沒有是 -1（Material.mainTexture／color 退回 _MainTex／_Color）</summary>
        public int MainTextureId = -1, MainColorId = -1;
        /// <summary>標了 [HDR] 的顏色屬性（m_Flags 16）：送進 shader 時不轉線性（Draw.IsColor）</summary>
        public readonly HashSet<int> HdrColors = new HashSet<int>();
        /// <summary>繪製端每次 draw 都會問的：屬性是不是顏色、貼圖屬性的預設貼圖名（m_DefTexture）。第一次問才算</summary>
        public readonly System.Collections.Generic.Dictionary<string, bool> IsColorCache = new System.Collections.Generic.Dictionary<string, bool>();
        public System.Collections.Generic.Dictionary<string, string> DefaultTexNames;
        public object Gpu;
    }

    public class NMaterial : NObject, ICloneableAsset
    {
        public NShader Shader;
        public readonly PropertySheet Props = new PropertySheet();
        public readonly HashSet<string> Keywords = new HashSet<string>();
        public int CustomRenderQueue = -1;
        public readonly Dictionary<string, string> Tags = new Dictionary<string, string>();
        public readonly HashSet<string> DisabledPasses = new HashSet<string>();
        public bool EnableInstancing, DoubleSidedGI;
        public MaterialGlobalIlluminationFlags GIFlags;
        public int Version;
        public Material M => (Material)Managed;

        public int RenderQueue => CustomRenderQueue >= 0 ? CustomRenderQueue : Shader?.RenderQueue ?? 2000;

        public UnityEngine.Object CloneAsset()
        {
            var m = (Material)Native.NewShell(typeof(Material));
            var n = Native.Bind(new NMaterial { Name = Name }, m);
            n.CopyFrom(this);
            return m;
        }

        public void CopyFrom(NMaterial o)
        {
            Shader = o.Shader;
            Props.CopyFrom(o.Props);
            Keywords.Clear(); Keywords.UnionWith(o.Keywords);
            CustomRenderQueue = o.CustomRenderQueue;
            Tags.Clear(); foreach (var kv in o.Tags) Tags[kv.Key] = kv.Value;
            DisabledPasses.Clear(); DisabledPasses.UnionWith(o.DisabledPasses);
            EnableInstancing = o.EnableInstancing;
            Version++;
        }

        // 屬性的值：材質設過的優先，沒有就用 shader 的預設
        public float GetFloat(int id) => Props.Floats.ContainsKey(id) || Props.Ints.ContainsKey(id) ? Props.GetFloat(id) : Shader?.Defaults.GetFloat(id) ?? 0f;
        public Vector4 GetVector(int id) => Props.Vectors.TryGetValue(id, out var v) ? v : Shader?.Defaults.GetVector(id) ?? Vector4.zero;
        public TexEnv GetTexEnv(int id)
        {
            if (Props.Textures.TryGetValue(id, out var e)) return e;
            if (Shader != null && Shader.Defaults.Textures.TryGetValue(id, out e)) return e;
            return new TexEnv { Scale = Vector2.one };
        }
        public bool Has(int id) => Props.Floats.ContainsKey(id) || Props.Ints.ContainsKey(id) || Props.Vectors.ContainsKey(id) || Props.Textures.ContainsKey(id) || Props.Matrices.ContainsKey(id)
            || (Shader != null && Shader.Props.Exists(p => PropertyIds.Of(p.name) == id));
    }

    public class NFont : NObject
    {
        public string File;
        public string[] Names = Array.Empty<string>();
        public int FontSize = 16, Ascent = 16, LineHeight = 16;
        public bool Dynamic = true;
        public Material Material;
        public CharacterInfo[] Chars = Array.Empty<CharacterInfo>();
    }

    public class NTextAsset : NObject
    {
        public byte[] Bytes;
        public string Text;
        public byte[] GetBytes() => Bytes ??= System.Text.Encoding.UTF8.GetBytes(Text ?? "");
    }

    public class NAudioClip : NObject
    {
        public string File;
        public int Channels = 2, Frequency = 44100, Samples;
        public AudioClipLoadType LoadType;
        public bool Preload = true, LoadInBackground, Stream;
        public float[] Data;
        public object Decoded;
        public AudioDataLoadState LoadState = AudioDataLoadState.Unloaded;
    }

    /// <summary>素材載入函式：public/game/assets 的索引 → 原生物件</summary>
    public static class GraphicsAssets
    {
        public static void Register()
        {
            Assets.Loaders["texture"] = (g, f, r, w) => Texture(g, r);
            Assets.Loaders["sprite"] = (g, f, r, w) => Sprite(g, r);
            Assets.Loaders["material"] = (g, f, r, w) => Material(g, r, r?["m_Name"]?.ToString());
            Assets.Loaders["shader"] = (g, f, r, w) => Shader(g, r);
            Assets.Loaders["font"] = (g, f, r, w) => Font(g, r);
            Assets.Loaders["text"] = (g, f, r, w) => Text(g, r);
            Assets.Loaders["audioclip"] = (g, f, r, w) => Audio(g, r);
            Assets.Loaders["builtin:Material"] = (g, f, r, w) => Material(g + ":" + f, r?["material"] as JsonObject, r?["name"]?.ToString(), r?["shaderName"]?.ToString());
            Assets.Loaders["builtin:Sprite"] = (g, f, r, w) => BuiltinSprite(g + ":" + f, r, w);
        }

        static Texture2D Texture(string guid, JsonObject r)
        {
            if (r == null) return null;
            var t = (Texture2D)Native.NewShell(typeof(Texture2D));
            var imp = r["importer"] as JsonObject;
            var n = Native.Bind(new NTexture
            {
                Name = r["name"]?.ToString() ?? "",
                Width = (int)Serializer.D(r["width"]),
                Height = (int)Serializer.D(r["height"]),
                File = r["file"] != null ? Assets.AssetPath(r["file"].ToString()) : null,
                Filter = (FilterMode)(int)(imp?["filterMode"] != null ? Serializer.D(imp["filterMode"]) : 1),
                WrapU = (TextureWrapMode)(int)(imp?["wrapU"] != null ? Serializer.D(imp["wrapU"]) : 1),
                WrapV = (TextureWrapMode)(int)(imp?["wrapV"] != null ? Serializer.D(imp["wrapV"]) : 1),
                SRGB = imp?["sRGBTexture"] == null || Serializer.D(imp["sRGBTexture"]) != 0,
                Mips = imp?["enableMipMap"] != null && Serializer.D(imp["enableMipMap"]) == 1,
                // Read/Write（TextureImporter.isReadable）：TMP 動態字型的 atlas 要寫得進去；匯入器較舊的輸出沒有這欄，當成關
                Readable = imp?["isReadable"] != null && Serializer.D(imp["isReadable"]) == 1,
                // importer 寫的格式名（保留原始壓縮資料的才有）與 mip 層數
                Compressed = CompressedFormat(r["format"]?.ToString()),
                MipCount = r["mips"] != null ? (int)Serializer.D(r["mips"]) : 1,
                Source = guid,
            }, t, true);
            if (r["depth"] != null) { n.Depth = (int)Serializer.D(r["depth"]); n.Dimension = TextureDimension.Tex2DArray; }
            return t;
        }

        static int CompressedFormat(string name) => name switch
        {
            "DXT1" => (int)TextureFormat.DXT1,
            "DXT5" => (int)TextureFormat.DXT5,
            "BC6H" => (int)TextureFormat.BC6H,
            "BC7" => (int)TextureFormat.BC7,
            null => 0,
            _ => throw new NotSupportedException("貼圖格式 " + name),
        };

        static Sprite Sprite(string guid, JsonObject r)
        {
            if (r == null) return null;
            var rd = r["m_RD"] as JsonObject;
            var texRef = rd?["texture"] as JsonObject;
            var tex = texRef != null ? Assets.Load(texRef["guid"]?.ToString(), texRef["fileID"]?.ToString() ?? "2800000", typeof(Texture2D)) as Texture2D : null;
            var s = (Sprite)Native.NewShell(typeof(Sprite));
            var n = Native.Bind(new NSprite
            {
                Name = r["m_Name"]?.ToString() ?? "",
                Texture = Native.Of<NTexture>(tex),
                Rect = (Rect)Serializer.Convert(r["m_Rect"], typeof(Rect), null),
                Pivot = (Vector2)Serializer.Convert(r["m_Pivot"], typeof(Vector2), null),
                Border = (Vector4)Serializer.Convert(r["m_Border"], typeof(Vector4), null),
                PixelsPerUnit = (float)Serializer.D(r["m_PixelsToUnits"]),
                TextureRect = (Rect)Serializer.Convert(rd?["textureRect"], typeof(Rect), null),
                TextureRectOffset = (Vector2)Serializer.Convert(rd?["textureRectOffset"], typeof(Vector2), null),
                Packed = r["m_SpriteAtlas"] != null,
                MeshFile = r["mesh"] != null ? Assets.AssetPath(r["mesh"].ToString()) : null,
                Source = guid,
            }, s, true);
            return s;
        }

        static Sprite BuiltinSprite(string key, JsonObject r, Type want)
        {
            var sp = r?["sprite"] as JsonObject;
            var tr = r?["texture"] as JsonObject;
            if (sp == null || tr == null) return null;
            var t = (Texture2D)Native.NewShell(typeof(Texture2D));
            var nt = Native.Bind(new NTexture
            {
                Name = tr["name"]?.ToString() ?? "",
                Width = (int)Serializer.D(tr["width"]),
                Height = (int)Serializer.D(tr["height"]),
                File = Assets.AssetPath(tr["file"].ToString()),
                Filter = (FilterMode)(int)Serializer.D(tr["filterMode"]),
                WrapU = (TextureWrapMode)(int)Serializer.D(tr["wrapU"]),
                WrapV = (TextureWrapMode)(int)Serializer.D(tr["wrapV"]),
                SRGB = Serializer.D(tr["colorSpace"]) == 1,
                Source = key,
            }, t, true);
            var rd = sp["m_RD"] as JsonObject;
            var s = (Sprite)Native.NewShell(typeof(Sprite));
            Native.Bind(new NSprite
            {
                Name = r["name"]?.ToString() ?? "",
                Texture = nt,
                Rect = (Rect)Serializer.Convert(sp["m_Rect"], typeof(Rect), null),
                Pivot = (Vector2)Serializer.Convert(sp["m_Pivot"], typeof(Vector2), null),
                Border = (Vector4)Serializer.Convert(sp["m_Border"], typeof(Vector4), null),
                PixelsPerUnit = (float)Serializer.D(sp["m_PixelsToUnits"]),
                TextureRect = (Rect)Serializer.Convert(rd?["textureRect"], typeof(Rect), null),
                TextureRectOffset = (Vector2)Serializer.Convert(rd?["textureRectOffset"], typeof(Vector2), null),
                InlineMesh = r["mesh"] as JsonObject,
                Source = key,
            }, s, true);
            if (want == typeof(Texture2D) || want == typeof(Texture)) return null;
            return s;
        }

        public static Material Material(string key, JsonObject r, string name, string shaderName = null)
        {
            if (r == null) return null;
            var m = (Material)Native.NewShell(typeof(Material));
            var n = Native.Bind(new NMaterial { Name = name ?? r["m_Name"]?.ToString() ?? "", Source = key }, m, true);
            shaderName ??= r["shaderName"]?.ToString();
            n.Shader = ShaderFor(r["m_Shader"] as JsonObject, shaderName);
            LoadProps(n, r);
            return m;
        }

        public static void LoadProps(NMaterial n, JsonObject r)
        {
            if (r["m_ValidKeywords"] is JsonArray vk) foreach (var k in vk) n.Keywords.Add(k.ToString());
            else if (r["m_ShaderKeywords"] is JsonValue sk && sk.ToString() is string s && s.Length > 0) foreach (var k in s.Split(' ', StringSplitOptions.RemoveEmptyEntries)) n.Keywords.Add(k);
            if (r["m_CustomRenderQueue"] != null) n.CustomRenderQueue = (int)Serializer.D(r["m_CustomRenderQueue"]);
            if (r["stringTagMap"] is JsonObject tags) foreach (var kv in tags) n.Tags[kv.Key] = kv.Value?.ToString();
            if (r["disabledShaderPasses"] is JsonArray dp) foreach (var p in dp) n.DisabledPasses.Add(p.ToString());
            var sp = r["m_SavedProperties"] as JsonObject;
            if (sp == null) return;
            if (sp["m_TexEnvs"] is JsonObject te)
                foreach (var kv in te)
                {
                    var e = kv.Value as JsonObject;
                    var id = PropertyIds.Of(kv.Key);
                    var tex = e?["m_Texture"] is JsonObject tr && tr["guid"] != null ? Assets.Load(tr["guid"].ToString(), tr["fileID"]?.ToString() ?? "2800000", typeof(Texture)) as Texture : null;
                    n.Props.Textures[id] = new TexEnv
                    {
                        Texture = tex,
                        Scale = e?["m_Scale"] != null ? (Vector2)Serializer.Convert(e["m_Scale"], typeof(Vector2), null) : Vector2.one,
                        Offset = e?["m_Offset"] != null ? (Vector2)Serializer.Convert(e["m_Offset"], typeof(Vector2), null) : Vector2.zero,
                    };
                }
            if (sp["m_Floats"] is JsonObject fl) foreach (var kv in fl) n.Props.Floats[PropertyIds.Of(kv.Key)] = (float)Serializer.D(kv.Value);
            if (sp["m_Ints"] is JsonObject it) foreach (var kv in it) { var id = PropertyIds.Of(kv.Key); n.Props.Ints[id] = (int)Serializer.D(kv.Value); n.Props.Floats[id] = (float)Serializer.D(kv.Value); }
            if (sp["m_Colors"] is JsonObject co)
                foreach (var kv in co)
                {
                    var c = kv.Value;
                    n.Props.Vectors[PropertyIds.Of(kv.Key)] = c?["r"] != null ? new Vector4(Serializer.F(c, "r"), Serializer.F(c, "g"), Serializer.F(c, "b"), Serializer.F(c, "a"))
                        : new Vector4(Serializer.F(c, "x"), Serializer.F(c, "y"), Serializer.F(c, "z"), Serializer.F(c, "w"));
                }
        }

        static NShader ShaderFor(JsonObject sref, string name)
        {
            var guid = sref?["guid"]?.ToString();
            if (guid != null && guid != Assets.BuiltinExtra && guid != Assets.BuiltinDefault)
            {
                var s = Assets.Load(guid, sref["fileID"]?.ToString() ?? "4800000", typeof(Shader)) as Shader;
                if ((object)s != null) return Native.Of<NShader>(s);
            }
            return name != null ? Native.Of<NShader>(Shaders.ByName(name)) : null;
        }

        static Shader Shader(string guid, JsonObject r)
        {
            if (r == null) return null;
            var name = r["name"]?.ToString() ?? "";
            // 照名字建過的內建殼就沿用；另一個同名的 shader 資產不能共用（各自只帶自己材質用到的變體，Unity 也是兩個 Shader）
            var existing = Shaders.Lookup(name);
            if ((object)existing != null && Native.Of<NShader>(existing)?.Source?.StartsWith("builtin:") == true) return existing;
            var s = (Shader)Native.NewShell(typeof(Shader));
            var n = Native.Bind(new NShader { Name = name, Record = r, Source = guid }, s, true);
            Shaders.Init(n, r);
            return s;
        }

        static Font Font(string guid, JsonObject r)
        {
            if (r == null) return null;
            var f = (Font)Native.NewShell(typeof(Font));
            var imp = r["importer"] as JsonObject;
            var names = new List<string>();
            if (imp?["fontNames"] is JsonArray fn) foreach (var x in fn) names.Add(x.ToString());
            Native.Bind(new NFont
            {
                Name = r["name"]?.ToString() ?? "",
                File = r["file"] != null ? Assets.AssetPath(r["file"].ToString()) : null,
                Names = names.ToArray(),
                FontSize = imp?["fontSize"] != null ? (int)Serializer.D(imp["fontSize"]) : 16,
                Source = guid,
            }, f, true);
            return f;
        }

        static TextAsset Text(string guid, JsonObject r)
        {
            if (r == null) return null;
            var t = (TextAsset)Native.NewShell(typeof(TextAsset));
            var n = Native.Bind(new NTextAsset { Name = r["name"]?.ToString() ?? "", Source = guid }, t, true);
            if (r["text"] != null) n.Text = r["text"].ToString();
            else if (r["file"] != null) n.Bytes = File.ReadAllBytes(Assets.AssetPath(r["file"].ToString()));
            else if (r["base64"] != null) n.Bytes = Convert.FromBase64String(r["base64"].ToString());
            return t;
        }

        static AudioClip Audio(string guid, JsonObject r)
        {
            if (r == null) return null;
            var a = (AudioClip)Native.NewShell(typeof(AudioClip));
            var ds = r["importer"]?["defaultSettings"] as JsonObject;
            Native.Bind(new NAudioClip
            {
                Name = r["name"]?.ToString() ?? "",
                File = r["file"] != null ? Assets.AssetPath(r["file"].ToString()) : null,
                Channels = (int)Serializer.D(r["channels"]),
                Frequency = (int)Serializer.D(r["frequency"]),
                Samples = (int)Serializer.D(r["samples"]),
                LoadType = (AudioClipLoadType)(int)Serializer.D(ds?["loadType"]),
                Preload = ds?["preloadAudioData"] == null || Serializer.D(ds["preloadAudioData"]) != 0,
                Source = guid,
            }, a, true);
            return a;
        }
    }
}
