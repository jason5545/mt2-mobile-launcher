// CommandBuffer 的執行（GPU 端）與攝影機的影像效果流程。
//   - 攝影機事件：不透明物件畫完（AfterForwardOpaque、BeforeImageEffectsOpaque、AfterImageEffectsOpaque、BeforeSkybox）→ 天空盒（AfterSkybox）
//     → BeforeForwardAlpha → 半透明畫完（AfterForwardAlpha、BeforeImageEffects）→ OnRenderImage → AfterImageEffects、AfterEverything。
//     延遲著色才有的事件（BeforeGBuffer、BeforeLighting、BeforeReflections…）不執行：這裡只有前向繪製。
//   - 攝影機有影像效果（啟用中的 OnRenderImage 腳本，或 BeforeImageEffects／AfterImageEffects 掛了 buffer）時先畫進中繼 RT
//     （allowHDR 時 R16G16B16A16_SFloat，否則 sRGB 8 位元），BuiltinRenderTextureType.CameraTarget 指它；影像效果的結果寫到攝影機的最終目標
//     （targetTexture 或畫面）。Unity 是這樣做的（未逐項查證中繼 RT 的格式選法；HDR 的格式照 SystemInfo.GetGraphicsFormat(DefaultFormat.HDR)）。
//   - 繪製（DrawMesh、帶材質的 Blit）用指定的 pass，vertex 的 y 取負：轉出來的 shader 是 D3D 的 bytecode，全螢幕繪製的 uv 寫死了
//     UNITY_UV_STARTS_AT_TOP 的翻轉，GL 的列序相反，位置也翻過來才對得上（同 TS 版 postProcess.ts）。y 翻過來繞向也反了，前向面改 CCW。
//   - 沒給材質的 Blit：引擎自己的複製（不轉色彩空間；sRGB 的 RT 由 GL 解碼、編碼）。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityNative.Icalls;

namespace UnityNative.Gl
{
    public sealed unsafe partial class GlGpu
    {
        struct Tgt { public uint Fbo; public int W, H; public bool Ok; }

        /// <summary>這台攝影機的 CameraTarget（中繼 RT，沒有影像效果時是最終目標）與最終目標</summary>
        Tgt camTarget, camFinal;
        RenderTexture camIntermediate;
        NCamera execCam;
        readonly Dictionary<NCamera, RenderTexture> intermediates = new Dictionary<NCamera, RenderTexture>();
        readonly Dictionary<int, RenderTexture> temps = new Dictionary<int, RenderTexture>();
        readonly List<NMonoBehaviour> renderImage = new List<NMonoBehaviour>();
        NMesh blitQuad;
        static readonly int idMainTexST = PropertyIds.Of("_MainTex_ST");

        void InitCommandBuffers()
        {
            CommandBuffers.Execute = b => { if (execCam == null) { camTarget = camFinal = MainTgt(); } Exec(b); RestoreCamera(); };
            CommandBuffers.Blit = GraphicsBlit;
        }

        Tgt MainTgt() => new Tgt { Fbo = fbo, W = fbW, H = fbH, Ok = true };

        static Tgt RtTgt(NRenderTexture n)
        {
            if (n == null) return default;
            GpuResources.UploadRT(n);
            var t = (GpuResources.RtTarget)n.Fbo;
            return new Tgt { Fbo = t.Fbo, W = t.W, H = t.H, Ok = true };
        }

        void Bind(Tgt t)
        {
            GL.glBindFramebuffer(GL.FRAMEBUFFER, t.Fbo);
            GL.glViewport(0, 0, t.W, t.H);
        }

        /// <summary>CommandBuffer 跑完（或攝影機中途的事件）：回到攝影機原本的目標與 viewport</summary>
        void RestoreCamera()
        {
            GL.FrontFace(GL.CW);
            if (execCam == null) { GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo); GL.glViewport(0, 0, fbW, fbH); return; }
            GL.glBindFramebuffer(GL.FRAMEBUFFER, camTarget.Fbo);
            var pr = execCam.PixelRect;
            GL.glViewport((int)pr.x, (int)pr.y, (int)pr.width, (int)pr.height);
            SetCamera(execCam, camTarget.W, camTarget.H);
        }

