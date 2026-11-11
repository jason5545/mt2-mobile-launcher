// 正確性閘（docs/perf-battle.md）：換掉計算方式的地方，診斷建置裡每次都同時跑舊路徑，輸出逐位元組比對。
// 只在 PerfVerify=true 的建置編進來（PERF_VERIFY）；宿主每 300 格把這一窗與累計的比對次數、不一致次數寫進 log。
#if PERF_VERIFY
using System;
using System.Runtime.InteropServices;

namespace UnityNative
{
    public static class Verify
    {
        static int winChecks, winBad, allChecks, allBad;
        static string first;
        static readonly NMesh oldMesh = new NMesh { Name = "verify" };

        public static UnityEngine.Random.State RandomState()
        {
            Icalls.UnityEngine_Random.get_state_Injected(out var s);
            return s;
        }

        /// <summary>粒子與拖尾：新路徑（Builder）已經跑完；亂數設回新路徑之前，跑舊路徑（產 NMesh），比頂點數、每個通道、索引、亂數用量。
        /// 比完亂數留在新路徑之後的狀態</summary>
        public static void Particles(UnityEngine.Random.State before, ParticleGeometry.Builder b, Action<NMesh> old)
        {
            Icalls.UnityEngine_Random.get_state_Injected(out var afterNew);
            var s = before;
            Icalls.UnityEngine_Random.set_state_Injected(ref s);
            old(oldMesh);
            Icalls.UnityEngine_Random.get_state_Injected(out var afterOld);
            var diff = Compare(oldMesh, b);
            if (diff == null && (afterNew.s0 != afterOld.s0 || afterNew.s1 != afterOld.s1 || afterNew.s2 != afterOld.s2 || afterNew.s3 != afterOld.s3)) diff = "亂數用量不同";
            Count(diff);
            Icalls.UnityEngine_Random.set_state_Injected(ref afterNew);
        }

        static void Count(string diff)
        {
            winChecks++; allChecks++;
            if (diff == null) return;
            winBad++; allBad++;
            first ??= $"第 {Engine.Time.FrameCount} 格：{diff}";
        }

        static string Compare(NMesh m, ParticleGeometry.Builder b)
        {
            if (m.VertexCount != b.Vtx) return $"頂點數 {m.VertexCount}／{b.Vtx}";
            for (int i = 0; i < m.Channels.Length; i++)
            {
                float[] data = null; int dim = 0;
                if (i == 0) { data = b.Pos; dim = 3; }
                else if (i == 1) { data = b.Nrm; dim = 3; }
                else if (i == 3) { data = b.Col; dim = 4; }
                else if (i >= 4 && i < 12 && i - 4 < b.TcCount) { data = b.Tc[i - 4]; dim = 4; }
                var c = m.Channels[i];
                if ((c == null) != (data == null)) return $"通道 {i} 有無不同";
                if (c == null) continue;
                if (c.Dim != dim) return $"通道 {i} 維度 {c.Dim}／{dim}";
                if (!MemoryMarshal.AsBytes(c.Data.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(data.AsSpan(0, b.Vtx * dim)))) return $"通道 {i} 內容不同";
            }
            if (m.Subs.Count != 1) return $"子網格 {m.Subs.Count} 個";
            if (!MemoryMarshal.AsBytes(m.Subs[0].Indices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.Ind.AsSpan(0, b.Idx)))) return "索引不同";
            return null;
        }

        /// <summary>每 300 格叫一次：這一窗與累計的比對結果（沒比對過回 null）</summary>
        public static string Report()
        {
            if (allChecks == 0) return null;
            var r = $"  [verify] 粒子新舊路徑：這窗比對 {winChecks} 次、不一致 {winBad} 次；累計 {allChecks} 次、不一致 {allBad} 次" + (first != null ? "；第一個：" + first : "");
            winChecks = winBad = 0;
            return r;
        }
    }
}
#endif
