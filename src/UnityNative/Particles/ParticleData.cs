// 粒子系統的資料：元件的序列化欄位解析成型別化的模組（數值、MinMaxCurve、MinMaxGradient、向量、子模組），
// 加上每顆粒子的穩定亂數、噪聲與 Shape 模組的發射。照 TS 版（unity-cs2ts/runtime/src/particles/curves.ts、noise.ts、shape.ts）。
//
// 序列化形式（UnityCsReference 2022.3 Modules/ParticleSystemEditor/SerializedMinMaxCurve.cs、SerializedMinMaxGradient.cs）：
//   MinMaxCurve：minMaxState（0 常數、1 曲線、2 兩曲線之間、3 兩常數之間）、scalar（常數；曲線模式是倍率）、minScalar、maxCurve、minCurve。
//   MinMaxGradient：minMaxState（0 顏色、1 漸層、2 兩顏色之間、3 兩漸層之間、4 隨機顏色＝在 maxGradient 上隨機取一點）。
// 「兩者之間」的隨機值每顆粒子固定（Unity 文件 MinMaxCurve），用粒子的 seed 加模組編號雜湊；Unity 原生的亂數序列看不到，數值不會一樣。
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public sealed class MMCurve
    {
        public int State;
        public float Scalar, MinScalar;
        public Curves.Curve Max, Min;

        public static readonly MMCurve Zero = new MMCurve();
        public static MMCurve Const(float v) => new MMCurve { Scalar = v, MinScalar = v };

        static float At(Curves.Curve c, float t) => c == null || c.Keys.Count == 0 ? 0 : c.Evaluate(t);

        public float Eval(float t, float r)
        {
            switch (State)
            {
                case 1: return Scalar * At(Max, t);
                case 2: { float a = At(Min, t), b = At(Max, t); return Scalar * (a + (b - a) * r); }
                case 3: return MinScalar + (Scalar - MinScalar) * r;
                default: return Scalar;
            }
        }

        public float MaxValue()
        {
            static float Top(Curves.Curve c) { float m = 0; if (c != null) foreach (var k in c.Keys) m = Math.Max(m, k.value); return m; }
            return State switch { 1 => Scalar * Top(Max), 2 => Scalar * Math.Max(Top(Min), Top(Max)), 3 => Math.Max(Scalar, MinScalar), _ => Scalar };
        }

        static Curves.Curve ParseCurve(JsonNode n)
        {
            var c = new Curves.Curve();
            foreach (var kn in n?["m_Curve"] as JsonArray ?? new JsonArray())
                if (kn is JsonObject k)
                    c.Keys.Add(new Keyframe(AnimationAssets.F(k["time"]), AnimationAssets.F(k["value"]), AnimationAssets.F(k["inSlope"]), AnimationAssets.F(k["outSlope"]),
                        AnimationAssets.F(k["inWeight"]), AnimationAssets.F(k["outWeight"])) { weightedMode = (WeightedMode)(int)AnimationAssets.F(k["weightedMode"]) });
            c.Keys.Sort((a, b) => a.time.CompareTo(b.time));
            return c;
        }

        static Curves.Curve ParseCurve(JsonElement n)
        {
            var c = new Curves.Curve();
            if (PJ.Get(n, "m_Curve") is { ValueKind: JsonValueKind.Array } keys)
                foreach (var k in keys.EnumerateArray())
                    if (k.ValueKind == JsonValueKind.Object)
                        c.Keys.Add(new Keyframe(PJ.F(k, "time"), PJ.F(k, "value"), PJ.F(k, "inSlope"), PJ.F(k, "outSlope"), PJ.F(k, "inWeight"), PJ.F(k, "outWeight"))
                            { weightedMode = (WeightedMode)(int)PJ.F(k, "weightedMode") });
            c.Keys.Sort((a, b) => a.time.CompareTo(b.time));
            return c;
        }

        public static MMCurve Parse(JsonElement o) => new MMCurve
        {
            State = (int)PJ.F(o, "minMaxState"),
            Scalar = PJ.F(o, "scalar"),
            MinScalar = PJ.F(o, "minScalar"),
            Max = ParseCurve(PJ.Get(o, "maxCurve")),
            Min = ParseCurve(PJ.Get(o, "minCurve")),
        };

        public static MMCurve Parse(JsonObject o) => new MMCurve
        {
            State = (int)AnimationAssets.F(o["minMaxState"]),
            Scalar = AnimationAssets.F(o["scalar"]),
            MinScalar = AnimationAssets.F(o["minScalar"]),
            Max = ParseCurve(o["maxCurve"]),
            Min = ParseCurve(o["minCurve"]),
        };

        // managed 的 ParticleSystem.MinMaxCurve（m_Mode 跟 minMaxState 同編號）
        public ParticleSystem.MinMaxCurve ToManaged()
        {
            var m = new ParticleSystem.MinMaxCurve { m_Mode = (ParticleSystemCurveMode)State, m_CurveMultiplier = Scalar, m_ConstantMax = Scalar, m_ConstantMin = MinScalar };
            if (State == 1 || State == 2) { m.m_CurveMax = new AnimationCurve(Max.Keys.ToArray()); if (State == 2) m.m_CurveMin = new AnimationCurve(Min.Keys.ToArray()); }
            return m;
        }

        public static MMCurve FromManaged(in ParticleSystem.MinMaxCurve m)
        {
            var c = new MMCurve { State = (int)m.m_Mode };
            switch (m.m_Mode)
            {
                case ParticleSystemCurveMode.Constant: c.Scalar = c.MinScalar = m.m_ConstantMax; break;
                case ParticleSystemCurveMode.TwoConstants: c.Scalar = m.m_ConstantMax; c.MinScalar = m.m_ConstantMin; break;
                default:
                    c.Scalar = m.m_CurveMultiplier;
                    c.Max = new Curves.Curve(); if (m.m_CurveMax != null) c.Max.Set(m.m_CurveMax.keys);
                    c.Min = new Curves.Curve(); if (m.m_CurveMin != null) c.Min.Set(m.m_CurveMin.keys);
                    break;
            }
            return c;
        }
    }

    public sealed class PGrad
    {
        public bool Fixed;
        public int NC = 2, NA = 2;
        public readonly Color[] Keys = new Color[8];
        public readonly float[] CTime = new float[8], ATime = new float[8];

        public static PGrad Parse(JsonElement g)
        {
            var r = new PGrad
            {
                Fixed = PJ.F(g, "m_Mode") == 1,
                NC = Math.Max(1, PJ.Has(g, "m_NumColorKeys") ? (int)PJ.F(g, "m_NumColorKeys") : 2),
                NA = Math.Max(1, PJ.Has(g, "m_NumAlphaKeys") ? (int)PJ.F(g, "m_NumAlphaKeys") : 2),
            };
            for (int i = 0; i < 8; i++)
            {
                var k = PJ.Get(g, PJ.Key[i]);
                r.Keys[i] = !PJ.Present(k) ? Color.white : new Color(PJ.F(k, "r"), PJ.F(k, "g"), PJ.F(k, "b"), PJ.F(k, "a"));
                r.CTime[i] = PJ.F(g, PJ.CTime[i]) / 65535f;
                r.ATime[i] = PJ.F(g, PJ.ATime[i]) / 65535f;
            }
            return r;
        }

        public static PGrad Parse(JsonObject g)
        {
            var r = new PGrad
            {
                Fixed = AnimationAssets.F(g["m_Mode"]) == 1,
                NC = Math.Max(1, g["m_NumColorKeys"] != null ? (int)AnimationAssets.F(g["m_NumColorKeys"]) : 2),
                NA = Math.Max(1, g["m_NumAlphaKeys"] != null ? (int)AnimationAssets.F(g["m_NumAlphaKeys"]) : 2),
            };
            for (int i = 0; i < 8; i++)
            {
                var k = g["key" + i];
                r.Keys[i] = k == null ? Color.white : new Color(AnimationAssets.F(k["r"]), AnimationAssets.F(k["g"]), AnimationAssets.F(k["b"]), AnimationAssets.F(k["a"]));
                r.CTime[i] = AnimationAssets.F(g["ctime" + i]) / 65535f;
                r.ATime[i] = AnimationAssets.F(g["atime" + i]) / 65535f;
            }
            return r;
        }

        public static PGrad FromManaged(Gradient g)
        {
            var r = new PGrad();
            if (g == null) return r;
            var ck = g.colorKeys; var ak = g.alphaKeys;
            r.Fixed = g.mode == GradientMode.Fixed;
            r.NC = Math.Clamp(ck.Length, 1, 8); r.NA = Math.Clamp(ak.Length, 1, 8);
            for (int i = 0; i < 8; i++) r.Keys[i] = Color.white;
            for (int i = 0; i < r.NC; i++) { var c = ck[i].color; r.Keys[i].r = c.r; r.Keys[i].g = c.g; r.Keys[i].b = c.b; r.CTime[i] = ck[i].time; }
            for (int i = 0; i < r.NA; i++) { r.Keys[i].a = ak[i].alpha; r.ATime[i] = ak[i].time; }
            return r;
        }

        public Gradient ToManaged()
        {
            var ck = new GradientColorKey[NC]; var ak = new GradientAlphaKey[NA];
            for (int i = 0; i < NC; i++) ck[i] = new GradientColorKey(new Color(Keys[i].r, Keys[i].g, Keys[i].b), CTime[i]);
            for (int i = 0; i < NA; i++) ak[i] = new GradientAlphaKey(Keys[i].a, ATime[i]);
            var g = new Gradient { mode = Fixed ? GradientMode.Fixed : GradientMode.Blend };
            g.SetKeys(ck, ak);
            return g;
        }

        /// <summary>照 Gradient_Evaluate：m_Mode 1 是 Fixed，不內插</summary>
        public Color Eval(float t)
        {
            Color o;
            if (t <= CTime[0]) o = Keys[0];
            else
            {
                o = Keys[NC - 1];
                for (int i = 1; i < NC; i++)
                    if (t <= CTime[i])
                    {
                        float t0 = CTime[i - 1], d = CTime[i] - t0;
                        float u = Fixed ? 1 : (t - t0) / (d == 0 ? 1 : d);
                        o = Color.LerpUnclamped(Keys[i - 1], Keys[i], u);
                        break;
                    }
            }
            float a;
            if (t <= ATime[0]) a = Keys[0].a;
            else
            {
                a = Keys[NA - 1].a;
                for (int i = 1; i < NA; i++)
                    if (t <= ATime[i])
                    {
                        float t0 = ATime[i - 1], d = ATime[i] - t0;
                        float u = Fixed ? 1 : (t - t0) / (d == 0 ? 1 : d);
                        a = Keys[i - 1].a + (Keys[i].a - Keys[i - 1].a) * u;
                        break;
                    }
            }
            o.a = a;
            return o;
        }
    }

    public sealed class MMGradient
    {
        public int State;
        public Color MaxColor = Color.white, MinColor = Color.white;
        public PGrad MaxG, MinG;

        public static MMGradient Parse(JsonElement o)
        {
            static Color C(JsonElement n) => !PJ.Present(n) ? Color.white : new Color(PJ.F(n, "r"), PJ.F(n, "g"), PJ.F(n, "b"), PJ.F(n, "a"));
            return new MMGradient
            {
                State = (int)PJ.F(o, "minMaxState"),
                MaxColor = C(PJ.Get(o, "maxColor")),
                MinColor = C(PJ.Get(o, "minColor")),
                MaxG = PJ.Get(o, "maxGradient") is { ValueKind: JsonValueKind.Object } a ? PGrad.Parse(a) : null,
                MinG = PJ.Get(o, "minGradient") is { ValueKind: JsonValueKind.Object } b ? PGrad.Parse(b) : null,
            };
        }

        public static MMGradient Parse(JsonObject o)
        {
            static Color C(JsonNode n) => n == null ? Color.white : new Color(AnimationAssets.F(n["r"]), AnimationAssets.F(n["g"]), AnimationAssets.F(n["b"]), AnimationAssets.F(n["a"]));
            return new MMGradient
            {
                State = (int)AnimationAssets.F(o["minMaxState"]),
                MaxColor = C(o["maxColor"]),
                MinColor = C(o["minColor"]),
                MaxG = o["maxGradient"] is JsonObject a ? PGrad.Parse(a) : null,
                MinG = o["minGradient"] is JsonObject b ? PGrad.Parse(b) : null,
            };
        }

        public Color Eval(float t, float r)
        {
            switch (State)
            {
                case 1: return MaxG?.Eval(t) ?? Color.white;
                case 2: return Color.LerpUnclamped(MinColor, MaxColor, r);
                case 3: return Color.LerpUnclamped(MinG?.Eval(t) ?? Color.white, MaxG?.Eval(t) ?? Color.white, r);
                case 4: return MaxG?.Eval(r) ?? Color.white;
                default: return MaxColor;
            }
        }

        public ParticleSystem.MinMaxGradient ToManaged() => new ParticleSystem.MinMaxGradient
        {
            m_Mode = (ParticleSystemGradientMode)State, m_ColorMax = MaxColor, m_ColorMin = MinColor, m_GradientMax = MaxG?.ToManaged(), m_GradientMin = MinG?.ToManaged(),
        };

        public static MMGradient FromManaged(in ParticleSystem.MinMaxGradient m) => new MMGradient
        {
            State = (int)m.m_Mode, MaxColor = m.m_ColorMax, MinColor = m.m_ColorMin,
            MaxG = m.m_GradientMax != null ? PGrad.FromManaged(m.m_GradientMax) : null, MinG = m.m_GradientMin != null ? PGrad.FromManaged(m.m_GradientMin) : null,
        };
    }

    /// <summary>JsonElement 的小工具（照 JsonNode 的 o[key]：不存在或 JSON 的 null 都當作沒有）</summary>
    static class PJ
    {
        public static readonly string[] Key = { "key0", "key1", "key2", "key3", "key4", "key5", "key6", "key7" };
        public static readonly string[] CTime = { "ctime0", "ctime1", "ctime2", "ctime3", "ctime4", "ctime5", "ctime6", "ctime7" };
        public static readonly string[] ATime = { "atime0", "atime1", "atime2", "atime3", "atime4", "atime5", "atime6", "atime7" };
        public static JsonElement Get(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) ? v : default;
        public static bool Present(JsonElement v) => v.ValueKind != JsonValueKind.Undefined && v.ValueKind != JsonValueKind.Null;
        public static bool Has(JsonElement o, string k) => Present(Get(o, k));
        public static float F(JsonElement o, string k) => AnimationAssets.F(Get(o, k));
        /// <summary>JsonObject.Count：屬性個數（重複的名字 JsonObject 會丟例外，匯入的資料沒有）</summary>
        public static int Count(JsonElement o) { int n = 0; foreach (var _ in o.EnumerateObject()) n++; return n; }
    }

    /// <summary>一個模組（或整個元件）：欄位照序列化名稱，值是 float、MMCurve、MMGradient、Vector3、子模組或子模組的清單</summary>
    public sealed class PMod
    {
        readonly Dictionary<string, object> v = new Dictionary<string, object>(StringComparer.Ordinal);
        public static readonly PMod Empty = new PMod();

        public static PMod Parse(JsonElement o)
        {
            var m = new PMod();
            if (o.ValueKind != JsonValueKind.Object) return m;
            foreach (var kv in o.EnumerateObject())
            {
                var n = kv.Value;
                switch (n.ValueKind)
                {
                    case JsonValueKind.Object:
                        if (PJ.Has(n, "minMaxState") && (PJ.Has(n, "scalar") || PJ.Has(n, "maxCurve"))) m.v[kv.Name] = MMCurve.Parse(n);
                        else if (PJ.Has(n, "minMaxState") && (PJ.Has(n, "maxColor") || PJ.Has(n, "maxGradient"))) m.v[kv.Name] = MMGradient.Parse(n);
                        else if (PJ.Count(n) <= 4 && PJ.Has(n, "x") && PJ.Has(n, "y")) m.v[kv.Name] = new Vector3(PJ.F(n, "x"), PJ.F(n, "y"), PJ.F(n, "z"));
                        else m.v[kv.Name] = Parse(n);
                        break;
                    case JsonValueKind.Array:
                    {
                        var list = new List<PMod>();
                        foreach (var e in n.EnumerateArray()) list.Add(e.ValueKind == JsonValueKind.Object ? Parse(e) : null);
                        m.v[kv.Name] = list;
                        break;
                    }
                    case JsonValueKind.Null: break;
                    default: m.v[kv.Name] = AnimationAssets.F(n); break;
                }
            }
            return m;
        }

        public static PMod Parse(JsonObject o)
        {
            var m = new PMod();
            if (o == null) return m;
            foreach (var kv in o)
            {
                var n = kv.Value;
                if (n is JsonValue) m.v[kv.Key] = AnimationAssets.F(n);
                else if (n is JsonObject jo)
                {
                    if (jo["minMaxState"] != null && (jo["scalar"] != null || jo["maxCurve"] != null)) m.v[kv.Key] = MMCurve.Parse(jo);
                    else if (jo["minMaxState"] != null && (jo["maxColor"] != null || jo["maxGradient"] != null)) m.v[kv.Key] = MMGradient.Parse(jo);
                    else if (jo.Count <= 4 && jo["x"] != null && jo["y"] != null) m.v[kv.Key] = new Vector3(AnimationAssets.F(jo["x"]), AnimationAssets.F(jo["y"]), AnimationAssets.F(jo["z"]));
                    else m.v[kv.Key] = Parse(jo);
                }
                else if (n is JsonArray ja)
                {
                    var list = new List<PMod>();
                    foreach (var e in ja) list.Add(e is JsonObject eo ? Parse(eo) : null);
                    m.v[kv.Key] = list;
                }
            }
            return m;
        }

        /// <summary>淺複製每一層（曲線物件不可變、setter 換掉整個物件，可以共用）</summary>
        public PMod Clone()
        {
            var m = new PMod();
            foreach (var kv in v)
                m.v[kv.Key] = kv.Value switch
                {
                    PMod p => p.Clone(),
                    List<PMod> l => l.ConvertAll(x => x?.Clone()),
                    _ => kv.Value,
                };
            return m;
        }

        public bool On => F("enabled") == 1;
        public bool Has(string k) => v.ContainsKey(k);
        public float F(string k, float def = 0) => v.TryGetValue(k, out var o) && o is float f ? f : def;
        public bool B(string k) => F(k) != 0;
        public MMCurve C(string k) => v.TryGetValue(k, out var o) && o is MMCurve c ? c : MMCurve.Zero;
        public MMCurve COrNull(string k) => v.TryGetValue(k, out var o) ? o as MMCurve : null;
        public MMGradient G(string k) => v.TryGetValue(k, out var o) ? o as MMGradient : null;
        public Vector3 V(string k, Vector3 def) => v.TryGetValue(k, out var o) && o is Vector3 x ? x : def;
        public PMod M(string k) => v.TryGetValue(k, out var o) && o is PMod m ? m : Empty;
        public List<PMod> L(string k) => v.TryGetValue(k, out var o) && o is List<PMod> l ? l : new List<PMod>();
        public void Set(string k, object value) { if (this != Empty) v[k] = value; }
        public void SetF(string k, float value) => Set(k, value);
        public void SetB(string k, bool value) => Set(k, value ? 1f : 0f);
    }

    /// <summary>每顆粒子的穩定亂數（seed × 模組編號）與系統層級的 xorshift32</summary>
    public static class PRand
    {
        public static float Hash01(uint seed, int salt)
        {
            uint h = seed ^ unchecked((uint)(salt + 0x9e3779b9) * 0x85ebca6b);
            h = unchecked((h ^ (h >> 16)) * 0x7feb352d);
            h = unchecked((h ^ (h >> 15)) * 0x846ca68b);
            h ^= h >> 16;
            return h / 4294967296f;
        }

        public const int Lifetime = 1, Speed = 2, Size = 3, SizeY = 4, SizeZ = 5, Rotation = 6, RotationY = 7, RotationZ = 8, Color = 9, Gravity = 10,
            VelocityX = 11, VelocityY = 12, VelocityZ = 13, Orbital = 14, Radial = 15, SpeedModifier = 16, ForceX = 17, ForceY = 18, ForceZ = 19, Limit = 20,
            Drag = 21, ColorOverLifetime = 22, SizeOverLifetime = 23, RotationOverLifetime = 24, Noise = 25, FrameOverTime = 26, StartFrame = 27, Row = 28,
            TrailLifetime = 29, TrailWidth = 30, TrailColor = 31, Custom = 32, Flip = 40, Inherit = 41, BurstProbability = 42;
    }

    public sealed class XorShift
    {
        uint s;
        public XorShift(uint seed) => Reseed(seed);
        public void Reseed(uint seed) => s = seed != 0 ? seed : 0x6d2b79f5;
        public float Next()
        {
            uint x = s;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            s = x;
            return s / 4294967296f;
        }
        public uint UInt() { Next(); return s; }
        public float Range(float a, float b) => a + (b - a) * Next();
    }

    /// <summary>3D 梯度噪聲（Ken Perlin 2002 的 improved noise）。Unity 的 Noise 模組是原生 curl noise，演算法看不到</summary>
    public static class PNoise
    {
        static readonly int[] P = Build();
        static int[] Build()
        {
            int[] b = { 151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,247,120,234,75,0,26,
                197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,
                231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,
                200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,
                47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,
                113,224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,
                181,199,106,157,184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180 };
            var p = new int[512];
            for (int i = 0; i < 512; i++) p[i] = b[i & 255];
            return p;
        }
        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        static float Grad(int h, float x, float y, float z)
        {
            int k = h & 15;
            float u = k < 8 ? x : y, v = k < 4 ? y : k == 12 || k == 14 ? x : z;
            return ((k & 1) == 0 ? u : -u) + ((k & 2) == 0 ? v : -v);
        }
        static float L(float a, float b, float t) => a + t * (b - a);
        public static float Noise3(float x, float y, float z)
        {
            float fx = MathF.Floor(x), fy = MathF.Floor(y), fz = MathF.Floor(z);
            int X = (int)fx & 255, Y = (int)fy & 255, Z = (int)fz & 255;
            x -= fx; y -= fy; z -= fz;
            float u = Fade(x), v = Fade(y), w = Fade(z);
            int A = P[X] + Y, AA = P[A] + Z, AB = P[A + 1] + Z, B = P[X + 1] + Y, BA = P[B] + Z, BB = P[B + 1] + Z;
            return L(L(L(Grad(P[AA], x, y, z), Grad(P[BA], x - 1, y, z), u), L(Grad(P[AB], x, y - 1, z), Grad(P[BB], x - 1, y - 1, z), u), v),
                L(L(Grad(P[AA + 1], x, y, z - 1), Grad(P[BA + 1], x - 1, y, z - 1), u), L(Grad(P[AB + 1], x, y - 1, z - 1), Grad(P[BB + 1], x - 1, y - 1, z - 1), u), v), w);
        }
        public static float Fractal3(float x, float y, float z, int octaves, float mul, float scale)
        {
            float sum = 0, amp = 1, norm = 0, f = 1;
            int n = Math.Clamp(octaves, 1, 4);
            for (int i = 0; i < n; i++) { sum += Noise3(x * f, y * f, z * f) * amp; norm += amp; amp *= mul; f *= scale; }
            return norm > 0 ? sum / norm : 0;
        }
    }

    /// <summary>
    /// Shape 模組的發射點與初始方向（區域空間，套過形狀自己的位移、旋轉、縮放）。依據 Unity 2022.3 手冊 Shape module：
    /// Sphere／Hemisphere 朝外；Cone 從底面圓朝 +Z 張開 angle 度（ConeVolume 再往前 0～length）；Box 系列方向 +Z；Circle 在 XY 平面朝外；
    /// SingleSidedEdge 是 X 軸上的線段、方向 +Y；Rectangle 方向 +Z。Mesh 形狀沒做（從原點朝隨機方向）。
    /// arc／radius 的 mode：0 Random、1 Loop、2 PingPong、3 BurstSpread。半徑取樣照面積／體積均勻（未查證）
    /// </summary>
    public static class PShape
    {
        const float Deg = MathF.PI / 180;

        static float Fraction(PMod p, XorShift rng, float systemTime, float systemT, int index, int count)
        {
            int mode = (int)p.F("mode");
            float f;
            if (mode == 3) f = count > 1 ? index / (float)count : 0;
            else if (mode == 1 || mode == 2)
            {
                float loops = systemTime * p.C("speed").Eval(systemT, rng.Next());
                if (mode == 1) f = loops - MathF.Floor(loops);
                else { float m = loops - 2 * MathF.Floor(loops / 2); f = m <= 1 ? m : 2 - m; }
            }
            else f = rng.Next();
            float spread = p.F("spread");
            if (spread > 0) f = MathF.Floor(f / spread) * spread;
            return f;
        }

        public static Vector3 RandomUnit(XorShift rng)
        {
            float z = rng.Range(-1, 1), a = rng.Next() * MathF.PI * 2, s = MathF.Sqrt(Math.Max(0, 1 - z * z));
            return new Vector3(s * MathF.Cos(a), s * MathF.Sin(a), z);
        }

        public static void Emit(PMod sh, XorShift rng, float systemTime, float systemT, int index, int count, out Vector3 pos, out Vector3 dir)
        {
            if (sh == null || !sh.Has("type") || sh.F("enabled", 1) == 0) { pos = Vector3.zero; dir = new Vector3(0, 0, 1); return; }
            float radius = sh.M("radius").F("value", 1);
            float thick = Math.Clamp(sh.F("radiusThickness", 1), 0, 1);
            float arc = sh.M("arc").F("value", 360) * Deg;
            int type = (int)sh.F("type", 4);
            switch (type)
            {
                case 0: case 1: case 2: case 3:
                    {
                        float t = type == 1 || type == 3 ? 0 : thick;
                        dir = RandomUnit(rng);
                        if (type == 2 || type == 3) dir.z = Math.Abs(dir.z);
                        float inner = 1 - t;
                        float r = radius * MathF.Cbrt(inner * inner * inner + (1 - inner * inner * inner) * rng.Next());
                        pos = dir * r;
                        break;
                    }
                case 4: case 7: case 8: case 9:
                    {
                        float theta = Fraction(sh.M("arc"), rng, systemTime, systemT, index, count) * arc;
                        float inner = 1 - (type == 7 || type == 9 ? 0 : thick);
                        float rho = MathF.Sqrt(inner * inner + (1 - inner * inner) * rng.Next());
                        float a = sh.F("angle", 25) * Deg * rho;
                        float c = MathF.Cos(theta), s = MathF.Sin(theta);
                        pos = new Vector3(c * rho * radius, s * rho * radius, 0);
                        dir = new Vector3(c * MathF.Sin(a), s * MathF.Sin(a), MathF.Cos(a));
                        if (type == 8 || type == 9) pos += dir * (rng.Next() * sh.F("length", 5));
                        break;
                    }
                case 5: case 15: case 16:
                    {
                        if (type == 5) pos = new Vector3(rng.Next() - 0.5f, rng.Next() - 0.5f, rng.Next() - 0.5f);
                        else if (type == 15)
                        {
                            int face = (int)(rng.Next() * 6);
                            float u = rng.Next() - 0.5f, v = rng.Next() - 0.5f, w = (face & 1) != 0 ? 0.5f : -0.5f;
                            int ax = face >> 1;
                            pos = new Vector3(ax == 0 ? w : u, ax == 1 ? w : ax == 0 ? u : v, ax == 2 ? w : v);
                        }
                        else
                        {
                            int e = (int)(rng.Next() * 12);
                            float t = rng.Next() - 0.5f, a = (e & 1) != 0 ? 0.5f : -0.5f, b = (e & 2) != 0 ? 0.5f : -0.5f;
                            int ax = e >> 2;
                            pos = new Vector3(ax == 0 ? t : a, ax == 1 ? t : ax == 0 ? a : b, ax == 2 ? t : b);
                        }
                        dir = new Vector3(0, 0, 1);
                        break;
                    }
                case 10: case 11:
                    {
                        float theta = Fraction(sh.M("arc"), rng, systemTime, systemT, index, count) * arc;
                        float inner = 1 - (type == 11 ? 0 : thick);
                        float r = radius * MathF.Sqrt(inner * inner + (1 - inner * inner) * rng.Next());
                        dir = new Vector3(MathF.Cos(theta), MathF.Sin(theta), 0);
                        pos = dir * r;
                        break;
                    }
                case 12:
                    {
                        float f = Fraction(sh.M("radius"), rng, systemTime, systemT, index, count);
                        pos = new Vector3((f * 2 - 1) * radius, 0, 0);
                        dir = new Vector3(0, 1, 0);
                        break;
                    }
                case 18:
                    pos = new Vector3(rng.Next() - 0.5f, rng.Next() - 0.5f, 0);
                    dir = new Vector3(0, 0, 1);
                    break;
                default:
                    pos = Vector3.zero;
                    dir = RandomUnit(rng);
                    break;
            }
            // 形狀自己的縮放、旋轉、位移（非等比縮放時方向跟著擠壓）
            var sc = sh.V("m_Scale", Vector3.one);
            pos = Vector3.Scale(pos, sc);
            dir = Vector3.Scale(dir, sc);
            var rot = sh.V("m_Rotation", Vector3.zero);
            if (rot != Vector3.zero)
            {
                var q = Quaternion.Euler(rot);
                pos = q * pos;
                dir = q * dir;
            }
            var off = sh.V("m_Position", Vector3.zero);
            pos += off;
            if (dir.sqrMagnitude < 1e-12f) dir = new Vector3(0, 0, 1);
            dir = dir.normalized;
            float sph = sh.F("sphericalDirectionAmount");
            if (sph > 0)
            {
                var rel = pos - off;
                if (rel.sqrMagnitude > 1e-12f) dir = Vector3.Lerp(dir, rel.normalized, sph).normalized;
            }
            float rnd = sh.F("randomDirectionAmount");
            if (rnd > 0) dir = Vector3.Lerp(dir, RandomUnit(rng), rnd).normalized;
            float rp = sh.F("randomPositionAmount");
            if (rp > 0) pos += RandomUnit(rng) * (rp * rng.Next());
        }
    }
}
