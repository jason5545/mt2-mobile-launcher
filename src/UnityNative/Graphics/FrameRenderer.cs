// 每格的繪製入口：先讓 uGUI 重建（Canvas.willRenderCanvases：排版、Graphic 重產網格），再整理出這一格要畫的東西交給 GPU 端。
// 沒有 GPU 端（桌面的無畫面模式）時只做前半，CanvasRenderer 的 depth 照樣算（GraphicRaycaster 的點擊順序靠它）。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public interface IGpu
    {
        void Frame(FrameList frame);
        void RenderCamera(NCamera cam, FrameList frame);
    }

    /// <summary>一個 Canvas 底下照繪製順序排好的 CanvasRenderer</summary>
    public sealed class CanvasBatch
    {
        public NCanvas Canvas;
        public readonly List<NCanvasRenderer> Items = new List<NCanvasRenderer>();
        public readonly List<(int index, NCanvasRenderer cr)> Pops = new List<(int, NCanvasRenderer)>();
        /// <summary>Items 與 Pops 交錯的繪製指令：(元素, 是不是 pop)</summary>
        public readonly List<(NCanvasRenderer cr, bool pop)> Order = new List<(NCanvasRenderer, bool)>();
    }

    public sealed class FrameList
    {
        public readonly List<CanvasBatch> Canvases = new List<CanvasBatch>();
        public readonly List<NCamera> Cameras = new List<NCamera>();
    }

    public static class FrameRenderer
    {
        public static IGpu Gpu;
        static readonly FrameList frame = new FrameList();
        public static FrameList Last => frame;

        public static void Register() => Engine.Render.Add(RenderFrame);

        static void RenderFrame()
        {
            long t0 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            // 這一格才啟用、還沒驅動的根 Canvas（Instantiate 出來的，NCanvas.Cloning）在排版前補上
            try { NCanvas.UpdateAll(); } catch (Exception e) { Engine.LogException(e); }
            try { Canvas.SendPreWillRenderCanvases(); } catch (Exception e) { Engine.LogException(e); }
            try { Canvas.SendWillRenderCanvases(); } catch (Exception e) { Engine.LogException(e); }
            long t1 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            Collect();
            long t2 = Engine.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            Gpu?.Frame(frame);
            if (Engine.Profile)
            {
                Engine.Add("    uGUI willRenderCanvases", System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                Engine.Add("    Collect", System.Diagnostics.Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
                Engine.Add("    Gpu.Frame", System.Diagnostics.Stopwatch.GetElapsedTime(t2).TotalMilliseconds);
            }
        }

        public static void RenderCamera(NCamera cam)
        {
            if (Gpu == null) return;
            Collect();
            Gpu.RenderCamera(cam, frame);
        }

        /// <summary>每個根 Canvas（和 overrideSorting 的子 Canvas）一批，照階層順序走 CanvasRenderer，順便給 depth</summary>
        static void Collect()
        {
            frame.Canvases.Clear();
            frame.Cameras.Clear();
            foreach (var c in NCamera.All) if (c.Alive && c.IsActiveAndEnabled) frame.Cameras.Add(c);
            frame.Cameras.Sort((a, b) => a.Depth.CompareTo(b.Depth));

            int order = 0;
            foreach (var c in NCanvas.All)
            {
                if (!c.Alive || !c.IsActiveAndEnabled) continue;
                if (!c.IsRoot && !c.OverrideSorting) continue;
                var b = new CanvasBatch { Canvas = c };
                c.RenderOrder = order++;
                int depth = 0;
                Walk(c.Tr, b, c, ref depth);
                frame.Canvases.Add(b);
            }
        }

        static void Walk(NTransform t, CanvasBatch b, NCanvas owner, ref int depth)
        {
            var go = t.Go;
            if (go == null || !go.ActiveInHierarchy) return;
            // 子 Canvas 有 overrideSorting：自成一批（另外排序），這裡跳過它的子樹
            var cv = go.Get<NCanvas>();
            // 停用的子 Canvas：整棵不畫（Unity 也是）；overrideSorting 的自成一批
            if (cv != null && cv != owner && (!cv.Enabled || cv.OverrideSorting)) return;
            NCanvasRenderer cr = null;
            foreach (var comp in go.Components) if (comp is NCanvasRenderer x && x.Alive) { cr = x; break; }
            if (cr != null)
            {
                // 沒網格、被 cull 的也照階層順序給 depth（Unity 也是）：隱形的 raycast 目標（UpdateGeometry 留空的 Graphic）要靠它才點得到，
                // GraphicRaycaster 自己會再看 cull；-1 只留給沒被任何 Canvas 走到的
                cr.AbsoluteDepth = depth++;
                cr.RelativeDepth = cr.AbsoluteDepth;
                b.Items.Add(cr);
                b.Order.Add((cr, false));
            }
            var ch = t.Children;
            for (int i = 0; i < ch.Count; i++) Walk(ch[i], b, owner, ref depth);
            // Mask 的 pop：子物件畫完之後畫 pop material（還原 stencil）
            if (cr != null && cr.HasPopInstruction) b.Order.Add((cr, true));
        }
    }
}
