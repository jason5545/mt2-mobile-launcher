// 效能量測（docs/perf-battle.md）：宿主每 300 格那行的 GC 欄位、慢格紀錄、ctl「gc」的 GC 資訊。都是只讀的計數，不改遊戲行為。
// 配置來源看分段計時的「配置（每格平均 KB）」（GC.GetAllocatedBytesForCurrentThread）：GC 的 AllocationTick 事件在 .NET for Android 收不到（EventSource 預設關掉）。
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityNative
{
    public static class PerfDiag
    {
        // ───────── 每窗的 GC ─────────

        static int winG0, winG1, winG2;
        static long winAlloc;
        static TimeSpan winPause;

        /// <summary>「GC a／b／c 次（這窗暫停 X ms、配置 Y MB）」：累計的 gen0／gen1／gen2 次數（CollectionCount(1) 含 gen2），括號裡是跟上一窗的差</summary>
        public static string WindowGc()
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long alloc = GC.GetTotalAllocatedBytes(false);
            var pause = GC.GetTotalPauseDuration();
            var s = $"GC {g0}／{g1}／{g2} 次（這窗暫停 {(pause - winPause).TotalMilliseconds:F0} ms、配置 {(alloc - winAlloc) / 1048576.0:F1} MB）";
            winG0 = g0; winG1 = g1; winG2 = g2; winAlloc = alloc; winPause = pause;
            return s;
        }

        // ───────── 慢格 ─────────

        /// <summary>超過這個毫秒數的格記一行（一窗最多 SlowMax 行）</summary>
        public static double SlowMs = 50;
        const int SlowMax = 8;
        static int fG0, fG1, fG2, slowInWindow;
        static TimeSpan fPause;

        public static void FrameBegin()
        {
            fG0 = GC.CollectionCount(0); fG1 = GC.CollectionCount(1); fG2 = GC.CollectionCount(2);
            fPause = GC.GetTotalPauseDuration();
        }

        /// <summary>這格超過 SlowMs 就回一行說明（GC 次數與暫停、這格最久的三段）；沒有就回 null</summary>
        public static string FrameEnd(int frame, double ms)
        {
            if (ms < SlowMs || slowInWindow >= SlowMax) return null;
            slowInWindow++;
            int d0 = GC.CollectionCount(0) - fG0, d1 = GC.CollectionCount(1) - fG1, d2 = GC.CollectionCount(2) - fG2;
            double pause = (GC.GetTotalPauseDuration() - fPause).TotalMilliseconds;
            var top = Engine.LastFrameLaps.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {kv.Value:F1}");
            string kind = d2 > 0 ? "gen2" : d1 > 0 ? "gen1" : d0 > 0 ? "gen0" : "無 GC";
            return $"  慢格：第 {frame} 格 {ms:F1} ms，GC +{d0}／+{d1}／+{d2}（{kind}）暫停 {pause:F1} ms；最久：{string.Join("、", top)}";
        }

        public static void WindowEnd() => slowInWindow = 0;

        /// <summary>每 300 格那行之後另外要寫的量測行（診斷建置的正確性閘比對）；沒有回 null</summary>
        public static string WindowExtra()
        {
#if PERF_VERIFY
            return Verify.Report();
#else
            return null;
#endif
        }

        /// <summary>ctl「gc」：最近一次各種 GC 的 heap 大小與暫停</summary>
        public static string GcInfo()
        {
            var lines = new List<string>();
            foreach (var kind in new[] { GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background })
            {
                var i = GC.GetGCMemoryInfo(kind);
                if (i.Index == 0) { lines.Add($"  [gc] {kind}：沒有"); continue; }
                var gens = string.Join("、", i.GenerationInfo.ToArray().Select((g, k) => $"{(k < 3 ? "gen" + k : k == 3 ? "LOH" : "POH")} {g.SizeBeforeBytes / 1048576.0:F1}→{g.SizeAfterBytes / 1048576.0:F1}"));
                lines.Add($"  [gc] {kind} #{i.Index} gen{i.Generation}：heap {i.HeapSizeBytes / 1048576.0:F0} MB、committed {i.TotalCommittedBytes / 1048576.0:F0} MB、碎片 {i.FragmentedBytes / 1048576.0:F0} MB、" +
                    $"promoted {i.PromotedBytes / 1048576.0:F1} MB、暫停 {string.Join("+", i.PauseDurations.ToArray().Select(p => p.TotalMilliseconds.ToString("F1")))} ms（{i.PauseTimePercentage:F1}%）；{gens}");
            }
            var any = GC.GetGCMemoryInfo();
            lines.Add($"  [gc] 總配置 {GC.GetTotalAllocatedBytes() / 1048576.0:F0} MB、總暫停 {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms、次數 {GC.CollectionCount(0)}／{GC.CollectionCount(1)}／{GC.CollectionCount(2)}、" +
                $"high load {any.HighMemoryLoadThresholdBytes / 1048576.0:F0} MB、可用 {any.TotalAvailableMemoryBytes / 1048576.0:F0} MB、GetTotalMemory {GC.GetTotalMemory(false) / 1048576.0:F0} MB");
            return string.Join("\n", lines);
        }
    }
}
