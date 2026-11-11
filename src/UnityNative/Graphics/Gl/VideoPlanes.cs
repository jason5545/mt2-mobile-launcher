// VideoPlayer 的畫面（Media/Video.cs）：
//   - 每格開頭叫解碼器 Upload（Android 在這裡把 SurfaceTexture 的 OES 貼圖轉成一般貼圖）；RenderTexture 模式接著畫進 targetTexture（整張）。
//   - CameraFarPlane：targetCamera 清完背景之後、畫場景之前；CameraNearPlane：場景畫完之後。範圍是攝影機的 viewport。
//   - aspectRatio 照 Unity 的定義：NoScaling 原尺寸、FitVertically 高對齊、FitHorizontally 寬對齊、FitInside 整張放得進去、
//     FitOutside 填滿（裁掉超出的）、Stretch 拉滿；空出來的地方是攝影機原本的內容（遠平面就是背景色）。
//   - 影片貼圖存的是 gamma 值：shader 轉成線性再寫（render target 是 sRGB，同其他貼圖），乘 targetCameraAlpha，premultiplied 混色。
using UnityEngine;
using UnityEngine.Video;

namespace UnityNative.Gl
{
    public sealed unsafe partial class GlGpu
    {
        const string VideoVert = "#version 300 es\nlayout(location = 0) in vec2 POSITION;\nuniform vec2 uScale;\nout vec2 vUv;\n" +
            "void main(){ vUv = vec2(POSITION.x * 0.5 + 0.5, 0.5 - POSITION.y * 0.5); gl_Position = vec4(POSITION * uScale, 0.0, 1.0); }";
        const string VideoFrag = "#version 300 es\nprecision highp float;\nuniform highp sampler2D tSource;\nuniform float uAlpha;\nin vec2 vUv;\nlayout(location = 0) out vec4 fragColor;\n" +
            "vec3 toLinear(vec3 c){ return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }\n" +
            "void main(){ vec3 c = toLinear(texture(tSource, vUv).rgb); fragColor = vec4(c * uAlpha, uAlpha); }";
        uint videoProg;

        /// <summary>每格開頭：解碼器把這格的畫面放進貼圖；RenderTexture 模式畫進 targetTexture</summary>
        void UploadVideos()
        {
            foreach (var v in NVideoPlayer.All)
            {
                if (!v.Alive || v.Decoder == null || !(v.Playing || v.Paused)) continue;
                try { v.Decoder.Upload(); }
                catch (System.Exception e) { Engine.LogException(e, v); continue; }
                if (v.RenderMode != VideoRenderMode.RenderTexture || !v.Showing) continue;
                var rt = Native.Of<NRenderTexture>(v.TargetTexture);
                if (rt == null) continue;
                var t = RtTgt(rt);
                if (!t.Ok) continue;
                Bind(t);
                GL.Disable(GL.SCISSOR_TEST);
                GL.glClearColor(0, 0, 0, 0);
                GL.glClear(GL.COLOR_BUFFER_BIT);
                DrawVideo(v, t.W, t.H, VideoAspectRatio.Stretch, 1);
            }
        }

        /// <summary>targetCamera 是 cam 的遠平面（near = false）或近平面的影片</summary>
        void DrawVideoPlanes(NCamera cam, bool near)
        {
            if (NVideoPlayer.All.Count == 0) return;
            var mode = near ? VideoRenderMode.CameraNearPlane : VideoRenderMode.CameraFarPlane;
            foreach (var v in NVideoPlayer.All)
            {
                if (v.RenderMode != mode || !v.Showing || Native.Of<NCamera>(v.TargetCamera) != cam) continue;
                var pr = cam.PixelRect;
                if (DumpNext) Dump.Add($"  影片 {v.Url} {(near ? "近" : "遠")}平面 time={v.Time:0.00} alpha={v.TargetCameraAlpha} aspect={v.AspectRatio}");
                DrawVideo(v, pr.width, pr.height, v.AspectRatio, v.TargetCameraAlpha);
            }
        }

        void DrawVideo(NVideoPlayer v, float w, float h, VideoAspectRatio aspect, float alpha)
        {
            var d = v.Decoder;
            if (videoProg == 0)
            {
                var vs = GL.Compile(GL.VERTEX_SHADER, ShaderSource.Adapt(VideoVert), out var vl);
                var fs = GL.Compile(GL.FRAGMENT_SHADER, ShaderSource.Adapt(VideoFrag), out var fl);
                videoProg = GL.Link(vs, fs, out var ll);
                if (videoProg == 0) { Engine.Log(LogType.Error, "影片 shader 失敗：" + vl + fl + ll); return; }
            }
            float vw = d.Width, vh = d.Height, sx = 1, sy = 1;
            if (vw > 0 && vh > 0 && w > 0 && h > 0)
            {
                float fitW = w / vw, fitH = h / vh, s;
                switch (aspect)
                {
                    case VideoAspectRatio.NoScaling: s = 1; break;
                    case VideoAspectRatio.FitVertically: s = fitH; break;
                    case VideoAspectRatio.FitHorizontally: s = fitW; break;
                    case VideoAspectRatio.FitInside: s = Mathf.Min(fitW, fitH); break;
                    case VideoAspectRatio.FitOutside: s = Mathf.Max(fitW, fitH); break;
                    default: s = 0; break; // Stretch
                }
                if (s > 0) { sx = vw * s / w; sy = vh * s / h; }
            }
            GL.Disable(GL.DEPTH_TEST); GL.Disable(GL.STENCIL_TEST); GL.Disable(GL.CULL_FACE);
            GL.DepthMask(0);
            GL.ColorMask(1, 1, 1, 1);
            GL.Enable(GL.BLEND);
            GL.BlendEquationSeparate(GL.FUNC_ADD, GL.FUNC_ADD);
            GL.BlendFuncSeparate(GL.ONE, GL.ONE_MINUS_SRC_ALPHA, GL.ONE, GL.ONE_MINUS_SRC_ALPHA);
            GL.UseProgram(videoProg);
            GL.ActiveTexture(GL.TEXTURE0);
            GL.BindTexture(GL.TEXTURE_2D, d.Texture);
            GL.glUniform1i(GL.Uniform(videoProg, "tSource"), 0);
            float* u = stackalloc float[4] { sx, sy, alpha, 0 };
            GL.glUniform2fv(GL.Uniform(videoProg, "uScale"), 1, u);
            GL.glUniform1fv(GL.Uniform(videoProg, "uAlpha"), 1, u + 2);
            GL.glBindVertexArray(quadVao);
            GL.glDrawArrays(GL.TRIANGLE_STRIP, 0, 4);
            GL.glBindVertexArray(0);
            GL.DepthMask(1);
            Draw.DrawCalls++;
        }
    }
}
