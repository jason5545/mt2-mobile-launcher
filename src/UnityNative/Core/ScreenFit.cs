// 視窗與遊戲看到的畫面大小（Screen.width／height、攝影機、Canvas）分開：遊戲可以要求一個解析度（Screen.SetResolution），
// 全螢幕時 Unity 把它等比縮進螢幕、旁邊補黑邊。原版 Diceomancer 的 AspectRatioHandler 一開就 SetResolution(寬, 寬 ÷ 16:9)，
// 21:9 的螢幕上是 16:9 置中、左右黑邊。這裡照做，但直接用螢幕上的像素大小畫（不先畫成要求的解析度再縮）：比例一樣，手機少畫很多像素；
// Screen.width／height 回的是實際畫的大小（跟 Unity 回要求的解析度不同，只差在數字，比例相同）。
// 預設關（MT2 的畫質設定也會 SetResolution，它的原生版一直是填滿視窗）：遊戲的 glue 打開（Diceomancer：native/glue/Glue.Dice/Wide.cs）。
//   Fixed：照最後一次 SetResolution 的比例；Range：比例夾在 MinAspect～MaxAspect（Diceomancer 的手機寬版，Jason 要求的改動）。
// 輸入：宿主給的是視窗座標，InputState 扣掉黑邊的位移。
using System;

namespace UnityNative
{
    public static class ScreenFit
    {
        public enum FitMode { Window, Fixed, Range }
        public static FitMode Mode = FitMode.Window;
        public static float MinAspect = 16f / 9f, MaxAspect = 2.4f;
        /// <summary>最後一次 Screen.SetResolution 要的大小（0 ＝ 沒要過）</summary>
        public static int RequestedWidth, RequestedHeight;
        /// <summary>畫面在視窗裡的位置與大小（像素，左下原點）</summary>
        public static int OffsetX, OffsetY, Width, Height;

        /// <summary>每格開頭：照視窗大小與模式算出畫面大小，寫進 Config.ScreenWidth／ScreenHeight</summary>
        public static void Update()
        {
            var c = Engine.Config;
            if (c == null) return;
            if (c.WindowWidth <= 0 || c.WindowHeight <= 0) { c.WindowWidth = c.ScreenWidth; c.WindowHeight = c.ScreenHeight; }
            int ww = c.WindowWidth, wh = c.WindowHeight, w = ww, h = wh;
            float win = (float)ww / Math.Max(1, wh);
            float aspect = win;
            if (Mode == FitMode.Fixed && RequestedWidth > 0 && RequestedHeight > 0) aspect = (float)RequestedWidth / RequestedHeight;
            else if (Mode == FitMode.Range) aspect = Math.Clamp(win, MinAspect, MaxAspect);
            if (Math.Abs(aspect - win) > 1e-4f)
            {
                if (win > aspect) { h = wh; w = (int)Math.Round(wh * aspect); }
                else { w = ww; h = (int)Math.Round(ww / aspect); }
            }
            OffsetX = (ww - w) / 2; OffsetY = (wh - h) / 2; Width = w; Height = h;
            c.ScreenWidth = w; c.ScreenHeight = h;
        }

        public static float MapX(float x) => x - OffsetX;
        public static float MapY(float y) => y - OffsetY;
    }
}