        static bool HasBuffers(NCamera cam, CameraEvent e) => cam.CommandBuffers.TryGetValue(e, out var l) && l.Count > 0;

        void Events(NCamera cam, CameraEvent e)
        {
            if (!cam.CommandBuffers.TryGetValue(e, out var l) || l.Count == 0) return;
            bool any = false;
            foreach (var cb in l.ToArray())
            {
                var n = UnityEngine_Rendering_CommandBuffer.Of(cb);
                if (n == null || n.Cmds.Count == 0) continue;
                if (DumpNext) Dump.Add($"  CommandBuffer {n.Name}（{e}）{n.Cmds.Count} 個指令");
                Exec(n);
                any = true;
            }
            if (any) RestoreCamera();
        }

        // ───────── 一台攝影機（含影像效果） ─────────

        void RenderCameraFull(NCamera cam, FrameList frame, Tgt final)
        {
            var go = cam.Go;
            Lifecycle.SendToEnabledScripts(go, Messages.OnPreCull);
            if (!cam.Alive || !cam.IsActiveAndEnabled) return;
            Lifecycle.SendToEnabledScripts(go, Messages.OnPreRender);
            Lifecycle.RenderImageScripts(go, renderImage);
            bool fx = renderImage.Count > 0 || HasBuffers(cam, CameraEvent.BeforeImageEffects) || HasBuffers(cam, CameraEvent.AfterImageEffects);
            camFinal = final;
            execCam = cam;
            if (!fx)
            {
                camTarget = final;
                camIntermediate = null;
                Bind(final);
                RenderCameraInto(cam, frame, final.W, final.H);
                Events(cam, CameraEvent.AfterForwardAlpha);
                Events(cam, CameraEvent.AfterEverything);
                execCam = null;
                Lifecycle.SendToEnabledScripts(go, Messages.OnPostRender);
                return;
            }

            // 中繼 RT：大小同最終目標
            if (!intermediates.TryGetValue(cam, out var rt) || !rt)
            {
                var desc = new RenderTextureDescriptor(final.W, final.H, cam.AllowHDR ? UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat
                    : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB, 24);
                rt = UnityEngine_RenderTexture.GetTemporary_Internal_Injected(ref desc);
                rt.name = "CameraTarget " + cam.Go?.Name;
                intermediates[cam] = rt;
            }
            var inter = Native.Of<NRenderTexture>(rt);
            if (inter.Width != final.W || inter.Height != final.H) { inter.Width = final.W; inter.Height = final.H; inter.GpuDirty = true; }
            camIntermediate = rt;
            camTarget = RtTgt(inter);
            // 只清深度（或不清）的攝影機：接著前面畫好的內容畫
            if (cam.ClearFlags == CameraClearFlags.Depth || cam.ClearFlags == CameraClearFlags.Nothing)
                Copy(final.Fbo == fbo ? colorTex : 0, camTarget, Vector2.one, Vector2.zero);
            Bind(camTarget);
            RenderCameraInto(cam, frame, camTarget.W, camTarget.H);
            Events(cam, CameraEvent.AfterForwardAlpha);
            Events(cam, CameraEvent.BeforeImageEffects);
            Lifecycle.SendToEnabledScripts(go, Messages.OnPostRender);

            // OnRenderImage：多個效果串起來，中間用暫存 RT；最後一個寫到 targetTexture（null＝畫面）
            if (renderImage.Count > 0)
            {
                RenderTexture src = rt;
                var made = new List<RenderTexture>();
                for (int i = 0; i < renderImage.Count; i++)
                {
                    RenderTexture dst = cam.TargetTexture;
                    if (i < renderImage.Count - 1)
                    {
                        var d = new RenderTextureDescriptor(final.W, final.H, Native.Of<NRenderTexture>(rt).ColorFormat, 0);
                        dst = UnityEngine_RenderTexture.GetTemporary_Internal_Injected(ref d);
                        made.Add(dst);
                    }
                    if (DumpNext) Dump.Add($"  OnRenderImage {renderImage[i].Info.Type.Name}");
                    Lifecycle.CallRenderImage(renderImage[i], src, dst);
                    src = dst;
                }
                foreach (var m in made) UnityEngine_RenderTexture.ReleaseTemporary(m);
            }
            else Copy(GpuResources.Of(rt).Id, final, Vector2.one, Vector2.zero);

            camTarget = final;
            camIntermediate = null;
            Bind(final);
            Events(cam, CameraEvent.AfterImageEffects);
            Events(cam, CameraEvent.AfterEverything);
            execCam = null;
            GL.glBindFramebuffer(GL.FRAMEBUFFER, final.Fbo);
        }

