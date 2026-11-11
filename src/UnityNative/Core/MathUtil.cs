// Unity 數學裡原生實作的部分（Matrix4x4.TRS、Inverse、Quaternion 的尤拉角換算…）。
// Unity 的尤拉角是 ZXY 順序：先轉 z、再 x、再 y（q = qy * qx * qz）。
using System;
using UnityEngine;

namespace UnityNative
{
    public static class MathUtil
    {
        public static Matrix4x4 TRS(Vector3 p, Quaternion q, Vector3 s)
        {
            float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f;
            float xx = q.x * x, yy = q.y * y, zz = q.z * z;
            float xy = q.x * y, xz = q.x * z, yz = q.y * z;
            float wx = q.w * x, wy = q.w * y, wz = q.w * z;
            Matrix4x4 m;
            m.m00 = (1f - (yy + zz)) * s.x; m.m01 = (xy - wz) * s.y; m.m02 = (xz + wy) * s.z; m.m03 = p.x;
            m.m10 = (xy + wz) * s.x; m.m11 = (1f - (xx + zz)) * s.y; m.m12 = (yz - wx) * s.z; m.m13 = p.y;
            m.m20 = (xz - wy) * s.x; m.m21 = (yz + wx) * s.y; m.m22 = (1f - (xx + yy)) * s.z; m.m23 = p.z;
            m.m30 = 0; m.m31 = 0; m.m32 = 0; m.m33 = 1;
            return m;
        }

        public static Matrix4x4 Mul(in Matrix4x4 a, in Matrix4x4 b)
        {
            Matrix4x4 r;
            r.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30;
            r.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31;
            r.m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32;
            r.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33;
            r.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30;
            r.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31;
            r.m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32;
            r.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33;
            r.m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30;
            r.m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31;
            r.m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32;
            r.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33;
            r.m30 = a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30;
            r.m31 = a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31;
            r.m32 = a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32;
            r.m33 = a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33;
            return r;
        }

        public static Vector3 MulPoint(in Matrix4x4 m, Vector3 p) =>
            new Vector3(m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
                        m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
                        m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23);

        public static Vector3 MulVector(in Matrix4x4 m, Vector3 v) =>
            new Vector3(m.m00 * v.x + m.m01 * v.y + m.m02 * v.z,
                        m.m10 * v.x + m.m11 * v.y + m.m12 * v.z,
                        m.m20 * v.x + m.m21 * v.y + m.m22 * v.z);

        public static bool Inverse(in Matrix4x4 m, out Matrix4x4 r)
        {
            float a00 = m.m00, a01 = m.m01, a02 = m.m02, a03 = m.m03;
            float a10 = m.m10, a11 = m.m11, a12 = m.m12, a13 = m.m13;
            float a20 = m.m20, a21 = m.m21, a22 = m.m22, a23 = m.m23;
            float a30 = m.m30, a31 = m.m31, a32 = m.m32, a33 = m.m33;
            float b00 = a00 * a11 - a01 * a10, b01 = a00 * a12 - a02 * a10, b02 = a00 * a13 - a03 * a10;
            float b03 = a01 * a12 - a02 * a11, b04 = a01 * a13 - a03 * a11, b05 = a02 * a13 - a03 * a12;
            float b06 = a20 * a31 - a21 * a30, b07 = a20 * a32 - a22 * a30, b08 = a20 * a33 - a23 * a30;
            float b09 = a21 * a32 - a22 * a31, b10 = a21 * a33 - a23 * a31, b11 = a22 * a33 - a23 * a32;
            float det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
            if (det == 0f || float.IsNaN(det)) { r = default; return false; }
            float inv = 1f / det;
            r.m00 = (a11 * b11 - a12 * b10 + a13 * b09) * inv;
            r.m01 = (a02 * b10 - a01 * b11 - a03 * b09) * inv;
            r.m02 = (a31 * b05 - a32 * b04 + a33 * b03) * inv;
            r.m03 = (a22 * b04 - a21 * b05 - a23 * b03) * inv;
            r.m10 = (a12 * b08 - a10 * b11 - a13 * b07) * inv;
            r.m11 = (a00 * b11 - a02 * b08 + a03 * b07) * inv;
            r.m12 = (a32 * b02 - a30 * b05 - a33 * b01) * inv;
            r.m13 = (a20 * b05 - a22 * b02 + a23 * b01) * inv;
            r.m20 = (a10 * b10 - a11 * b08 + a13 * b06) * inv;
            r.m21 = (a01 * b08 - a00 * b10 - a03 * b06) * inv;
            r.m22 = (a30 * b04 - a31 * b02 + a33 * b00) * inv;
            r.m23 = (a21 * b02 - a20 * b04 - a23 * b00) * inv;
            r.m30 = (a11 * b07 - a10 * b09 - a12 * b06) * inv;
            r.m31 = (a00 * b09 - a01 * b07 + a02 * b06) * inv;
            r.m32 = (a31 * b01 - a30 * b03 - a32 * b00) * inv;
            r.m33 = (a20 * b03 - a21 * b01 + a22 * b00) * inv;
            return true;
        }

