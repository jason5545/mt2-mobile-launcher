// 數學（Quaternion、Matrix4x4、Vector3、Mathf）、Random（Xorshift128，跟 Unity 位元一致）、
// AnimationCurve、Gradient（原生物件，m_Ptr 自己管）、PlayerPrefs 的儲存、Hash128。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Quaternion
    {
        public static void FromToRotation_Injected(ref Vector3 fromDirection, ref Vector3 toDirection, out Quaternion ret) => ret = MathUtil.FromToRotation(fromDirection, toDirection);
        public static void Inverse_Injected(ref Quaternion rotation, out Quaternion ret) => ret = MathUtil.Inverse(rotation);
        public static void Slerp_Injected(ref Quaternion a, ref Quaternion b, float t, out Quaternion ret) => ret = MathUtil.Slerp(a, b, Mathf.Clamp01(t));
        public static void SlerpUnclamped_Injected(ref Quaternion a, ref Quaternion b, float t, out Quaternion ret) => ret = MathUtil.Slerp(a, b, t);
        public static void Lerp_Injected(ref Quaternion a, ref Quaternion b, float t, out Quaternion ret) => ret = MathUtil.Lerp(a, b, Mathf.Clamp01(t));
        public static void LerpUnclamped_Injected(ref Quaternion a, ref Quaternion b, float t, out Quaternion ret) => ret = MathUtil.Lerp(a, b, t);
        public static void Internal_FromEulerRad_Injected(ref Vector3 euler, out Quaternion ret) => ret = MathUtil.FromEulerRad(euler);
        public static void Internal_ToEulerRad_Injected(ref Quaternion rotation, out Vector3 ret) => ret = MathUtil.ToEulerRad(rotation);
        public static void Internal_ToAxisAngleRad_Injected(ref Quaternion q, out Vector3 axis, out float angle)
        {
            var n = MathUtil.Normalize(q);
            angle = 2f * MathF.Acos(Math.Clamp(n.w, -1f, 1f));
            float s = MathF.Sqrt(Math.Max(0f, 1f - n.w * n.w));
            axis = s < 1e-6f ? Vector3.right : new Vector3(n.x / s, n.y / s, n.z / s);
        }
        public static void AngleAxis_Injected(float angle, ref Vector3 axis, out Quaternion ret) => ret = MathUtil.AngleAxis(angle, axis);
        public static void LookRotation_Injected(ref Vector3 forward, ref Vector3 upwards, out Quaternion ret) => ret = MathUtil.LookRotation(forward, upwards);
    }

    public static unsafe partial class UnityEngine_Matrix4x4
    {
        public static void GetRotation_Injected(ref Matrix4x4 _unity_self, out Quaternion ret) => ret = MathUtil.MatrixRotation(_unity_self);
        public static void GetLossyScale_Injected(ref Matrix4x4 m, out Vector3 ret) =>
            ret = new Vector3(new Vector3(m.m00, m.m10, m.m20).magnitude, new Vector3(m.m01, m.m11, m.m21).magnitude, new Vector3(m.m02, m.m12, m.m22).magnitude);
        public static bool IsIdentity_Injected(ref Matrix4x4 m) => m == Matrix4x4.identity;
        public static float GetDeterminant_Injected(ref Matrix4x4 m)
        {
            MathUtil.Inverse(m, out _);
            return m.m00 * (m.m11 * (m.m22 * m.m33 - m.m23 * m.m32) - m.m12 * (m.m21 * m.m33 - m.m23 * m.m31) + m.m13 * (m.m21 * m.m32 - m.m22 * m.m31))
                 - m.m01 * (m.m10 * (m.m22 * m.m33 - m.m23 * m.m32) - m.m12 * (m.m20 * m.m33 - m.m23 * m.m30) + m.m13 * (m.m20 * m.m32 - m.m22 * m.m30))
                 + m.m02 * (m.m10 * (m.m21 * m.m33 - m.m23 * m.m31) - m.m11 * (m.m20 * m.m33 - m.m23 * m.m30) + m.m13 * (m.m20 * m.m31 - m.m21 * m.m30))
                 - m.m03 * (m.m10 * (m.m21 * m.m32 - m.m22 * m.m31) - m.m11 * (m.m20 * m.m32 - m.m22 * m.m30) + m.m12 * (m.m20 * m.m31 - m.m21 * m.m30));
        }
        public static bool ValidTRS_Injected(ref Matrix4x4 m) => true;
        public static void TRS_Injected(ref Vector3 pos, ref Quaternion q, ref Vector3 s, out Matrix4x4 ret) => ret = MathUtil.TRS(pos, q, s);
        public static bool Inverse3DAffine_Injected(ref Matrix4x4 input, ref Matrix4x4 result) => MathUtil.Inverse(input, out result);
        public static void Inverse_Injected(ref Matrix4x4 m, out Matrix4x4 ret) { if (!MathUtil.Inverse(m, out ret)) ret = Matrix4x4.zero; }
        public static void Transpose_Injected(ref Matrix4x4 m, out Matrix4x4 ret)
        {
            ret = new Matrix4x4();
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) ret[r, c] = m[c, r];
        }
        public static void Ortho_Injected(float left, float right, float bottom, float top, float zNear, float zFar, out Matrix4x4 ret)
        {
            ret = Matrix4x4.identity;
            ret.m00 = 2f / (right - left); ret.m11 = 2f / (top - bottom); ret.m22 = -2f / (zFar - zNear);
            ret.m03 = -(right + left) / (right - left); ret.m13 = -(top + bottom) / (top - bottom); ret.m23 = -(zFar + zNear) / (zFar - zNear);
        }
        public static void Perspective_Injected(float fov, float aspect, float zNear, float zFar, out Matrix4x4 ret)
        {
            float f = 1f / MathF.Tan(fov * Mathf.Deg2Rad * 0.5f);
            ret = new Matrix4x4();
            ret.m00 = f / aspect; ret.m11 = f; ret.m22 = (zFar + zNear) / (zNear - zFar); ret.m23 = 2f * zFar * zNear / (zNear - zFar); ret.m32 = -1f;
        }
        public static void LookAt_Injected(ref Vector3 from, ref Vector3 to, ref Vector3 up, out Matrix4x4 ret) => ret = MathUtil.TRS(from, MathUtil.LookRotation(to - from, up), Vector3.one);
        public static void Frustum_Injected(float left, float right, float bottom, float top, float zNear, float zFar, out Matrix4x4 ret)
        {
            ret = new Matrix4x4();
            ret.m00 = 2f * zNear / (right - left); ret.m02 = (right + left) / (right - left);
            ret.m11 = 2f * zNear / (top - bottom); ret.m12 = (top + bottom) / (top - bottom);
            ret.m22 = -(zFar + zNear) / (zFar - zNear); ret.m23 = -2f * zFar * zNear / (zFar - zNear); ret.m32 = -1f;
        }
        public static bool CompareApproximately_Injected(ref Matrix4x4 a, ref Matrix4x4 b, float threshold)
        {
            for (int i = 0; i < 16; i++) if (MathF.Abs(a[i] - b[i]) > threshold) return false;
            return true;
        }
    }

    public static unsafe partial class UnityEngine_Vector3
    {
        public static void OrthoNormalize2(ref Vector3 a, ref Vector3 b) { a = a.normalized; b = (b - Vector3.Dot(a, b) * a).normalized; }
        public static void OrthoNormalize3(ref Vector3 a, ref Vector3 b, ref Vector3 c) { OrthoNormalize2(ref a, ref b); c = (c - Vector3.Dot(a, c) * a - Vector3.Dot(b, c) * b).normalized; }
        public static void Slerp_Injected(ref Vector3 a, ref Vector3 b, float t, out Vector3 ret) => ret = SlerpV(a, b, Mathf.Clamp01(t));
        public static void SlerpUnclamped_Injected(ref Vector3 a, ref Vector3 b, float t, out Vector3 ret) => ret = SlerpV(a, b, t);
        static Vector3 SlerpV(Vector3 a, Vector3 b, float t)
        {
            float ma = a.magnitude, mb = b.magnitude;
            if (ma < 1e-6f || mb < 1e-6f) return Vector3.LerpUnclamped(a, b, t);
            var q = MathUtil.FromToRotation(a, b);
            var r = MathUtil.Slerp(Quaternion.identity, q, t) * (a / ma);
            return r * Mathf.LerpUnclamped(ma, mb, t);
        }
        public static void RotateTowards_Injected(ref Vector3 current, ref Vector3 target, float maxRadiansDelta, float maxMagnitudeDelta, out Vector3 ret)
        {
            float mc = current.magnitude, mt = target.magnitude;
            if (mc < 1e-6f || mt < 1e-6f) { ret = Vector3.MoveTowards(current, target, maxMagnitudeDelta); return; }
            float ang = Vector3.Angle(current, target) * Mathf.Deg2Rad;
            float t = ang < 1e-6f ? 1f : Math.Min(1f, maxRadiansDelta / ang);
            var dir = SlerpV(current / mc, target / mt, t).normalized;
            ret = dir * Mathf.MoveTowards(mc, mt, maxMagnitudeDelta);
        }
    }

    public static unsafe partial class UnityEngine_Mathf
    {
        public static int ClosestPowerOfTwo(int value)
        {
            int n = NextPowerOfTwo(value), p = n >> 1;
            return value - p < n - value ? p : n;
        }
        public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
        public static int NextPowerOfTwo(int value)
        {
            if (value <= 0) return 0;
            value--; value |= value >> 1; value |= value >> 2; value |= value >> 4; value |= value >> 8; value |= value >> 16;
            return value + 1;
        }
        public static float GammaToLinearSpace(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        public static float LinearToGammaSpace(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        public static ushort FloatToHalf(float val) => BitConverter.HalfToUInt16Bits((Half)val);
        public static float HalfToFloat(ushort val) => (float)BitConverter.UInt16BitsToHalf(val);
        public static float PerlinNoise(float x, float y) => Perlin.Noise(x, y);
        public static float PerlinNoise1D(float x) => Perlin.Noise(x, 0f);
    }

    public static unsafe partial class UnityEngine_Random
    {
        static uint s0, s1, s2, s3;
        static int seed;

        static void Init(int sd)
        {
            seed = sd;
            s0 = (uint)sd;
            s1 = unchecked(s0 * 1812433253u + 1u);
            s2 = unchecked(s1 * 1812433253u + 1u);
            s3 = unchecked(s2 * 1812433253u + 1u);
        }

        static UnityEngine_Random() => Init((int)(DateTime.Now.Ticks & 0x7fffffff));

        static uint Next()
        {
            uint t = s0;
            t ^= t << 11;
            t ^= t >> 8;
            s0 = s1; s1 = s2; s2 = s3;
            s3 = s3 ^ (s3 >> 19) ^ t;
            return s3;
        }

        /// <summary>[0, 1]：低 23 位元除以 2^23 − 1（兩端都含）</summary>
        static float Next01() => (Next() & 0x7fffff) / 8388607f;

        public static void InitState(int s) => Init(s);
        public static float Range(float minInclusive, float maxInclusive)
        {
            float t = Next01();
            return t * minInclusive + (1f - t) * maxInclusive;
        }
        public static int RandomRangeInt(int min, int max)
        {
            if (min < max) return min + (int)(Next() % (uint)(max - min));
            if (min > max) return min - (int)(Next() % (uint)(min - max));
            return min;
        }
        public static float get_value() => Next01();
        public static void GetRandomUnitCircle(out Vector2 output)
        {
            float a = Range(0f, MathF.PI * 2f), r = MathF.Sqrt(Next01());
            output = new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
        }
        public static int get_seed() => seed;
        public static void set_seed(int value) => Init(value);
        public static void get_state_Injected(out UnityEngine.Random.State ret) => ret = new UnityEngine.Random.State { s0 = (int)s0, s1 = (int)s1, s2 = (int)s2, s3 = (int)s3 };
        public static void set_state_Injected(ref UnityEngine.Random.State value) { s0 = (uint)value.s0; s1 = (uint)value.s1; s2 = (uint)value.s2; s3 = (uint)value.s3; }
        public static void get_insideUnitSphere_Injected(out Vector3 ret)
        {
            get_onUnitSphere_Injected(out var d);
            ret = d * MathF.Cbrt(Next01());
        }
        public static void get_onUnitSphere_Injected(out Vector3 ret)
        {
            float z = Range(-1f, 1f), a = Range(0f, MathF.PI * 2f), r = MathF.Sqrt(1f - z * z);
            ret = new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, z);
        }
        public static void get_rotation_Injected(out Quaternion ret) => get_rotationUniform_Injected(out ret);
        public static void get_rotationUniform_Injected(out Quaternion ret)
        {
            float u1 = Next01(), u2 = Next01() * MathF.PI * 2f, u3 = Next01() * MathF.PI * 2f;
            float a = MathF.Sqrt(1f - u1), b = MathF.Sqrt(u1);
            ret = new Quaternion(a * MathF.Sin(u2), a * MathF.Cos(u2), b * MathF.Sin(u3), b * MathF.Cos(u3));
        }
    }

    public static unsafe partial class UnityEngine_AnimationCurve
    {
        public static void Internal_Destroy(IntPtr ptr) => Curves.Release(ptr);
        public static IntPtr Internal_Create(Keyframe[] keys) => Curves.New(keys);
        public static bool Internal_Equals(AnimationCurve self, IntPtr other) => Curves.Get(self.m_Ptr).SameAs(Curves.Get(other));
        public static void Internal_CopyFrom(AnimationCurve self, IntPtr other) => Curves.Get(self.m_Ptr).CopyFrom(Curves.Get(other));
        public static float Evaluate(AnimationCurve self, float time) => Curves.Get(self.m_Ptr).Evaluate(time);
        public static int AddKey(AnimationCurve self, float time, float value) => Curves.Get(self.m_Ptr).Add(new Keyframe(time, value));
        public static void ClearKeys(AnimationCurve self) => Curves.Get(self.m_Ptr).Keys.Clear();
        public static void RemoveKey(AnimationCurve self, int index) => Curves.Get(self.m_Ptr).Keys.RemoveAt(index);
        public static int get_length(AnimationCurve self) => Curves.Get(self.m_Ptr).Keys.Count;
        public static void SetKeys(AnimationCurve self, Keyframe[] keys) => Curves.Get(self.m_Ptr).Set(keys);
        public static Keyframe[] GetKeys(AnimationCurve self) => Curves.Get(self.m_Ptr).Keys.ToArray();
        public static int GetHashCode(AnimationCurve self) => self.m_Ptr.GetHashCode();
        public static void SmoothTangents(AnimationCurve self, int index, float weight) { }
        public static WrapMode get_preWrapMode(AnimationCurve self) => Curves.Get(self.m_Ptr).Pre;
        public static void set_preWrapMode(AnimationCurve self, WrapMode value) => Curves.Get(self.m_Ptr).Pre = value;
        public static WrapMode get_postWrapMode(AnimationCurve self) => Curves.Get(self.m_Ptr).Post;
        public static void set_postWrapMode(AnimationCurve self, WrapMode value) => Curves.Get(self.m_Ptr).Post = value;
        public static int AddKey_Internal_Injected(AnimationCurve self, ref Keyframe key) => Curves.Get(self.m_Ptr).Add(key);
        public static int MoveKey_Injected(AnimationCurve self, int index, ref Keyframe key)
        {
            var c = Curves.Get(self.m_Ptr);
            c.Keys.RemoveAt(index);
            return c.Add(key);
        }
        public static void GetKey_Injected(AnimationCurve self, int index, out Keyframe ret) => ret = Curves.Get(self.m_Ptr).Keys[index];
    }

    public static unsafe partial class UnityEngine_Gradient
    {
        public static IntPtr Init() => Curves.NewGradient();
        public static void Cleanup(Gradient self) => Curves.ReleaseGradient(self.m_Ptr);
        public static bool Internal_Equals(Gradient self, IntPtr other) => self.m_Ptr == other;
        // 取出、設定都複製一份（Unity 也是）：直接交出內部陣列的話，呼叫端改元素會改到原生那份，複製出來的 Gradient 也會共用同一個陣列
        public static GradientColorKey[] get_colorKeys(Gradient self) => (GradientColorKey[])Curves.G(self).Colors.Clone();
        public static void set_colorKeys(Gradient self, GradientColorKey[] value) => Curves.G(self).Colors = (GradientColorKey[])(value ?? Array.Empty<GradientColorKey>()).Clone();
        public static GradientAlphaKey[] get_alphaKeys(Gradient self) => (GradientAlphaKey[])Curves.G(self).Alphas.Clone();
        public static void set_alphaKeys(Gradient self, GradientAlphaKey[] value) => Curves.G(self).Alphas = (GradientAlphaKey[])(value ?? Array.Empty<GradientAlphaKey>()).Clone();
        public static GradientMode get_mode(Gradient self) => Curves.G(self).Mode;
        public static void set_mode(Gradient self, GradientMode value) => Curves.G(self).Mode = value;
        public static ColorSpace get_colorSpace(Gradient self) => ColorSpace.Gamma;
        public static void set_colorSpace(Gradient self, ColorSpace value) { }
        public static void SetKeys(Gradient self, GradientColorKey[] colorKeys, GradientAlphaKey[] alphaKeys)
        {
            var g = Curves.G(self);
            g.Colors = (GradientColorKey[])(colorKeys ?? Array.Empty<GradientColorKey>()).Clone();
            g.Alphas = (GradientAlphaKey[])(alphaKeys ?? Array.Empty<GradientAlphaKey>()).Clone();
        }
        public static void Evaluate_Injected(Gradient self, float time, out Color ret) => ret = Curves.G(self).Evaluate(time);
    }
}