        // ───────── 複製與 Graphics.Blit ─────────

        void Copy(uint srcTex, Tgt dst, Vector2 scale, Vector2 offset)
        {
            if (srcTex == 0 || !dst.Ok) return;
            Bind(dst);
            GL.Disable(GL.BLEND); GL.Disable(GL.DEPTH_TEST); GL.Disable(GL.STENCIL_TEST); GL.Disable(GL.CULL_FACE); GL.Disable(GL.SCISSOR_TEST);
            GL.ColorMask(1, 1, 1, 1);
            GL.UseProgram(Builtin.CopyProgram);
            GL.ActiveTexture(GL.TEXTURE0);
            GL.BindTexture(GL.TEXTURE_2D, srcTex);
            GL.glUniform1i(GL.Uniform(Builtin.CopyProgram, "tSource"), 0);
            float* so = stackalloc float[4] { scale.x, scale.y, offset.x, offset.y };
            GL.glUniform4fv(GL.Uniform(Builtin.CopyProgram, "uScaleOffset"), 1, so);
            GL.glBindVertexArray(quadVao);
            GL.glDrawArrays(GL.TRIANGLE_STRIP, 0, 4);
            GL.glBindVertexArray(0);
            Draw.DrawCalls++;
        }

        /// <summary>Graphics.Blit：dest 是 null 時寫到目前攝影機的最終目標（OnRenderImage 裡），不在攝影機裡就是畫面</summary>
        void GraphicsBlit(Texture source, RenderTexture dest, Material mat, int pass, Vector2 scale, Vector2 offset)
        {
            var dst = (object)dest != null ? RtTgt(Native.Of<NRenderTexture>(dest)) : execCam != null ? camFinal : MainTgt();
            BlitTo(source, dst, mat, pass, scale, offset);
            if (execCam != null) RestoreCamera();
            else GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
        }

        void BlitTo(Texture source, Tgt dst, Material mat, int pass, Vector2 scale, Vector2 offset)
        {
            if (!dst.Ok) return;
            var nm = Native.Of<NMaterial>(mat);
            if (nm == null)
            {
                Copy((object)source != null ? GpuResources.Of(source).Id : 0, dst, scale, offset);
                return;
            }
            Bind(dst);
            ov.Clear();
            ov.MainTex = source;
            ov.Sheet.Vectors[idMainTexST] = new Vector4(scale.x, scale.y, offset.x, offset.y);
            var g = GpuMesh.Of(BlitQuad());
            DrawPasses(nm, pass, Matrix4x4.identity, null, g, 0);
        }

        /// <summary>Blit 的四邊形：位置與 uv 都是 0–1（Unity 的 Blit 也是，shader 自己換成裁切座標）</summary>
        NMesh BlitQuad()
        {
            if (blitQuad != null) return blitQuad;
            var m = new NMesh { Name = "Blit quad" };
            m.Subs.Clear();
            m.Subs.Add(new NMesh.Sub { Indices = new[] { 0, 1, 2, 0, 2, 3 } });
            m.Channels[(int)VertexAttribute.Position] = new NMesh.Channel { Data = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, Dim = 3, Format = VertexAttributeFormat.Float32 };
            m.Channels[(int)VertexAttribute.TexCoord0] = new NMesh.Channel { Data = new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, Dim = 2, Format = VertexAttributeFormat.Float32 };
            m.VertexCount = 4;
            m.RecalculateBounds();
            return blitQuad = m;
        }

