// 正確性閘（docs/perf-battle.md）：粒子網格的舊路徑，原封不動取自 22eb380 的 ParticleGeometry（只改類別名稱）。
// 只在診斷建置（PerfVerify=true → PERF_VERIFY）編進來，繪製端每格把新路徑的輸出跟這份逐位元組比對；驗完就拿掉。
#if PERF_VERIFY
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public static class ParticleGeometryOld
    {
        static readonly int[] StreamSize = { 3, 3, 4, 4, 2, 2, 2, 2, 1, 1, 3, 1, 1, 2, 3, 1, 3, 1, 3, 3, 1, 1, 1, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 1, 2, 3, 1, 1, 2 };

        /// <summary>m_VertexStreams 的十六進位字串 → 資料流編號</summary>
        public static int[] ParseStreams(string hex)
        {
            int[] def = { 0, 1, 3, 4 };
            if (string.IsNullOrEmpty(hex)) return def;
            var o = new List<int>();
            for (int i = 0; i + 1 < hex.Length; i += 2) o.Add(Convert.ToInt32(hex.Substring(i, 2), 16));
            return o.Count > 0 ? o.ToArray() : def;
        }

        public sealed class Layout
        {
            public int[] Streams;
            public readonly Dictionary<int, int> Offset = new Dictionary<int, int>();
            public int TexcoordCount;
        }

        public static Layout LayoutOf(int[] streams)
        {
            var l = new Layout { Streams = streams };
            int at = 0;
            foreach (var s in streams)
            {
                if (s <= 3) continue;
                l.Offset[s] = at;
                at += s < StreamSize.Length ? StreamSize[s] : 1;
            }
            l.TexcoordCount = (at + 3) / 4;
            return l;
        }

        static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        sealed class Builder
        {
            public int Vtx, Idx;
            public float[] Pos, Nrm, Col;
            public float[][] Tc;
            public int[] Ind;
            public readonly Layout L;
            public Builder(int maxV, int maxI, Layout l)
            {
                L = l;
                Pos = new float[maxV * 3]; Nrm = new float[maxV * 3]; Col = new float[maxV * 4];
                Tc = new float[Math.Max(1, l.TexcoordCount)][];
                for (int k = 0; k < Tc.Length; k++) Tc[k] = new float[maxV * 4];
                Ind = new int[maxI];
            }
            public void T(int v, int k, float value) => Tc[k >> 2][v * 4 + (k & 3)] = value;
            public void S(int v, int s, float a, float b = 0, float c = 0, float d = 0)
            {
                if (!L.Offset.TryGetValue(s, out var at)) return;
                int n = s < StreamSize.Length ? StreamSize[s] : 1;
                T(v, at, a); if (n > 1) T(v, at + 1, b); if (n > 2) T(v, at + 2, c); if (n > 3) T(v, at + 3, d);
            }
            public void Into(NMesh m)
            {
                void Ch(VertexAttribute a, float[] data, int dim)
                {
                    var arr = new float[Vtx * dim];
                    Array.Copy(data, arr, arr.Length);
                    m.Channels[(int)a] = new NMesh.Channel { Data = arr, Dim = dim, Format = VertexAttributeFormat.Float32 };
                }
                for (int i = 0; i < m.Channels.Length; i++) m.Channels[i] = null;
                Ch(VertexAttribute.Position, Pos, 3);
                Ch(VertexAttribute.Normal, Nrm, 3);
                Ch(VertexAttribute.Color, Col, 4);
                for (int k = 0; k < Tc.Length && k < 8; k++) Ch(VertexAttribute.TexCoord0 + k, Tc[k], 4);
                m.VertexCount = Vtx;
                m.Subs.Clear();
                var ind = new int[Idx];
                Array.Copy(Ind, ind, Idx);
                m.Subs.Add(new NMesh.Sub { Indices = ind });
                m.IndexFormat = Vtx > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                m.RecalculateBounds();
                m.Version++;
            }
        }

        public static Vector3 Size(ParticleSim sim, int i, bool withPose = true)
        {
            float t = sim.Age[i] / sim.Life[i];
            uint seed = sim.Seed[i];
            float x = sim.Sx[i], y = sim.Sy[i], z = sim.Sz[i];
            var sol = sim.Mod("SizeModule");
            if (sol.On)
            {
                float r = PRand.Hash01(seed, PRand.SizeOverLifetime);
                float kx = sol.C("curve").Eval(t, r);
                if (sol.F("separateAxes") == 1) { x *= kx; y *= sol.C("y").Eval(t, r); z *= sol.C("z").Eval(t, r); }
                else { x *= kx; y *= kx; z *= kx; }
            }
            var noise = sim.Mod("NoiseModule");
            if (noise.On)
            {
                float amt = noise.COrNull("sizeAmount")?.Eval(t, 0.5f) ?? noise.F("sizeAmount");
                if (amt != 0) { float k = 1 + sim.Nsz[i] * amt; x *= k; y *= k; z *= k; }
            }
            if (withPose && sim.Pose != null) { var s = sim.Pose.Value.SizeScale; x *= Math.Abs(s.x); y *= Math.Abs(s.y); z *= Math.Abs(s.z); }
            return new Vector3(x, y, z);
        }

        public static Color ColorOf(ParticleSim sim, int i)
        {
            var c = sim.Col[i];
            var col = sim.Mod("ColorModule");
            if (col.On && col.G("gradient") is MMGradient g) c *= g.Eval(sim.Age[i] / sim.Life[i], PRand.Hash01(sim.Seed[i], PRand.ColorOverLifetime));
            return c;
        }

        /// <summary>貼圖格：u0、v0、du、dv、blend、frame（沒開 Texture Sheet Animation 是整張）</summary>
        static void Sheet(ParticleSim sim, int i, float[] o)
        {
            var uv = sim.Mod("UVModule");
            if (!uv.On || (int)uv.F("mode") != 0) { o[0] = 0; o[1] = 0; o[2] = 1; o[3] = 1; o[4] = 0; o[5] = 0; return; }
            int tx = Math.Max(1, (int)uv.F("tilesX", 1)), ty = Math.Max(1, (int)uv.F("tilesY", 1));
            float t = sim.Age[i] / sim.Life[i];
            uint seed = sim.Seed[i];
            bool singleRow = (int)uv.F("animationType") == 1;
            int frames = singleRow ? tx : tx * ty;
            float cycles = uv.F("cycles", 1);
            float f = uv.C("frameOverTime").Eval(t, PRand.Hash01(seed, PRand.FrameOverTime)) * frames * cycles + uv.C("startFrame").Eval(t, PRand.Hash01(seed, PRand.StartFrame));
            float whole = MathF.Floor(f), blend = f - whole;
            int fi = (((int)whole % frames) + frames) % frames;
            int col, row;
            if (singleRow) { row = (int)uv.F("rowMode") == 1 ? (int)(PRand.Hash01(seed, PRand.Row) * ty) : (int)uv.F("rowIndex"); col = fi; }
            else { col = fi % tx; row = fi / tx; }
            o[0] = col / (float)tx; o[1] = 1 - (row + 1f) / ty; o[2] = 1f / tx; o[3] = 1f / ty; o[4] = blend; o[5] = singleRow ? row * tx + fi : fi;
        }

        static float ClampScreen(float size, Vector3 center, in PCam cam, float minF, float maxF)
        {
            float h;
            if (cam.Ortho) h = cam.OrthoSize * 2;
            else { float d = Math.Max(1e-4f, Vector3.Dot(center - cam.Position, cam.Forward)); h = 2 * d * MathF.Tan(cam.Fov * MathF.PI / 360); }
            if (h <= 0) return size;
            float f = size / h;
            return f > maxF ? maxF * h : f < minF ? minF * h : size;
        }

        static readonly float[] QCX = { -0.5f, 0.5f, 0.5f, -0.5f }, QCY = { -0.5f, -0.5f, 0.5f, 0.5f }, QU = { 0, 1, 1, 0 }, QV = { 0, 0, 1, 1 };

        /// <summary>粒子本身的網格寫進 target</summary>
        public static void Particles(ParticleSim sim, NParticleSystemRenderer pr, in PCam cam, bool linear, bool subtractEmitter, NMesh meshAsset, NMesh target)
        {
            var r = pr.R;
            int mode = (int)r.F("m_RenderMode");
            var layout = LayoutOf(pr.VertexStreams);
            int n = mode == 5 ? 0 : sim.N;
            var mesh = mode == 4 ? meshAsset : null;
            var mPos = mesh?.Channels[(int)VertexAttribute.Position];
            int perV = mesh != null && mPos != null ? mesh.VertexCount : 4;
            var mIdx = mesh != null && mesh.Subs.Count > 0 ? mesh.Subs[0].Indices : null;
            int perI = mIdx?.Length ?? 6;
            var b = new Builder(n * perV, n * perI, layout);
            if (n == 0 || (mode == 4 && (mesh == null || mPos == null || mIdx == null))) { b.Into(target); return; }
            int align = (int)r.F("m_RenderAlignment");
            var pivot = r.V("m_Pivot", Vector3.zero);
            var flip = r.V("m_Flip", Vector3.zero);
            float minF = r.F("m_MinParticleSize"), maxF = r.F("m_MaxParticleSize", 0.5f);
            float lengthScale = r.F("m_LengthScale", 2), velocityScale = r.F("m_VelocityScale");
            bool lin = linear && r.F("m_ApplyActiveColorSpace", 1) != 0;
            var uvMod = sim.Mod("UVModule");
            float uvFlipU = uvMod.On ? uvMod.F("flipU") : 0, uvFlipV = uvMod.On ? uvMod.F("flipV") : 0;
            var custom = sim.Mod("CustomDataModule");
            bool rot3D = sim.Initial.F("rotation3D") == 1;
            Vector3 sub = subtractEmitter && sim.Pose != null ? sim.Pose.Value.Position : Vector3.zero;
            // 畫的順序
            var order = new int[n];
            for (int k = 0; k < n; k++) order[k] = k;
            int sortMode = (int)r.F("m_SortMode");
            if (sortMode == 1)
            {
                var d = new float[n];
                for (int k = 0; k < n; k++) d[k] = (sim.WorldPos(k) - cam.Position).sqrMagnitude;
                Array.Sort(order, (a, c) => d[c].CompareTo(d[a]));
            }
            else if (sortMode == 2) Array.Sort(order, (a, c) => sim.Age[a].CompareTo(sim.Age[c]));
            else if (sortMode == 3) Array.Sort(order, (a, c) => sim.Age[c].CompareTo(sim.Age[a]));
            var frame = new float[6];
            bool hasUV = layout.Offset.TryGetValue(4, out int uvAt), hasCorner = layout.Offset.TryGetValue(11, out int cornerAt);
            float flipAx = Math.Abs(flip.x), flipAy = Math.Abs(flip.y);
            var mNrm = mesh?.Channels[(int)VertexAttribute.Normal];
            var mCol = mesh?.Channels[(int)VertexAttribute.Color];
            var mUv = mesh?.Channels[(int)VertexAttribute.TexCoord0];
            var cv = new float[4];
            void Cust(int slot, int i, float t)
            {
                cv[0] = cv[1] = cv[2] = cv[3] = 0;
                if (!custom.On) return;
                int m = (int)custom.F("mode" + slot);
                if (m == 1)
                {
                    int count = (int)custom.F("vectorComponentCount" + slot, 4);
                    for (int k = 0; k < count && k < 4; k++) cv[k] = custom.C("vector" + slot + "_" + k).Eval(t, PRand.Hash01(sim.Seed[i], PRand.Custom + slot * 8 + k));
                }
                else if (m == 2 && custom.G("color" + slot) is MMGradient g)
                {
                    var c = g.Eval(t, PRand.Hash01(sim.Seed[i], PRand.Custom + slot * 8));
                    cv[0] = c.r; cv[1] = c.g; cv[2] = c.b; cv[3] = c.a;
                }
            }
            for (int oi = 0; oi < n; oi++)
            {
                int i = order[oi];
                float t = sim.Age[i] / sim.Life[i];
                uint seed = sim.Seed[i];
                var center = sim.WorldPos(i);
                var size = Size(sim, i);
                var col = ColorOf(sim, i);
                if (lin) { col.r = ToLinear(col.r); col.g = ToLinear(col.g); col.b = ToLinear(col.b); }
                var velSim = new Vector3(sim.Vx[i] + sim.Avx[i], sim.Vy[i] + sim.Avy[i], sim.Vz[i] + sim.Avz[i]);
                var vel = !sim.WorldSpace && sim.Pose != null ? MathUtil.MulVector(sim.Pose.Value.ToWorld, velSim).normalized * velSim.magnitude : velSim;
                Sheet(sim, i, frame);
                bool flipU = (flipAx > 0 && PRand.Hash01(seed, PRand.Flip + 1) < flipAx) != (uvFlipU > 0 && PRand.Hash01(seed, PRand.Flip + 3) < uvFlipU);
                bool flipV = (flipAy > 0 && PRand.Hash01(seed, PRand.Flip + 2) < flipAy) != (uvFlipV > 0 && PRand.Hash01(seed, PRand.Flip + 4) < uvFlipV);
                int v0 = b.Vtx;
                if (mode == 4)
                {
                    Quaternion q = rot3D ? Quaternion.Euler(-sim.Rx[i] * Mathf.Rad2Deg, -sim.Ry[i] * Mathf.Rad2Deg, -sim.Rz[i] * Mathf.Rad2Deg) : MathUtil.AngleAxis(-sim.Rz[i] * Mathf.Rad2Deg, Vector3.forward);
                    if (align == 2 && sim.Pose != null) q = sim.Pose.Value.Rotation * q;
                    var trs = MathUtil.TRS(center, q, size);
                    for (int k = 0; k < mesh.VertexCount; k++)
                    {
                        var c = MathUtil.MulPoint(trs, new Vector3(mPos.Data[k * mPos.Dim], mPos.Data[k * mPos.Dim + 1], mPos.Dim > 2 ? mPos.Data[k * mPos.Dim + 2] : 0)) - sub;
                        int vi = b.Vtx++;
                        b.Pos[vi * 3] = c.x; b.Pos[vi * 3 + 1] = c.y; b.Pos[vi * 3 + 2] = c.z;
                        if (mNrm != null)
                        {
                            var nn = q * new Vector3(mNrm.Data[k * mNrm.Dim], mNrm.Data[k * mNrm.Dim + 1], mNrm.Data[k * mNrm.Dim + 2]);
                            b.Nrm[vi * 3] = nn.x; b.Nrm[vi * 3 + 1] = nn.y; b.Nrm[vi * 3 + 2] = nn.z;
                        }
                        for (int ch = 0; ch < 4; ch++) b.Col[vi * 4 + ch] = col[ch] * (mCol != null && mCol.Dim >= 4 ? mCol.Data[k * mCol.Dim + ch] : 1);
                        float mu = mUv != null ? mUv.Data[k * mUv.Dim] : 0, mv = mUv != null ? mUv.Data[k * mUv.Dim + 1] : 0;
                        if (hasUV) { b.T(vi, uvAt, frame[0] + mu * frame[2]); b.T(vi, uvAt + 1, frame[1] + mv * frame[3]); }
                    }
                    foreach (var ix in mIdx) b.Ind[b.Idx++] = v0 + ix;
                }
                else
                {
                    Vector3 right, up, nrm;
                    float w = size.x, h = size.y;
                    if (mode == 1)
                    {
                        float sp = vel.magnitude;
                        var dir = vel - cam.Forward * Vector3.Dot(vel, cam.Forward);
                        if (dir.sqrMagnitude < 1e-12f) dir = cam.Up;
                        dir = dir.normalized;
                        right = dir;
                        up = Vector3.Cross(cam.Forward, dir).normalized;
                        w = size.x * lengthScale + sp * velocityScale;
                        nrm = -cam.Forward;
                    }
                    else
                    {
                        if (mode == 2) { right = Vector3.right; up = Vector3.forward; nrm = Vector3.up; }
                        else if (mode == 3)
                        {
                            var toCam = cam.Position - center; toCam.y = 0;
                            if (toCam.sqrMagnitude < 1e-12f) toCam = -cam.Forward;
                            toCam = toCam.normalized;
                            up = Vector3.up; right = Vector3.Cross(up, toCam).normalized; nrm = toCam;
                        }
                        else if (align == 1) { right = Vector3.right; up = Vector3.up; nrm = Vector3.back; }
                        else if (align == 2 && sim.Pose != null) { var prot = sim.Pose.Value.Rotation; right = prot * Vector3.right; up = prot * Vector3.up; nrm = prot * Vector3.back; }
                        else if (align == 3)
                        {
                            var toCam = (cam.Position - center).normalized;
                            right = Vector3.Cross(cam.Up, toCam).normalized; up = Vector3.Cross(toCam, right).normalized; nrm = toCam;
                        }
                        else if (align == 4 && vel.sqrMagnitude > 1e-12f)
                        {
                            nrm = -vel.normalized; right = Vector3.Cross(cam.Up, nrm).normalized; up = Vector3.Cross(nrm, right).normalized;
                        }
                        else { right = cam.Right; up = cam.Up; nrm = -cam.Forward; }
                        if (minF > 0 || maxF < 10)
                        {
                            float s0 = Math.Max(w, h), s1 = ClampScreen(s0, center, cam, minF, maxF);
                            if (s1 != s0 && s0 > 0) { w *= s1 / s0; h *= s1 / s0; }
                        }
                        float ang = -sim.Rz[i];
                        if (rot3D && (sim.Rx[i] != 0 || sim.Ry[i] != 0))
                        {
                            var rq = MathUtil.FromBasis(right, up, nrm) * Quaternion.Euler(-sim.Rx[i] * Mathf.Rad2Deg, -sim.Ry[i] * Mathf.Rad2Deg, ang * Mathf.Rad2Deg);
                            right = rq * Vector3.right; up = rq * Vector3.up;
                        }
                        else if (ang != 0)
                        {
                            float c = MathF.Cos(ang), s = MathF.Sin(ang);
                            var rx = right;
                            right = right * c + up * s;
                            up = up * c - rx * s;
                        }
                    }
                    float px = pivot.x * w, py = pivot.y * h;
                    for (int k = 0; k < 4; k++)
                    {
                        var corner = center + right * (QCX[k] * w + px) + up * (QCY[k] * h + py) - sub;
                        int vi = b.Vtx++;
                        b.Pos[vi * 3] = corner.x; b.Pos[vi * 3 + 1] = corner.y; b.Pos[vi * 3 + 2] = corner.z;
                        b.Nrm[vi * 3] = nrm.x; b.Nrm[vi * 3 + 1] = nrm.y; b.Nrm[vi * 3 + 2] = nrm.z;
                        b.Col[vi * 4] = col.r; b.Col[vi * 4 + 1] = col.g; b.Col[vi * 4 + 2] = col.b; b.Col[vi * 4 + 3] = col.a;
                        float u = QU[k], v = QV[k];
                        if (flipU) u = 1 - u;
                        if (flipV) v = 1 - v;
                        if (hasUV) { b.T(vi, uvAt, frame[0] + u * frame[2]); b.T(vi, uvAt + 1, frame[1] + v * frame[3]); }
                        if (layout.Offset.ContainsKey(5))
                        {
                            var uvm = sim.Mod("UVModule");
                            int tx = Math.Max(1, (int)uvm.F("tilesX", 1)), ty = Math.Max(1, (int)uvm.F("tilesY", 1));
                            int nf = ((int)frame[5] + 1) % (tx * ty);
                            b.S(vi, 5, (nf % tx) / (float)tx + u * frame[2], 1 - (nf / tx + 1f) / ty + v * frame[3]);
                        }
                        if (hasCorner) b.T(vi, cornerAt, k);
                    }
                    b.Ind[b.Idx++] = v0; b.Ind[b.Idx++] = v0 + 3; b.Ind[b.Idx++] = v0 + 2;
                    b.Ind[b.Idx++] = v0; b.Ind[b.Idx++] = v0 + 2; b.Ind[b.Idx++] = v0 + 1;
                }
                if (layout.Offset.Count > 1)
                {
                    float sp = vel.magnitude;
                    for (int vv = v0; vv < b.Vtx; vv++)
                    {
                        b.S(vv, 8, frame[4]); b.S(vv, 9, frame[5]);
                        b.S(vv, 10, center.x - sub.x, center.y - sub.y, center.z - sub.z);
                        b.S(vv, 12, size.x); b.S(vv, 13, size.x, size.y); b.S(vv, 14, size.x, size.y, size.z);
                        b.S(vv, 15, sim.Rz[i]); b.S(vv, 16, sim.Rx[i], sim.Ry[i], sim.Rz[i]);
                        b.S(vv, 19, vel.x, vel.y, vel.z); b.S(vv, 20, sp); b.S(vv, 21, t); b.S(vv, 22, 1 / sim.Life[i]);
                        float s1 = PRand.Hash01(seed, 101), s2 = PRand.Hash01(seed, 102), s3 = PRand.Hash01(seed, 103), s4 = PRand.Hash01(seed, 104);
                        for (int st = 23; st <= 26; st++) b.S(vv, st, s1, s2, s3, s4);
                        for (int st = 27; st <= 30; st++) b.S(vv, st, UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value);
                        Cust(0, i, t); for (int st = 31; st <= 34; st++) b.S(vv, st, cv[0], cv[1], cv[2], cv[3]);
                        Cust(1, i, t); for (int st = 35; st <= 38; st++) b.S(vv, st, cv[0], cv[1], cv[2], cv[3]);
                        b.S(vv, 46, i);
                    }
                }
            }
            b.Into(target);
        }

        /// <summary>拖尾的網格（每顆粒子一條，加上粒子死掉後留下來的；Ribbon 模式照出生順序連成一條）</summary>
        public static void Trails(ParticleSim sim, NParticleSystemRenderer pr, in PCam cam, bool linear, bool subtractEmitter, NMesh target)
        {
            var r = pr.R;
            var trail = sim.Mod("TrailModule");
            var layout = LayoutOf(pr.TrailVertexStreams);
            if (!trail.On) { new Builder(0, 0, layout).Into(target); return; }
            bool lin = linear && r.F("m_ApplyActiveColorSpace", 1) != 0;
            Vector3 sub = subtractEmitter && sim.Pose != null ? sim.Pose.Value.Position : Vector3.zero;
            var strips = new List<(List<Vector3> pts, Color color, float size, float t, uint seed)>();
            Vector3 ToWorld(Vector3 p) => !sim.WorldSpace && trail.F("worldSpace") != 1 && sim.Pose != null ? MathUtil.MulPoint(sim.Pose.Value.ToWorld, p) : p;
            if ((int)trail.F("mode") == 1)
            {
                int count = Math.Max(1, (int)trail.F("ribbonCount", 1));
                var order = new int[sim.N];
                for (int k = 0; k < sim.N; k++) order[k] = k;
                Array.Sort(order, (a, c) => sim.Id[a].CompareTo(sim.Id[c]));
                for (int g = 0; g < count; g++)
                {
                    var pts = new List<Vector3>();
                    Color color = Color.white; float size = 0;
                    for (int k = g; k < order.Length; k += count)
                    {
                        int i = order[k];
                        pts.Add(sim.WorldPos(i)); color = ColorOf(sim, i); size = Size(sim, i).x;
                    }
                    if (pts.Count > 1) strips.Add((pts, color, size, 0, (uint)g));
                }
            }
            else
            {
                for (int i = 0; i < sim.N; i++)
                {
                    var tp = sim.Trails[i];
                    if (tp == null || tp.Count == 0) continue;
                    var pts = new List<Vector3>();
                    foreach (var p in tp) pts.Add(ToWorld(p.P));
                    pts.Add(sim.WorldPos(i));
                    strips.Add((pts, ColorOf(sim, i), Size(sim, i).x, sim.Age[i] / sim.Life[i], sim.Seed[i]));
                }
                foreach (var o in sim.Orphans)
                {
                    var pts = new List<Vector3>();
                    foreach (var p in o.Points) pts.Add(ToWorld(p.P));
                    strips.Add((pts, o.Color, o.Size, 1, 0));
                }
            }
            int total = 0;
            foreach (var s in strips) total += s.pts.Count;
            var b = new Builder(total * 2, total * 6, layout);
            foreach (var s in strips)
            {
                int m = s.pts.Count;
                if (m < 2) continue;
                int v0 = b.Vtx;
                Vector3 lastSide = cam.Right;
                for (int k = 0; k < m; k++)
                {
                    float u = 1 - k / (float)(m - 1);
                    var p = s.pts[k];
                    var seg = s.pts[Math.Min(m - 1, k + 1)] - s.pts[Math.Max(0, k - 1)];
                    var toCam = cam.Position - p;
                    var side = Vector3.Cross(seg, toCam);
                    // 兩點重合時沿用前一個點的側向（改用 cam.right 會轉 90 度，那一段的三角形反過來）
                    if (side.sqrMagnitude < 1e-12f) side = k > 0 ? lastSide : cam.Right;
                    side = side.normalized;
                    lastSide = side;
                    float width = trail.C("widthOverTrail").Eval(u, PRand.Hash01(s.seed, PRand.TrailWidth));
                    if (trail.F("sizeAffectsWidth", 1) != 0) width *= s.size;
                    var cTrail = trail.G("colorOverTrail")?.Eval(u, PRand.Hash01(s.seed, PRand.TrailColor)) ?? Color.white;
                    var cLife = trail.G("colorOverLifetime")?.Eval(s.t, PRand.Hash01(s.seed, PRand.TrailColor + 1)) ?? Color.white;
                    var nrm = toCam.normalized;
                    for (int side2 = 0; side2 < 2; side2++)
                    {
                        int vi = b.Vtx++;
                        var c = p + side * ((side2 == 0 ? -0.5f : 0.5f) * width) - sub;
                        b.Pos[vi * 3] = c.x; b.Pos[vi * 3 + 1] = c.y; b.Pos[vi * 3 + 2] = c.z;
                        b.Nrm[vi * 3] = nrm.x; b.Nrm[vi * 3 + 1] = nrm.y; b.Nrm[vi * 3 + 2] = nrm.z;
                        for (int ch = 0; ch < 4; ch++)
                        {
                            float v = (trail.F("inheritParticleColor", 1) != 0 ? s.color[ch] : 1) * cTrail[ch] * cLife[ch];
                            if (lin && ch < 3) v = ToLinear(v);
                            b.Col[vi * 4 + ch] = v;
                        }
                        b.S(vi, 4, u, side2);
                    }
                    if (k > 0)
                    {
                        // 面向攝影機看順時針（Unity 的正面）：−0.5 那排在右、+0.5 那排在左
                        int a0 = v0 + (k - 1) * 2, a1 = a0 + 1, c0 = v0 + k * 2, c1 = c0 + 1;
                        b.Ind[b.Idx++] = a0; b.Ind[b.Idx++] = c1; b.Ind[b.Idx++] = c0;
                        b.Ind[b.Idx++] = a0; b.Ind[b.Idx++] = a1; b.Ind[b.Idx++] = c1;
                    }
                }
            }
            b.Into(target);
        }
    }
}
#endif