namespace UnityNative
{
    /// <summary>AnimationCurve 與 Gradient 的原生資料</summary>
    public static class Curves
    {
        public sealed class Curve
        {
            public readonly List<Keyframe> Keys = new List<Keyframe>();
            public WrapMode Pre = WrapMode.ClampForever, Post = WrapMode.ClampForever;

            public void Set(Keyframe[] keys)
            {
                Keys.Clear();
                if (keys != null) Keys.AddRange(keys);
                Keys.Sort((a, b) => a.time.CompareTo(b.time));
            }

            public int Add(Keyframe k)
            {
                for (int i = 0; i < Keys.Count; i++) if (Keys[i].time == k.time) return -1;
                int idx = Keys.FindIndex(x => x.time > k.time);
                if (idx < 0) { Keys.Add(k); return Keys.Count - 1; }
                Keys.Insert(idx, k);
                return idx;
            }

            public void CopyFrom(Curve o) { Keys.Clear(); Keys.AddRange(o.Keys); Pre = o.Pre; Post = o.Post; }
            public bool SameAs(Curve o)
            {
                if (o == null || o.Keys.Count != Keys.Count) return false;
                for (int i = 0; i < Keys.Count; i++) if (!Keys[i].Equals(o.Keys[i])) return false;
                return true;
            }

