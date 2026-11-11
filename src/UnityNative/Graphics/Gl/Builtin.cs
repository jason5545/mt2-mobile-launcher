// Unity 內建 shader 的手寫版（它們在 build 的 unity_builtin_extra 裡，匯出資料沒有），算法照 TS 版 render/builtinShaders.ts：
//   UI/Default      color = vertexColor × _Color；frag = (tex + _TextureSampleAdd) × color，RectMask2D 的 _ClipRect，rgb *= a（Blend One OneMinusSrcAlpha）
//   Sprites/Default color = vertexColor × _Color × _RendererColor；frag = tex × color，rgb *= a
// 跟轉出來的 shader 用同一套 cbuffer 填法：一個 std140 的 block，欄位位移在這裡寫死。
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace UnityNative.Gl
{
    public static unsafe class Builtin
    {
        const string Head = "#version 300 es\nprecision highp float;\nprecision highp int;\n";
        const string Block = @"
layout(std140) uniform BuiltinGlobals {
    mat4 unity_ObjectToWorld;   // 0
    mat4 unity_MatrixVP;        // 64
    vec4 _Color;                // 128
    vec4 _ClipRect;             // 144
    vec4 _TextureSampleAdd;     // 160
    vec4 _UIMaskSoftness;       // 176
    vec4 _ScreenParams;         // 192
    vec4 _UseUIAlphaClip;       // 208
    vec4 _RendererColor;        // 224
    vec4 _Cutoff;               // 240（Sprites/Mask 的 alpha 門檻，x）
};
";
        static readonly (string name, int off, int dim, bool mat)[] Fields =
        {
            ("unity_ObjectToWorld", 0, 4, true), ("unity_MatrixVP", 64, 4, true), ("_Color", 128, 4, false), ("_ClipRect", 144, 4, false),
            ("_TextureSampleAdd", 160, 4, false), ("_UIMaskSoftness", 176, 2, false), ("_ScreenParams", 192, 4, false), ("_UseUIAlphaClip", 208, 1, false),
            ("_RendererColor", 224, 4, false), ("_Cutoff", 240, 1, false),
        };

        const string UiVert = Head + Block + @"
layout(location = 0) in vec4 POSITION;
layout(location = 1) in vec4 COLOR;
layout(location = 2) in vec4 TEXCOORD0;
out vec2 vUv;
out vec4 vColor;
out vec4 vMask;
void main() {
    vec4 wp = unity_ObjectToWorld * vec4(POSITION.xyz, 1.0);
    vec4 clipPos = unity_MatrixVP * wp;
    vUv = TEXCOORD0.xy;
    vec2 pixelSize = clipPos.w / abs(mat2(unity_MatrixVP) * _ScreenParams.xy);
    vec4 clampedRect = clamp(_ClipRect, vec4(-2e10), vec4(2e10));
    vMask = vec4(POSITION.xy * 2.0 - clampedRect.xy - clampedRect.zw, 0.25 / (0.25 * _UIMaskSoftness.xy + abs(pixelSize)));
    vColor = COLOR * _Color;
    gl_Position = clipPos;
}";

        const string UiFrag = Head + Block + @"
uniform highp sampler2D _MainTex;
in vec2 vUv;
in vec4 vColor;
in vec4 vMask;
layout(location = 0) out vec4 fragColor;
void main() {
    vec4 col = vColor;
    col.a = roundEven(col.a * 255.0) * (1.0 / 255.0);
    vec4 c = (texture(_MainTex, vUv) + _TextureSampleAdd) * col;
    vec2 m = clamp((_ClipRect.zw - _ClipRect.xy - abs(vMask.xy)) * vMask.zw, 0.0, 1.0);
    c.a *= m.x * m.y;
    if (_UseUIAlphaClip.x > 0.5 && c.a < 0.001) discard;
    c.rgb *= c.a;
    fragColor = c;
}";

        /// <summary>UI/Default Font（舊 Text）：顏色是頂點色，alpha 乘字型貼圖的 alpha</summary>
        const string FontFrag = Head + Block + @"
uniform highp sampler2D _MainTex;
in vec2 vUv;
in vec4 vColor;
in vec4 vMask;
layout(location = 0) out vec4 fragColor;
void main() {
    vec4 c = vColor;
    c.a *= texture(_MainTex, vUv).a + _TextureSampleAdd.a;
    vec2 m = clamp((_ClipRect.zw - _ClipRect.xy - abs(vMask.xy)) * vMask.zw, 0.0, 1.0);
    c.a *= m.x * m.y;
    c.rgb *= c.a;
    fragColor = c;
}";

        const string SpriteVert = Head + Block + @"
layout(location = 0) in vec4 POSITION;
layout(location = 1) in vec4 COLOR;
layout(location = 2) in vec4 TEXCOORD0;
out vec2 vUv;
out vec4 vColor;
void main() {
    vUv = TEXCOORD0.xy;
    vColor = COLOR * _Color * _RendererColor;
    gl_Position = unity_MatrixVP * (unity_ObjectToWorld * vec4(POSITION.xyz, 1.0));
}";

        const string SpriteFrag = Head + Block + @"
uniform highp sampler2D _MainTex;
in vec2 vUv;
in vec4 vColor;
layout(location = 0) out vec4 fragColor;
void main() {
    vec4 c = texture(_MainTex, vUv) * vColor;
    c.rgb *= c.a;
    fragColor = c;
}";

        /// <summary>Sprites/Mask（DefaultResourcesExtra/Sprites-Mask.shader）：ColorMask 0，clip(tex.a − _Cutoff)；只用來寫 stencil（GlGpu.DrawSpriteMask）</summary>
        const string MaskFrag = Head + Block + @"
uniform highp sampler2D _MainTex;
in vec2 vUv;
in vec4 vColor;
layout(location = 0) out vec4 fragColor;
void main() {
    vec4 c = texture(_MainTex, vUv);
    if (c.a - _Cutoff.x < 0.0) discard;
    fragColor = c;
}";

        /// <summary>找不到 shader 時的代用（照 Sprites/Default 畫，記在報告裡）</summary>
        const string ColoredFrag = Head + Block + @"
in vec2 vUv;
in vec4 vColor;
layout(location = 0) out vec4 fragColor;
void main() { vec4 c = vColor; c.rgb *= c.a; fragColor = c; }";

        const string BlitVert = "#version 300 es\nlayout(location = 0) in vec2 POSITION;\nout vec2 vUv;\nvoid main(){ vUv = POSITION * 0.5 + 0.5; gl_Position = vec4(POSITION, 0.0, 1.0); }";
        const string BlitFrag = "#version 300 es\nprecision highp float;\nuniform highp sampler2D tSource;\nin vec2 vUv;\nlayout(location = 0) out vec4 fragColor;\n" +
            "vec3 toSRGB(vec3 c){ return mix(c * 12.92, 1.055 * pow(c, vec3(1.0/2.4)) - 0.055, step(vec3(0.0031308), c)); }\n" +
            "void main(){ vec4 c = texture(tSource, vUv); fragColor = vec4(toSRGB(c.rgb), 1.0); }";

        /// <summary>RT 之間的複製（Graphics.Blit、CommandBuffer.Blit 沒給材質時）：不轉色彩空間，sRGB 的來源與目標由 GL 自己解碼、編碼</summary>
        const string CopyFrag = "#version 300 es\nprecision highp float;\nuniform highp sampler2D tSource;\nuniform vec4 uScaleOffset;\nin vec2 vUv;\nlayout(location = 0) out vec4 fragColor;\n" +
            "void main(){ fragColor = texture(tSource, vUv * uScaleOffset.xy + uScaleOffset.zw); }";

        static readonly Dictionary<string, GpuProgram> cache = new Dictionary<string, GpuProgram>();
        public static uint BlitProgram, CopyProgram;

        static JsonObject PremulState() => (JsonObject)JsonNode.Parse("{\"blend\":{\"src\":1,\"dst\":10,\"srcAlpha\":1,\"dstAlpha\":10,\"op\":0,\"opAlpha\":0},\"culling\":0,\"zWrite\":0,\"zTest\":{\"prop\":\"unity_GUIZTestMode\",\"val\":8},\"colorMask\":{\"prop\":\"_ColorMask\",\"val\":15},\"stencilRef\":{\"prop\":\"_Stencil\",\"val\":0},\"stencilReadMask\":{\"prop\":\"_StencilReadMask\",\"val\":255},\"stencilWriteMask\":{\"prop\":\"_StencilWriteMask\",\"val\":255}}");

        public static GpuProgram Get(string shaderName)
        {
            if (cache.TryGetValue(shaderName ?? "", out var p)) return p;
            string vs, fs;
            switch (shaderName)
            {
                case "UI/Default": case "UI/DefaultETC1": vs = UiVert; fs = UiFrag; break;
                case "UI/Default Font": case "GUI/Text Shader": vs = UiVert; fs = FontFrag; break;
                case "Sprites/Default": vs = SpriteVert; fs = SpriteFrag; break;
                case "Sprites/Mask": vs = SpriteVert; fs = MaskFrag; break;
                case "Hidden/Internal-Colored": vs = SpriteVert; fs = ColoredFrag; break;
                default: return null;
            }
            p = Compile(shaderName, vs, fs);
            cache[shaderName] = p;
            return p;
        }

        /// <summary>沒有可用的 shader：照 Sprites/Default 畫（顏色、貼圖照常），名字記下來</summary>
        public static GpuProgram StandIn() => Get("Sprites/Default");

        static GpuProgram Compile(string name, string vsrc, string fsrc)
        {
            var v = GL.Compile(GL.VERTEX_SHADER, ShaderSource.Adapt(ShaderSource.D3dClipToGl(vsrc)), out var vl); // 拿到的投影是 D3D 的（Draw.GpuProjection）
            var f = GL.Compile(GL.FRAGMENT_SHADER, ShaderSource.Adapt(fsrc), out var fl);
            if (v == 0 || f == 0) { Engine.Log(UnityEngine.LogType.Error, "內建 shader " + name + " 編譯失敗：" + vl + fl); return null; }
            var prog = GL.Link(v, f, out var ll);
            if (prog == 0) { Engine.Log(UnityEngine.LogType.Error, "內建 shader " + name + " link 失敗：" + ll); return null; }
            var gp = new GpuProgram { Prog = prog, Name = name, Pass = new PassState { State = PremulState() } };
            var b = new GpuBlock { Name = "BuiltinGlobals", Binding = 0, Size = 256, Data = new byte[256] };
            foreach (var (n, off, dim, mat) in Fields) b.Fields.Add(new CbField { Name = n, Offset = off, Dim = dim, Rows = mat ? 4 : 0, Matrix = mat });
            var idx = GL.BlockIndex(prog, "BuiltinGlobals");
            if (idx != GL.INVALID_INDEX) { GL.glUniformBlockBinding(prog, idx, 0); gp.Blocks.Add(b); }
            GL.UseProgram(prog);
            int loc = GL.Uniform(prog, "_MainTex");
            if (loc >= 0) { GL.glUniform1i(loc, 0); gp.Textures.Add(new GpuTex { Location = loc, Name = "_MainTex", Unit = 0 }); }
            gp.Inputs.Add(new GpuInput { Location = 0, Semantic = "POSITION" });
            gp.Inputs.Add(new GpuInput { Location = 1, Semantic = "COLOR" });
            gp.Inputs.Add(new GpuInput { Location = 2, Semantic = "TEXCOORD", Index = 0 });
            return gp;
        }

        public static void InitBlit()
        {
            var v = GL.Compile(GL.VERTEX_SHADER, ShaderSource.Adapt(BlitVert), out var vl);
            var f = GL.Compile(GL.FRAGMENT_SHADER, ShaderSource.Adapt(BlitFrag), out var fl);
            BlitProgram = GL.Link(v, f, out var ll);
            if (BlitProgram == 0) Engine.Log(UnityEngine.LogType.Error, "blit shader 失敗：" + vl + fl + ll);
            var v2 = GL.Compile(GL.VERTEX_SHADER, ShaderSource.Adapt(BlitVert), out var vl2);
            var f2 = GL.Compile(GL.FRAGMENT_SHADER, ShaderSource.Adapt(CopyFrag), out var fl2);
            CopyProgram = GL.Link(v2, f2, out var ll2);
            if (CopyProgram == 0) Engine.Log(UnityEngine.LogType.Error, "copy shader 失敗：" + vl2 + fl2 + ll2);
        }
    }
}