        /// <summary>材質的一個 pass（pass &lt; 0：全部的 pass）畫一個子網格，y 取負（見檔頭）</summary>
        void DrawPasses(NMaterial mat, int pass, Matrix4x4 model, PropertySheet block, GpuMesh g, int sub)
        {
            var name = mat.Shader?.Name;
            var e = ShaderLib.Entry(mat.Shader);
            kwTmp.Clear();
            kwTmp.AddRange(mat.Keywords);
            foreach (var k in UnityEngine_Shader.GlobalKeywords) kwTmp.Add(k);
            int count = pass >= 0 ? 1 : PassCount(e);
            GL.FrontFace(GL.CCW);
            for (int i = 0; i < count; i++)
            {
                var p = ShaderLib.Pass(e, kwTmp, pass >= 0 ? pass : i, true);
                if (p == null || p.Prog == 0)
                {
                    if (StandIns.Add((name ?? "(null)") + " pass " + (pass >= 0 ? pass : i))) Engine.Log(LogType.Warning, "繪製：CommandBuffer 用的 shader " + (name ?? "(沒有)") + " pass " + (pass >= 0 ? pass : i) + " 沒有可用的程式");
                    continue;
                }
                Draw.Use(p, model, mat, block, ov, false);
                stream.DrawMesh(p, g, sub);
                Draw.DrawCalls++;
            }
            GL.FrontFace(GL.CW);
        }

        static int PassCount(ShaderEntry e) => (e?.Index?["subShaders"] as System.Text.Json.Nodes.JsonArray)?[0]?["passes"] is System.Text.Json.Nodes.JsonArray a ? a.Count : 0;

        // ───────── 指令 ─────────

        Tgt TargetOf(RenderTargetIdentifier id, Tgt cur)
        {
            switch (id.m_Type)
            {
                case BuiltinRenderTextureType.CameraTarget: return camTarget.Ok ? camTarget : MainTgt();
                case BuiltinRenderTextureType.CurrentActive: return cur;
                case BuiltinRenderTextureType.BufferPtr:
                    if (id.m_BufferPointer == UnityEngine_Display.ColorBufferPtr || id.m_BufferPointer == UnityEngine_Display.DepthBufferPtr) return camFinal.Ok ? camFinal : MainTgt();
                    break;
            }
            var t = TexOf(id);
            return t is RenderTexture r ? RtTgt(Native.Of<NRenderTexture>(r)) : default;
        }

        Texture TexOf(RenderTargetIdentifier id)
        {
            switch (id.m_Type)
            {
                case BuiltinRenderTextureType.None: return null;
                case BuiltinRenderTextureType.CameraTarget: return camIntermediate;
                case BuiltinRenderTextureType.PropertyName:
                    if (temps.TryGetValue(id.m_NameID, out var tr)) return tr;
                    return UnityEngine_Shader.GetGlobalTextureImpl(id.m_NameID);
            }
            return id.m_InstanceID != 0 ? Native.ById(id.m_InstanceID)?.Managed as Texture : null;
        }

