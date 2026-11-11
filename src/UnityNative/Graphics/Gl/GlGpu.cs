// GL 的 GPU 端：一格的畫法（內建管線、2D 為主）。
//   色彩：linear 流程。貼圖 SRGB8_ALPHA8、畫進 SRGB8_ALPHA8 的 render target（混色在線性空間），最後 blit 到畫面時轉回 sRGB（同 TS 版）。
//   攝影機照 depth 由小到大畫；每台攝影機收它 cullingMask 裡的 Renderer 和指給它的 Canvas（Screen Space - Camera、World Space），
//   排序：sorting layer → order in layer → render queue → 距攝影機遠的先畫 → 場景樹順序（同一個 Canvas 裡就是 uGUI 的順序）。
//   Overlay Canvas 畫在所有攝影機之後，螢幕像素座標，照 sortingOrder。
//   uGUI 的頂點：轉到根 Canvas 的本地座標（RectMask2D 的 _ClipRect 在這個座標系），顏色是 gamma 值相乘後整體轉線性（同 TS 版）。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative.Gl
{
    public sealed unsafe partial class GlGpu : IGpu
    {
        uint fbo, colorTex, depthRb, quadVbo, quadVao;
        int fbW, fbH;
        public int DefaultFramebuffer;
        readonly Stream stream = new Stream();
        /// <summary>這台攝影機要畫的 SpriteRenderer 頂點（PrepareSprites 一次收齊、一次上傳）</summary>
        readonly Stream spriteStream = new Stream();
        readonly Overrides ov = new Overrides();
        public readonly HashSet<string> StandIns = new HashSet<string>();
        public int LastDrawCalls, LastBatches;
        public Action AfterFrame;
        /// <summary>下一格把繪製清單印出來（除錯）</summary>
        public bool DumpNext;
        public readonly List<string> Dump = new List<string>();

        public GlGpu()
        {
            GpuResources.Init();
            Draw.Init();
            Builtin.InitBlit();
            stream.Init();
            spriteStream.Init();
            quadVbo = GL.Gen(GL.glGenBuffers);
            quadVao = GL.Gen(GL.glGenVertexArrays);
            GL.glBindVertexArray(quadVao);
            GL.glBindBuffer(GL.ARRAY_BUFFER, quadVbo);
            float[] q = { -1, -1, 1, -1, -1, 1, 1, 1 };
            fixed (float* p = q) GL.glBufferData(GL.ARRAY_BUFFER, q.Length * 4, p, GL.STATIC_DRAW);
            GL.glEnableVertexAttribArray(0);
            GL.glVertexAttribPointer(0, 2, GL.FLOAT, 0, 8, null);
            GL.glBindVertexArray(0);
            GL.FrontFace(GL.CW);
            Shaders.DefaultTextureResolver = null;
            InitCommandBuffers();
        }

        void EnsureTarget(int w, int h)
        {
            if (fbo != 0 && w == fbW && h == fbH) return;
            if (fbo != 0)
            {
                uint f = fbo, t = colorTex, r = depthRb;
                GL.glDeleteFramebuffers(1, &f); GL.DeleteTextures(1, &t); GL.glDeleteRenderbuffers(1, &r);
            }
            fbW = w; fbH = h;
            colorTex = GL.Gen(GL.glGenTextures);
            GL.BindTexture(GL.TEXTURE_2D, colorTex);
            GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.SRGB8_ALPHA8, w, h, 0, GL.RGBA, GL.UNSIGNED_BYTE, null);
            GpuResources.Params(GL.TEXTURE_2D, FilterMode.Bilinear, TextureWrapMode.Clamp, TextureWrapMode.Clamp, false);
            depthRb = GL.Gen(GL.glGenRenderbuffers);
            GL.glBindRenderbuffer(GL.RENDERBUFFER, depthRb);
            GL.glRenderbufferStorage(GL.RENDERBUFFER, GL.DEPTH24_STENCIL8, w, h);
            fbo = GL.Gen(GL.glGenFramebuffers);
            GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
            GL.glFramebufferTexture2D(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.TEXTURE_2D, colorTex, 0);
            GL.glFramebufferRenderbuffer(GL.FRAMEBUFFER, GL.DEPTH_STENCIL_ATTACHMENT, GL.RENDERBUFFER, depthRb);
            var st = GL.glCheckFramebufferStatus(GL.FRAMEBUFFER);
            if (st != GL.FRAMEBUFFER_COMPLETE) Engine.Log(LogType.Error, "GL：主 render target 不完整 0x" + st.ToString("x"));
        }

        // ───────── 一格 ─────────

        public void Frame(FrameList frame)
        {
            stream.NewFrame();
            spriteStream.NewFrame();
            int w = Engine.Config.ScreenWidth, h = Engine.Config.ScreenHeight;
            EnsureTarget(w, h);
            Draw.DrawCalls = 0;
            UploadVideos();
            LastBatches = 0;
            grabbed.Clear();
            GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
            GL.glViewport(0, 0, w, h);
            GL.Disable(GL.SCISSOR_TEST);
            GL.ColorMask(1, 1, 1, 1);
            GL.DepthMask(1);
            GL.StencilMask(0xff);
            GL.glClearColor(0, 0, 0, 1);
            GL.glClearDepthf(1);
            GL.glClearStencil(0);
            GL.glClear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT);
            if (DumpNext) Dump.Add("清完，中心像素 " + Probe(w, h));

            long p0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            // 畫進 RenderTexture 的攝影機先畫
            foreach (var cam in frame.Cameras)
            {
                var rt = Native.Of<NRenderTexture>(cam.TargetTexture);
                if (rt == null) continue;
                RenderCameraFull(cam, frame, RtTgt(rt));
            }
            long p1 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
            foreach (var cam in frame.Cameras)
            {
                if ((object)cam.TargetTexture != null) continue;
                RenderCameraFull(cam, frame, MainTgt());
            }
            if (DumpNext) Dump.Add("攝影機畫完，中心像素 " + Probe(w, h));
            long p2 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DrawOverlay(frame, w, h);
            long p3 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (DumpNext) Dump.Add("Overlay 畫完，中心像素 " + Probe(w, h));

            Present();
            LastDrawCalls = Draw.DrawCalls;
            if (Engine.Profile)
            {
                Engine.Add("      攝影機→RT", System.Diagnostics.Stopwatch.GetElapsedTime(p0, p1).TotalMilliseconds);
                Engine.Add("      攝影機→畫面", System.Diagnostics.Stopwatch.GetElapsedTime(p1, p2).TotalMilliseconds);
                Engine.Add("      Overlay Canvas", System.Diagnostics.Stopwatch.GetElapsedTime(p2, p3).TotalMilliseconds);
                Engine.Add("      輸出 blit", System.Diagnostics.Stopwatch.GetElapsedTime(p3).TotalMilliseconds);
            }
            if (DumpNext) { DumpNext = false; foreach (var l in Dump) Engine.Config.LogSink(l); Dump.Clear(); }
            AfterFrame?.Invoke();
        }


        /// <summary>把畫好的畫面（線性）貼到視窗（sRGB）。宿主照 targetFrameRate 跳過的格只呼叫這個，重貼上一格</summary>
        public void Present()
        {
            if (fbo == 0) return;
            int w = fbW, h = fbH;
            // 輸出到畫面：線性 → sRGB；畫面比視窗小（ScreenFit 的黑邊）時先清成黑、畫在中間
            GL.glBindFramebuffer(GL.FRAMEBUFFER, (uint)DefaultFramebuffer);
            int ww = Engine.Config.WindowWidth > 0 ? Engine.Config.WindowWidth : w, wh = Engine.Config.WindowHeight > 0 ? Engine.Config.WindowHeight : h;
            if (ww != w || wh != h)
            {
                GL.glViewport(0, 0, ww, wh);
                GL.Disable(GL.SCISSOR_TEST);
                GL.ColorMask(1, 1, 1, 1);
                GL.glClearColor(0, 0, 0, 1);
                GL.glClear(GL.COLOR_BUFFER_BIT);
            }
            GL.glViewport(ScreenFit.OffsetX, ScreenFit.OffsetY, w, h);
            GL.Disable(GL.BLEND); GL.Disable(GL.DEPTH_TEST); GL.Disable(GL.STENCIL_TEST); GL.Disable(GL.CULL_FACE);
            GL.ColorMask(1, 1, 1, 1);
            GL.UseProgram(Builtin.BlitProgram);
            GL.ActiveTexture(GL.TEXTURE0);
            GL.BindTexture(GL.TEXTURE_2D, colorTex);
            GL.glUniform1i(GL.Uniform(Builtin.BlitProgram, "tSource"), 0);
            GL.glBindVertexArray(quadVao);
            GL.glDrawArrays(GL.TRIANGLE_STRIP, 0, 4);
            GL.glBindVertexArray(0);
        }

        public void RenderCamera(NCamera cam, FrameList frame) { }

        string Probe(int w, int h)
        {
            byte* px = stackalloc byte[4];
            GL.glBindFramebuffer(GL.READ_FRAMEBUFFER, fbo);
            GL.glReadPixels(w / 2, h / 2, 1, 1, GL.RGBA, GL.UNSIGNED_BYTE, px);
            return $"{px[0]},{px[1]},{px[2]},{px[3]}";
        }

        /// <summary>讀回畫面（截圖用）：RGBA，左下原點。讀內部的 sRGB render target（存的就是畫面上的 sRGB 值），不讀視窗的（Retina 的大小對不上）</summary>
        public byte[] ReadPixels(out int w, out int h)
        {
            w = fbW; h = fbH;
            var px = new byte[w * h * 4];
            GL.glBindFramebuffer(GL.READ_FRAMEBUFFER, fbo);
            GL.glPixelStorei(GL.PACK_ALIGNMENT, 1);
            fixed (byte* p = px) GL.glReadPixels(0, 0, w, h, GL.RGBA, GL.UNSIGNED_BYTE, p);
            return px;
        }

        // ───────── 攝影機 ─────────

        sealed class Item
        {
            public int Layer, Order, Queue, Seq;
            /// <summary>同 layer、同 order 裡排在所有 renderer 之後：SpriteMask 的寫入 1、清除 2（Graphics/SpriteMask.cs）</summary>
            public int Late, MaskPass;
            public float Dist;
            public NRenderer Renderer;
            public CanvasBatch Canvas;
            public NTransform Tr;
            /// <summary>SpriteRenderer 在 spriteStream 裡的索引區段（PrepareSprites 填）</summary>
            public int IdxStart, IdxCount;
            int[] tree;

            /// <summary>場景樹順序（場景載入先後、根的順序、每層的 sibling index），同距離時用（同 TS 版繪製排序最後一個鍵 node.order）。要比才算</summary>
            public int[] Tree
            {
                get
                {
                    if (tree != null) return tree;
                    var path = new List<int>();
                    for (var t = Tr; t != null; t = t.Parent) path.Add(t.SiblingIndex);
                    path.Add(Tr?.Go?.Scene?.Handle ?? 0);
                    path.Reverse();
                    return tree = path.ToArray();
                }
            }

            public static int CompareTree(Item a, Item b)
            {
                var x = a.Tree; var y = b.Tree;
                for (int i = 0; i < x.Length && i < y.Length; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
                return x.Length.CompareTo(y.Length); // 祖先先畫
            }
        }

        readonly List<Item> items = new List<Item>();
        int seq;

        void SetCamera(NCamera cam, int w, int h)
        {
            Draw.CamGen++;
            var c = Draw.Cam;
            c.V = cam.WorldToCamera;
            c.P = cam.Projection;
            c.VP = MathUtil.Mul(c.P, c.V);
            MathUtil.Inverse(c.V, out c.InvV);
            c.Position = cam.Tr.Position;
            c.Near = cam.Near; c.Far = cam.Far;
            c.Ortho = cam.Orthographic;
            c.OrthoH = cam.OrthoSize; c.OrthoW = cam.OrthoSize * cam.Aspect;
            var pr = cam.PixelRect;
            c.Width = (int)pr.width; c.Height = (int)pr.height;
            c.Overlay = false;
        }

        readonly double[] KindMs = new double[7];
        readonly long[] KindAlloc = new long[7];

        void RenderCameraInto(NCamera cam, FrameList frame, int w, int h)
        {
            if (DumpNext) Dump.Add($"Camera {PathOf(cam.Go)} 場景={cam.Go.Scene?.Name} depth={cam.Depth} clear={cam.ClearFlags} bg={cam.Background} mask=0x{cam.CullingMask:x} ortho={cam.Orthographic} size={cam.OrthoSize} rt={(cam.TargetTexture != null ? cam.TargetTexture.name + " " + cam.TargetTexture.width + "x" + cam.TargetTexture.height : "-")}");
            var pr = cam.PixelRect;
            GL.glViewport((int)pr.x, (int)pr.y, (int)pr.width, (int)pr.height);
            GL.Enable(GL.SCISSOR_TEST);
            GL.glScissor((int)pr.x, (int)pr.y, (int)pr.width, (int)pr.height);
            GL.ColorMask(1, 1, 1, 1); GL.DepthMask(1); GL.StencilMask(0xff);
            switch (cam.ClearFlags)
            {
                case CameraClearFlags.Skybox:
                case CameraClearFlags.SolidColor:
                    {
                        var b = cam.Background;
                        GL.glClearColor(Mathf.GammaToLinearSpace(b.r), Mathf.GammaToLinearSpace(b.g), Mathf.GammaToLinearSpace(b.b), b.a);
                        GL.glClear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT);
                        break;
                    }
                case CameraClearFlags.Depth: GL.glClear(GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT); break;
            }
            GL.Disable(GL.SCISSOR_TEST);
            SetCamera(cam, w, h);
            ShaderLib.Mode = RenderPipelines.ModeFor(cam);
            Lights2DBegin(cam, (int)pr.width, (int)pr.height);
            Icalls.UnityEngine_Camera.Current = cam.C;
            DrawVideoPlanes(cam, false);

            long c0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            items.Clear();
            seq = 0;
            int mask = cam.CullingMask;
            int offView = 0;
            var lineFwd = cam.Tr.LocalToWorld.MultiplyVector(Vector3.forward).normalized;
            foreach (var r in NRenderer.All)
            {
                if (!r.Alive || !r.Enabled || r.Go == null || !r.Go.ActiveInHierarchy) continue;
                if ((mask & (1 << r.Go.Layer)) == 0) continue;
                if (r is NSpriteMask sm)
                {
                    if (sm.Sprite == null) continue;
                    var d = Distance(cam, r.Tr.Position);
                    // 自訂範圍關閉：影響全部，寫入在最前、清除在最後
                    items.Add(new Item
                    {
                        Renderer = r, MaskPass = 1, Late = 1, Queue = 3000, Dist = d, Seq = seq++, Tr = r.Tr,
                        Layer = sm.CustomRange ? SortingLayers.Value(sm.BackLayerID) : int.MinValue, Order = sm.CustomRange ? sm.BackOrder : int.MinValue,
                    });
                    items.Add(new Item
                    {
                        Renderer = r, MaskPass = 2, Late = 2, Queue = 3000, Dist = d, Seq = seq++, Tr = r.Tr,
                        Layer = sm.CustomRange ? SortingLayers.Value(sm.FrontLayerID) : int.MaxValue, Order = sm.CustomRange ? sm.FrontOrder : int.MaxValue,
                    });
                    continue;
                }
                if (!(r is NSpriteRenderer || r is NMeshRenderer || r is NParticleSystemRenderer || r is NLineRenderer)) continue;
                if ((r is NSpriteRenderer || r is NMeshRenderer) && SafeAreaCull.Outside(cam, r)) continue;
                if (OutsideView(cam, r, lineFwd)) { offView++; continue; }
                var mat = r.Shared.Length > 0 ? Native.Of<NMaterial>(r.Shared[0]) : null;
                var sg = SortingGroupOf(r);
                items.Add(new Item
                {
                    Renderer = r,
                    Layer = SortingLayers.Value(sg?.SortingLayerID ?? r.SortingLayerID),
                    Order = sg?.SortingOrder ?? r.SortingOrder,
                    Queue = mat?.RenderQueue ?? 3000,
                    Dist = Distance(cam, r is NLineRenderer lr ? lr.WorldCenter : r.Tr.Position),
                    Seq = seq++,
                    Tr = r.Tr,
                });
            }
            foreach (var b in frame.Canvases)
            {
                var root = b.Canvas.Root;
                var mode = root.EffectiveMode;
                if (mode == RenderMode.ScreenSpaceOverlay) continue;
                if (mode == RenderMode.ScreenSpaceCamera && Native.Of<NCamera>(root.WorldCamera) != cam) continue;
                if (mode == RenderMode.WorldSpace && (mask & (1 << b.Canvas.Go.Layer)) == 0) continue;
                items.Add(new Item
                {
                    Canvas = b,
                    Layer = SortingLayers.Value(b.Canvas.IsRoot || b.Canvas.OverrideSorting ? b.Canvas.SortingLayerID : root.SortingLayerID),
                    Order = b.Canvas.IsRoot || b.Canvas.OverrideSorting ? b.Canvas.SortingOrder : root.SortingOrder,
                    Queue = 3000,
                    Dist = mode == RenderMode.ScreenSpaceCamera ? root.PlaneDistance : Distance(cam, b.Canvas.Tr.Position),
                    Seq = seq++,
                    Tr = b.Canvas.Tr,
                });
            }
            long c1 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (DumpNext) Dump.Add($"  視野外不畫 {offView} 個（SpriteRenderer、LineRenderer）");
            items.Sort((a, b) =>
            {
                bool ao = a.Queue <= 2500, bo = b.Queue <= 2500;
                if (ao != bo) return ao ? -1 : 1;
                if (ao) { int q = a.Queue.CompareTo(b.Queue); return q != 0 ? q : a.Dist.CompareTo(b.Dist); }
                int c = a.Layer.CompareTo(b.Layer); if (c != 0) return c;
                c = a.Order.CompareTo(b.Order); if (c != 0) return c;
                c = a.Late.CompareTo(b.Late); if (c != 0) return c;
                c = a.Queue.CompareTo(b.Queue); if (c != 0) return c;
                c = b.Dist.CompareTo(a.Dist); if (c != 0) return c;
                // 同距離照場景樹順序：World Space Canvas 跟它底下的 Spine 角色同深度時，角色（樹裡排在背景後面）要畫在背景上面（battle_intro 的 Boss）
                c = Item.CompareTree(a, b); if (c != 0) return c;
                return a.Seq.CompareTo(b.Seq);
            });
            if (DumpNext)
                foreach (var it in items)
                    Dump.Add($"  排序 layer={it.Layer} order={it.Order}{(it.MaskPass == 1 ? " 遮罩寫入" : it.MaskPass == 2 ? " 遮罩清除" : "")} queue={it.Queue} dist={it.Dist:F2} seq={it.Seq} {(it.Canvas != null ? "Canvas " + PathOf(it.Canvas.Canvas.Go) : PathOf(it.Renderer.Go))}");
            PrepareSprites();
            // 天空盒（清除模式 Skybox 的攝影機）：不透明的（queue ≤ 2500）之後、其他的之前；不透明畫完的攝影機事件在天空盒前後（CommandExec.cs 檔頭）
            bool skyPending = cam.ClearFlags == CameraClearFlags.Skybox;
            bool opaqueDone = false;
            long c2 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            for (int ii = 0; ii < items.Count; ii++)
            {
                var it = items[ii];
                if (!opaqueDone && it.Queue > 2500) { opaqueDone = true; OpaqueDone(cam, ref skyPending); }
                if (lit2d && it.MaskPass == 0) Lights2DLayer(it.Layer);
                long d0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0, a0 = Engine.Profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
                int kind = 0, used = 1;
                try
                {
                    if (it.Canvas != null) { kind = 1; DrawCanvas(it.Canvas); }
                    else if (it.Renderer is NSpriteMask sm) { kind = 6; DrawSpriteMask(sm, it.MaskPass); }
                    else if (it.Renderer is NSpriteRenderer sr) { kind = 2; DrawSprite(sr, it); }
                    else if (it.Renderer is NMeshRenderer mr) { kind = 3; DrawMeshRenderer(mr); }
                    else if (it.Renderer is NParticleSystemRenderer psr) { kind = 4; DrawParticles(psr, cam); }
                    else if (it.Renderer is NLineRenderer) { kind = 5; used = DrawLines(ii, cam); }
                }
                catch (Exception e) { Engine.LogException(e, (NObject)it.Renderer ?? it.Canvas?.Canvas); }
                ii += used - 1;
                if (Engine.Profile && kind > 0) { KindMs[kind] += System.Diagnostics.Stopwatch.GetElapsedTime(d0).TotalMilliseconds; KindAlloc[kind] += GC.GetAllocatedBytesForCurrentThread() - a0; }
            }
            if (Engine.Profile)
            {
                Engine.Add("        收集", System.Diagnostics.Stopwatch.GetElapsedTime(c0, c1).TotalMilliseconds);
                Engine.Add("        排序", System.Diagnostics.Stopwatch.GetElapsedTime(c1, c2).TotalMilliseconds);
                Engine.Add("        畫 Canvas", KindMs[1]); Engine.Add("        畫 Sprite", KindMs[2]);
                Engine.Add("        畫 Mesh", KindMs[3]); Engine.Add("        畫粒子", KindMs[4]);
                Engine.Add("        畫線", KindMs[5]); Engine.Add("        畫 SpriteMask", KindMs[6]);
                Engine.AddAlloc("        畫 Canvas", KindAlloc[1]); Engine.AddAlloc("        畫 Sprite", KindAlloc[2]); Engine.AddAlloc("        畫 Mesh", KindAlloc[3]);
                Engine.AddAlloc("        畫粒子", KindAlloc[4]); Engine.AddAlloc("        畫線", KindAlloc[5]);
                System.Array.Clear(KindMs); System.Array.Clear(KindAlloc);
            }
            if (!opaqueDone) OpaqueDone(cam, ref skyPending);
            Lights2DEnd();
            DrawVideoPlanes(cam, true);
            Icalls.UnityEngine_Camera.Current = null;
        }

        void OpaqueDone(NCamera cam, ref bool skyPending)
        {
            Events(cam, CameraEvent.AfterForwardOpaque);
            Events(cam, CameraEvent.BeforeImageEffectsOpaque);
            Events(cam, CameraEvent.AfterImageEffectsOpaque);
            Events(cam, CameraEvent.BeforeSkybox);
            if (skyPending) { skyPending = false; DrawSkybox(cam); }
            Events(cam, CameraEvent.AfterSkybox);
            Events(cam, CameraEvent.BeforeForwardAlpha);
        }

        // ───────── 天空盒（RenderSettings.skybox） ─────────
        // 照 TS 版 sceneRenderer 的 buildSkybox：Skybox/6 Sided 一個 pass 畫一面（Front +Z、Back −Z、Left +X、Right −X、Up +Y、Down −Y，照 shader
        // 屬性的標註）。Unity 引擎內部的天空盒網格看不到原始碼，這裡每面一片四邊形，UV 照「從裡面看出去，貼圖正立」排（未查證，同 TS 版）。
        // 位置跟著攝影機、大小是遠平面的一半；材質的 ZTest LEqual／ZWrite Off 讓前面不透明物件擋住它。
        // 其他天空盒 shader（Procedural、Cubemap、Panoramic）還沒做：清成背景色。

        NMesh skyMesh;
        Material skyMat;
        string skyKey;
        static readonly HashSet<string> skyWarned = new HashSet<string>();

        Material SkyboxMaterial()
        {
            var rs = Scenes.Active != null && Render.Settings.TryGetValue(Scenes.Active, out var s) ? s["RenderSettings"] as System.Text.Json.Nodes.JsonObject : null;
            var r = rs?["m_SkyboxMaterial"] as System.Text.Json.Nodes.JsonObject;
            var guid = r?["guid"]?.ToString();
            if (guid == null) return null;
            var key = guid + ":" + r["fileID"];
            if (key != skyKey) { skyKey = key; skyMat = Assets.Load(guid, r["fileID"]?.ToString(), typeof(Material)) as Material; }
            return skyMat;
        }

        NMesh SkyMesh()
        {
            if (skyMesh != null) return skyMesh;
            float[][] faces =
            {
                new float[] { -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1 },      // Front +Z
                new float[] { 1, -1, -1, -1, -1, -1, -1, 1, -1, 1, 1, -1 },  // Back −Z
                new float[] { 1, -1, 1, 1, -1, -1, 1, 1, -1, 1, 1, 1 },      // Left +X
                new float[] { -1, -1, -1, -1, -1, 1, -1, 1, 1, -1, 1, -1 },  // Right −X
                new float[] { -1, 1, 1, 1, 1, 1, 1, 1, -1, -1, 1, -1 },      // Up +Y
                new float[] { -1, -1, -1, 1, -1, -1, 1, -1, 1, -1, -1, 1 },  // Down −Y
            };
            var pos = new float[6 * 4 * 3];
            var uv = new float[6 * 4 * 2];
            var m = new NMesh { Name = "Skybox" };
            m.Subs.Clear();
            for (int f = 0; f < 6; f++)
            {
                Array.Copy(faces[f], 0, pos, f * 12, 12);
                Array.Copy(new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 0, uv, f * 8, 8);
                int b = f * 4;
                m.Subs.Add(new NMesh.Sub { Indices = new[] { b, b + 1, b + 2, b, b + 2, b + 3 } });
            }
            m.Channels[(int)UnityEngine.Rendering.VertexAttribute.Position] = new NMesh.Channel { Data = pos, Dim = 3, Format = UnityEngine.Rendering.VertexAttributeFormat.Float32 };
            m.Channels[(int)UnityEngine.Rendering.VertexAttribute.TexCoord0] = new NMesh.Channel { Data = uv, Dim = 2, Format = UnityEngine.Rendering.VertexAttributeFormat.Float32 };
            m.VertexCount = 24;
            m.RecalculateBounds();
            return skyMesh = m;
        }

        void DrawSkybox(NCamera cam)
        {
            var mat = Native.Of<NMaterial>(SkyboxMaterial());
            if (mat == null) return;
            var name = mat.Shader?.Name;
            if (name != "Skybox/6 Sided")
            {
                if (skyWarned.Add(name ?? "(null)")) Engine.Log(LogType.Warning, "繪製：天空盒 shader " + name + " 還沒做，清成背景色");
                return;
            }
            var progs = ProgramsFor(mat, null);
            var g = GpuMesh.Of(SkyMesh());
            float s = cam.Far * 0.5f;
            var model = Matrix4x4.TRS(cam.Tr.Position, Quaternion.identity, new Vector3(s, s, s));
            if (DumpNext) Dump.Add($"  Skybox {mat.Name} pass {progs.Count}");
            for (int i = 0; i < progs.Count && i < 6; i++)
            {
                var p = progs[i];
                if (p.Prog == 0) continue;
                ov.Clear();
                Draw.Use(p, model, mat, null, ov, false);
                stream.DrawMesh(p, g, i);
                Draw.DrawCalls++;
            }
        }

        // ───────── 視錐剔除 ─────────
        // Unity 照 renderer 的包圍盒剔掉攝影機看不到的東西。這裡用實際要畫的幾何：SpriteRenderer 是頂點的範圍（含翻轉、九宮格）套上 model，
        // LineRenderer 是建好的世界座標網格；轉到 clip 座標後全部角落都在同一個裁切面外（留千分之一的邊界，蓋過 CPU 與 GPU 的捨入差）才不畫。
        // 凸包整個在一個平面外，GPU 本來就不會產生任何像素，跳過跟畫了一樣。材質有 GrabPass 的照畫（抓畫面本身有作用）。
        // Diceomancer 戰鬥時整張大地圖（OuterMapParent 底下的連線與節點，寬版約 210 個）在畫面外，以前每格照畫

        static readonly Vector3[] cornerTmp = new Vector3[8];

        bool OutsideView(NCamera cam, NRenderer r, Vector3 lineFwd)
        {
            int n;
            if (r is NSpriteRenderer sr)
            {
                var sp = Native.Of<NSprite>(sr.Sprite);
                if (sp == null) return false;
                SpriteMesh.Ensure(sp);
                float x0, x1, y0, y1;
                if (sr.DrawMode != SpriteDrawMode.Simple)
                {
                    // 同 Stream.AddSlicedSprite：網格是 [−pivot·w, (1 − pivot)·w] 乘上 size 與翻轉的正負號
                    float w = Math.Abs(sr.Size.x), h = Math.Abs(sr.Size.y);
                    float sx = (sr.Size.x < 0 ? -1 : 1) * (sr.FlipX ? -1 : 1), sy = (sr.Size.y < 0 ? -1 : 1) * (sr.FlipY ? -1 : 1);
                    float a = -sp.Pivot.x * w * sx, b = (-sp.Pivot.x * w + w) * sx, c = -sp.Pivot.y * h * sy, d = (-sp.Pivot.y * h + h) * sy;
                    x0 = Math.Min(a, b); x1 = Math.Max(a, b); y0 = Math.Min(c, d); y1 = Math.Max(c, d);
                }
                else
                {
                    var v = sp.Vertices;
                    if (v == null || v.Length == 0) return false;
                    float fx = sr.FlipX ? -1 : 1, fy = sr.FlipY ? -1 : 1;
                    x0 = y0 = float.MaxValue; x1 = y1 = float.MinValue;
                    foreach (var p in v)
                    {
                        float x = p.x * fx, y = p.y * fy;
                        x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                    }
                }
                var m = sr.Tr.LocalToWorld;
                for (int i = 0; i < 4; i++) cornerTmp[i] = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, 0));
                n = 4;
            }
            else if (r is NLineRenderer lr)
            {
                if (lr.Shared.Length == 0 || !lr.Build(cam.Orthographic, cam.Tr.Position, lineFwd)) return false;
                var b = lr.DrawMesh.Bounds;
                for (int i = 0; i < 8; i++) cornerTmp[i] = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                n = 8;
            }
            else return false;
            if (!AllOutsideOnePlane(cornerTmp, n)) return false;
            var mat = r.Shared.Length > 0 ? Native.Of<NMaterial>(r.Shared[0]) : null;
            if (mat == null && r is NSpriteRenderer) mat = Native.Of<NMaterial>(Icalls.UnityEngine_Material.GetDefaultMaterial());
            return mat != null && !MayGrab(mat);
        }

        readonly Dictionary<NShader, bool> shaderGrabs = new Dictionary<NShader, bool>();

        /// <summary>材質的 shader 有沒有 GrabPass（任何 subshader 的任何 pass，比畫的時候挑的 pass 寬）：shader 的固定性質，每個 shader 查一次</summary>
        bool MayGrab(NMaterial mat)
        {
            var shader = mat.Shader;
            var name = shader?.Name;
            if (name == null) return false; // 沒有 shader：代用的 Sprites/Default
            if (shaderGrabs.TryGetValue(shader, out var g)) return g;
            g = false;
            if (Builtin.Get(name) == null && ShaderLib.Entry(shader)?.Index?["subShaders"] is System.Text.Json.Nodes.JsonArray subs)
                foreach (var sub in subs)
                    foreach (var p in sub?["passes"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray())
                        if ((int)Serializer.D(p?["type"]) == 2) g = true;
            shaderGrabs[shader] = g;
            return g;
        }

        /// <summary>點（世界座標）照目前攝影機的 VP 轉到 clip 座標，全部在左、右、下、上其中同一個面外</summary>
        static bool AllOutsideOnePlane(Vector3[] pts, int n)
        {
            var vp = Draw.Cam.VP;
            int outside = 0b1111;
            for (int i = 0; i < n && outside != 0; i++)
            {
                double x = pts[i].x, y = pts[i].y, z = pts[i].z;
                double cx = vp.m00 * x + vp.m01 * y + vp.m02 * z + vp.m03;
                double cy = vp.m10 * x + vp.m11 * y + vp.m12 * z + vp.m13;
                double cw = vp.m30 * x + vp.m31 * y + vp.m32 * z + vp.m33;
                if (!(cw > 1e-6)) return false; // 攝影機後面（透視）：不判斷
                double tol = 1e-3 * (Math.Abs(cw) + Math.Max(Math.Abs(cx), Math.Abs(cy)));
                if (!(cx - cw > tol)) outside &= ~1;
                if (!(-cx - cw > tol)) outside &= ~2;
                if (!(cy - cw > tol)) outside &= ~4;
                if (!(-cy - cw > tol)) outside &= ~8;
            }
            return outside != 0;
        }

        static NSortingGroup SortingGroupOf(NRenderer r)
        {
            for (var t = r.Tr; t != null; t = t.Parent)
            {
                var g = t.Go?.Get<NSortingGroup>();
                if (g != null && g.Enabled) return g;
            }
            return null;
        }

        static float Distance(NCamera cam, Vector3 p)
        {
            if (cam.Orthographic || cam.TransparencySort == TransparencySortMode.Orthographic)
                return -MathUtil.MulPoint(cam.WorldToCamera, p).z;
            return (p - cam.Tr.Position).magnitude;
        }

        void DrawOverlay(FrameList frame, int w, int h)
        {
            ShaderLib.Mode = RenderPipelines.OverlayMode();
            var list = new List<CanvasBatch>();
            foreach (var b in frame.Canvases) if (b.Canvas.Root.EffectiveMode == RenderMode.ScreenSpaceOverlay) list.Add(b);
            list.Sort((a, b) =>
            {
                int c = SortingLayers.Value(Layer(a)).CompareTo(SortingLayers.Value(Layer(b)));
                if (c != 0) return c;
                c = Order(a).CompareTo(Order(b));
                return c != 0 ? c : a.Canvas.RenderOrder.CompareTo(b.Canvas.RenderOrder);
            });
            if (list.Count == 0) return;
            GL.glViewport(0, 0, w, h);
            Draw.CamGen++;
            var c = Draw.Cam;
            c.V = Matrix4x4.identity;
            Icalls.UnityEngine_Matrix4x4.Ortho_Injected(0, w, 0, h, -10000, 10000, out c.P);
            c.VP = c.P; c.InvV = Matrix4x4.identity;
            c.Position = Vector3.zero; c.Near = -10000; c.Far = 10000; c.Ortho = true; c.OrthoW = w * 0.5f; c.OrthoH = h * 0.5f;
            c.Width = w; c.Height = h; c.Overlay = true;
            // Overlay 的 UI 跟攝影機畫的東西不共用深度；stencil 清掉（Mask 從 0 開始）
            GL.StencilMask(0xff);
            GL.glClear(GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT);
            foreach (var b in list)
            {
                try { DrawCanvas(b); } catch (Exception e) { Engine.LogException(e, b.Canvas); }
            }
        }

        static int Layer(CanvasBatch b) => b.Canvas.IsRoot || b.Canvas.OverrideSorting ? b.Canvas.SortingLayerID : b.Canvas.Root.SortingLayerID;
        static int Order(CanvasBatch b) => b.Canvas.IsRoot || b.Canvas.OverrideSorting ? b.Canvas.SortingOrder : b.Canvas.Root.SortingOrder;

        // ───────── uGUI ─────────

        static readonly int idClipRect = PropertyIds.Of("_ClipRect"), idTexAdd = PropertyIds.Of("_TextureSampleAdd"), idSoft = PropertyIds.Of("_UIMaskSoftness");
        static readonly Vector4 NoClip = new Vector4(-2e10f, -2e10f, 2e10f, 2e10f);

        static string PathOf(NGameObject g)
        {
            var parts = new List<string>();
            for (var t = g?.Transform; t != null && parts.Count < 6; t = t.Parent) parts.Add(t.Go.Name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        void DrawCanvas(CanvasBatch b)
        {
            if (DumpNext) Dump.Add($"Canvas {PathOf(b.Canvas.Go)} 場景={b.Canvas.Go.Scene?.Name} mode={b.Canvas.Root.EffectiveMode} layer={Layer(b)} order={Order(b)} 元素 {b.Items.Count}");
            var root = b.Canvas.Root;
            var rootT = root.Tr;
            var toRoot = rootT.WorldToLocal;
            var model = rootT.LocalToWorld;
            stream.BeginBatch();
            pending.Clear();
            Batch cur = null;
            var cullCam = root.EffectiveMode == RenderMode.ScreenSpaceOverlay ? null : Icalls.UnityEngine_Camera.Current is Camera cc ? Native.Of<NCamera>(cc) : null;
            foreach (var (cr, pop) in b.Order)
            {
                if (!cr.Alive || cr.Geo == null || cr.Geo.VertexCount == 0 || cr.Cull) continue;
                if (!pop && SafeAreaCull.Aspect > 0 && SafeAreaCull.Outside(cullCam, cr)) continue;
                var mats = pop ? cr.PopMaterials : cr.Materials;
                int count = pop ? cr.PopMaterialCount : cr.MaterialCount;
                if (count == 0 && mats.Count > 0) count = mats.Count;
                float alpha = cr.InheritedAlpha();
                var color = cr.Color;
                if (cr.CullTransparentMesh && color.a * alpha <= 0f && !pop) continue;
                for (int mi = 0; mi < Math.Max(1, count); mi++)
                {
                    var mat = mi < mats.Count ? Native.Of<NMaterial>(mats[mi]) : null;
                    if (mat == null) mat = Native.Of<NMaterial>(Icalls.UnityEngine_Canvas.GetDefaultCanvasMaterial());
                    var tex = cr.Texture;
                    bool clip = cr.ClipEnabled;
                    var key = (mat, (object)tex, clip, clip ? cr.ClipRect : default, cr.ClipSoftness);
                    if (cur == null || pop || !cur.Same(key))
                    {
                        Queue(cur);
                        cur = new Batch { Material = mat, Texture = tex, Clip = clip, ClipRect = cr.ClipRect, Softness = cr.ClipSoftness, Pop = pop };
                    }
                    var m = MathUtil.Mul(toRoot, cr.Tr.LocalToWorld);
                    if (DumpNext) { cur.FirstName ??= PathOf(cr.Go); cur.Names++; }
                    cur.Add(stream, cr.Geo, m, color, alpha);
                    if (pop) { Queue(cur); cur = null; }
                }
            }
            Queue(cur);
            // 整個 Canvas 的頂點收完一次傳上去，各批照索引區段畫（原本每批各傳一次，手機上 300 批要 7 ms）
            if (pending.Count == 0) return;
            long u0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            stream.UploadAll();
            if (Engine.Profile) Engine.Add("          UI 上傳", System.Diagnostics.Stopwatch.GetElapsedTime(u0).TotalMilliseconds);
            foreach (var pb in pending) Flush(pb, model);
        }

        readonly List<Batch> pending = new List<Batch>();
        void Queue(Batch b) { if (b != null && b.IdxCount > 0) pending.Add(b); }

        sealed class Batch
        {
            public NMaterial Material;
            public object Texture;
            public bool Clip, Pop;
            public Rect ClipRect;
            public Vector2 Softness;
            public int Start, Count, IdxStart, IdxCount;
            public string FirstName;
            public int Names;
            public bool Same((NMaterial, object, bool, Rect, Vector2) k) => k.Item1 == Material && k.Item2 == Texture && k.Item3 == Clip && k.Item4 == (Clip ? ClipRect : default) && k.Item5 == Softness;
            public void Add(Stream s, UIGeometry g, Matrix4x4 m, Color tint, float alpha)
            {
                if (Count == 0 && IdxCount == 0) { Start = s.VertexCount; IdxStart = s.IndexCount; }
                s.AddUI(g, m, tint, alpha, Start);
                Count = s.VertexCount - Start;
                IdxCount = s.IndexCount - IdxStart;
            }
        }

        void Flush(Batch b, Matrix4x4 model)
        {
            if (b == null || b.IdxCount == 0) return;
            LastBatches++;
            var mat = b.Material;
            if (DumpNext)
                Dump.Add($"  批 {LastBatches}：{b.FirstName}（{b.Names} 個元素）material={mat?.Name} shader={mat?.Shader?.Name} tex={(b.Texture as Texture)?.name} clip={b.Clip} pop={b.Pop} 頂點 {b.Count} 第一個顏色=({stream.ColorAt(b.Start)})");
            ov.Clear();
            ov.MainTex = b.Texture as Texture;
            ov.Sheet.Vectors[idClipRect] = b.Clip ? new Vector4(b.ClipRect.xMin, b.ClipRect.yMin, b.ClipRect.xMax, b.ClipRect.yMax) : NoClip;
            ov.Sheet.Vectors[idSoft] = new Vector4(b.Softness.x, b.Softness.y, 0, 0);
            var progs = ProgramsFor(mat, b.Clip ? "UNITY_UI_CLIP_RECT" : null);
            if (progs == null) return;
            foreach (var p in progs)
            {
                if (p.Pass?.Type == 2) { Grab(p.Pass); continue; }
                if (p.Prog == 0) continue;
                long q0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                Draw.Use(p, model, mat, null, ov, true);
                long q1 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                stream.DrawUploaded(p, b.IdxStart, b.IdxCount);
                if (Engine.Profile)
                {
                    Engine.Add("        UI Draw.Use", System.Diagnostics.Stopwatch.GetElapsedTime(q0, q1).TotalMilliseconds);
                    Engine.Add("        UI DrawRange", System.Diagnostics.Stopwatch.GetElapsedTime(q1).TotalMilliseconds);
                }
                Draw.DrawCalls++;
            }
        }

        static readonly List<string> kwTmp = new List<string>();

        List<GpuProgram> ProgramsFor(NMaterial mat, string extraKeyword) => ProgramsFor(mat, extraKeyword, null);

        // ProgramsFor 的結果每個材質（加上額外的 keyword）記一份：ShaderLib.Programs 每次都把 keyword 排序、接成字串當鍵（每次 draw 都配置字串），
        // 材質的 keyword、全域 keyword、shader、模式都跟上次一樣時，ShaderLib 會用同一個鍵回同一份清單，這裡直接回那份
        sealed class ProgMemo { public NShader Shader; public int Mode; public string[] MatKw, GlobalKw; public List<GpuProgram> List; }
        readonly Dictionary<(NMaterial, string), ProgMemo> progMemo = new Dictionary<(NMaterial, string), ProgMemo>();

        static bool SameSet(HashSet<string> set, string[] arr)
        {
            if (set.Count != arr.Length) return false;
            foreach (var k in arr) if (!set.Contains(k)) return false;
            return true;
        }

        List<GpuProgram> Remember(NMaterial mat, string extraKeyword, List<GpuProgram> list)
        {
            if (progMemo.Count >= 4096) progMemo.Clear(); // 材質一直新建時不要無限長
            var globals = Icalls.UnityEngine_Shader.GlobalKeywords;
            var g = new string[globals.Count];
            globals.CopyTo(g);
            var m = new string[mat.Keywords.Count];
            mat.Keywords.CopyTo(m);
            progMemo[(mat, extraKeyword)] = new ProgMemo { Shader = mat.Shader, Mode = ShaderLib.Mode, MatKw = m, GlobalKw = g, List = list };
            return list;
        }

        /// <summary>extras：引擎這次繪製另外開的 keyword（光照圖、內建光照的 multi_compile_fwdbase…）</summary>
        List<GpuProgram> ProgramsFor(NMaterial mat, string extraKeyword, List<string> extras)
        {
            var name = mat?.Shader?.Name;
            var builtin = Builtin.Get(name);
            if (builtin != null) return One(builtin);
            if (extras == null && mat != null && progMemo.TryGetValue((mat, extraKeyword), out var memo) && memo.Shader == mat.Shader && memo.Mode == ShaderLib.Mode
                && SameSet(mat.Keywords, memo.MatKw) && SameSet(Icalls.UnityEngine_Shader.GlobalKeywords, memo.GlobalKw))
                return memo.List;
            var e = ShaderLib.Entry(mat.Shader);
            if (e != null && !e.Failed)
            {
                kwTmp.Clear();
                kwTmp.AddRange(mat.Keywords);
                foreach (var g in Icalls.UnityEngine_Shader.GlobalKeywords) kwTmp.Add(g);
                if (extraKeyword != null) kwTmp.Add(extraKeyword);
                if (extras != null) kwTmp.AddRange(extras);
                var list = ShaderLib.Programs(e, kwTmp, mat.DisabledPasses);
                if (list != null && list.Count > 0) return extras == null ? Remember(mat, extraKeyword, list) : list;
                // URP：這個 renderer 不畫這個 shader 的任何 pass（例如 2D renderer 碰到只有 UniversalForward 的 shader），Unity 不畫
                if (list != null && ShaderLib.NoMatchingPass(e, kwTmp)) return extras == null ? Remember(mat, extraKeyword, list) : list;
            }
            if (StandIns.Add(name ?? "(null)")) Engine.Log(LogType.Warning, "繪製：shader " + (name ?? "(沒有)") + " 沒有可用的程式，用 Sprites/Default 代用");
            return One(Builtin.StandIn());
        }

        readonly List<GpuProgram> one = new List<GpuProgram>(1);
        List<GpuProgram> One(GpuProgram p) { one.Clear(); if (p != null) one.Add(p); return one; }

        // ───────── SpriteRenderer ─────────

        static readonly int idRendererColor = PropertyIds.Of("_RendererColor"), idFlip = PropertyIds.Of("_Flip");

        /// <summary>這台攝影機要畫的 SpriteRenderer 頂點照繪製順序全部收進 spriteStream、一次傳上去（Stream.UploadAll），畫的時候各畫自己那段索引；
        /// 頂點的值跟以前一個一個收（Stream.AddSprite／AddSlicedSprite）完全一樣。以前每個 sprite 各傳一次（換一組緩衝、兩次 glBufferData、重設頂點屬性），
        /// Diceomancer 寬版戰鬥 112 個 sprite 桌面一格 0.32 ms</summary>
        void PrepareSprites()
        {
            spriteStream.BeginBatch();
            foreach (var it in items)
            {
                if (!(it.Renderer is NSpriteRenderer sr)) continue;
                it.IdxStart = spriteStream.IndexCount;
                var sp = Native.Of<NSprite>(sr.Sprite);
                if (sp != null)
                {
                    SpriteMesh.Ensure(sp);
                    var c = sr.Color;
                    var lin = new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a);
                    if (sr.DrawMode != SpriteDrawMode.Simple) spriteStream.AddSlicedSprite(sp, sr.Size, sr.FlipX, sr.FlipY, lin);
                    else spriteStream.AddSprite(sp, sr.FlipX, sr.FlipY, lin);
                }
                it.IdxCount = spriteStream.IndexCount - it.IdxStart;
            }
            if (spriteStream.IndexCount > 0) spriteStream.UploadAll();
        }

        void DrawSprite(NSpriteRenderer sr, Item it)
        {
            var sp = Native.Of<NSprite>(sr.Sprite);
            if (sp == null) return;
            var mat = sr.Shared.Length > 0 ? Native.Of<NMaterial>(sr.Shared[0]) : null;
            if (mat == null) mat = Native.Of<NMaterial>(Icalls.UnityEngine_Material.GetDefaultMaterial());
            var model = sr.Tr.LocalToWorld;
            if (DumpNext) Dump.Add($"  Sprite {PathOf(sr.Go)} material={mat?.Name} shader={mat?.Shader?.Name} tex={sp.Texture?.Managed?.name} block={(sr.Block != null ? "有" : "-")} mask={sr.MaskInteraction} mode={sr.DrawMode}");
            ov.Clear();
            ov.MainTex = (Texture)sp.Texture?.Managed;
            ov.Sheet.Vectors[idRendererColor] = Vector4.one;
            ov.Sheet.Vectors[idFlip] = new Vector4(1, 1, 0, 0);
            var progs = ProgramsFor(mat, null);
            Draw.MaskInteraction = (int)sr.MaskInteraction;
            try
            {
                foreach (var p in progs)
                {
                    if (p.Pass?.Type == 2) { Grab(p.Pass); continue; }
                    if (p.Prog == 0) continue;
                    long q0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    Draw.Use(p, model, mat, sr.Block, ov, false);
                    long q1 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    spriteStream.DrawUploaded(p, it.IdxStart, it.IdxCount);
                    if (Engine.Profile)
                    {
                        Engine.Add("          Sprite Draw.Use", System.Diagnostics.Stopwatch.GetElapsedTime(q0, q1).TotalMilliseconds);
                        Engine.Add("          Sprite DrawRange", System.Diagnostics.Stopwatch.GetElapsedTime(q1).TotalMilliseconds);
                    }
                    Draw.DrawCalls++;
                }
            }
            finally { Draw.MaskInteraction = 0; }
        }

        // ───────── SpriteMask ─────────

        static readonly int idCutoff = PropertyIds.Of("_Cutoff");

        /// <summary>SpriteMask 的寫入（pass 1）或清除（pass 2）：sprite 的網格畫進 stencil 的 0x80，alpha 低於 cutoff 的像素不算（Sprites/Mask）</summary>
        void DrawSpriteMask(NSpriteMask m, int pass)
        {
            var sp = Native.Of<NSprite>(m.Sprite);
            if (sp == null) return;
            var p = Builtin.Get("Sprites/Mask");
            if (p == null) return;
            SpriteMesh.Ensure(sp);
            stream.BeginBatch();
            stream.AddSprite(sp, false, false, Color.white);
            ov.Clear();
            ov.MainTex = (Texture)sp.Texture?.Managed;
            ov.Sheet.Vectors[idRendererColor] = Vector4.one;
            ov.Sheet.Floats[idCutoff] = m.AlphaCutoff;
            Draw.MaskPass = pass;
            try
            {
                Draw.Use(p, m.Tr.LocalToWorld, null, null, ov, false);
                stream.DrawRange(p, 0, stream.VertexCount, 0, stream.IndexCount);
                Draw.DrawCalls++;
            }
            finally { Draw.MaskPass = 0; }
        }

        // ───────── ParticleSystemRenderer ─────────

        /// <summary>粒子與拖尾照這台攝影機產成世界座標的網格（材質 0 畫粒子、材質 1 畫拖尾）</summary>
        void DrawParticles(NParticleSystemRenderer pr, NCamera cam)
        {
            Draw.MaskInteraction = (int)pr.R.F("m_MaskInteraction");
            try { DrawParticlesMasked(pr, cam); }
            finally { Draw.MaskInteraction = 0; }
        }

        void DrawParticlesMasked(NParticleSystemRenderer pr, NCamera cam)
        {
            var sim = pr.System?.Sim;
            if (sim == null || sim.Pose == null) return;
            var pc = PCam.Of(cam);
            if (sim.N > 0 && (int)pr.R.F("m_RenderMode") != 5 && pr.Shared.Length > 0)
            {
                var b = pr.DrawBuilder ??= new ParticleGeometry.Builder();
#if PERF_VERIFY
                var r0 = Verify.RandomState();
#endif
                ParticleGeometry.Fill(sim, pr, pc, true, false, Native.Of<NMesh>(pr.ParticleMesh), b);
#if PERF_VERIFY
                Verify.Particles(r0, b, m => ParticleGeometryOld.Particles(sim, pr, pc, true, false, Native.Of<NMesh>(pr.ParticleMesh), m));
#endif
                DrawBuilt(b, (GpuMesh)(pr.DrawGpu ??= new GpuMesh()), Native.Of<NMaterial>(pr.Shared[0]), pr.Block);
            }
            if (sim.Mod("TrailModule").On && pr.Shared.Length > 1 && (sim.N > 0 || sim.Orphans.Count > 0))
            {
                var b = pr.DrawTrailBuilder ??= new ParticleGeometry.Builder();
#if PERF_VERIFY
                var r0 = Verify.RandomState();
#endif
                ParticleGeometry.FillTrails(sim, pr, pc, true, false, b);
#if PERF_VERIFY
                Verify.Particles(r0, b, m => ParticleGeometryOld.Trails(sim, pr, pc, true, false, m));
#endif
                DrawBuilt(b, (GpuMesh)(pr.DrawTrailGpu ??= new GpuMesh()), Native.Of<NMaterial>(pr.Shared[1]), pr.Block);
            }
        }

        /// <summary>粒子網格已經在世界座標（model 是單位矩陣）：頂點直接從 Builder 上傳，材質的每個 pass 畫一次（沒有頂點就不畫，也不上傳）</summary>
        void DrawBuilt(ParticleGeometry.Builder b, GpuMesh g, NMaterial mat, PropertySheet block)
        {
            if (mat == null || b.Vtx == 0) return;
            g.UploadBuilt(b);
            ov.Clear();
            var progs = ProgramsFor(mat, null);
            foreach (var p in progs)
            {
                if (p.Pass?.Type == 2) { Grab(p.Pass); continue; }
                if (p.Prog == 0) continue;
                Draw.Use(p, Matrix4x4.identity, mat, block, ov, false);
                stream.DrawMesh(p, g, 0);
                Draw.DrawCalls++;
            }
        }

        /// <summary>排序後相鄰的兩個 renderer 之間沒有別的事要做（OpaqueDone 的攝影機事件、2D 光照換 layer、SpriteMask 的寫入或清除），
        /// 才能合成一次 draw</summary>
        bool Joinable(Item a, Item b) => (a.Queue > 2500) == (b.Queue > 2500) && (!lit2d || a.Layer == b.Layer) && a.MaskPass == 0 && b.MaskPass == 0;

        /// <summary>材質只有一個要畫的 pass、沒有 GrabPass 才能把幾個 renderer 合成一次 draw：多 pass 時一個一個畫是 A0 A1 B0 B1，合起來會變成 A0 B0 A1 B1；
        /// GrabPass 每個 renderer 前都抓一次畫面，合起來只抓一次</summary>
        static bool SinglePass(List<GpuProgram> progs)
        {
            int n = 0;
            foreach (var p in progs)
            {
                if (p.Pass?.Type == 2) return false;
                if (p.Prog != 0) n++;
            }
            return n == 1;
        }

        /// <summary>LineRenderer：照這台攝影機建世界座標的帶子（Graphics/LineRenderer.cs），從 items[i] 開始畫，回傳用掉幾個 item。
        /// 網格本來就在世界座標、model 是單位矩陣，接在後面同材質、沒有 MaterialPropertyBlock、遮罩互動一樣的線頂點接起來一次畫，
        /// 每個頂點的值與 uniform 都跟一條一條畫相同，三角形照原本的先後順序（同一次 draw 裡照索引順序混色）。
        /// 地圖、小地圖的連線（Diceomancer 寬版戰鬥 132 條）以前每條各一次 Draw.Use、每格重傳網格</summary>
        int DrawLines(int i, NCamera cam)
        {
            var first = (NLineRenderer)items[i].Renderer;
            var mat = first.Shared.Length > 0 ? Native.Of<NMaterial>(first.Shared[0]) : null;
            if (mat == null) return 1;
            var progs = ProgramsFor(mat, null);
            int end = i + 1;
            if (first.Block == null && SinglePass(progs))
                while (end < items.Count && items[end].Renderer is NLineRenderer l2 && Joinable(items[end - 1], items[end]) && l2.Block == null
                    && l2.MaskInteraction == first.MaskInteraction && l2.Shared.Length > 0 && Native.Of<NMaterial>(l2.Shared[0]) == mat)
                    end++;
            var fwd = cam.Tr.LocalToWorld.MultiplyVector(Vector3.forward).normalized;
            stream.BeginBatch();
            for (int k = i; k < end; k++)
            {
                var lr = (NLineRenderer)items[k].Renderer;
                if (lr.Build(cam.Orthographic, cam.Tr.Position, fwd) && lr.DrawMesh.VertexCount > 0) stream.AddMesh(lr.DrawMesh);
            }
            if (DumpNext) Dump.Add($"  LineRenderer {PathOf(first.Go)} 起 {end - i} 條 material={mat.Name} shader={mat.Shader?.Name} 頂點 {stream.VertexCount}");
            if (stream.IndexCount == 0) return end - i;
            ov.Clear();
            progs = ProgramsFor(mat, null); // 上面的清單可能是共用的（內建 shader），重拿一次
            Draw.MaskInteraction = (int)first.MaskInteraction;
            try
            {
                foreach (var p in progs)
                {
                    if (p.Pass?.Type == 2) { Grab(p.Pass); continue; }
                    if (p.Prog == 0) continue;
                    Draw.Use(p, Matrix4x4.identity, mat, first.Block, ov, false);
                    stream.DrawRange(p, 0, stream.VertexCount, 0, stream.IndexCount);
                    Draw.DrawCalls++;
                }
            }
            finally { Draw.MaskInteraction = 0; }
            return end - i;
        }

        // ───────── MeshRenderer ─────────

        static readonly int idLightmap = PropertyIds.Of("unity_Lightmap"), idLightmapST = PropertyIds.Of("unity_LightmapST");
        readonly List<string> meshKw = new List<string>();
        readonly PropertySheet lightSheet = new PropertySheet();

        static string ClipOf(Vector3 p)
        {
            var c = Draw.Cam.VP * new Vector4(p.x, p.y, p.z, 1);
            return c.w == 0 ? "w=0" : $"({c.x / c.w:F2},{c.y / c.w:F2},{c.z / c.w:F2}) w={c.w:F1}";
        }

        void DrawMeshRenderer(NMeshRenderer mr)
        {
            var mf = mr.Go.Get<NMeshFilter>();
            var mesh = Native.Of<NMesh>(mf?.Mesh);
            if (mesh == null || mesh.VertexCount == 0) return;
            var g = GpuMesh.Of(mesh);
            var model = mr.Tr.LocalToWorld;
            // 烘焙光照圖（Renderer.lightmapIndex／lightmapScaleOffset）：網格要有第二組 uv（光照圖座標）才用 LIGHTMAP_ON 變體
            var lm = mesh.Channels[(int)UnityEngine.Rendering.VertexAttribute.TexCoord1] != null ? Icalls.UnityEngine_LightmapSettings.ColorOf(mr) : null;
            // 內建光照（ForwardBase）的 keyword：DIRECTIONAL＋LIGHTMAP_ON 或 LIGHTPROBE_SH、霧；不在 keyword 空間的 shader（Unlit…）會被濾掉
            meshKw.Clear();
            lightSheet.Vectors.Clear();
            BuiltinLighting.Apply(mr.Go.Layer, lm != null, meshKw, lightSheet);
            if (DumpNext) Dump.Add($"  MeshRenderer {PathOf(mr.Go)} mesh={mesh.Name} 頂點 {mesh.VertexCount} 材質 {string.Join("、", Array.ConvertAll(mr.Shared, m => Native.Of<NMaterial>(m)?.Shader?.Name ?? "null"))} " +
                $"光照圖 {(lm != null ? lm.name + " st=" + mr.LightmapST : "-")} 中心 clip={ClipOf(mr.WorldBounds.center)} 大小 {mr.WorldBounds.size}");
            for (int si = 0; si < g.Subs.Count; si++)
            {
                var matObj = si < mr.Shared.Length ? mr.Shared[si] : (mr.Shared.Length > 0 ? mr.Shared[mr.Shared.Length - 1] : null);
                var mat = Native.Of<NMaterial>(matObj);
                if (mat == null) continue;
                var block = mr.BlockPerMaterial.TryGetValue(si, out var pb) ? pb : mr.Block;
                ov.Clear();
                foreach (var kv in lightSheet.Vectors) ov.Sheet.Vectors[kv.Key] = kv.Value;
                if (lm != null)
                {
                    ov.Sheet.Textures[idLightmap] = new TexEnv { Texture = lm, Scale = Vector2.one };
                    ov.Sheet.Vectors[idLightmapST] = mr.LightmapST;
                }
                var progs = ProgramsFor(mat, null, meshKw);
                foreach (var p in progs)
                {
                    if (p.Pass?.Type == 2) { Grab(p.Pass); continue; }
                    if (p.Prog == 0) continue;
                    Draw.Use(p, model, mat, block, ov, false);
                    stream.DrawMesh(p, g, si);
                    Draw.DrawCalls++;
                }
            }
        }
    }
}