        public static Quaternion FromEulerRad(Vector3 e)
        {
            float cx = MathF.Cos(e.x * 0.5f), sx = MathF.Sin(e.x * 0.5f);
            float cy = MathF.Cos(e.y * 0.5f), sy = MathF.Sin(e.y * 0.5f);
            float cz = MathF.Cos(e.z * 0.5f), sz = MathF.Sin(e.z * 0.5f);
            return new Quaternion(
                cy * sx * cz + sy * cx * sz,
                sy * cx * cz - cy * sx * sz,
                cy * cx * sz - sy * sx * cz,
                cy * cx * cz + sy * sx * sz);
        }

        /// <summary>四元數 → ZXY 尤拉角（弧度）。Unity 回 0～2π，Quaternion.eulerAngles 在 managed 那邊再轉成度數並 MakePositive</summary>
        public static Vector3 ToEulerRad(Quaternion q)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            // R = Ry·Rx·Rz：m12 = -sin(x)，m02/m22 = tan(y)，m10/m11 = tan(z)
            float sinx = Math.Clamp(2f * (w * x - y * z), -1f, 1f);
            float ex, ey, ez;
            if (MathF.Abs(sinx) < 0.9999999f)
            {
                ex = MathF.Asin(sinx);
                ey = MathF.Atan2(2f * (x * z + w * y), 1f - 2f * (x * x + y * y));
                ez = MathF.Atan2(2f * (x * y + w * z), 1f - 2f * (x * x + z * z));
            }
            else
            {
                // 萬向鎖：x = ±90°，z 當 0，旋轉全部放到 y（-m20 / m00）
                ex = MathF.PI * 0.5f * MathF.Sign(sinx);
                ey = MathF.Atan2(-2f * (x * z - w * y), 1f - 2f * (y * y + z * z));
                ez = 0f;
            }
            return new Vector3(ex, ey, ez);
        }

        public static Quaternion Normalize(Quaternion q)
        {
            float m = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-30f) return Quaternion.identity;
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        public static Quaternion Inverse(Quaternion q)
        {
            float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (n < 1e-30f) return Quaternion.identity;
            return new Quaternion(-q.x / n, -q.y / n, -q.z / n, q.w / n);
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f) { dot = -dot; b = new Quaternion(-b.x, -b.y, -b.z, -b.w); }
            if (dot > 0.9995f) return Lerp(a, b, t);
            float theta = MathF.Acos(Math.Min(dot, 1f));
            float s = MathF.Sin(theta);
            float wa = MathF.Sin((1f - t) * theta) / s, wb = MathF.Sin(t * theta) / s;
            return new Quaternion(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb);
        }

        public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            float sb = dot < 0f ? -t : t, sa = 1f - t;
            return Normalize(new Quaternion(a.x * sa + b.x * sb, a.y * sa + b.y * sb, a.z * sa + b.z * sb, a.w * sa + b.w * sb));
        }

        public static Quaternion AngleAxis(float deg, Vector3 axis)
        {
            float m = axis.magnitude;
            if (m < 1e-6f) return Quaternion.identity;
            axis /= m;
            float h = deg * Mathf.Deg2Rad * 0.5f, s = MathF.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, MathF.Cos(h));
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            if (forward.sqrMagnitude < 1e-12f) return Quaternion.identity;
            forward = forward.normalized;
            var right = Vector3.Cross(up, forward);
            if (right.sqrMagnitude < 1e-12f)
            {
                // up 跟 forward 平行：Unity 改用 FromToRotation(z, forward)
                return FromToRotation(Vector3.forward, forward);
            }
            right = right.normalized;
            var u = Vector3.Cross(forward, right);
            return FromBasis(right, u, forward);
        }

        public static Quaternion FromBasis(Vector3 r, Vector3 u, Vector3 f)
        {
            float m00 = r.x, m01 = u.x, m02 = f.x;
            float m10 = r.y, m11 = u.y, m12 = f.y;
            float m20 = r.z, m21 = u.z, m22 = f.z;
            float tr = m00 + m11 + m22;
            Quaternion q;
            if (tr > 0f)
            {
                float s = MathF.Sqrt(tr + 1f) * 2f;
                q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return Normalize(q);
        }

        public static Quaternion FromToRotation(Vector3 from, Vector3 to)
        {
            from = from.normalized; to = to.normalized;
            float d = Vector3.Dot(from, to);
            if (d >= 1f - 1e-6f) return Quaternion.identity;
            if (d <= -1f + 1e-6f)
            {
                var axis = Vector3.Cross(Vector3.right, from);
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(Vector3.up, from);
                return AngleAxis(180f, axis);
            }
            var c = Vector3.Cross(from, to);
            return Normalize(new Quaternion(c.x, c.y, c.z, 1f + d));
        }

        public static Quaternion MatrixRotation(in Matrix4x4 m)
        {
            var r = new Vector3(m.m00, m.m10, m.m20).normalized;
            var u = new Vector3(m.m01, m.m11, m.m21).normalized;
            var f = new Vector3(m.m02, m.m12, m.m22).normalized;
            return FromBasis(r, u, f);
        }
    }
}
