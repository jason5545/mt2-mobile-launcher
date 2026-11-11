// LineRenderer（原生元件）：Diceomancer 的地圖連線（LinePrefab）、小地圖連線（MiniLine）、出牌與骰子結果的 25 點曲線箭頭（UseCardBezierScript、DiceResultPointer）。
// 網格每格照攝影機建在世界座標（同粒子），寬度本來就是世界單位、不吃 Transform 縮放（Unity 手冊）。規則照網頁版 lineMesh.ts，差在：
//   - 多點的線接成一條帶子：每個點兩個頂點，側向量用前後兩點的方向（端點用相鄰那段），不是每段各畫一片。
//   - Alignment View：側向量 ＝ normalize(cross(切線, 視線))，正交攝影機的視線是攝影機的 forward，透視是攝影機到點；TransformZ 用物件的 forward。
//   - 寬度 ＝ widthMultiplier × widthCurve(距離比例)（AnimationCurve 照 Unity 的切線插值）；顏色 ＝ colorGradient(距離比例)，m_ApplyActiveColorSpace 時 rgb 轉線性。
//   - uv：Stretch u ＝ 比例 × textureScale.x；Tile u ＝ 世界距離 × textureScale.x（未查證，同網頁版）；DistributePerSegment u ＝ 點序 ÷ (點數 − 1)；RepeatPerSegment u ＝ 點序。
//     v：+側向量那邊 0、另一邊 1（未查證）。
//   - m_Loop：最後一點接回第一點。
// 沒做：numCornerVertices、numCapVertices（圓角、圓頭），寫進差異清單。
// startColor／endColor 改漸層第一個與最後一個 key（顏色與 alpha 各一），startWidth／endWidth 改寬度曲線第一個與最後一個 key（只有一個 key 時補成兩個）：Unity 的原生實作查不到，未查證。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public sealed class NLineRenderer : NRenderer
    {
        public readonly List<Vector3> Positions = new List<Vector3>();
        public float WidthMultiplier = 1;
        public AnimationCurve WidthCurve = new AnimationCurve(new Keyframe(0, 1));
        public Gradient ColorGradient = new Gradient();
        public int NumCornerVertices, NumCapVertices;
        public LineAlignment Alignment;
        public LineTextureMode TextureMode;
        public Vector2 TextureScale = Vector2.one;
        public float ShadowBias = 0.5f;
        public bool GenerateLightingData, UseWorldSpace = true, Loop, ApplyActiveColorSpace = true;
        public SpriteMaskInteraction MaskInteraction;
        public NMesh DrawMesh;
        /// <summary>寬度曲線或漸層改過就加一（只有這個檔案的 icall 與 Load／CopyFrom 會改；Get 都回複本，外面拿不到本體）</summary>
        public int StyleVersion;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            StyleVersion++;
            Positions.Clear();
            if (d["m_Positions"] is JsonArray ps) foreach (var p in ps) Positions.Add((Vector3)Serializer.Convert(p, typeof(Vector3), null));
            if (d["m_Parameters"] is JsonObject pr)
            {
                if (pr["widthMultiplier"] != null) WidthMultiplier = (float)Serializer.D(pr["widthMultiplier"]);
                if (pr["widthCurve"] != null) WidthCurve = (AnimationCurve)Serializer.Convert(pr["widthCurve"], typeof(AnimationCurve), ctx);
                if (pr["colorGradient"] != null) ColorGradient = (Gradient)Serializer.Convert(pr["colorGradient"], typeof(Gradient), ctx);
                NumCornerVertices = (int)Serializer.D(pr["numCornerVertices"]);
                NumCapVertices = (int)Serializer.D(pr["numCapVertices"]);
                Alignment = (LineAlignment)(int)Serializer.D(pr["alignment"]);
                TextureMode = (LineTextureMode)(int)Serializer.D(pr["textureMode"]);
                if (pr["textureScale"] != null) TextureScale = (Vector2)Serializer.Convert(pr["textureScale"], typeof(Vector2), null);
                if (pr["shadowBias"] != null) ShadowBias = (float)Serializer.D(pr["shadowBias"]);
                GenerateLightingData = Serializer.D(pr["generateLightingData"]) != 0;
            }
            UseWorldSpace = d["m_UseWorldSpace"] == null || Serializer.D(d["m_UseWorldSpace"]) != 0;
            Loop = Serializer.D(d["m_Loop"]) != 0;
            ApplyActiveColorSpace = d["m_ApplyActiveColorSpace"] == null || Serializer.D(d["m_ApplyActiveColorSpace"]) != 0;
            MaskInteraction = (SpriteMaskInteraction)(int)Serializer.D(d["m_MaskInteraction"]);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NLineRenderer)src;
            StyleVersion++;
            Positions.Clear(); Positions.AddRange(s.Positions);
            WidthMultiplier = s.WidthMultiplier;
            WidthCurve = new AnimationCurve(s.WidthCurve.keys) { preWrapMode = s.WidthCurve.preWrapMode, postWrapMode = s.WidthCurve.postWrapMode };
            ColorGradient = CopyGradient(s.ColorGradient);
            NumCornerVertices = s.NumCornerVertices; NumCapVertices = s.NumCapVertices; Alignment = s.Alignment; TextureMode = s.TextureMode;
            TextureScale = s.TextureScale; ShadowBias = s.ShadowBias; GenerateLightingData = s.GenerateLightingData;
            UseWorldSpace = s.UseWorldSpace; Loop = s.Loop; ApplyActiveColorSpace = s.ApplyActiveColorSpace; MaskInteraction = s.MaskInteraction;
        }

        public static Gradient CopyGradient(Gradient g)
        {
            var c = new Gradient();
            c.SetKeys(g.colorKeys, g.alphaKeys);
            c.mode = g.mode;
            return c;
        }

        readonly List<Vector3> worldTmp = new List<Vector3>();

        /// <summary>世界座標的點（m_Loop 時最後補回第一點）。回傳的清單共用，下次呼叫就覆寫</summary>
        List<Vector3> WorldPoints()
        {
            var w = worldTmp;
            w.Clear();
            var m = Tr.LocalToWorld;
            foreach (var p in Positions) w.Add(UseWorldSpace ? p : m.MultiplyPoint3x4(p));
            if (Loop && w.Count > 2) w.Add(w[0]);
            return w;
        }

        /// <summary>點的世界包圍盒中心（排序距離用：Unity 對非 sprite 的 renderer 用 bounds 中心）</summary>
        public Vector3 WorldCenter
        {
            get
            {
                if (Positions.Count == 0) return Tr.Position;
                var w = WorldPoints();
                var b = new Bounds(w[0], Vector3.zero);
                foreach (var p in w) b.Encapsulate(p);
                return b.center;
            }
        }

        public override Bounds LocalBounds
        {
            get
            {
                if (Positions.Count == 0) return default;
                var w2l = Tr.WorldToLocal;
                var b = new Bounds(UseWorldSpace ? w2l.MultiplyPoint3x4(Positions[0]) : Positions[0], Vector3.zero);
                foreach (var p in Positions) b.Encapsulate(UseWorldSpace ? w2l.MultiplyPoint3x4(p) : p);
                float half = WidthMultiplier * 0.5f;
                b.Expand(new Vector3(half, half, 0) * 2);
                return b;
            }
        }

        static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        // 上次 Build 的輸入（世界座標的點、攝影機、物件的 forward、外觀）：全部一樣就沿用上次的網格與回傳值。
        // 地圖、小地圖的連線大多不動，以前每格重建再重傳（Diceomancer 寬版戰鬥 132 條，桌面一格 0.76 ms）
        Vector3[] builtPts = Array.Empty<Vector3>();
        int builtCount = -1, builtStyle = -1;
        bool builtOk, builtOrtho, builtLin;
        float builtWidth;
        LineAlignment builtAlign;
        LineTextureMode builtTexMode;
        Vector2 builtTexScale;
        Vector3 builtCamPos, builtCamFwd, builtObjFwd;

        static bool Eq(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);

        /// <summary>照這台攝影機建世界座標的網格；畫不出來（不到兩點、全部重合）回 false</summary>
        public bool Build(bool ortho, Vector3 camPos, Vector3 camForward)
        {
            var w = WorldPoints();
            int n = w.Count;
            var objForward = Tr.LocalToWorld.MultiplyVector(Vector3.forward).normalized;
            bool same = n == builtCount && builtStyle == StyleVersion && ortho == builtOrtho && ApplyActiveColorSpace == builtLin && WidthMultiplier.Equals(builtWidth)
                && Alignment == builtAlign && TextureMode == builtTexMode && TextureScale.x.Equals(builtTexScale.x) && TextureScale.y.Equals(builtTexScale.y)
                && Eq(camPos, builtCamPos) && Eq(camForward, builtCamFwd) && Eq(objForward, builtObjFwd);
            for (int i = 0; same && i < n; i++) same = Eq(w[i], builtPts[i]);
            if (same) return builtOk;
            builtCount = n; builtStyle = StyleVersion; builtOrtho = ortho; builtLin = ApplyActiveColorSpace; builtWidth = WidthMultiplier;
            builtAlign = Alignment; builtTexMode = TextureMode; builtTexScale = TextureScale; builtCamPos = camPos; builtCamFwd = camForward; builtObjFwd = objForward;
            if (builtPts.Length < n) builtPts = new Vector3[n];
            w.CopyTo(builtPts);
            return builtOk = BuildMesh(w, ortho, camPos, camForward, objForward);
        }

        bool BuildMesh(List<Vector3> w, bool ortho, Vector3 camPos, Vector3 camForward, Vector3 objForward)
        {
            int n = w.Count;
            if (n < 2) return false;
            var dist = new float[n];
            for (int i = 1; i < n; i++) dist[i] = dist[i - 1] + Vector3.Distance(w[i - 1], w[i]);
            float total = dist[n - 1];
            if (total < 1e-9f) return false;
            var pos = new float[n * 2 * 3];
            var col = new float[n * 2 * 4];
            var uv = new float[n * 2 * 4];
            var nrm = new float[n * 2 * 3];
            var ind = new List<int>((n - 1) * 6);
            for (int i = 0; i < n; i++)
            {
                Vector3 t = i == 0 ? w[1] - w[0] : i == n - 1 ? w[n - 1] - w[n - 2] : w[i + 1] - w[i - 1];
                if (t.sqrMagnitude < 1e-18f && i > 0) t = w[i] - w[i - 1];
                var f = Alignment == LineAlignment.TransformZ ? objForward : ortho ? camForward : (w[i] - camPos).normalized;
                var side = Vector3.Cross(t, f);
                side = side.sqrMagnitude < 1e-18f ? Vector3.zero : side.normalized;
                float frac = dist[i] / total;
                float half = WidthMultiplier * WidthCurve.Evaluate(frac) * 0.5f;
                var c = ColorGradient.Evaluate(frac);
                if (ApplyActiveColorSpace) c = new Color(Lin(c.r), Lin(c.g), Lin(c.b), c.a);
                float u = TextureMode switch
                {
                    LineTextureMode.Tile => dist[i] * TextureScale.x,
                    LineTextureMode.DistributePerSegment => (float)i / (n - 1) * TextureScale.x,
                    LineTextureMode.RepeatPerSegment => i * TextureScale.x,
                    _ => frac * TextureScale.x,
                };
                for (int s = 0; s < 2; s++)
                {
                    int v = i * 2 + s;
                    var p = w[i] + side * (s == 0 ? half : -half);
                    pos[v * 3] = p.x; pos[v * 3 + 1] = p.y; pos[v * 3 + 2] = p.z;
                    nrm[v * 3] = -f.x; nrm[v * 3 + 1] = -f.y; nrm[v * 3 + 2] = -f.z;
                    col[v * 4] = c.r; col[v * 4 + 1] = c.g; col[v * 4 + 2] = c.b; col[v * 4 + 3] = c.a;
                    uv[v * 4] = u; uv[v * 4 + 1] = s;
                }
                if (i + 1 < n)
                {
                    int b = i * 2;
                    ind.Add(b); ind.Add(b + 1); ind.Add(b + 2);
                    ind.Add(b + 2); ind.Add(b + 1); ind.Add(b + 3);
                }
            }
            var m = DrawMesh ??= new NMesh { Name = "line" };
            for (int i = 0; i < m.Channels.Length; i++) m.Channels[i] = null;
            m.Channels[(int)VertexAttribute.Position] = new NMesh.Channel { Data = pos, Dim = 3, Format = VertexAttributeFormat.Float32 };
            m.Channels[(int)VertexAttribute.Normal] = new NMesh.Channel { Data = nrm, Dim = 3, Format = VertexAttributeFormat.Float32 };
            m.Channels[(int)VertexAttribute.Color] = new NMesh.Channel { Data = col, Dim = 4, Format = VertexAttributeFormat.Float32 };
            m.Channels[(int)VertexAttribute.TexCoord0] = new NMesh.Channel { Data = uv, Dim = 4, Format = VertexAttributeFormat.Float32 };
            m.VertexCount = n * 2;
            m.Subs.Clear();
            m.Subs.Add(new NMesh.Sub { Indices = ind.ToArray() });
            m.IndexFormat = n * 2 > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.RecalculateBounds();
            m.Version++;
            return true;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_LineRenderer
    {
        static NLineRenderer L(LineRenderer l) => Native.Self<NLineRenderer>(l);

        public static float get_startWidth(LineRenderer self) { var k = L(self).WidthCurve.keys; return k.Length > 0 ? k[0].value : 1; }
        public static void set_startWidth(LineRenderer self, float value) => SetEndKey(L(self), true, value);
        public static float get_endWidth(LineRenderer self) { var k = L(self).WidthCurve.keys; return k.Length > 0 ? k[^1].value : 1; }
        public static void set_endWidth(LineRenderer self, float value) => SetEndKey(L(self), false, value);

        static void SetEndKey(NLineRenderer l, bool start, float value)
        {
            var k = l.WidthCurve.keys;
            if (k.Length < 2)
            {
                float v = k.Length > 0 ? k[0].value : 1;
                k = new[] { new Keyframe(0, v), new Keyframe(1, v) };
            }
            if (start) k[0].value = value; else k[^1].value = value;
            l.WidthCurve.keys = k;
            l.StyleVersion++;
        }

        public static float get_widthMultiplier(LineRenderer self) => L(self).WidthMultiplier;
        public static void set_widthMultiplier(LineRenderer self, float value) => L(self).WidthMultiplier = value;
        public static int get_numCornerVertices(LineRenderer self) => L(self).NumCornerVertices;
        public static void set_numCornerVertices(LineRenderer self, int value) => L(self).NumCornerVertices = value;
        public static int get_numCapVertices(LineRenderer self) => L(self).NumCapVertices;
        public static void set_numCapVertices(LineRenderer self, int value) => L(self).NumCapVertices = value;
        public static bool get_useWorldSpace(LineRenderer self) => L(self).UseWorldSpace;
        public static void set_useWorldSpace(LineRenderer self, bool value) => L(self).UseWorldSpace = value;
        public static bool get_loop(LineRenderer self) => L(self).Loop;
        public static void set_loop(LineRenderer self, bool value) => L(self).Loop = value;
        public static int get_positionCount(LineRenderer self) => L(self).Positions.Count;
        public static void set_positionCount(LineRenderer self, int value)
        {
            var p = L(self).Positions;
            if (value < p.Count) p.RemoveRange(Math.Max(0, value), p.Count - Math.Max(0, value));
            while (p.Count < value) p.Add(Vector3.zero);
        }
        public static float get_shadowBias(LineRenderer self) => L(self).ShadowBias;
        public static void set_shadowBias(LineRenderer self, float value) => L(self).ShadowBias = value;
        public static bool get_generateLightingData(LineRenderer self) => L(self).GenerateLightingData;
        public static void set_generateLightingData(LineRenderer self, bool value) => L(self).GenerateLightingData = value;
        public static LineTextureMode get_textureMode(LineRenderer self) => L(self).TextureMode;
        public static void set_textureMode(LineRenderer self, LineTextureMode value) => L(self).TextureMode = value;
        public static LineAlignment get_alignment(LineRenderer self) => L(self).Alignment;
        public static void set_alignment(LineRenderer self, LineAlignment value) => L(self).Alignment = value;
        public static SpriteMaskInteraction get_maskInteraction(LineRenderer self) => L(self).MaskInteraction;
        public static void set_maskInteraction(LineRenderer self, SpriteMaskInteraction value) => L(self).MaskInteraction = value;
        public static AnimationCurve GetWidthCurveCopy(LineRenderer self) { var c = L(self).WidthCurve; return new AnimationCurve(c.keys) { preWrapMode = c.preWrapMode, postWrapMode = c.postWrapMode }; }
        public static void SetWidthCurve(LineRenderer self, AnimationCurve curve) { if (curve != null) { L(self).WidthCurve = new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode }; L(self).StyleVersion++; } }
        public static Gradient GetColorGradientCopy(LineRenderer self) => NLineRenderer.CopyGradient(L(self).ColorGradient);
        public static void SetColorGradient(LineRenderer self, Gradient gradient) { if (gradient != null) { L(self).ColorGradient = NLineRenderer.CopyGradient(gradient); L(self).StyleVersion++; } }
        public static int GetPositions(LineRenderer self, Vector3[] positions)
        {
            var p = L(self).Positions;
            int n = Math.Min(p.Count, positions?.Length ?? 0);
            for (int i = 0; i < n; i++) positions[i] = p[i];
            return n;
        }
        public static void SetPositions(LineRenderer self, Vector3[] positions)
        {
            var p = L(self).Positions;
            int n = Math.Min(p.Count, positions?.Length ?? 0);
            for (int i = 0; i < n; i++) p[i] = positions[i];
        }
        public static void SetPositionsWithNativeContainer(LineRenderer self, IntPtr positions, int count)
        {
            var p = L(self).Positions;
            var src = (Vector3*)positions;
            int n = Math.Min(p.Count, count);
            for (int i = 0; i < n; i++) p[i] = src[i];
        }
        public static int GetPositionsWithNativeContainer(LineRenderer self, IntPtr positions, int length)
        {
            var p = L(self).Positions;
            var dst = (Vector3*)positions;
            int n = Math.Min(p.Count, length);
            for (int i = 0; i < n; i++) dst[i] = p[i];
            return n;
        }

        public static void get_startColor_Injected(LineRenderer self, out Color ret) => ret = EndColor(L(self).ColorGradient, true);
        public static void set_startColor_Injected(LineRenderer self, ref Color value) { SetEndColor(L(self).ColorGradient, true, value); L(self).StyleVersion++; }
        public static void get_endColor_Injected(LineRenderer self, out Color ret) => ret = EndColor(L(self).ColorGradient, false);
        public static void set_endColor_Injected(LineRenderer self, ref Color value) { SetEndColor(L(self).ColorGradient, false, value); L(self).StyleVersion++; }

        static Color EndColor(Gradient g, bool start)
        {
            var ck = g.colorKeys; var ak = g.alphaKeys;
            var c = ck.Length > 0 ? ck[start ? 0 : ck.Length - 1].color : Color.white;
            c.a = ak.Length > 0 ? ak[start ? 0 : ak.Length - 1].alpha : 1;
            return c;
        }

        static void SetEndColor(Gradient g, bool start, Color value)
        {
            var ck = g.colorKeys; var ak = g.alphaKeys;
            if (ck.Length == 0) ck = new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) };
            if (ak.Length == 0) ak = new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) };
            int ci = start ? 0 : ck.Length - 1, ai = start ? 0 : ak.Length - 1;
            ck[ci].color = new Color(value.r, value.g, value.b, 1);
            ak[ai].alpha = value.a;
            g.SetKeys(ck, ak);
        }

        public static void SetPosition_Injected(LineRenderer self, int index, ref Vector3 position)
        {
            var p = L(self).Positions;
            if (index < 0 || index >= p.Count) { Engine.Log(LogType.Error, "LineRenderer.SetPosition index out of bounds!"); return; }
            p[index] = position;
        }
        public static void GetPosition_Injected(LineRenderer self, int index, out Vector3 ret)
        {
            var p = L(self).Positions;
            ret = index >= 0 && index < p.Count ? p[index] : Vector3.zero;
        }
        public static void get_textureScale_Injected(LineRenderer self, out Vector2 ret) => ret = L(self).TextureScale;
        public static void set_textureScale_Injected(LineRenderer self, ref Vector2 value) => L(self).TextureScale = value;
    }
}
