// 寬版的畫外物件（Diceomancer 的手機寬版，網頁版 render/sceneRenderer.ts 的 outsideSafeArea）：原版只看得到 16:9，
// 畫面拉寬之後，原本擺在 16:9 外面的東西（設定面板的裝飾卡、CardWarning 的 Purple Mist 提示…）會跑進來。
// 正交攝影機底下，包圍盒在攝影機座標裡整個落在中間 Aspect 寬度以外的就不畫；glue 登記的節點（加寬的 Canvas、推到兩側的 HUD、地圖）連同子物件免裁。
// Aspect ＝ 0 時整個關掉（預設；MT2 與 Diceomancer 的非寬版不受影響）。
// 直向（Diceomancer 的手機直向版面，網頁版 game/portraitLayout.ts 的 outsidePortraitBand）：攝影機的可見高度比原版高，原版 16:9 上下外面的東西會露出來，
// glue 設 Band（原版 16:9 的高佔攝影機可見高的比例，＝ 畫面寬高比）之後，上下整個落在範圍外的也不畫，UI 元素的射線也打不到
//（CanvasRenderer.absoluteDepth 回 −1，原版的 GraphicRaycaster 跳過）。Band ＝ 0 時（預設）算法跟以前逐行相同。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public static class SafeAreaCull
    {
        public static float Aspect;
        /// <summary>上下的範圍：佔攝影機可見半高的比例（0 ＝ 不裁上下，預設；網頁版 computeWideCull 的 limY）。打開時左右的範圍也照這個半高算（寬 ＝ 半高 × Aspect）</summary>
        public static float Band;

        static float HalfHeight(NCamera cam) => Band > 0 ? cam.OrthoSize * Band : cam.OrthoSize;
        static readonly HashSet<NTransform> exempt = new HashSet<NTransform>();

        static readonly HashSet<NTransform> barrier = new HashSet<NTransform>();

        public static void Exempt(Transform t) { var n = Native.Of<NTransform>(t); if (n != null) exempt.Add(n); }
        public static void Unexempt(Transform t) { var n = Native.Of<NTransform>(t); if (n != null) exempt.Remove(n); }
        /// <summary>往上找免裁時到這裡就停（設定面板：置中的彈出視窗，停在畫面外的裝飾卡照樣裁）</summary>
        public static void Barrier(Transform t) { var n = Native.Of<NTransform>(t); if (n != null) barrier.Add(n); }

        static bool Exempted(NTransform t)
        {
            for (var x = t; x != null; x = x.Parent)
            {
                if (barrier.Contains(x)) return false;
                if (exempt.Contains(x)) return true;
            }
            return false;
        }

        static bool OutsideX(NCamera cam, float minX, float maxX)
        {
            float h = HalfHeight(cam) * Aspect;
            return maxX < -h || minX > h;
        }

        static bool OutsideY(NCamera cam, float minY, float maxY) => Band > 0 && (maxY < -HalfHeight(cam) || minY > HalfHeight(cam));

        /// <summary>renderer 的世界包圍盒整個在中間 16:9 外</summary>
        public static bool Outside(NCamera cam, NRenderer r)
        {
            if (Aspect <= 0 || !cam.Orthographic || Exempted(r.Tr)) return false;
            var b = r.WorldBounds;
            if (b.size == Vector3.zero) return false;
            var m = cam.WorldToCamera;
            float mn = float.MaxValue, mx = float.MinValue, yn = float.MaxValue, yx = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                var c = m.MultiplyPoint3x4(p);
                mn = Math.Min(mn, c.x); mx = Math.Max(mx, c.x);
                yn = Math.Min(yn, c.y); yx = Math.Max(yx, c.y);
            }
            return OutsideX(cam, mn, mx) || OutsideY(cam, yn, yx);
        }

        /// <summary>uGUI 元素（網格頂點）整個在中間 16:9 外</summary>
        public static bool Outside(NCamera cam, NCanvasRenderer cr)
        {
            if (Aspect <= 0 || cam == null || !cam.Orthographic || cr.Geo == null || cr.Geo.VertexCount == 0 || Exempted(cr.Tr)) return false;
            var g = cr.Geo;
            float lx0 = float.MaxValue, lx1 = float.MinValue, ly0 = float.MaxValue, ly1 = float.MinValue;
            for (int i = 0; i < g.VertexCount; i++)
            {
                float x = g.Pos[i * 3], y = g.Pos[i * 3 + 1];
                lx0 = Math.Min(lx0, x); lx1 = Math.Max(lx1, x); ly0 = Math.Min(ly0, y); ly1 = Math.Max(ly1, y);
            }
            var m = MathUtil.Mul(cam.WorldToCamera, cr.Tr.LocalToWorld);
            float mn = float.MaxValue, mx = float.MinValue, yn = float.MaxValue, yx = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var c = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? lx0 : lx1, (i & 2) == 0 ? ly0 : ly1, 0));
                mn = Math.Min(mn, c.x); mx = Math.Max(mx, c.x);
                yn = Math.Min(yn, c.y); yx = Math.Max(yx, c.y);
            }
            return OutsideX(cam, mn, mx) || OutsideY(cam, yn, yx);
        }

        /// <summary>
        /// UI 元素的 RectTransform 四角整個在上下範圍外（Band 打開時才算；網頁版 outsidePortraitBand，engine/events.ts 的射線用）：
        /// CanvasRenderer.absoluteDepth 回 −1，原版的 GraphicRaycaster（GraphicRaycaster.cs:280 的 depth != −1）就跳過。看主攝影機，免裁的規則同繪製
        /// </summary>
        public static bool OutsideBand(NCanvasRenderer cr)
        {
            if (Band <= 0 || cr?.Tr == null || Exempted(cr.Tr)) return false;
            var cam = NCamera.Main();
            if (cam == null || !cam.Orthographic) return false;
            var r = cr.Tr.Rect;
            var m = MathUtil.Mul(cam.WorldToCamera, cr.Tr.LocalToWorld);
            float yn = float.MaxValue, yx = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                float y = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? r.xMin : r.xMax, (i & 2) == 0 ? r.yMin : r.yMax, 0)).y;
                yn = Math.Min(yn, y); yx = Math.Max(yx, y);
            }
            return OutsideY(cam, yn, yx);
        }
    }
}
