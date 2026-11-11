// 3D 物理的查詢半邊：碰撞器（BoxCollider、SphereCollider、CapsuleCollider）與射線（Physics.Raycast／RaycastAll／RaycastNonAlloc）。
// 不做剛體模擬、碰撞事件；uGUI 的 PhysicsRaycaster 靠射線打 3D 碰撞器（MT2 戰鬥的房間與出牌落點 SpawnPointUI 就是 BoxCollider，
// 10/19 以前原生版沒有，手牌拖到房間放不下去）。規則同 TS 版（PhysicsRaycaster 打 BoxCollider、SphereCollider）：
//   - 碰撞器要 GameObject 在階層裡啟用、碰撞器 enabled；圖層照 layerMask 篩；isTrigger 的照 QueryTriggerInteraction（UseGlobal 看 Physics.queriesHitTriggers，預設打得到）。
//   - Box：射線換到碰撞器的本地座標做 slab 測試（含旋轉與不等比縮放），命中點換回世界座標算距離與法線。
//   - Sphere：中心換到世界座標，半徑乘 lossyScale 三軸取最大（Unity 的規則）。Capsule：用兩端球心連線上最近點的球近似（只求命中與距離）。
//   - 從碰撞器裡面射出不算命中（同 PhysX 的射線查詢預設不報起點在形狀內的 initial overlap；未查證 Unity 的設定）。
// MeshCollider 沒做（MT2 戰鬥與地圖用不到；碰到就當打不到）。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public class NCollider : NBehaviour
    {
        public bool IsTrigger;
        public Vector3 Center;
        /// <summary>建過、還沒銷毀的碰撞器（AddComponent 建的沒有 Load，建構時就登記；查詢時再看 Alive 與啟用）</summary>
        public static readonly List<NCollider> All = new List<NCollider>();

        public NCollider() => All.Add(this);

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Enabled = d["m_Enabled"] == null || Serializer.D(d["m_Enabled"]) != 0;
            IsTrigger = d["m_IsTrigger"] != null && Serializer.D(d["m_IsTrigger"]) != 0;
            if (d["m_Center"] != null) Center = (Vector3)Serializer.Convert(d["m_Center"], typeof(Vector3), null);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NCollider)src;
            IsTrigger = s.IsTrigger; Center = s.Center;
        }

        public override void OnDestroyNative() => All.Remove(this);

        /// <summary>射線（世界座標、dir 已正規化）打這個碰撞器：命中回 true 與距離、點、法線</summary>
        public virtual bool Hit(Ray ray, out float dist, out Vector3 point, out Vector3 normal) { dist = 0; point = normal = default; return false; }

        public virtual Bounds WorldBounds => new Bounds(Tr?.Position ?? Vector3.zero, Vector3.zero);

        public static void Register()
        {
            Loader.Factories["BoxCollider"] = () => new NBoxCollider();
            Loader.Factories["SphereCollider"] = () => new NSphereCollider();
            Loader.Factories["CapsuleCollider"] = () => new NCapsuleCollider();
        }
    }

    public sealed class NBoxCollider : NCollider
    {
        public Vector3 Size = Vector3.one;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            if (d["m_Size"] != null) Size = (Vector3)Serializer.Convert(d["m_Size"], typeof(Vector3), null);
        }

        public override void CopyFrom(NComponent src) { base.CopyFrom(src); Size = ((NBoxCollider)src).Size; }

        public override bool Hit(Ray ray, out float dist, out Vector3 point, out Vector3 normal)
        {
            dist = 0; point = normal = default;
            var tr = Tr;
            if (tr == null) return false;
            var w2l = tr.WorldToLocal;
            var o = w2l.MultiplyPoint3x4(ray.origin);
            var d = w2l.MultiplyVector(ray.direction);
            var min = Center - Size * 0.5f;
            var max = Center + Size * 0.5f;
            float tMin = float.NegativeInfinity, tMax = float.PositiveInfinity;
            int axis = -1; float sign = 0;
            for (int i = 0; i < 3; i++)
            {
                if (Mathf.Abs(d[i]) < 1e-12f)
                {
                    if (o[i] < min[i] || o[i] > max[i]) return false;
                    continue;
                }
                float t1 = (min[i] - o[i]) / d[i], t2 = (max[i] - o[i]) / d[i];
                float s = -1;
                if (t1 > t2) { (t1, t2) = (t2, t1); s = 1; }
                if (t1 > tMin) { tMin = t1; axis = i; sign = s; }
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return false;
            }
            if (tMin < 0 || axis < 0) return false; // 起點在盒子裡或盒子在後面
            var lp = o + d * tMin;
            point = tr.LocalToWorld.MultiplyPoint3x4(lp);
            dist = Vector3.Distance(ray.origin, point);
            var ln = Vector3.zero; ln[axis] = sign;
            normal = w2l.transpose.MultiplyVector(ln).normalized;
            return true;
        }

        public override Bounds WorldBounds
        {
            get
            {
                var m = Tr?.LocalToWorld ?? Matrix4x4.identity;
                var b = new Bounds(m.MultiplyPoint3x4(Center), Vector3.zero);
                for (int i = 0; i < 8; i++)
                    b.Encapsulate(m.MultiplyPoint3x4(Center + Vector3.Scale(Size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                return b;
            }
        }
    }

    public sealed class NSphereCollider : NCollider
    {
        public float Radius = 0.5f;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            if (d["m_Radius"] != null) Radius = (float)Serializer.D(d["m_Radius"]);
        }

        public override void CopyFrom(NComponent src) { base.CopyFrom(src); Radius = ((NSphereCollider)src).Radius; }

        float WorldRadius { get { var s = Tr?.LossyScale ?? Vector3.one; return Radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)); } }

        public override bool Hit(Ray ray, out float dist, out Vector3 point, out Vector3 normal)
        {
            var c = (Tr?.LocalToWorld ?? Matrix4x4.identity).MultiplyPoint3x4(Center);
            return Physics3D.RaySphere(ray, c, WorldRadius, out dist, out point, out normal);
        }

        public override Bounds WorldBounds => new Bounds((Tr?.LocalToWorld ?? Matrix4x4.identity).MultiplyPoint3x4(Center), Vector3.one * (2 * WorldRadius));
    }

    public sealed class NCapsuleCollider : NCollider
    {
        public float Radius = 0.5f, Height = 2f;
        public int Direction = 1;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            if (d["m_Radius"] != null) Radius = (float)Serializer.D(d["m_Radius"]);
            if (d["m_Height"] != null) Height = (float)Serializer.D(d["m_Height"]);
            if (d["m_Direction"] != null) Direction = (int)Serializer.D(d["m_Direction"]);
        }

        public override void CopyFrom(NComponent src) { base.CopyFrom(src); var s = (NCapsuleCollider)src; Radius = s.Radius; Height = s.Height; Direction = s.Direction; }

        public override bool Hit(Ray ray, out float dist, out Vector3 point, out Vector3 normal)
        {
            var m = Tr?.LocalToWorld ?? Matrix4x4.identity;
            var s = Tr?.LossyScale ?? Vector3.one;
            var axis = Vector3.zero; axis[Direction] = 1;
            float r = Radius * Mathf.Max(Mathf.Abs(s[(Direction + 1) % 3]), Mathf.Abs(s[(Direction + 2) % 3]));
            float half = Mathf.Max(0, Height * 0.5f * Mathf.Abs(s[Direction]) - r);
            var c = m.MultiplyPoint3x4(Center);
            var a = m.MultiplyVector(axis).normalized;
            // 射線與軸線最近的那一點當球心（近似）
            var w = ray.origin - c;
            float b = Vector3.Dot(a, ray.direction), dd = Vector3.Dot(a, w), e = Vector3.Dot(ray.direction, w);
            float den = 1 - b * b;
            float sAxis = den > 1e-6f ? (dd - b * e) / den : 0;
            sAxis = Mathf.Clamp(sAxis, -half, half);
            return Physics3D.RaySphere(ray, c + a * sAxis, r, out dist, out point, out normal);
        }
    }

    public static class Physics3D
    {
        public static bool QueriesHitTriggers = true;

        public static bool RaySphere(Ray ray, Vector3 c, float r, out float dist, out Vector3 point, out Vector3 normal)
        {
            dist = 0; point = normal = default;
            var oc = ray.origin - c;
            float b = Vector3.Dot(oc, ray.direction);
            float cc = oc.sqrMagnitude - r * r;
            if (cc <= 0) return false; // 起點在球裡
            float disc = b * b - cc;
            if (disc < 0) return false;
            float t = -b - Mathf.Sqrt(disc);
            if (t < 0) return false;
            dist = t;
            point = ray.origin + ray.direction * t;
            normal = (point - c).normalized;
            return true;
        }

        /// <summary>全部命中，照距離由近到遠</summary>
        public static List<RaycastHit> Cast(Ray ray, float maxDistance, int mask, QueryTriggerInteraction q)
        {
            var hits = new List<RaycastHit>();
            var dir = ray.direction;
            if (dir.sqrMagnitude < 1e-20f) return hits;
            ray = new Ray(ray.origin, dir.normalized);
            bool triggers = q == QueryTriggerInteraction.Collide || (q == QueryTriggerInteraction.UseGlobal && QueriesHitTriggers);
            foreach (var c in NCollider.All.ToArray())
            {
                if (!c.Alive || !c.IsActiveAndEnabled) continue;
                if ((mask & (1 << c.Go.Layer)) == 0) continue;
                if (c.IsTrigger && !triggers) continue;
                if (!c.Hit(ray, out var d, out var p, out var n) || d > maxDistance) continue;
                hits.Add(new RaycastHit { m_Point = p, m_Normal = n, m_Distance = d, m_Collider = c.InstanceId });
            }
            hits.Sort((a, b) => a.m_Distance.CompareTo(b.m_Distance));
            return hits;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Physics
    {
        public static void get_defaultPhysicsScene_Injected(out PhysicsScene ret) => ret = default;
        public static Collider GetColliderByInstanceID(int instanceID) => Native.ById(instanceID)?.Managed as Collider;
        public static bool get_queriesHitTriggers() => Physics3D.QueriesHitTriggers;
        public static void set_queriesHitTriggers(bool value) => Physics3D.QueriesHitTriggers = value;
        public static RaycastHit[] Internal_RaycastAll_Injected(ref PhysicsScene physicsScene, ref Ray ray, float maxDistance, int mask, QueryTriggerInteraction queryTriggerInteraction)
            => Physics3D.Cast(ray, maxDistance, mask, queryTriggerInteraction).ToArray();
    }

    public static unsafe partial class UnityEngine_PhysicsScene
    {
        public static bool IsValid_Internal_Injected(ref PhysicsScene physicsScene) => true;
        public static bool IsEmpty_Internal_Injected(ref PhysicsScene physicsScene) => NCollider.All.Count == 0;
        public static bool Internal_RaycastTest_Injected(ref PhysicsScene physicsScene, ref Ray ray, float maxDistance, int layerMask, QueryTriggerInteraction queryTriggerInteraction)
            => Physics3D.Cast(ray, maxDistance, layerMask, queryTriggerInteraction).Count > 0;
        public static bool Internal_Raycast_Injected(ref PhysicsScene physicsScene, ref Ray ray, float maxDistance, ref RaycastHit hit, int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            var l = Physics3D.Cast(ray, maxDistance, layerMask, queryTriggerInteraction);
            if (l.Count == 0) return false;
            hit = l[0];
            return true;
        }
        public static int Internal_RaycastNonAlloc_Injected(ref PhysicsScene physicsScene, ref Ray ray, RaycastHit[] raycastHits, float maxDistance, int mask, QueryTriggerInteraction queryTriggerInteraction)
        {
            var l = Physics3D.Cast(ray, maxDistance, mask, queryTriggerInteraction);
            int n = Math.Min(l.Count, raycastHits?.Length ?? 0);
            for (int i = 0; i < n; i++) raycastHits[i] = l[i];
            return n;
        }
    }

    public static unsafe partial class UnityEngine_Collider
    {
        static NCollider C(Collider c) => Native.Self<NCollider>(c);
        public static bool get_enabled(Collider self) => C(self)?.Enabled ?? false;
        public static void set_enabled(Collider self, bool value) { var c = C(self); if (c != null) c.Enabled = value; }
        public static bool get_isTrigger(Collider self) => C(self)?.IsTrigger ?? false;
        public static void set_isTrigger(Collider self, bool value) { var c = C(self); if (c != null) c.IsTrigger = value; }
        public static Rigidbody get_attachedRigidbody(Collider self) => null;
        public static void get_bounds_Injected(Collider self, out Bounds ret) => ret = C(self)?.WorldBounds ?? default;
        public static void Raycast_Injected(Collider self, ref Ray ray, float maxDistance, ref bool hasHit, out RaycastHit ret)
        {
            ret = default;
            var c = C(self);
            var r = new Ray(ray.origin, ray.direction.normalized);
            hasHit = c != null && c.Hit(r, out var d, out var p, out var n) && d <= maxDistance;
            if (hasHit) { c.Hit(r, out var d2, out var p2, out var n2); ret = new RaycastHit { m_Point = p2, m_Normal = n2, m_Distance = d2, m_Collider = c.InstanceId }; }
        }
    }

    public static unsafe partial class UnityEngine_BoxCollider
    {
        static NBoxCollider B(BoxCollider c) => Native.Self<NBoxCollider>(c);
        public static void get_center_Injected(BoxCollider self, out Vector3 ret) => ret = B(self)?.Center ?? default;
        public static void set_center_Injected(BoxCollider self, ref Vector3 value) { var b = B(self); if (b != null) b.Center = value; }
        public static void get_size_Injected(BoxCollider self, out Vector3 ret) => ret = B(self)?.Size ?? Vector3.one;
        public static void set_size_Injected(BoxCollider self, ref Vector3 value) { var b = B(self); if (b != null) b.Size = value; }
    }

    public static unsafe partial class UnityEngine_SphereCollider
    {
        static NSphereCollider S(SphereCollider c) => Native.Self<NSphereCollider>(c);
        public static float get_radius(SphereCollider self) => S(self)?.Radius ?? 0.5f;
        public static void set_radius(SphereCollider self, float value) { var s = S(self); if (s != null) s.Radius = value; }
        public static void get_center_Injected(SphereCollider self, out Vector3 ret) => ret = S(self)?.Center ?? default;
        public static void set_center_Injected(SphereCollider self, ref Vector3 value) { var s = S(self); if (s != null) s.Center = value; }
    }

    public static unsafe partial class UnityEngine_CapsuleCollider
    {
        static NCapsuleCollider K(CapsuleCollider c) => Native.Self<NCapsuleCollider>(c);
        public static float get_radius(CapsuleCollider self) => K(self)?.Radius ?? 0.5f;
        public static void set_radius(CapsuleCollider self, float value) { var k = K(self); if (k != null) k.Radius = value; }
        public static float get_height(CapsuleCollider self) => K(self)?.Height ?? 2f;
        public static void set_height(CapsuleCollider self, float value) { var k = K(self); if (k != null) k.Height = value; }
        public static int get_direction(CapsuleCollider self) => K(self)?.Direction ?? 1;
        public static void set_direction(CapsuleCollider self, int value) { var k = K(self); if (k != null) k.Direction = value; }
        public static void get_center_Injected(CapsuleCollider self, out Vector3 ret) => ret = K(self)?.Center ?? default;
        public static void set_center_Injected(CapsuleCollider self, ref Vector3 value) { var k = K(self); if (k != null) k.Center = value; }
    }
}
