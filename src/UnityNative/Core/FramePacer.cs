// 照 Unity 的規則限制每秒格數（宿主每次 vsync 都問一次要不要跑遊戲；不跑的那次只重貼上一格，GlGpu.Present）：
//   QualitySettings.vSyncCount > 0：每個 vblank 都跑（vSyncCount 2 以上：每 vSyncCount 個）；
//   否則 Application.targetFrameRate > 0：每 1／targetFrameRate 秒跑一格；−1 不限。
// 照時間排、不照 vsync 次數：手機的更新率會自己變（HyperOS 觸控中 120Hz、閒置 1.1 秒降到 60Hz，app 要的更新率會被無視，網頁版 10/15 查到的）。
// Diceomancer 原版預設 maxFps 60、vSync 關（PauseMenu.ApplyVisuals 開機就套），設定裡選 120 才跑 120（同網頁版）。
namespace UnityNative
{
    public sealed class FramePacer
    {
        double nextDue = double.NegativeInfinity, lastCall = double.NaN, vsync = 1.0 / 60.0;
        int calls;

        /// <summary>now：秒（單調遞增）</summary>
        public bool ShouldRun(double now)
        {
            if (!double.IsNaN(lastCall))
            {
                double d = now - lastCall;
                if (d > 0 && d < 0.1) vsync = vsync * 0.9 + d * 0.1;
            }
            lastCall = now;
            int vs = Icalls.UnityEngine_QualitySettings.get_vSyncCount();
            if (vs > 0) return ++calls % vs == 0;
            int target = Engine.TargetFrameRate;
            if (target <= 0) return true;
            double interval = 1.0 / target;
            if (now < nextDue - vsync * 0.5) return false;
            nextDue = now - nextDue > interval ? now + interval : nextDue + interval;
            return true;
        }
    }
}