        void Exec(NCommandBuffer b)
        {
            var cur = camTarget.Ok ? camTarget : MainTgt();
            Bind(cur);
            var cam = Draw.Cam;
            Matrix4x4 sv = cam.V, sp = cam.P, svp = cam.VP;
            foreach (var c in b.Cmds)
            {
                try
                {
                    switch (c.Kind)
                    {
                        case CmdKind.GetTemp:
                            {
                                var d = c.Desc;
                                // 負的寬高：攝影機畫面大小除以 −n（Unity 的 GetTemporaryRT 文件）
                                if (d.width < 0) d.width = Math.Max(1, (execCam != null ? (int)execCam.PixelRect.width : fbW) / -d.width);
                                if (d.height < 0) d.height = Math.Max(1, (execCam != null ? (int)execCam.PixelRect.height : fbH) / -d.height);
                                if (temps.TryGetValue(c.Id, out var old)) UnityEngine_RenderTexture.ReleaseTemporary(old);
                                var rt = UnityEngine_RenderTexture.GetTemporary_Internal_Injected(ref d);
                                var n = Native.Of<NRenderTexture>(rt);
                                if (n.Filter != c.Filter) { n.Filter = c.Filter; if (n.Gpu is GpuTexture gt && gt.Id != 0) { GL.BindTexture(GL.TEXTURE_2D, gt.Id); GpuResources.Params(GL.TEXTURE_2D, n.Filter, n.WrapU, n.WrapV, false); } }
                                temps[c.Id] = rt;
                                UnityEngine_Shader.SetGlobalTextureImpl(c.Id, rt);
                                break;
                            }
                        case CmdKind.ReleaseTemp:
                            if (temps.Remove(c.Id, out var rel)) UnityEngine_RenderTexture.ReleaseTemporary(rel);
                            break;
                        case CmdKind.GlobalFloat: UnityEngine_Shader.SetGlobalFloatImpl(c.Id, c.F); break;
                        case CmdKind.GlobalVector: { var v = c.V; UnityEngine_Shader.SetGlobalVectorImpl_Injected(c.Id, ref v); break; }
                        case CmdKind.GlobalMatrix: { var m = c.M; UnityEngine_Shader.SetGlobalMatrixImpl_Injected(c.Id, ref m); break; }
                        case CmdKind.GlobalTexture: UnityEngine_Shader.SetGlobalTextureImpl(c.Id, TexOf(c.A)); break;
                        case CmdKind.Keyword:
                            if (c.On) UnityEngine_Shader.GlobalKeywords.Add(c.Keyword); else UnityEngine_Shader.GlobalKeywords.Remove(c.Keyword);
                            break;
                        case CmdKind.ViewProjection:
                            cam.V = c.M; cam.P = c.M2; cam.VP = MathUtil.Mul(c.M2, c.M); Draw.CamGen++;
                            break;
                        case CmdKind.SetTarget:
                            {
                                var t = TargetOf(c.A, cur);
                                if (t.Ok) { cur = t; Bind(cur); }
                                break;
                            }
                        case CmdKind.ClearTarget:
                            {
                                uint bits = 0;
                                GL.Disable(GL.SCISSOR_TEST);
                                if ((c.ClearFlags & RTClearFlags.Color) != 0)
                                {
                                    var col = c.Color.linear;
                                    GL.ColorMask(1, 1, 1, 1);
                                    GL.glClearColor(col.r, col.g, col.b, col.a);
                                    bits |= GL.COLOR_BUFFER_BIT;
                                }
                                if ((c.ClearFlags & RTClearFlags.Depth) != 0) { GL.DepthMask(1); GL.glClearDepthf(c.F); bits |= GL.DEPTH_BUFFER_BIT; }
                                if ((c.ClearFlags & RTClearFlags.Stencil) != 0) { GL.StencilMask(0xff); GL.glClearStencil((int)c.Stencil); bits |= GL.STENCIL_BUFFER_BIT; }
                                if (bits != 0) GL.glClear(bits);
                                break;
                            }
                        case CmdKind.Viewport:
                            GL.glViewport((int)c.Rect.x, (int)c.Rect.y, (int)c.Rect.width, (int)c.Rect.height);
                            break;
                        case CmdKind.DrawMesh:
                            {
                                var nm = Native.Of<NMaterial>(c.Mat);
                                var mesh = Native.Of<NMesh>(c.Mesh);
                                if (nm == null || mesh == null || mesh.VertexCount == 0) break;
                                ov.Clear();
                                DrawPasses(nm, c.Pass, c.M, c.Props, GpuMesh.Of(mesh), c.Sub);
                                break;
                            }
                        case CmdKind.Blit:
                            {
                                var src = (object)c.Tex != null ? c.Tex : TexOf(c.A);
                                var dst = TargetOf(c.B, cur);
                                if (!dst.Ok) break;
                                BlitTo(src, dst, c.Mat, c.Pass, c.Scale, c.Offset);
                                cur = dst;
                                break;
                            }
                    }
                }
                catch (Exception e) { Engine.LogException(e); }
            }
            cam.V = sv; cam.P = sp; cam.VP = svp; Draw.CamGen++;
            GL.FrontFace(GL.CW);
        }
    }
}
