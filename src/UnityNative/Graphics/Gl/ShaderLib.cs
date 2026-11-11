// 匯入器轉好的 shader（public/game/shaders/<stem>/index.json ＋ 每個變體的 GLSL ES 3.00）→ GL program。
// 規則照 TS 版 render/unityShader.ts：
//   - pass：LightMode 沒標、Always、ForwardBase、SRPDefaultUnlit、UniversalForward 的第一個（內建管線）；GrabPass（type 2）另外處理
//   - 變體：材質 keyword ∩ 這個 stage 的 keyword 空間，找 keyword 完全相同的；沒有就找相同 keyword 最多、多出來最少的
//   - cbuffer：GLSL 裡是 std140 的 UBO（vec4／uvec4 m[N]，版面同 D3D register），照 index.json 的 byte offset 依名稱填
//   - 貼圖：sampler 是 SPIRV_Cross_Combined t{register} s{samplerRegister}
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative.Gl
{
    public sealed class CbField
    {
        public string Name; public int Offset, Dim, Rows, ArraySize; public bool Matrix;
        /// <summary>每次 draw 都要的查表先算好：屬性 id；NotBuiltin = 名字不是內建變數（Draw.Builtin 的 found 只看名字）</summary>
        public int Id = int.MinValue;
        public bool NotBuiltin;
        /// <summary>欄位種類（Draw.Resolve 第一次用到時照名字定下來，-1 還沒定）；_ST、_TexelSize 的貼圖 id 與名字</summary>
        public sbyte Kind = -1;
        public int TexId;
        public string TexName;
        /// <summary>上次查「這個屬性是不是顏色」的 shader 與結果（Draw.IsColor 的快取，換 shader 才重查）</summary>
        public NShader ColorShader;
        public bool ColorIs;
    }

    public sealed class GpuBlock
    {
        public string Name;     // cbuffer 名（$Globals、UnityPerDraw…）
        public uint Binding;
        public int Size;
        public readonly List<CbField> Fields = new List<CbField>();
        public byte[] Data;
        /// <summary>uniform 快取：欄位全是材質屬性（不是內建變數、不是矩陣）時，每個材質記一份算好的 Data，
        /// 材質／全域／覆寫都沒變就直接用。Memoable：-1 還不知道（第一次 Fill 完才知道哪些欄位是內建變數）、0 不行、1 可以</summary>
        public sbyte Memoable = -1;
        public readonly Dictionary<object, BlockMemo> Memos = new Dictionary<object, BlockMemo>();
        /// <summary>欄位全是只看時間、攝影機、model 的內建變數（UnityPerFrame、UnityPerCamera、UnityPerDraw）：
        /// 同一格、攝影機沒換、model 一樣就沿用上次的 Data。CamStatic：-1 還不知道、0 不行、1 可以</summary>
        public sbyte CamStatic = -1;
        public int CamFrame = -1, CamGenSeen = -1;
        public UnityEngine.Matrix4x4 CamModel;
    }

    public sealed class BlockMemo
    {
        public byte[] Data;
        public int MatVer, PropsVer, GlobalVer, Ov;
    }

    public sealed class GpuTex { public int Location; public string Name; public int Unit; public bool Array; }

    public sealed class GpuInput { public int Location; public string Semantic; public int Index; }

    public sealed class PassState
    {
        public JsonObject State;
        public JsonObject Tags;
        public int Type;
        public string GrabTexture;
        /// <summary>State 預先編好的版本（Draw.ApplyState 第一次用到才編）</summary>
        public Draw.CompiledState Compiled;
    }

    public sealed class GpuProgram
    {
        public uint Prog;
        public readonly List<GpuBlock> Blocks = new List<GpuBlock>();
        public readonly List<GpuTex> Textures = new List<GpuTex>();
        public readonly List<GpuInput> Inputs = new List<GpuInput>();
        public PassState Pass;
        public string Name;
        /// <summary>手寫的內建 shader：uniform 用普通的 uniform（名字直接對）</summary>
        public Dictionary<string, int> Plain;
    }

    public sealed class ShaderEntry
    {
        public string Name, Dir;
        public JsonObject Index;
        public readonly Dictionary<string, List<GpuProgram>> Cache = new Dictionary<string, List<GpuProgram>>();
        /// <summary>沒有 pass 對得上 renderer 標籤的組合（Programs 的 cache key）</summary>
        public readonly HashSet<string> NoMatch = new HashSet<string>();
        public bool Failed;
    }

    public static unsafe class ShaderLib
    {
        static Dictionary<string, string> dirByName, dirByGuid;
        static readonly Dictionary<string, ShaderEntry> entries = new Dictionary<string, ShaderEntry>();
        static readonly Dictionary<string, ShaderEntry> entriesByDir = new Dictionary<string, ShaderEntry>();
        public static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>index.json 開頭的字串欄位（鍵照字母排，guid 在 name 前面；before 以後的不算，免得拿到深層的同名鍵）</summary>
        static string HeadString(string txt, string key, int before = int.MaxValue)
        {
            int i = txt.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
            if (i < 0 || i > before) return null;
            int a = txt.IndexOf('"', i + key.Length + 3) + 1, b = txt.IndexOf('"', a);
            return a > 0 && b > a ? System.Text.RegularExpressions.Regex.Unescape(txt.Substring(a, b - a)) : null;
        }

        static void Scan()
        {
            if (dirByName != null) return;
            dirByName = new Dictionary<string, string>();
            dirByGuid = new Dictionary<string, string>();
            var root = Path.Combine(Assets.Dir, "shaders");
            if (!Directory.Exists(root)) return;
            // 同名的目錄只靠名字找時先到先贏：照目錄名排序，不看檔案系統列舉的順序（重新轉換後會變）
            var dirs = new List<string>(Directory.EnumerateDirectories(root));
            dirs.Sort(StringComparer.Ordinal);
            foreach (var d in dirs)
            {
                var f = Path.Combine(d, "index.json");
                if (!File.Exists(f)) continue;
                try
                {
                    using var s = File.OpenRead(f);
                    // 只讀 name、guid：index.json 可能很大，整份 parse 留到用到時
                    var head = new byte[4096];
                    int n = s.Read(head, 0, head.Length);
                    var txt = Encoding.UTF8.GetString(head, 0, n);
                    int ni = txt.IndexOf("\"name\":", StringComparison.Ordinal);
                    string name = HeadString(txt, "name"), guid = ni >= 0 ? HeadString(txt, "guid", ni) : null;
                    if (name == null)
                    {
                        var j = JsonNode.Parse(File.ReadAllText(f));
                        name = j?["name"]?.ToString();
                        guid = j?["guid"]?.ToString();
                    }
                    if (name != null && !dirByName.ContainsKey(name)) dirByName[name] = d;
                    if (guid != null) dirByGuid[guid] = d;
                }
                catch { }
            }
        }

        /// <summary>shader 的轉換結果：先照它自己的資產 GUID（同名的 shader 資產各有各的變體），舊的輸出沒有 guid 才照名字</summary>
        public static ShaderEntry Entry(NShader shader)
        {
            if (shader?.Name == null) return null;
            Scan();
            if (shader.Source != null && dirByGuid.TryGetValue(shader.Source, out var dir))
            {
                if (entriesByDir.TryGetValue(dir, out var hit)) return hit;
                return entriesByDir[dir] = Load(shader.Name, dir);
            }
            return Entry(shader.Name);
        }

        public static ShaderEntry Entry(string name)
        {
            if (name == null) return null;
            if (entries.TryGetValue(name, out var e)) return e;
            Scan();
            if (dirByName.TryGetValue(name, out var dir))
            {
                if (!entriesByDir.TryGetValue(dir, out e)) entriesByDir[dir] = e = Load(name, dir);
            }
            else e = new ShaderEntry { Name = name, Failed = true };
            entries[name] = e;
            return e;
        }

        static ShaderEntry Load(string name, string dir)
        {
            var e = new ShaderEntry { Name = name, Dir = dir };
            try { e.Index = Assets.ParseFile(Path.Combine(dir, "index.json")); }
            catch (Exception ex) { e.Failed = true; Engine.Log(LogType.Warning, "shader " + name + " 的 index.json 讀不了：" + ex.Message); }
            return e;
        }

        static readonly string[] OkLightModes = { null, "Always", "ForwardBase", "SRPDefaultUnlit", "UniversalForward", "Universal2D" };

        /// <summary>目前這台攝影機的 renderer 種類（RenderPipelines.BuiltIn／UrpForward／Urp2D），繪製端每台攝影機開始時設</summary>
        public static int Mode;

        /// <summary>URP 底下這個材質沒有任何 pass 對得上 renderer 的標籤：Unity 不畫它（繪製端不用代用 shader）</summary>
        public static bool NoMatchingPass(ShaderEntry e, ICollection<string> keywords) => e != null && e.NoMatch.Contains(Mode + "|" + string.Join(" ", Sorted(keywords)));

        static string LightModeOf(JsonObject p)
        {
            var tags = p["tags"] as JsonObject;
            return tags?["LIGHTMODE"]?.ToString() ?? tags?["LightMode"]?.ToString();
        }

        /// <summary>這個 renderer 會畫的 pass，照畫的順序（RenderPipelines 檔頭的規則）</summary>
        static List<JsonObject> PassesFor(JsonArray passes)
        {
            var r = new List<JsonObject>();
            var tags = RenderPipelines.Tags[Mode];
            if (tags == null)
            {
                foreach (var pn in passes)
                {
                    var lm = LightModeOf((JsonObject)pn);
                    if (lm != null && Array.FindIndex(OkLightModes, x => string.Equals(x, lm, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                    r.Add((JsonObject)pn);
                }
                return r;
            }
            foreach (var tag in tags)
                foreach (var pn in passes)
                {
                    var lm = LightModeOf((JsonObject)pn) ?? "SRPDefaultUnlit";
                    if (!string.Equals(lm, tag, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!r.Contains((JsonObject)pn)) r.Add((JsonObject)pn);
                    break;
                }
            return r;
        }

        /// <summary>材質要畫的 pass（每個 pass 一個 program，照順序畫）</summary>
        public static List<GpuProgram> Programs(ShaderEntry e, ICollection<string> keywords, ICollection<string> disabledPasses)
        {
            if (e == null || e.Failed || e.Index == null) return null;
            var key = Mode + "|" + string.Join(" ", Sorted(keywords));
            if (e.Cache.TryGetValue(key, out var list)) return list;
            list = new List<GpuProgram>();
            var subs = e.Index["subShaders"] as JsonArray;
            if (subs != null && subs.Count > 0)
            {
                var sub = (JsonObject)subs[0];
                var chosen = PassesFor(sub["passes"] as JsonArray ?? new JsonArray());
                if (chosen.Count == 0 && Mode != RenderPipelines.BuiltIn) e.NoMatch.Add(key);
                foreach (var p in chosen)
                {
                    var lm = LightModeOf(p);
                    if (lm != null && disabledPasses != null && disabledPasses.Contains(lm)) continue;
                    int type = (int)Serializer.D(p["type"]);
                    if (type == 2)
                    {
                        // GrabPass：沒有程式，記一個標記讓繪製端抓畫面
                        list.Add(new GpuProgram { Pass = new PassState { Type = 2, GrabTexture = p["textureName"]?.ToString() ?? "" }, Name = e.Name });
                        continue;
                    }
                    var prog = Build(e, p, keywords);
                    if (prog != null) list.Add(prog);
                    // 內建管線的 2D 素材：一個 pass 就夠（多 pass 的 shader 照畫）
                }
            }
            e.Cache[key] = list;
            return list;
        }

        /// <summary>
        /// 指定的一個 pass（CommandBuffer.DrawMesh／Blit 的 shaderPass：subshader 0 的第幾個 pass，不看 LightMode）。
        /// flipY：vertex 最後把 gl_Position.y 取負。轉出來的 GLSL 是 D3D 的 bytecode，全螢幕繪製的 uv 寫死了 UNITY_UV_STARTS_AT_TOP 的翻轉；
        /// GL 的 render target 列序相反，位置也跟著翻才對得上（同 TS 版 postProcess.ts）
        /// </summary>
        public static GpuProgram Pass(ShaderEntry e, ICollection<string> keywords, int pass, bool flipY)
        {
            if (e == null || e.Failed || e.Index == null) return null;
            var key = "#" + pass + (flipY ? "f " : " ") + string.Join(" ", Sorted(keywords));
            if (e.Cache.TryGetValue(key, out var list)) return list.Count > 0 ? list[0] : null;
            list = new List<GpuProgram>(1);
            var passes = (e.Index["subShaders"] as JsonArray)?[0]?["passes"] as JsonArray;
            if (passes != null && pass >= 0 && pass < passes.Count && passes[pass] is JsonObject p && (int)Serializer.D(p["type"]) != 2)
            {
                var prog = Build(e, p, keywords, flipY);
                if (prog != null) list.Add(prog);
            }
            e.Cache[key] = list;
            return list.Count > 0 ? list[0] : null;
        }

        static IEnumerable<string> Sorted(ICollection<string> k)
        {
            var l = new List<string>(k ?? Array.Empty<string>());
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        static JsonObject PickVariant(JsonObject stage, JsonArray keywordNames, ICollection<string> want)
        {
            var variants = stage?["variants"] as JsonArray;
            if (variants == null || variants.Count == 0) return null;
            // 這個 stage 的 keyword 空間
            var space = new HashSet<string>();
            var mask = stage["keywordStateMask"]?.ToString() ?? "";
            for (int i = 0; i + 3 < mask.Length; i += 4)
            {
                int idx = Convert.ToInt32(mask.Substring(i + 2, 2) + mask.Substring(i, 2), 16);
                if (keywordNames != null && idx < keywordNames.Count) space.Add(keywordNames[idx].ToString());
            }
            var wanted = new HashSet<string>();
            foreach (var k in want ?? Array.Empty<string>()) if (space.Count == 0 || space.Contains(k)) wanted.Add(k);
            // 沒有剛好的變體（打包時被剝掉）：挑跟目標相同的 keyword 最多的，同分挑多出來的最少的（同 TS 版 chooseVariant）。
            // 只挑子集合會讓 vertex 與 fragment 落在不同的變體：內建 Standard 帶 _NORMALMAP 的 fragment 變體都多帶
            // _GLOSSYREFLECTIONS_OFF／_SPECULARHIGHLIGHTS_OFF，vertex 卻有剛好的，兩邊的 varying 對不上（MT2 火堆籠子整個不見，10/19 實測）
            JsonObject best = null;
            int bestHit = -1, bestExtra = int.MaxValue;
            foreach (var vn in variants)
            {
                var v = (JsonObject)vn;
                if (v["converted"] is JsonValue c && c.TryGetValue<bool>(out var conv) && !conv) continue;
                var kws = new HashSet<string>();
                foreach (var k in v["keywords"] as JsonArray ?? new JsonArray()) kws.Add(k.ToString());
                if (kws.SetEquals(wanted)) return v;
                int hit = 0;
                foreach (var k in kws) if (wanted.Contains(k)) hit++;
                int extra = kws.Count - hit;
                if (hit > bestHit || (hit == bestHit && extra < bestExtra)) { best = v; bestHit = hit; bestExtra = extra; }
            }
            return best;
        }

        static unsafe GpuProgram Build(ShaderEntry e, JsonObject pass, ICollection<string> keywords, bool flipY = false)
        {
            var stages = pass["stages"] as JsonObject;
            var kwNames = e.Index["keywordNames"] as JsonArray;
            var vs = PickVariant(stages?["vertex"] as JsonObject, kwNames, keywords);
            var fs = PickVariant(stages?["fragment"] as JsonObject, kwNames, keywords);
            if (vs == null || fs == null) { Report(e.Name, "沒有轉好的變體"); return null; }
            var vsrc = ReadGlsl(e, vs);
            var fsrc = ReadGlsl(e, fs);
            if (vsrc == null || fsrc == null) { Report(e.Name, "GLSL 檔不在"); return null; }
            if (flipY) { int end = vsrc.LastIndexOf('}'); vsrc = vsrc.Substring(0, end) + "    gl_Position.y = -gl_Position.y;\n}\n"; }
            var v = GL.Compile(GL.VERTEX_SHADER, ShaderSource.Adapt(ShaderSource.D3dClipToGl(vsrc)), out var vlog);
            if (v == 0) { Report(e.Name, "vertex 編譯失敗：" + vlog); return null; }
            var f = GL.Compile(GL.FRAGMENT_SHADER, ShaderSource.Adapt(fsrc), out var flog);
            if (f == 0) { Report(e.Name, "fragment 編譯失敗：" + flog); return null; }
            var prog = GL.Link(v, f, out var llog);
            GL.glDeleteShader(v); GL.glDeleteShader(f);
            if (prog == 0) { Report(e.Name, "link 失敗：" + llog); return null; }

            var gp = new GpuProgram { Prog = prog, Name = e.Name, Pass = new PassState { State = pass["state"] as JsonObject, Tags = pass["tags"] as JsonObject } };
            uint binding = 0;
            AddBlocks(gp, prog, vs, ref binding);
            AddBlocks(gp, prog, fs, ref binding);
            GL.UseProgram(prog);
            int unit = 0;
            AddTextures(gp, prog, vs, vsrc, ref unit);
            AddTextures(gp, prog, fs, fsrc, ref unit);
            foreach (var inp in vs["vertexInputs"] as JsonArray ?? new JsonArray())
                gp.Inputs.Add(new GpuInput { Location = (int)Serializer.D(inp["location"]), Semantic = inp["semantic"]?.ToString(), Index = (int)Serializer.D(inp["index"]) });
            return gp;
        }

        static void Report(string shader, string msg)
        {
            if (Reported.Add(shader + msg)) Engine.Log(LogType.Warning, "shader " + shader + "：" + msg);
        }

        static string ReadGlsl(ShaderEntry e, JsonObject v)
        {
            var file = v["files"]?["glsl"]?.ToString();
            if (file == null) return null;
            var p = Path.Combine(e.Dir, file);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        static void AddBlocks(GpuProgram gp, uint prog, JsonObject variant, ref uint binding)
        {
            var ubs = variant["uniformBlocks"] as JsonObject;
            if (ubs == null) return;
            var cbs = variant["parameters"]?["constantBuffers"] as JsonArray;
            var sizes = new Dictionary<string, int>();
            foreach (var u in variant["reflection"]?["ubos"] as JsonArray ?? new JsonArray()) sizes[u["name"]?.ToString() ?? ""] = (int)Serializer.D(u["block_size"]);
            foreach (var kv in ubs)
            {
                var blockName = kv.Value?["block"]?.ToString();
                if (blockName == null) continue;
                var idx = GL.BlockIndex(prog, blockName);
                if (idx == GL.INVALID_INDEX) continue;
                var b = new GpuBlock { Name = kv.Key, Binding = binding++ };
                GL.glUniformBlockBinding(prog, idx, b.Binding);
                JsonObject cb = null;
                foreach (var c in cbs ?? new JsonArray()) if (c?["name"]?.ToString() == kv.Key) { cb = (JsonObject)c; break; }
                int size = sizes.TryGetValue(blockName, out var s) ? s : (int)Serializer.D(cb?["size"]);
                b.Size = Math.Max(16, (size + 15) / 16 * 16);
                b.Data = new byte[b.Size];
                if (cb != null)
                {
                    foreach (var vn in cb["vectors"] as JsonArray ?? new JsonArray())
                        b.Fields.Add(new CbField { Name = vn["name"]?.ToString(), Offset = (int)Serializer.D(vn["offset"]), Dim = (int)Serializer.D(vn["dim"]), ArraySize = (int)Serializer.D(vn["arraySize"]) });
                    foreach (var mn in cb["matrices"] as JsonArray ?? new JsonArray())
                        b.Fields.Add(new CbField { Name = mn["name"]?.ToString(), Offset = (int)Serializer.D(mn["offset"]), Rows = (int)Serializer.D(mn["rows"]), ArraySize = (int)Serializer.D(mn["arraySize"]), Matrix = true });
                }
                gp.Blocks.Add(b);
            }
        }

        static void AddTextures(GpuProgram gp, uint prog, JsonObject variant, string src, ref int unit)
        {
            foreach (var t in variant["parameters"]?["textures"] as JsonArray ?? new JsonArray())
            {
                var name = t["name"]?.ToString();
                int reg = (int)Serializer.D(t["register"]);
                var uniforms = new List<string>();
                if (t["samplerRegister"] != null) uniforms.Add("SPIRV_Cross_Combinedt" + reg + "s" + (int)Serializer.D(t["samplerRegister"]));
                else
                {
                    // samplerRegister 是 null（共用別的貼圖的取樣器：unity_SpecCube1 用 samplerunity_SpecCube0、Shader Graph 的 _MainTex）：
                    // 照 GLSL 的宣告找這個貼圖暫存器實際配到的取樣器，可能不只一個（同 TS 版）。沒綁上的 sampler 留在 0 號 unit，
                    // 跟 0 號的 cubemap 型別不同時整個 draw 被 GL 拒絕（INVALID_OPERATION；MT2 的 Spine 角色畫不出來，10/19 實測）
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src ?? "",
                        @"uniform\s+(?:(?:highp|mediump|lowp)\s+)?\w+\s+(SPIRV_Cross_Combinedt" + reg + @"s\d+)\s*;"))
                        uniforms.Add(m.Groups[1].Value);
                }
                var locs = new List<int>();
                foreach (var u in uniforms) { int l = GL.Uniform(prog, u); if (l >= 0) locs.Add(l); }
                // 沒有 sampler 的貼圖（Load／texelFetch）：SPIRV-Cross 叫 SPIRV_Cross_CombinedtNSPIRV_Cross_DummySampler
                if (locs.Count == 0) { int l = GL.Uniform(prog, "SPIRV_Cross_Combinedt" + reg + "SPIRV_Cross_DummySampler"); if (l >= 0) locs.Add(l); }
                foreach (var loc in locs)
                {
                    GL.glUniform1i(loc, unit);
                    gp.Textures.Add(new GpuTex { Location = loc, Name = name, Unit = unit, Array = Serializer.D(t["dim"]) == 5 });
                    unit++;
                }
            }
        }
    }

    /// <summary>GLSL ES 3.00 ↔ 桌面 GL 4.1 core</summary>
    public static class ShaderSource
    {
        public static string Adapt(string src)
        {
            if (!GL.Desktop) return src;
            var s = src.Replace("#version 300 es", "#version 410 core");
            return s;
        }

        /// <summary>vertex shader 照 D3D 的 clip z 算完（拿到的是 Draw.GpuProjection 的矩陣），最後換回 GL 的 [−w, w]：z_gl ＝ w − 2·z_d3d（同 TS 版 d3dClipToGl）</summary>
        public static string D3dClipToGl(string src)
        {
            var renamed = System.Text.RegularExpressions.Regex.Replace(src, @"\bvoid\s+main\s*\(\s*\)", "void main_d3d()");
            if (renamed == src) return src;
            return renamed + "\nvoid main()\n{\n    main_d3d();\n    gl_Position.z = gl_Position.w - 2.0 * gl_Position.z;\n}\n";
        }
    }
}