            public float Evaluate(float t)
            {
                int n = Keys.Count;
                if (n == 0) return 0f;
                if (n == 1) return Keys[0].value;
                float t0 = Keys[0].time, t1 = Keys[n - 1].time;
                if (t < t0) t = Wrap(t, t0, t1, Pre);
                else if (t > t1) t = Wrap(t, t0, t1, Post);
                if (t <= t0) return Keys[0].value;
                if (t >= t1) return Keys[n - 1].value;
                int i = 0;
                while (i < n - 2 && t > Keys[i + 1].time) i++;
                return Segment(Keys[i], Keys[i + 1], t);
            }

            static float Wrap(float t, float t0, float t1, WrapMode m)
            {
                float len = t1 - t0;
                if (len <= 0) return t0;
                switch (m)
                {
                    case WrapMode.Loop: return t0 + Mod(t - t0, len);
                    case WrapMode.PingPong:
                        {
                            float x = Mod(t - t0, len * 2f);
                            return t0 + (x > len ? len * 2f - x : x);
                        }
                    default: return t < t0 ? t0 : t1;
                }
            }

            static float Mod(float a, float b) { float r = a % b; return r < 0 ? r + b : r; }

            /// <summary>Unity 的曲線：兩個關鍵影格之間是 Hermite；有權重（weightedMode）的段用三次 Bézier 求解</summary>
            static float Segment(Keyframe a, Keyframe b, float t)
            {
                float dt = b.time - a.time;
                if (dt <= 0) return a.value;
                if (float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent)) return a.value;
                bool wOut = (a.weightedMode & WeightedMode.Out) != 0, wIn = (b.weightedMode & WeightedMode.In) != 0;
                if (wOut || wIn)
                {
                    float ow = wOut ? a.outWeight : 1f / 3f, iw = wIn ? b.inWeight : 1f / 3f;
                    return Bezier(a, b, ow, iw, t);
                }
                float s = (t - a.time) / dt;
                float s2 = s * s, s3 = s2 * s;
                float h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
                return h00 * a.value + h10 * dt * a.outTangent + h01 * b.value + h11 * dt * b.inTangent;
            }

