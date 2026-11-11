// 2D 物理的查詢半邊：碰撞器（Box、Circle、Capsule、Polygon）的命中判定，給舊版滑鼠事件（SendMouseEvents → CameraRaycastHelper.RaycastTry2D）
// 與 Physics2D.OverlapPoint 用。不做剛體模擬、碰撞事件、Composite。Diceomancer 的書桌標籤、地圖節點、戰鬥的骰子與卡片都靠 OnMouse 系列打 2D 碰撞器。
// 規則同網頁版（diceomancer-web src/runtime/engine/mouseEvents.ts）：
//   - 碰撞器要 GameObject 在階層裡啟用、碰撞器 enabled；圖層照 layerMask 篩；isTrigger 照 Physics2D.queriesHitTriggers（預設打得到）；
//     世界縮放在 xy 上是 0 的不算（Diceomancer 用 scale 0 收起 RollNumberBackGround，底下的 SettingButton 碰撞器還開著）。
//   - 射線（3D）跟碰撞器所在的平面（物件世界座標的 z）求交點，交點換到碰撞器的本地座標比形狀；命中照離射線起點的距離排（Physics2D.GetRayIntersection 依深度排）。
//   - Box：offset ± size/2，再往外擴 edgeRadius。Circle：圓心是 offset 換到世界座標，半徑乘世界縮放 x、y 取大（Box2D 的圓不會變橢圓）。
//   - Capsule：CapsuleDirection2D Vertical（0）沿 y、半徑 size.x/2；Horizontal（1）沿 x、半徑 size.y/2。
//   - Polygon：m_Points.m_Paths（本地座標、不含 offset），落在任一條路徑內（奇偶規則）就算；多條重疊不挖洞（未查證）。
// 未查證：RaycastTry2D 命中有 Rigidbody2D 的碰撞器時，Unity 是否改送給剛體的 GameObject（這裡送碰撞器自己的；資料裡只有 1 個 Rigidbody2D）。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public class NCollider2D : NBehaviour
    {
        public bool IsTrigger;
        public Vector2 Offset;
        public static readonly List<NCollider2D> All = new List<NCollider2D>();

        public NCollider2D() => All.Add(this);

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Enabled = d["m_Enabled"] == null || Serializer.D(d["m_Enabled"]) != 0;
            IsTrigger = d["m_IsTrigger"] != null && Serializer.D(d["m_IsTrigger"]) != 0;
            if (d["m_Offset"] != null) Offset = (Vector2)Serializer.Convert(d["m_Offset"], typeof(Vector2), null);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NCollider2D)src;
            IsTrigger = s.IsTrigger; Offset = s.Offset;
        }

        public override void OnDestroyNative() => All.Remove(this);

        /// <summary>點（碰撞器的本地座標，z 不看）在不在形狀裡</summary>
        public virtual bool ContainsLocal(Vector2 p) => false;

        /// <summary>點（世界座標）在不在形狀裡：換到本地座標再比（Circle 自己算世界半徑）</summary>
        public virtual bool ContainsWorld(Vector3 p)
        {
            var tr = Tr;
            if (tr == null) return false;
            var l = tr.WorldToLocal.MultiplyPoint3x4(p);
            return ContainsLocal(new Vector2(l.x, l.y));
        }

        /// <summary>形狀上的點（本地座標），算 bounds 用</summary>
        protected virtual IEnumerable<Vector2> Outline() { yield return Offset; }

        public virtual Bounds WorldBounds
        {
            get
            {
                var m = Tr?.LocalToWorld ?? Matrix4x4.identity;
                bool first = true;
                var b = default(Bounds);
                foreach (var p in Outline())
                {
                    var w = m.MultiplyPoint3x4(new Vector3(p.x, p.y, 0));
                    if (first) { b = new Bounds(w, Vector3.zero); first = false; }
                    else b.Encapsulate(w);
                }
                return b;
            }
        }

        public bool Queryable(int layerMask, bool triggers)
            => Alive && IsActiveAndEnabled && Go != null && (layerMask & (1 << Go.Layer)) != 0 && (triggers || !IsTrigger) && !Degenerate;

        /// <summary>xy 平面上縮成 0（祖先 scale 0 收起來的 UI 常見）：形狀沒有面積，打不到（同網頁版：世界矩陣行列式接近 0 就跳過）</summary>
        public bool Degenerate
        {
            get
            {
                var m = Tr?.LocalToWorld ?? Matrix4x4.zero;
                return Mathf.Abs(m.m00 * m.m11 - m.m01 * m.m10) < 1e-12f;
            }
        }

        public static void Register()
        {
            Loader.Factories["BoxCollider2D"] = () => new NBoxCollider2D();
            Loader.Factories["CircleCollider2D"] = () => new NCircleCollider2D();
            Loader.Factories["CapsuleCollider2D"] = () => new NCapsuleCollider2D();
            Loader.Factories["PolygonCollider2D"] = () => new NPolygonCollider2D();
        }
    }

    public sealed class NBoxCollider2D : NCollider2D
    {
        public Vector2 Size = Vector2.one;
        public float EdgeRadius;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            if (d["m_Size"] != null) Size = (Vector2)Serializer.Convert(d["m_Size"], typeof(Vector2), null);
            if (d["m_EdgeRadius"] != null) EdgeRadius = (float)Serializer.D(d["m_EdgeRadius"]);
        }

        public override void CopyFrom(NComponent src) { base.CopyFrom(src); var s = (NBoxCollider2D)src; Size = s.Size; EdgeRadius = s.EdgeRadius; }

        public override bool ContainsLocal(Vector2 p)
            => Mathf.Abs(p.x - Offset.x) <= Size.x * 0.5f + EdgeRadius && Mathf.Abs(p.y - Offset.y) <= Size.y * 0.5f + EdgeRadius;

        protected override IEnumerable<Vector2> Outline()
        {
            var h = Size * 0.5f + new Vector2(EdgeRadius, EdgeRadius);
            yield return Offset + new Vector2(-h.x, -h.y); yield return Offset + new Vector2(h.x, -h.y);
            yield return Offset + new Vector2(-h.x, h.y); yield return Offset + new Vector2(h.x, h.y);
        }
    }

    public sealed class NCircleCollider2D : NCollider2D
    {
        public float Radius = 0.5f;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            if (d["m_Radius"] != null) Radius = (float)Serializer.D(d["m_Radius"]);
        }

        public override void CopyFrom(NComponent src) { base.CopyFrom(src); Radius = ((NCircleCollider2D)src).Radius; }

        float WorldRadius(Matrix4x4 m)
        {
            float sx = new Vector2(m.m00, m.m10).magnitude, sy = new Vector2(m.m01, m.m11).magnitude;
            return Radius * Mathf.Max(sx, sy);
        }

        public override bool ContainsLocal(Vector2 p) => (p - Offset).sqrMagnitude <= Radius * Radius;

        public override bool ContainsWorld(Vector3 p)
        {
            var tr = Tr;
            if (tr == null) return false;
            var m = tr.LocalToWorld;
            var c = m.MultiplyPoint3x4(new Vector3(Offset.x, Offset.y, 0));
            float r = WorldRadius(m);
            float dx = p.x - c.x, dy = p.y - c.y;
            return dx * dx + dy * dy <= r * r;
        }

        public override Bounds WorldBounds
        {
            get
            {
                var m = Tr?.LocalToWorld ?? Matrix4x4.identity;
                var c = m.MultiplyPoint3x4(new Vector3(Offset.x, Offset.y, 0));
                float r = WorldRadius(m);
                return new Bounds(c, new Vector3(r * 2, r * 2, 0));
            }
        }
    }

    public sealed class NCapsuleCollider2D : NCollider2D
    {
        public Vector2 Size = Vector2.one;
        public CapsuleDirection2D Direction;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            if (d["m_Size"] != null) Size = (Vector2)Serializer.Convert(d["m_Size"], typeof(Vector2), null);
            Direction = (CapsuleDirection2D)(int)Serializer.D(d["m_Direction"]);
        }

        public override void CopyFrom(NComponent src) { base.CopyFrom(src); var s = (NCapsuleCollider2D)src; Size = s.Size; Direction = s.Direction; }

        public override bool ContainsLocal(Vector2 p)
        {
            var q = p - Offset;
            bool vertical = Direction == CapsuleDirection2D.Vertical;
            float r = (vertical ? Size.x : Size.y) * 0.5f;
            float half = Mathf.Max(0, (vertical ? Size.y : Size.x) * 0.5f - r);
            if (vertical) q.y -= Mathf.Clamp(q.y, -half, half);
            else q.x -= Mathf.Clamp(q.x, -half, half);
            return q.sqrMagnitude <= r * r;
        }

        protected override IEnumerable<Vector2> Outline()
        {
            var h = Size * 0.5f;
            yield return Offset + new Vector2(-h.x, -h.y); yield return Offset + new Vector2(h.x, -h.y);
            yield return Offset + new Vector2(-h.x, h.y); yield return Offset + new Vector2(h.x, h.y);
        }
    }

    public sealed class NPolygonCollider2D : NCollider2D
    {
        public List<Vector2[]> Paths = new List<Vector2[]>();

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            Paths.Clear();
            if (d["m_Points"]?["m_Paths"] is JsonArray ps)
                foreach (var path in ps)
                    if (path is JsonArray pts)
                    {
                        var a = new Vector2[pts.Count];
                        for (int i = 0; i < a.Length; i++) a[i] = (Vector2)Serializer.Convert(pts[i], typeof(Vector2), null);
                        Paths.Add(a);
                    }
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            Paths = new List<Vector2[]>();
            foreach (var p in ((NPolygonCollider2D)src).Paths) Paths.Add((Vector2[])p.Clone());
        }

        public override bool ContainsLocal(Vector2 p)
        {
            float x = p.x - Offset.x, y = p.y - Offset.y;
            foreach (var path in Paths)
            {
                bool inside = false;
                for (int i = 0, j = path.Length - 1; i < path.Length; j = i++)
                {
                    var a = path[i]; var b = path[j];
                    if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                }
                if (inside) return true;
            }
            return false;
        }

        protected override IEnumerable<Vector2> Outline()
        {
            foreach (var path in Paths) foreach (var p in path) yield return p + Offset;
        }
    }

    public static class Physics2DQuery
    {
        public static bool QueriesHitTriggers = true;

        /// <summary>3D 射線打 2D 碰撞器（Physics2D.GetRayIntersection）：命中的碰撞器與距離，由近到遠</summary>
        public static List<(NCollider2D c, float dist, Vector3 point)> RayIntersection(Ray ray, float distance, int layerMask)
        {
            var hits = new List<(NCollider2D, float, Vector3)>();
            var dir = ray.direction;
            if (Mathf.Abs(dir.z) < 1e-12f) return hits;
            foreach (var c in All())
            {
                if (!c.Queryable(layerMask, QueriesHitTriggers)) continue;
                float t = (c.Tr.Position.z - ray.origin.z) / dir.z;
                if (t < 0 || t > distance) continue;
                var p = ray.origin + dir * t;
                if (!c.ContainsWorld(p)) continue;
                hits.Add((c, t, p));
            }
            hits.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return hits;
        }

        /// <summary>RayIntersection 換成 RaycastHit2D（distance 是沿正規化射線的長度，fraction ＝ distance ÷ 查詢距離）</summary>
        public static List<RaycastHit2D> RayHits(Vector3 origin, Vector3 direction, float distance, int layerMask)
        {
            var list = new List<RaycastHit2D>();
            if (direction.sqrMagnitude < 1e-20f) return list;
            foreach (var (c, d, p) in RayIntersection(new Ray(origin, direction), distance, layerMask))
                list.Add(new RaycastHit2D
                {
                    m_Point = new Vector2(p.x, p.y), m_Centroid = new Vector2(p.x, p.y), m_Distance = d,
                    m_Fraction = float.IsInfinity(distance) || distance <= 0 ? 0 : d / distance, m_Collider = c.InstanceId,
                });
            return list;
        }

        /// <summary>點（世界 xy）落在哪些碰撞器裡（Physics2D.OverlapPoint）；z 照 ContactFilter2D 的深度範圍篩</summary>
        public static List<NCollider2D> OverlapPoint(Vector2 point, ContactFilter2D filter)
        {
            var list = new List<NCollider2D>();
            int mask = filter.useLayerMask ? filter.layerMask.value : -1;
            foreach (var c in All())
            {
                if (!c.Queryable(mask, filter.useTriggers)) continue;
                if (filter.useDepth)
                {
                    float z = c.Tr.Position.z;
                    bool inside = z >= filter.minDepth && z <= filter.maxDepth;
                    if (filter.useOutsideDepth ? inside : !inside) continue;
                }
                var p = new Vector3(point.x, point.y, c.Tr.Position.z);
                if (c.ContainsWorld(p)) list.Add(c);
            }
            return list;
        }

        static NCollider2D[] All() => NCollider2D.All.ToArray();
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_CameraRaycastHelper
    {
        public static GameObject RaycastTry2D_Injected(Camera cam, ref Ray ray, float distance, int layerMask)
        {
            var l = Physics2DQuery.RayIntersection(ray, distance, layerMask);
            return l.Count > 0 ? l[0].c.Go?.Managed as GameObject : null;
        }

        /// <summary>3D：最近的 3D 碰撞器（Physics/Physics.cs）</summary>
        public static GameObject RaycastTry_Injected(Camera cam, ref Ray ray, float distance, int layerMask)
        {
            var l = Physics3D.Cast(ray, distance, layerMask, QueryTriggerInteraction.UseGlobal);
            if (l.Count == 0) return null;
            return (Native.ById(l[0].m_Collider) as NCollider)?.Go?.Managed as GameObject;
        }
    }

    public static unsafe partial class UnityEngine_Physics2D
    {
        public static bool get_queriesHitTriggers() => Physics2DQuery.QueriesHitTriggers;
        public static void set_queriesHitTriggers(bool value) => Physics2DQuery.QueriesHitTriggers = value;

        /// <summary>Physics2D.GetRayIntersectionAll：uGUI 的 GraphicRaycaster 在 blockingObjects 是 TwoD／All 時用最近一個的距離擋住更遠的圖形
        /// （SecondaryCanvas 的教學背景板靠自己的 BoxCollider2D 擋掉自己，教學按鈕才點得到）</summary>
        public static RaycastHit2D[] GetRayIntersectionAll_Internal_Injected(ref PhysicsScene2D scene, ref Vector3 origin, ref Vector3 direction, float distance, int layerMask)
            => Physics2DQuery.RayHits(origin, direction, distance, layerMask).ToArray();
    }

    public static unsafe partial class UnityEngine_PhysicsScene2D
    {
        public static bool IsValid_Internal_Injected(ref PhysicsScene2D scene) => true;
        public static bool IsEmpty_Internal_Injected(ref PhysicsScene2D scene) => NCollider2D.All.Count == 0;

        public static void GetRayIntersection_Internal_Injected(ref PhysicsScene2D scene, ref Vector3 origin, ref Vector3 direction, float distance, int layerMask, out RaycastHit2D ret)
        {
            var l = Physics2DQuery.RayHits(origin, direction, distance, layerMask);
            ret = l.Count > 0 ? l[0] : default;
        }

        public static int GetRayIntersectionArray_Internal_Injected(ref PhysicsScene2D scene, ref Vector3 origin, ref Vector3 direction, float distance, int layerMask, RaycastHit2D[] results)
        {
            var l = Physics2DQuery.RayHits(origin, direction, distance, layerMask);
            int n = Math.Min(l.Count, results?.Length ?? 0);
            for (int i = 0; i < n; i++) results[i] = l[i];
            return n;
        }

        public static int GetRayIntersectionList_Internal_Injected(ref PhysicsScene2D scene, ref Vector3 origin, ref Vector3 direction, float distance, int layerMask, List<RaycastHit2D> results)
        {
            var l = Physics2DQuery.RayHits(origin, direction, distance, layerMask);
            results.Clear();
            results.AddRange(l);
            return l.Count;
        }

        public static Collider2D OverlapPoint_Internal_Injected(ref PhysicsScene2D scene, ref Vector2 point, ref ContactFilter2D filter)
        {
            var l = Physics2DQuery.OverlapPoint(point, filter);
            return l.Count > 0 ? l[0].Managed as Collider2D : null;
        }

        public static int OverlapPointArray_Internal_Injected(ref PhysicsScene2D scene, ref Vector2 point, ref ContactFilter2D filter, Collider2D[] results)
        {
            var l = Physics2DQuery.OverlapPoint(point, filter);
            int n = Math.Min(l.Count, results?.Length ?? 0);
            for (int i = 0; i < n; i++) results[i] = l[i].Managed as Collider2D;
            return n;
        }

        public static int OverlapPointList_Internal_Injected(ref PhysicsScene2D scene, ref Vector2 point, ref ContactFilter2D filter, List<Collider2D> results)
        {
            var l = Physics2DQuery.OverlapPoint(point, filter);
            results.Clear();
            foreach (var c in l) results.Add(c.Managed as Collider2D);
            return l.Count;
        }
    }

    public static unsafe partial class UnityEngine_Collider2D
    {
        static NCollider2D C(Collider2D c) => Native.Self<NCollider2D>(c);
        public static bool get_isTrigger(Collider2D self) => C(self)?.IsTrigger ?? false;
        public static void set_isTrigger(Collider2D self, bool value) { var c = C(self); if (c != null) c.IsTrigger = value; }
        public static void get_offset_Injected(Collider2D self, out Vector2 ret) => ret = C(self)?.Offset ?? default;
        public static void set_offset_Injected(Collider2D self, ref Vector2 value) { var c = C(self); if (c != null) c.Offset = value; }
        public static void get_bounds_Injected(Collider2D self, out Bounds ret) => ret = C(self)?.WorldBounds ?? default;
        public static Rigidbody2D get_attachedRigidbody(Collider2D self) => null;
        public static bool get_usedByComposite(Collider2D self) => false;
        public static void set_usedByComposite(Collider2D self, bool value) { }
    }

    public static unsafe partial class UnityEngine_BoxCollider2D
    {
        static NBoxCollider2D B(BoxCollider2D c) => Native.Self<NBoxCollider2D>(c);
        public static void get_size_Injected(BoxCollider2D self, out Vector2 ret) => ret = B(self)?.Size ?? Vector2.one;
        public static void set_size_Injected(BoxCollider2D self, ref Vector2 value) { var b = B(self); if (b != null) b.Size = value; }
        public static float get_edgeRadius(BoxCollider2D self) => B(self)?.EdgeRadius ?? 0;
        public static void set_edgeRadius(BoxCollider2D self, float value) { var b = B(self); if (b != null) b.EdgeRadius = value; }
    }

    public static unsafe partial class UnityEngine_CircleCollider2D
    {
        static NCircleCollider2D C(CircleCollider2D c) => Native.Self<NCircleCollider2D>(c);
        public static float get_radius(CircleCollider2D self) => C(self)?.Radius ?? 0.5f;
        public static void set_radius(CircleCollider2D self, float value) { var c = C(self); if (c != null) c.Radius = value; }
    }

    public static unsafe partial class UnityEngine_CapsuleCollider2D
    {
        static NCapsuleCollider2D K(CapsuleCollider2D c) => Native.Self<NCapsuleCollider2D>(c);
        public static CapsuleDirection2D get_direction(CapsuleCollider2D self) => K(self)?.Direction ?? CapsuleDirection2D.Vertical;
        public static void set_direction(CapsuleCollider2D self, CapsuleDirection2D value) { var k = K(self); if (k != null) k.Direction = value; }
        public static void get_size_Injected(CapsuleCollider2D self, out Vector2 ret) => ret = K(self)?.Size ?? Vector2.one;
        public static void set_size_Injected(CapsuleCollider2D self, ref Vector2 value) { var k = K(self); if (k != null) k.Size = value; }
    }

    public static unsafe partial class UnityEngine_PolygonCollider2D
    {
        static NPolygonCollider2D P(PolygonCollider2D c) => Native.Self<NPolygonCollider2D>(c);
        public static int get_pathCount(PolygonCollider2D self) => P(self)?.Paths.Count ?? 0;
        public static void set_pathCount(PolygonCollider2D self, int value)
        {
            var p = P(self); if (p == null) return;
            while (p.Paths.Count > value) p.Paths.RemoveAt(p.Paths.Count - 1);
            while (p.Paths.Count < value) p.Paths.Add(Array.Empty<Vector2>());
        }
        public static int GetTotalPointCount(PolygonCollider2D self) { int n = 0; foreach (var a in P(self)?.Paths ?? new List<Vector2[]>()) n += a.Length; return n; }
        public static Vector2[] get_points(PolygonCollider2D self) { var p = P(self); return p != null && p.Paths.Count > 0 ? (Vector2[])p.Paths[0].Clone() : Array.Empty<Vector2>(); }
        public static void set_points(PolygonCollider2D self, Vector2[] value) { var p = P(self); if (p == null) return; p.Paths.Clear(); p.Paths.Add((Vector2[])(value ?? Array.Empty<Vector2>()).Clone()); }
        public static Vector2[] GetPath_Internal(PolygonCollider2D self, int index) { var p = P(self); return p != null && index < p.Paths.Count ? (Vector2[])p.Paths[index].Clone() : Array.Empty<Vector2>(); }
        public static void SetPath_Internal(PolygonCollider2D self, int index, Vector2[] points)
        {
            var p = P(self); if (p == null) return;
            while (p.Paths.Count <= index) p.Paths.Add(Array.Empty<Vector2>());
            p.Paths[index] = (Vector2[])(points ?? Array.Empty<Vector2>()).Clone();
        }
    }
}
