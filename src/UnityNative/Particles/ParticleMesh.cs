// 粒子的網格：ParticleSystemRenderer（繪製端每台攝影機畫之前產一次）與 BakeMesh／BakeTrailsMesh（UIParticle）共用。
// 照 TS 版（unity-cs2ts/runtime/src/particles/particleMesh.ts），依據 Unity 2022.3 手冊 Renderer module、ParticleSystemRenderer 腳本 API：
//   - renderMode：0 Billboard、1 Stretch（沿速度拉長，長度 ＝ 大小 × lengthScale ＋ 速度 × velocityScale）、2 HorizontalBillboard、
//     3 VerticalBillboard、4 Mesh、5 None（不畫粒子，拖尾照畫）。renderAlignment：0 View、1 World、2 Local、3 Facing、4 Velocity。
//   - 大小 ＝ 起始大小 × Size over Lifetime × 發射器縮放；顏色 ＝ 起始顏色 × Color over Lifetime；
//     Linear 色彩空間且 m_ApplyActiveColorSpace 時頂點色轉線性（BakeMesh 保留 gamma，由 Canvas 的線性流程轉）。
//   - min／maxParticleSize：畫面上的大小（佔 viewport 高度的比例）夾在這之間。sortMode：0 照順序、1 由遠到近、2 老的最後、3 新的最後。
//   - 自訂頂點資料流：Position、Normal、Tangent、Color 有自己的語意，其他依序塞進 TEXCOORD0、TEXCOORD1…（每個 4 個分量）。
//     m_VertexStreams 是位元組的十六進位字串；匯入時只含數字的被讀成整數、開頭的 0 不見了，這裡補回來。
//   - Texture Sheet Animation：格子從左上角開始；frameOverTime × 格數 × cycles；SingleRow 照 rowIndex 或隨機一列。
//   - 粒子的旋轉：正的角度是順時針（未查證）；3D 旋轉照 Unity 的 ZXY。三角形照 Unity 的正面（面向攝影機看順時針）。
//   - 拖尾：面向攝影機的長條，寬 ＝ widthOverTrail ×（sizeAffectsWidth 時乘粒子大小），
//     顏色 ＝（inheritParticleColor 時乘粒子顏色）× colorOverLifetime × colorOverTrail；uv.x 頭 0 尾 1。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public struct PCam
    {
        public Vector3 Position, Right, Up, Forward;
        public bool Ortho;
        public float Fov, OrthoSize;

        public static PCam Of(NCamera c)
        {
            if (c == null) return new PCam { Position = new Vector3(0, 0, -10), Right = Vector3.right, Up = Vector3.up, Forward = Vector3.forward, Fov = 60, OrthoSize = 5 };
            var q = c.Tr.Rotation;
            return new PCam { Position = c.Tr.Position, Right = q * Vector3.right, Up = q * Vector3.up, Forward = q * Vector3.forward, Ortho = c.Orthographic, Fov = c.Fov, OrthoSize = c.OrthoSize };
        }
    }

    public sealed class NParticleSystemRenderer : NRenderer
    {
        /// <summary>Renderer 的欄位（同 NParticleSystem.Comp：用到才解析）</summary>
        public PMod R
        {
            get => r ??= rRaw != null ? PMod.Parse(global::System.Text.Json.JsonDocument.Parse(rRaw).RootElement) : new PMod();
            set => r = value;
        }
        PMod r;
        byte[] rRaw;
        public Mesh ParticleMesh;
        /// <summary>m_VertexStreams、m_TrailVertexStreams 的原始值（十六進位字串；只含數字的被匯入成整數）</summary>
        public string Streams, TrailStreams;
        /// <summary>繪製端每台攝影機產的頂點（粒子、拖尾各一份，陣列與 GL 緩衝都重用，不再每格產一份 NMesh）</summary>
        public ParticleGeometry.Builder DrawBuilder, DrawTrailBuilder;
        public object DrawGpu, DrawTrailGpu;
        (ParticleGeometry.Layout l, float custom, string streams) layoutCache, trailLayoutCache;

        /// <summary>LayoutOf(VertexStreams／TrailVertexStreams)：m_UseCustom…VertexStreams 與資料流字串沒變就用上次算的（Layout 建好之後不會改）</summary>
        public ParticleGeometry.Layout CachedLayout(bool trail)
        {
            float custom = R.F(trail ? "m_UseCustomTrailVertexStreams" : "m_UseCustomVertexStreams");
            var streams = trail ? TrailStreams : Streams;
            ref var c = ref trail ? ref trailLayoutCache : ref layoutCache;
            if (c.l == null || c.custom != custom || !ReferenceEquals(c.streams, streams))
                c = (ParticleGeometry.LayoutOf(trail ? TrailVertexStreams : VertexStreams), custom, streams);
            return c.l;
        }

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            if (El.ValueKind == global::System.Text.Json.JsonValueKind.Object) { rRaw = global::System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(El).ToArray(); r = null; }
            else R = PMod.Parse(d);
            ParticleMesh = Serializer.Ref(d["m_Mesh"], typeof(Mesh), ctx) as Mesh;
            Streams = RawStreams(d["m_VertexStreams"]);
            TrailStreams = RawStreams(d["m_TrailVertexStreams"]);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NParticleSystemRenderer)src;
            if (s.r != null) R = s.r.Clone();
            else { rRaw = s.rRaw; r = null; }
            ParticleMesh = s.ParticleMesh;
            Streams = s.Streams; TrailStreams = s.TrailStreams;
        }

        public NParticleSystem System => Go?.Get<NParticleSystem>();

        /// <summary>字串照原樣；被讀成數字的補回開頭的 0（Position 一定在最前面，所以開頭是 00）</summary>
        static string RawStreams(JsonNode n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue<string>(out var s)) return s;
            var hex = v.ToJsonString().Split('.')[0];
            if (hex.Length % 2 == 1) hex = "0" + hex;
            if (!hex.StartsWith("00")) hex = "00" + hex;
            return hex;
        }

        public int[] VertexStreams => R.F("m_UseCustomVertexStreams") == 1 ? ParticleGeometry.ParseStreams(Streams) : new[] { 0, 1, 3, 4 };
        public int[] TrailVertexStreams => R.F("m_UseCustomTrailVertexStreams") == 1 ? ParticleGeometry.ParseStreams(TrailStreams) : new[] { 0, 1, 3, 4 };
    }

    public static class ParticleGeometry
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
            /// <summary>同 Offset，用資料流編號直接索引（沒有的是 −1）：Builder.S 每個頂點、每個資料流都要查</summary>
            public int[] At = Array.Empty<int>();
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
            int max = -1;
            foreach (var s in l.Offset.Keys) max = Math.Max(max, s);
            l.At = new int[max + 1];
            Array.Fill(l.At, -1);
            foreach (var kv in l.Offset) l.At[kv.Key] = kv.Value;
            return l;
        }

        static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>產頂點的暫存：陣列留著下一格重用（只會變大），Reset 把這次要用的範圍清成 0（沒寫到的值跟新配置的陣列一樣是 0）。
        /// 用到的是前 Vtx 個頂點、前 Idx 個索引；Into 照這個長度複製成 NMesh（BakeMesh 用），繪製端直接從這裡上傳</summary>
        public sealed class Builder
        {
            public int Vtx, Idx;
            public float[] Pos = Array.Empty<float>(), Nrm = Array.Empty<float>(), Col = Array.Empty<float>();
            public float[][] Tc = Array.Empty<float[]>();
            /// <summary>這次用到的 TEXCOORD 組數（Tc 可能比這個長）</summary>
            public int TcCount;
            public int[] Ind = Array.Empty<int>();
            public Layout L;

            public void Reset(int maxV, int maxI, Layout l)
            {
                L = l;
                Vtx = Idx = 0;
                Clear(ref Pos, maxV * 3); Clear(ref Nrm, maxV * 3); Clear(ref Col, maxV * 4);
                TcCount = Math.Max(1, l.TexcoordCount);
                if (Tc.Length < TcCount) Array.Resize(ref Tc, TcCount);
                for (int k = 0; k < TcCount; k++) Clear(ref Tc[k], maxV * 4);
                if (Ind.Length < maxI) Ind = new int[Math.Max(maxI, Ind.Length * 2)];
            }

            static void Clear(ref float[] a, int n)
            {
                if (a == null || a.Length < n) a = new float[Math.Max(n, (a?.Length ?? 0) * 2)];
                else Array.Clear(a, 0, n);
            }
            public void T(int v, int k, float value) => Tc[k >> 2][v * 4 + (k & 3)] = value;
            public void S(int v, int s, float a, float b = 0, float c = 0, float d = 0)
            {
                if ((uint)s >= (uint)L.At.Length) return;
                int at = L.At[s];
                if (at < 0) return;
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
                for (int k = 0; k < TcCount && k < 8; k++) Ch(VertexAttribute.TexCoord0 + k, Tc[k], 4);
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

        /// <summary>一次產頂點（Fill、FillTrails）裡每顆粒子都要讀的模組欄位：開頭照名字查一次（以前每顆粒子查好幾次字典），
        /// 產頂點的期間這些值不會變</summary>
        sealed class Mods
        {
            public bool SizeOn, SizeSeparate, NoiseOn, SheetOn, SingleRow;
            public MMCurve SizeCurve, SizeY, SizeZ, NoiseSizeCurve, FrameOverTime, StartFrame;
            public float NoiseSizeAmount, Cycles;
            public MMGradient ColorGradient;
            public int TilesX, TilesY, RowMode, RowIndex;

            public Mods Of(ParticleSim sim)
            {
                var sol = sim.Mod("SizeModule");
                SizeOn = sol.On;
                SizeCurve = sol.C("curve"); SizeSeparate = sol.F("separateAxes") == 1; SizeY = sol.C("y"); SizeZ = sol.C("z");
                var noise = sim.Mod("NoiseModule");
                NoiseOn = noise.On;
                NoiseSizeCurve = noise.COrNull("sizeAmount"); NoiseSizeAmount = noise.F("sizeAmount");
                var col = sim.Mod("ColorModule");
                ColorGradient = col.On ? col.G("gradient") : null;
                var uv = sim.Mod("UVModule");
                SheetOn = uv.On && (int)uv.F("mode") == 0;
                TilesX = Math.Max(1, (int)uv.F("tilesX", 1)); TilesY = Math.Max(1, (int)uv.F("tilesY", 1));
                SingleRow = (int)uv.F("animationType") == 1;
                Cycles = uv.F("cycles", 1);
                FrameOverTime = uv.C("frameOverTime"); StartFrame = uv.C("startFrame");
                RowMode = (int)uv.F("rowMode"); RowIndex = (int)uv.F("rowIndex");
                return this;
            }
        }

        static readonly Mods mods = new Mods();

        public static Vector3 Size(ParticleSim sim, int i, bool withPose = true) => Size(sim, i, withPose, new Mods().Of(sim));

        static Vector3 Size(ParticleSim sim, int i, bool withPose, Mods m)
        {
            float t = sim.Age[i] / sim.Life[i];
            uint seed = sim.Seed[i];
            float x = sim.Sx[i], y = sim.Sy[i], z = sim.Sz[i];
            if (m.SizeOn)
            {
                float r = PRand.Hash01(seed, PRand.SizeOverLifetime);
                float kx = m.SizeCurve.Eval(t, r);
                if (m.SizeSeparate) { x *= kx; y *= m.SizeY.Eval(t, r); z *= m.SizeZ.Eval(t, r); }
                else { x *= kx; y *= kx; z *= kx; }
            }
            if (m.NoiseOn)
            {
                float amt = m.NoiseSizeCurve?.Eval(t, 0.5f) ?? m.NoiseSizeAmount;
                if (amt != 0) { float k = 1 + sim.Nsz[i] * amt; x *= k; y *= k; z *= k; }
            }
            if (withPose && sim.Pose != null) { var s = sim.Pose.Value.SizeScale; x *= Math.Abs(s.x); y *= Math.Abs(s.y); z *= Math.Abs(s.z); }
            return new Vector3(x, y, z);
        }

        public static Color ColorOf(ParticleSim sim, int i) => ColorOf(sim, i, new Mods().Of(sim));

        static Color ColorOf(ParticleSim sim, int i, Mods m)
        {
            var c = sim.Col[i];
            if (m.ColorGradient is MMGradient g) c *= g.Eval(sim.Age[i] / sim.Life[i], PRand.Hash01(sim.Seed[i], PRand.ColorOverLifetime));
            return c;
        }

        /// <summary>貼圖格：u0、v0、du、dv、blend、frame（沒開 Texture Sheet Animation 是整張）</summary>
        static void Sheet(ParticleSim sim, int i, float[] o, Mods m)
        {
            if (!m.SheetOn) { o[0] = 0; o[1] = 0; o[2] = 1; o[3] = 1; o[4] = 0; o[5] = 0; return; }
            int tx = m.TilesX, ty = m.TilesY;
            float t = sim.Age[i] / sim.Life[i];
            uint seed = sim.Seed[i];
            bool singleRow = m.SingleRow;
            int frames = singleRow ? tx : tx * ty;
            float cycles = m.Cycles;
            float f = m.FrameOverTime.Eval(t, PRand.Hash01(seed, PRand.FrameOverTime)) * frames * cycles + m.StartFrame.Eval(t, PRand.Hash01(seed, PRand.StartFrame));
            float whole = MathF.Floor(f), blend = f - whole;
            int fi = (((int)whole % frames) + frames) % frames;
            int col, row;
            if (singleRow) { row = m.RowMode == 1 ? (int)(PRand.Hash01(seed, PRand.Row) * ty) : m.RowIndex; col = fi; }
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

        /// <summary>粒子本身的網格寫進 target（BakeMesh）</summary>
        public static void Particles(ParticleSim sim, NParticleSystemRenderer pr, in PCam cam, bool linear, bool subtractEmitter, NMesh meshAsset, NMesh target)
        {
            var b = new Builder();
            Fill(sim, pr, cam, linear, subtractEmitter, meshAsset, b);
            b.Into(target);
        }

        // 每格重用的暫存（引擎只在主執行緒跑）
        static int[] orderBuf = Array.Empty<int>();
        static float[] distBuf = Array.Empty<float>();
        static readonly float[] frameBuf = new float[6], cvBuf = new float[4];
        static float[] sortDist;
        static ParticleSim sortSim;
        static readonly Comparison<int> FarFirst = (a, c) => sortDist[c].CompareTo(sortDist[a]);
        static readonly Comparison<int> OldFirst = (a, c) => sortSim.Age[a].CompareTo(sortSim.Age[c]);
        static readonly Comparison<int> YoungFirst = (a, c) => sortSim.Age[c].CompareTo(sortSim.Age[a]);
        static readonly Comparison<int> ById = (a, c) => sortSim.Id[a].CompareTo(sortSim.Id[c]);
        static readonly string[] ModeKey = { "mode0", "mode1" }, CountKey = { "vectorComponentCount0", "vectorComponentCount1" }, ColorKey = { "color0", "color1" };
        static readonly string[][] VectorKey = { new[] { "vector0_0", "vector0_1", "vector0_2", "vector0_3" }, new[] { "vector1_0", "vector1_1", "vector1_2", "vector1_3" } };

        static int[] Grow(ref int[] a, int n) { if (a.Length < n) a = new int[Math.Max(n, a.Length * 2)]; return a; }
        static float[] Grow(ref float[] a, int n) { if (a.Length < n) a = new float[Math.Max(n, a.Length * 2)]; return a; }

        /// <summary>Custom Data 模組的 slot（0、1）在這顆粒子、這個時間的值寫進 cv</summary>
        static void Cust(PMod custom, ParticleSim sim, float[] cv, int slot, int i, float t)
        {
            cv[0] = cv[1] = cv[2] = cv[3] = 0;
            if (!custom.On) return;
            int m = (int)custom.F(ModeKey[slot]);
            if (m == 1)
            {
                int count = (int)custom.F(CountKey[slot], 4);
                for (int k = 0; k < count && k < 4; k++) cv[k] = custom.C(VectorKey[slot][k]).Eval(t, PRand.Hash01(sim.Seed[i], PRand.Custom + slot * 8 + k));
            }
            else if (m == 2 && custom.G(ColorKey[slot]) is MMGradient g)
            {
                var c = g.Eval(t, PRand.Hash01(sim.Seed[i], PRand.Custom + slot * 8));
                cv[0] = c.r; cv[1] = c.g; cv[2] = c.b; cv[3] = c.a;
            }
        }

        /// <summary>粒子本身的頂點寫進 b（繪製端每格重用同一個 b）</summary>
        public static void Fill(ParticleSim sim, NParticleSystemRenderer pr, in PCam cam, bool linear, bool subtractEmitter, NMesh meshAsset, Builder b)
        {
            var r = pr.R;
            int mode = (int)r.F("m_RenderMode");
            var layout = pr.CachedLayout(false);
            int n = mode == 5 ? 0 : sim.N;
            var mesh = mode == 4 ? meshAsset : null;
            var mPos = mesh?.Channels[(int)VertexAttribute.Position];
            int perV = mesh != null && mPos != null ? mesh.VertexCount : 4;
            var mIdx = mesh != null && mesh.Subs.Count > 0 ? mesh.Subs[0].Indices : null;
            int perI = mIdx?.Length ?? 6;
            b.Reset(n * perV, n * perI, layout);
            if (n == 0 || (mode == 4 && (mesh == null || mPos == null || mIdx == null))) return;
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
            // 畫的順序（Span.Sort 跟 Array.Sort 是同一個 ArraySortHelper，順序相同）
            var order = Grow(ref orderBuf, n);
            for (int k = 0; k < n; k++) order[k] = k;
            int sortMode = (int)r.F("m_SortMode");
            if (sortMode == 1)
            {
                var d = Grow(ref distBuf, n);
                for (int k = 0; k < n; k++) d[k] = (sim.WorldPos(k) - cam.Position).sqrMagnitude;
                sortDist = d;
                order.AsSpan(0, n).Sort(FarFirst);
                sortDist = null;
            }
            else if (sortMode == 2) { sortSim = sim; order.AsSpan(0, n).Sort(OldFirst); sortSim = null; }
            else if (sortMode == 3) { sortSim = sim; order.AsSpan(0, n).Sort(YoungFirst); sortSim = null; }
            var frame = frameBuf;
            var md = mods.Of(sim);
            bool hasNextFrame = layout.Offset.ContainsKey(5);
            bool hasUV = layout.Offset.TryGetValue(4, out int uvAt), hasCorner = layout.Offset.TryGetValue(11, out int cornerAt);
            float flipAx = Math.Abs(flip.x), flipAy = Math.Abs(flip.y);
            var mNrm = mesh?.Channels[(int)VertexAttribute.Normal];
            var mCol = mesh?.Channels[(int)VertexAttribute.Color];
            var mUv = mesh?.Channels[(int)VertexAttribute.TexCoord0];
            var cv = cvBuf;
            for (int oi = 0; oi < n; oi++)
            {
                int i = order[oi];
                float t = sim.Age[i] / sim.Life[i];
                uint seed = sim.Seed[i];
                var center = sim.WorldPos(i);
                var size = Size(sim, i, true, md);
                var col = ColorOf(sim, i, md);
                if (lin) { col.r = ToLinear(col.r); col.g = ToLinear(col.g); col.b = ToLinear(col.b); }
                var velSim = new Vector3(sim.Vx[i] + sim.Avx[i], sim.Vy[i] + sim.Avy[i], sim.Vz[i] + sim.Avz[i]);
                var vel = !sim.WorldSpace && sim.Pose != null ? MathUtil.MulVector(sim.Pose.Value.ToWorld, velSim).normalized * velSim.magnitude : velSim;
                Sheet(sim, i, frame, md);
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
                        if (hasNextFrame)
                        {
                            int tx = md.TilesX, ty = md.TilesY;
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
                        Cust(custom, sim, cv, 0, i, t); for (int st = 31; st <= 34; st++) b.S(vv, st, cv[0], cv[1], cv[2], cv[3]);
                        Cust(custom, sim, cv, 1, i, t); for (int st = 35; st <= 38; st++) b.S(vv, st, cv[0], cv[1], cv[2], cv[3]);
                        b.S(vv, 46, i);
                    }
                }
            }
        }

        /// <summary>拖尾的網格寫進 target（BakeTrailsMesh）</summary>
        public static void Trails(ParticleSim sim, NParticleSystemRenderer pr, in PCam cam, bool linear, bool subtractEmitter, NMesh target)
        {
            var b = new Builder();
            FillTrails(sim, pr, cam, linear, subtractEmitter, b);
            b.Into(target);
        }

        // 拖尾的每條線：點放在 trailPts 的 [start, start + count)，照收集的順序
        static readonly List<Vector3> trailPts = new List<Vector3>();
        static readonly List<(int start, int count, Color color, float size, float t, uint seed)> strips = new List<(int, int, Color, float, float, uint)>();
        static int[] ribbonOrder = Array.Empty<int>();

        static Vector3 TrailToWorld(ParticleSim sim, PMod trail, Vector3 p) => !sim.WorldSpace && trail.F("worldSpace") != 1 && sim.Pose != null ? MathUtil.MulPoint(sim.Pose.Value.ToWorld, p) : p;

        /// <summary>拖尾的頂點寫進 b（每顆粒子一條，加上粒子死掉後留下來的；Ribbon 模式照出生順序連成一條）</summary>
        public static void FillTrails(ParticleSim sim, NParticleSystemRenderer pr, in PCam cam, bool linear, bool subtractEmitter, Builder b)
        {
            var r = pr.R;
            var trail = sim.Mod("TrailModule");
            var layout = pr.CachedLayout(true);
            if (!trail.On) { b.Reset(0, 0, layout); return; }
            var md = mods.Of(sim);
            bool lin = linear && r.F("m_ApplyActiveColorSpace", 1) != 0;
            Vector3 sub = subtractEmitter && sim.Pose != null ? sim.Pose.Value.Position : Vector3.zero;
            trailPts.Clear();
            strips.Clear();
            if ((int)trail.F("mode") == 1)
            {
                int count = Math.Max(1, (int)trail.F("ribbonCount", 1));
                var order = Grow(ref ribbonOrder, sim.N);
                for (int k = 0; k < sim.N; k++) order[k] = k;
                sortSim = sim; order.AsSpan(0, sim.N).Sort(ById); sortSim = null;
                for (int g = 0; g < count; g++)
                {
                    int start = trailPts.Count;
                    Color color = Color.white; float size = 0;
                    for (int k = g; k < sim.N; k += count)
                    {
                        int i = order[k];
                        trailPts.Add(sim.WorldPos(i)); color = ColorOf(sim, i, md); size = Size(sim, i, true, md).x;
                    }
                    int m = trailPts.Count - start;
                    if (m > 1) strips.Add((start, m, color, size, 0, (uint)g));
                    else trailPts.RemoveRange(start, m);
                }
            }
            else
            {
                for (int i = 0; i < sim.N; i++)
                {
                    var tp = sim.Trails[i];
                    if (tp == null || tp.Count == 0) continue;
                    int start = trailPts.Count;
                    foreach (var p in tp) trailPts.Add(TrailToWorld(sim, trail, p.P));
                    trailPts.Add(sim.WorldPos(i));
                    strips.Add((start, trailPts.Count - start, ColorOf(sim, i, md), Size(sim, i, true, md).x, sim.Age[i] / sim.Life[i], sim.Seed[i]));
                }
                foreach (var o in sim.Orphans)
                {
                    int start = trailPts.Count;
                    foreach (var p in o.Points) trailPts.Add(TrailToWorld(sim, trail, p.P));
                    strips.Add((start, trailPts.Count - start, o.Color, o.Size, 1, 0));
                }
            }
            int total = 0;
            foreach (var st in strips) total += st.count;
            b.Reset(total * 2, total * 6, layout);
            var widthOverTrail = trail.C("widthOverTrail");
            bool sizeAffectsWidth = trail.F("sizeAffectsWidth", 1) != 0, inheritColor = trail.F("inheritParticleColor", 1) != 0;
            var colorOverTrail = trail.G("colorOverTrail");
            var colorOverLifetime = trail.G("colorOverLifetime");
            foreach (var s in strips)
            {
                int m = s.count;
                if (m < 2) continue;
                int v0 = b.Vtx;
                Vector3 lastSide = cam.Right;
                for (int k = 0; k < m; k++)
                {
                    float u = 1 - k / (float)(m - 1);
                    var p = trailPts[s.start + k];
                    var seg = trailPts[s.start + Math.Min(m - 1, k + 1)] - trailPts[s.start + Math.Max(0, k - 1)];
                    var toCam = cam.Position - p;
                    var side = Vector3.Cross(seg, toCam);
                    // 兩點重合時沿用前一個點的側向（改用 cam.right 會轉 90 度，那一段的三角形反過來）
                    if (side.sqrMagnitude < 1e-12f) side = k > 0 ? lastSide : cam.Right;
                    side = side.normalized;
                    lastSide = side;
                    float width = widthOverTrail.Eval(u, PRand.Hash01(s.seed, PRand.TrailWidth));
                    if (sizeAffectsWidth) width *= s.size;
                    var cTrail = colorOverTrail?.Eval(u, PRand.Hash01(s.seed, PRand.TrailColor)) ?? Color.white;
                    var cLife = colorOverLifetime?.Eval(s.t, PRand.Hash01(s.seed, PRand.TrailColor + 1)) ?? Color.white;
                    var nrm = toCam.normalized;
                    for (int side2 = 0; side2 < 2; side2++)
                    {
                        int vi = b.Vtx++;
                        var c = p + side * ((side2 == 0 ? -0.5f : 0.5f) * width) - sub;
                        b.Pos[vi * 3] = c.x; b.Pos[vi * 3 + 1] = c.y; b.Pos[vi * 3 + 2] = c.z;
                        b.Nrm[vi * 3] = nrm.x; b.Nrm[vi * 3 + 1] = nrm.y; b.Nrm[vi * 3 + 2] = nrm.z;
                        for (int ch = 0; ch < 4; ch++)
                        {
                            float v = (inheritColor ? s.color[ch] : 1) * cTrail[ch] * cLife[ch];
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
        }
    }
}

namespace UnityNative.Icalls
{
    using UnityNative;

    public static unsafe partial class UnityEngine_ParticleSystemRenderer
    {
        static NParticleSystemRenderer R(ParticleSystemRenderer r) => Native.Self<NParticleSystemRenderer>(r);

        /// <summary>
        /// BakeMesh：粒子照 camera 的方向產成網格。ParticleSystemBakeMeshOptions：BakeRotationAndScale（1）時 Local 模擬扣掉發射器的世界位置
        ///（UIParticle 再乘自己的矩陣），BakePosition（2）一起烤進去，兩者（3）＝世界座標（未查證細節）。頂點色存 gamma
        /// </summary>
        static void Bake(ParticleSystemRenderer self, Mesh mesh, Camera camera, int options, bool trails)
        {
            var r = R(self);
            var m = Native.Self<NMesh>(mesh);
            var sim = r.System?.Sim;
            if (sim == null || sim.Pose == null) { m.Clear(false); return; }
            var cam = PCam.Of(Native.Of<NCamera>(camera));
            bool subtract = options == 1 && !sim.WorldSpace;
            if (trails) ParticleGeometry.Trails(sim, r, cam, false, subtract, m);
            else ParticleGeometry.Particles(sim, r, cam, false, subtract, Native.Of<NMesh>(r.ParticleMesh), m);
        }

#if !NO_ParticleSystemBakeMeshOptions
        public static void BakeMesh(ParticleSystemRenderer self, Mesh mesh, Camera camera, ParticleSystemBakeMeshOptions options) => Bake(self, mesh, camera, (int)options, false);
        public static void BakeTrailsMesh(ParticleSystemRenderer self, Mesh mesh, Camera camera, ParticleSystemBakeMeshOptions options) => Bake(self, mesh, camera, (int)options, true);
#else
        // 2022.3 前期（Diceomancer 的 2022.3.3）還是舊的 useTransform 版：照文件「把 Transform 的旋轉與縮放烤進去」＝ BakeRotationAndScale（1）
        public static void BakeMesh(ParticleSystemRenderer self, Mesh mesh, Camera camera, bool useTransform) => Bake(self, mesh, camera, useTransform ? 1 : 0, false);
        public static void BakeTrailsMesh(ParticleSystemRenderer self, Mesh mesh, Camera camera, bool useTransform) => Bake(self, mesh, camera, useTransform ? 1 : 0, true);
#endif

        public static ParticleSystemRenderMode get_renderMode(ParticleSystemRenderer self) => (ParticleSystemRenderMode)(int)R(self).R.F("m_RenderMode");
        public static void set_renderMode(ParticleSystemRenderer self, ParticleSystemRenderMode value) => R(self).R.SetF("m_RenderMode", (int)value);
        public static ParticleSystemRenderSpace get_alignment(ParticleSystemRenderer self) => (ParticleSystemRenderSpace)(int)R(self).R.F("m_RenderAlignment");
        public static void set_alignment(ParticleSystemRenderer self, ParticleSystemRenderSpace value) => R(self).R.SetF("m_RenderAlignment", (int)value);
        public static ParticleSystemSortMode get_sortMode(ParticleSystemRenderer self) => (ParticleSystemSortMode)(int)R(self).R.F("m_SortMode");
        public static void set_sortMode(ParticleSystemRenderer self, ParticleSystemSortMode value) => R(self).R.SetF("m_SortMode", (int)value);
        public static float get_lengthScale(ParticleSystemRenderer self) => R(self).R.F("m_LengthScale", 2);
        public static void set_lengthScale(ParticleSystemRenderer self, float value) => R(self).R.SetF("m_LengthScale", value);
        public static float get_velocityScale(ParticleSystemRenderer self) => R(self).R.F("m_VelocityScale");
        public static void set_velocityScale(ParticleSystemRenderer self, float value) => R(self).R.SetF("m_VelocityScale", value);
        public static float get_minParticleSize(ParticleSystemRenderer self) => R(self).R.F("m_MinParticleSize");
        public static void set_minParticleSize(ParticleSystemRenderer self, float value) => R(self).R.SetF("m_MinParticleSize", value);
        public static float get_maxParticleSize(ParticleSystemRenderer self) => R(self).R.F("m_MaxParticleSize", 0.5f);
        public static void set_maxParticleSize(ParticleSystemRenderer self, float value) => R(self).R.SetF("m_MaxParticleSize", value);
        public static float get_sortingFudge(ParticleSystemRenderer self) => R(self).R.F("m_SortingFudge");
        public static void set_sortingFudge(ParticleSystemRenderer self, float value) => R(self).R.SetF("m_SortingFudge", value);
        public static SpriteMaskInteraction get_maskInteraction(ParticleSystemRenderer self) => (SpriteMaskInteraction)(int)R(self).R.F("m_MaskInteraction");
        public static void set_maskInteraction(ParticleSystemRenderer self, SpriteMaskInteraction value) => R(self).R.SetF("m_MaskInteraction", (int)value);
        public static Mesh get_mesh(ParticleSystemRenderer self) => R(self).ParticleMesh;
        public static void set_mesh(ParticleSystemRenderer self, Mesh value) => R(self).ParticleMesh = value;
        public static int get_meshCount(ParticleSystemRenderer self) => R(self).ParticleMesh != null ? 1 : 0;
        /// <summary>trailMaterial：材質清單的第二個</summary>
        public static Material get_trailMaterial(ParticleSystemRenderer self) { var r = R(self); return r.Shared.Length > 1 ? r.Shared[1] : null; }
        public static void set_trailMaterial(ParticleSystemRenderer self, Material value)
        {
            var r = R(self);
            if (r.Shared.Length < 2) Array.Resize(ref r.Shared, 2);
            r.Shared[1] = value;
        }
        public static void get_pivot_Injected(ParticleSystemRenderer self, out Vector3 ret) => ret = R(self).R.V("m_Pivot", Vector3.zero);
        public static void set_pivot_Injected(ParticleSystemRenderer self, ref Vector3 value) => R(self).R.Set("m_Pivot", value);
        public static void get_flip_Injected(ParticleSystemRenderer self, out Vector3 ret) => ret = R(self).R.V("m_Flip", Vector3.zero);
        public static void set_flip_Injected(ParticleSystemRenderer self, ref Vector3 value) => R(self).R.Set("m_Flip", value);
        public static int get_activeVertexStreamsCount(ParticleSystemRenderer self) => R(self).VertexStreams.Length;
        public static void GetActiveVertexStreams(ParticleSystemRenderer self, List<ParticleSystemVertexStream> streams)
        {
            streams.Clear();
            foreach (var s in R(self).VertexStreams) streams.Add((ParticleSystemVertexStream)s);
        }
        public static int get_activeTrailVertexStreamsCount(ParticleSystemRenderer self) => R(self).TrailVertexStreams.Length;
        public static void GetActiveTrailVertexStreams(ParticleSystemRenderer self, List<ParticleSystemVertexStream> streams)
        {
            streams.Clear();
            foreach (var s in R(self).TrailVertexStreams) streams.Add((ParticleSystemVertexStream)s);
        }
    }
}