            static float Bezier(Keyframe a, Keyframe b, float ow, float iw, float t)
            {
                float dt = b.time - a.time;
                float x1 = ow, x2 = 1f - iw;
                float y0 = a.value, y1 = a.value + a.outTangent * ow * dt, y2 = b.value - b.inTangent * iw * dt, y3 = b.value;
                float x = (t - a.time) / dt;
                // 對 x 解三次方程（Newton），x(u) = 3(1-u)^2 u x1 + 3(1-u) u^2 x2 + u^3
                float u = x;
                for (int i = 0; i < 8; i++)
                {
                    float iu = 1 - u;
                    float xu = 3 * iu * iu * u * x1 + 3 * iu * u * u * x2 + u * u * u - x;
                    float d = 3 * iu * iu * x1 + 6 * iu * u * (x2 - x1) + 3 * u * u * (1 - x2);
                    if (MathF.Abs(d) < 1e-6f) break;
                    u = Math.Clamp(u - xu / d, 0f, 1f);
                }
                float v = 1 - u;
                return v * v * v * y0 + 3 * v * v * u * y1 + 3 * v * u * u * y2 + u * u * u * y3;
            }
        }

        public sealed class Grad
        {
            public GradientColorKey[] Colors = { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) };
            public GradientAlphaKey[] Alphas = { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) };
            public GradientMode Mode;

            public Color Evaluate(float t)
            {
                var c = Eval(Colors, t, k => (k.color.r, k.color.g, k.color.b), k => k.time);
                float a = EvalA(t);
                return new Color(c.Item1, c.Item2, c.Item3, a);
            }

            (float, float, float) Eval(GradientColorKey[] ks, float t, Func<GradientColorKey, (float, float, float)> v, Func<GradientColorKey, float> time)
            {
                if (ks == null || ks.Length == 0) return (1, 1, 1);
                if (t <= ks[0].time) return v(ks[0]);
                for (int i = 1; i < ks.Length; i++)
                {
                    if (t <= ks[i].time)
                    {
                        if (Mode == GradientMode.Fixed) return v(ks[i]);
                        float f = (t - ks[i - 1].time) / Math.Max(1e-6f, ks[i].time - ks[i - 1].time);
                        var a = v(ks[i - 1]); var b = v(ks[i]);
                        return (a.Item1 + (b.Item1 - a.Item1) * f, a.Item2 + (b.Item2 - a.Item2) * f, a.Item3 + (b.Item3 - a.Item3) * f);
                    }
                }
                return v(ks[ks.Length - 1]);
            }

            float EvalA(float t)
            {
                var ks = Alphas;
                if (ks == null || ks.Length == 0) return 1;
                if (t <= ks[0].time) return ks[0].alpha;
                for (int i = 1; i < ks.Length; i++)
                    if (t <= ks[i].time)
                    {
                        if (Mode == GradientMode.Fixed) return ks[i].alpha;
                        float f = (t - ks[i - 1].time) / Math.Max(1e-6f, ks[i].time - ks[i - 1].time);
                        return ks[i - 1].alpha + (ks[i].alpha - ks[i - 1].alpha) * f;
                    }
                return ks[ks.Length - 1].alpha;
            }
        }

        static readonly Dictionary<IntPtr, Curve> curves = new Dictionary<IntPtr, Curve>();
        static readonly Dictionary<IntPtr, Grad> grads = new Dictionary<IntPtr, Grad>();
        static long next = 1;
        static readonly object gate = new object();

        public static IntPtr New(Keyframe[] keys)
        {
            var c = new Curve();
            c.Set(keys);
            lock (gate) { var p = (IntPtr)next++; curves[p] = c; return p; }
        }
        public static Curve Get(IntPtr p) { lock (gate) return curves.TryGetValue(p, out var c) ? c : (curves[p] = new Curve()); }
        public static void Release(IntPtr p) { lock (gate) curves.Remove(p); }
        public static IntPtr NewGradient() { lock (gate) { var p = (IntPtr)next++; grads[p] = new Grad(); return p; } }
        public static Grad G(Gradient g) { lock (gate) return grads.TryGetValue(g.m_Ptr, out var x) ? x : (grads[g.m_Ptr] = new Grad()); }
        public static void ReleaseGradient(IntPtr p) { lock (gate) grads.Remove(p); }

        public static AnimationCurve FromJson(JsonNode node)
        {
            var keys = new List<Keyframe>();
            if (node?["m_Curve"] is JsonArray arr)
                foreach (var k in arr)
                {
                    var kf = new Keyframe(Serializer.F(k, "time"), Serializer.F(k, "value"), Slope(k?["inSlope"]), Slope(k?["outSlope"]), Serializer.F(k, "inWeight"), Serializer.F(k, "outWeight"));
                    kf.weightedMode = (WeightedMode)(int)Serializer.D(k?["weightedMode"]);
                    keys.Add(kf);
                }
            var c = new AnimationCurve(keys.ToArray());
            if (node?["m_PreInfinity"] != null) c.preWrapMode = Infinity((int)Serializer.D(node["m_PreInfinity"]));
            if (node?["m_PostInfinity"] != null) c.postWrapMode = Infinity((int)Serializer.D(node["m_PostInfinity"]));
            return c;
        }

        static float Slope(JsonNode n) => n == null ? 0f : (float)Serializer.D(n);

        // YAML 的 m_PreInfinity／m_PostInfinity：2 = ClampForever、0 = PingPong、1 = Loop（Unity 內部的 InternalWrapMode）
        static WrapMode Infinity(int v) => v switch { 0 => WrapMode.PingPong, 1 => WrapMode.Loop, _ => WrapMode.ClampForever };

        public static Gradient GradientFromJson(JsonNode node)
        {
            var g = new Gradient();
            if (node is not JsonObject o) return g;
            int nc = (int)Serializer.D(o["m_NumColorKeys"]), na = (int)Serializer.D(o["m_NumAlphaKeys"]);
            var colors = new List<GradientColorKey>();
            var alphas = new List<GradientAlphaKey>();
            for (int i = 0; i < 8; i++)
            {
                var key = o["key" + i];
                if (key == null) continue;
                float ct = (float)Serializer.D(o["ctime" + i]) / 65535f, at = (float)Serializer.D(o["atime" + i]) / 65535f;
                if (i < nc) colors.Add(new GradientColorKey(new Color(Serializer.F(key, "r"), Serializer.F(key, "g"), Serializer.F(key, "b"), 1), ct));
                if (i < na) alphas.Add(new GradientAlphaKey(Serializer.F(key, "a"), at));
            }
            g.mode = (GradientMode)(int)Serializer.D(o["m_Mode"]);
            if (colors.Count > 0 || alphas.Count > 0) g.SetKeys(colors.ToArray(), alphas.ToArray());
            return g;
        }
    }

    public static class Perlin
    {
        static readonly int[] p = Build();
        static int[] Build()
        {
            int[] perm = { 151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180 };
            var r = new int[512];
            for (int i = 0; i < 512; i++) r[i] = perm[i & 255];
            return r;
        }
        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        static float Grad(int h, float x, float y) { h &= 7; float u = h < 4 ? x : y, v = h < 4 ? y : x; return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -2f * v : 2f * v); }
        /// <summary>Unity 的 PerlinNoise 回 0～1（大約）</summary>
        public static float Noise(float x, float y)
        {
            int xi = (int)MathF.Floor(x) & 255, yi = (int)MathF.Floor(y) & 255;
            float xf = x - MathF.Floor(x), yf = y - MathF.Floor(y);
            float u = Fade(xf), v = Fade(yf);
            int aa = p[p[xi] + yi], ab = p[p[xi] + yi + 1], ba = p[p[xi + 1] + yi], bb = p[p[xi + 1] + yi + 1];
            float l1 = Mathf.Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
            float l2 = Mathf.Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
            return (Mathf.Lerp(l1, l2, v) + 1f) * 0.5f;
        }
    }

    /// <summary>PlayerPrefs：存成 persistentDataPath 下的 JSON</summary>
    public static class Prefs
    {
        static Dictionary<string, object> data;
        static string File => Path.Combine(Engine.Config.PersistentDir, "playerprefs.json");

        static Dictionary<string, object> D
        {
            get
            {
                if (data != null) return data;
                data = new Dictionary<string, object>();
                if (System.IO.File.Exists(File))
                {
                    var doc = JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject;
                    if (doc != null)
                        foreach (var kv in doc)
                        {
                            if (kv.Value is JsonValue v)
                            {
                                if (v.TryGetValue<long>(out var l)) data[kv.Key] = l;
                                else if (v.TryGetValue<double>(out var d)) data[kv.Key] = d;
                                else if (v.TryGetValue<string>(out var s)) data[kv.Key] = s;
                            }
                        }
                }
                return data;
            }
        }

        public static object Get(string key) => D.TryGetValue(key, out var v) ? v : null;
        public static void Set(string key, int v) => D[key] = (long)v;
        public static void Set(string key, float v) => D[key] = (double)v;
        public static void Set(string key, string v) => D[key] = v;
        public static void Delete(string key) => D.Remove(key);
        public static void Clear() => D.Clear();

        public static void Save()
        {
            var o = new JsonObject();
            foreach (var kv in D)
                o[kv.Key] = kv.Value switch { long l => JsonValue.Create(l), double d => JsonValue.Create(d), string s => JsonValue.Create(s), _ => null };
            System.IO.File.WriteAllText(File, o.ToJsonString());
        }
    }

    public static class HashUtil
    {
        public static Hash128 Parse(string s)
        {
            var bytes = new byte[16];
            if (s != null)
                for (int i = 0; i < 16 && i * 2 + 1 < s.Length; i++)
                    byte.TryParse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]);
            return new Hash128 { u64_0 = BitConverter.ToUInt64(bytes, 0), u64_1 = BitConverter.ToUInt64(bytes, 8) };
        }

        public static string ToString(Hash128 h)
        {
            var b = new byte[16];
            BitConverter.GetBytes(h.u64_0).CopyTo(b, 0);
            BitConverter.GetBytes(h.u64_1).CopyTo(b, 8);
            return Convert.ToHexString(b).ToLowerInvariant();
        }

        /// <summary>Unity 用 SpookyHash V2；這裡先用 MD5 混入原本的值（只在執行環境內部比對時一致）</summary>
        public static Hash128 Compute(byte[] data, Hash128 seed)
        {
            var buf = new byte[data.Length + 16];
            BitConverter.GetBytes(seed.u64_0).CopyTo(buf, 0);
            BitConverter.GetBytes(seed.u64_1).CopyTo(buf, 8);
            data.CopyTo(buf, 16);
            var md5 = System.Security.Cryptography.MD5.HashData(buf);
            return new Hash128 { u64_0 = BitConverter.ToUInt64(md5, 0), u64_1 = BitConverter.ToUInt64(md5, 8) };
        }
    }
}
