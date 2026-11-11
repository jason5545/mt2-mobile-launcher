// Transform、RectTransform 的 internal call。
using System;
using UnityEngine;

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Transform
    {
        static NTransform T(Transform self) => Native.Self<NTransform>(self);
        static Transform M(NTransform t) => (Transform)t?.Managed;

        public static int GetRotationOrderInternal(Transform self) => (int)RotationOrder.OrderZXY;
        public static void SetRotationOrderInternal(Transform self, RotationOrder rotationOrder) { }
        public static Transform GetParent(Transform self) => M(T(self).Parent);
        public static void SetParent(Transform self, Transform parent, bool worldPositionStays) =>
            Hierarchy.SetParent(T(self), Native.Of<NTransform>(parent), worldPositionStays);
        public static void GetPositionAndRotation(Transform self, out Vector3 position, out Quaternion rotation) { var t = T(self); position = t.Position; rotation = t.Rotation; }
        public static void GetLocalPositionAndRotation(Transform self, out Vector3 localPosition, out Quaternion localRotation) { var t = T(self); localPosition = t.LocalPosition; localRotation = t.LocalRotation; }

        public static void TransformDirections(Transform self, Vector3* directions, int count, Vector3* transformedDirections, int transformedCount)
        { var r = T(self).Rotation; for (int i = 0; i < count; i++) transformedDirections[i] = r * directions[i]; }
        public static void InverseTransformDirections(Transform self, Vector3* directions, int count, Vector3* transformedDirections, int transformedCount)
        { var r = MathUtil.Inverse(T(self).Rotation); for (int i = 0; i < count; i++) transformedDirections[i] = r * directions[i]; }
        public static void TransformVectors(Transform self, Vector3* vectors, int count, Vector3* transformedVectors, int transformedCount)
        { var m = T(self).LocalToWorld; for (int i = 0; i < count; i++) transformedVectors[i] = MathUtil.MulVector(m, vectors[i]); }
        public static void InverseTransformVectors(Transform self, Vector3* vectors, int count, Vector3* transformedVectors, int transformedCount)
        { var m = T(self).WorldToLocal; for (int i = 0; i < count; i++) transformedVectors[i] = MathUtil.MulVector(m, vectors[i]); }
        public static void TransformPoints(Transform self, Vector3* positions, int count, Vector3* transformedPositions, int transformedCount)
        { var m = T(self).LocalToWorld; for (int i = 0; i < count; i++) transformedPositions[i] = MathUtil.MulPoint(m, positions[i]); }
        public static void InverseTransformPoints(Transform self, Vector3* positions, int count, Vector3* transformedPositions, int transformedCount)
        { var m = T(self).WorldToLocal; for (int i = 0; i < count; i++) transformedPositions[i] = MathUtil.MulPoint(m, positions[i]); }

        public static Transform GetRoot(Transform self) => M(T(self).Root);
        public static int get_childCount(Transform self) => T(self).Children.Count;
        public static void DetachChildren(Transform self)
        {
            foreach (var c in T(self).Children.ToArray()) Hierarchy.SetParent(c, null, true);
        }
        public static void SetAsFirstSibling(Transform self) => T(self).SetSiblingIndex(0);
        public static void SetAsLastSibling(Transform self) => T(self).SetSiblingIndex(int.MaxValue);
        public static void SetSiblingIndex(Transform self, int index) => T(self).SetSiblingIndex(index);
        public static void MoveAfterSibling(Transform self, Transform transform, bool notifyEditorAndMarkDirty)
        {
            var t = T(self);
            var other = Native.Of<NTransform>(transform);
            if (other == null) { t.SetSiblingIndex(0); return; }
            int i = other.SiblingIndex;
            t.SetSiblingIndex(t.SiblingIndex < i ? i : i + 1);
        }
        public static int GetSiblingIndex(Transform self) => T(self).SiblingIndex;

        public static Transform FindRelativeTransformWithPath(Transform transform, string path, bool isActiveOnly)
        {
            var t = T(transform);
            if (string.IsNullOrEmpty(path)) return transform;
            var parts = path.Split('/');
            return M(Comps.FindPath(t, parts, 0, isActiveOnly));
        }

        public static void SendTransformChangedScale(Transform self) { }
        public static bool IsChildOf(Transform self, Transform parent) => Native.Of<NTransform>(parent) is NTransform p && T(self).IsChildOf(p);
        public static bool get_hasChanged(Transform self) => T(self).HasChanged;
        public static void set_hasChanged(Transform self, bool value) => T(self).HasChanged = value;
        public static Transform GetChild(Transform self, int index)
        {
            var ch = T(self).Children;
            if ((uint)index >= (uint)ch.Count) throw new UnityException("Transform child out of bounds");
            return M(ch[index]);
        }
        public static int GetChildCount(Transform self) => T(self).Children.Count;
        public static int internal_getHierarchyCapacity(Transform self) => Math.Max(T(self).HierarchyCapacity, internal_getHierarchyCount(self));
        public static void internal_setHierarchyCapacity(Transform self, int value) => T(self).HierarchyCapacity = value;
        public static int internal_getHierarchyCount(Transform self)
        {
            int n = 0;
            void Count(NTransform t) { n++; foreach (var c in t.Children) Count(c); }
            Count(T(self).Root);
            return n;
        }
        public static bool IsNonUniformScaleTransform(Transform self)
        {
            var s = T(self).LossyScale;
            return Mathf.Abs(s.x - s.y) > 1e-5f || Mathf.Abs(s.y - s.z) > 1e-5f;
        }
        public static void SetConstrainProportionsScale(Transform self, bool isLinked) { }
        public static bool IsConstrainProportionsScale(Transform self) => false;

        public static void get_position_Injected(Transform self, out Vector3 ret) => ret = T(self).Position;
        public static void set_position_Injected(Transform self, ref Vector3 value) => T(self).Position = value;
        public static void get_localPosition_Injected(Transform self, out Vector3 ret) => ret = T(self).LocalPosition;
        public static void set_localPosition_Injected(Transform self, ref Vector3 value) => T(self).LocalPosition = value;

        public static void GetLocalEulerAngles_Injected(Transform self, RotationOrder order, out Vector3 ret) =>
            ret = Quaternion.Internal_MakePositive(MathUtil.ToEulerRad(T(self).LocalRotation) * Mathf.Rad2Deg);
        public static void SetLocalEulerAngles_Injected(Transform self, ref Vector3 euler, RotationOrder order) =>
            T(self).LocalRotation = MathUtil.FromEulerRad(euler * Mathf.Deg2Rad);
        public static void SetLocalEulerHint_Injected(Transform self, ref Vector3 euler) { }

        public static void get_rotation_Injected(Transform self, out Quaternion ret) => ret = T(self).Rotation;
        public static void set_rotation_Injected(Transform self, ref Quaternion value) => T(self).Rotation = MathUtil.Normalize(value);
        public static void get_localRotation_Injected(Transform self, out Quaternion ret) => ret = T(self).LocalRotation;
        public static void set_localRotation_Injected(Transform self, ref Quaternion value) => T(self).LocalRotation = MathUtil.Normalize(value);
        public static void get_localScale_Injected(Transform self, out Vector3 ret) => ret = T(self).LocalScale;
        public static void set_localScale_Injected(Transform self, ref Vector3 value) => T(self).LocalScale = value;
        public static void get_worldToLocalMatrix_Injected(Transform self, out Matrix4x4 ret) => ret = T(self).WorldToLocal;
        public static void get_localToWorldMatrix_Injected(Transform self, out Matrix4x4 ret) => ret = T(self).LocalToWorld;
        public static void SetPositionAndRotation_Injected(Transform self, ref Vector3 position, ref Quaternion rotation)
        { var t = T(self); t.Rotation = rotation; t.Position = position; }
        public static void SetLocalPositionAndRotation_Injected(Transform self, ref Vector3 localPosition, ref Quaternion localRotation)
        { var t = T(self); t.LocalRotation = localRotation; t.LocalPosition = localPosition; }

        public static void RotateAroundInternal_Injected(Transform self, ref Vector3 axis, float angle)
        {
            var t = T(self);
            t.Rotation = MathUtil.Normalize(MathUtil.AngleAxis(angle * Mathf.Rad2Deg, axis) * t.Rotation);
        }

        public static void Internal_LookAt_Injected(Transform self, ref Vector3 worldPosition, ref Vector3 worldUp)
        {
            var t = T(self);
            var f = worldPosition - t.Position;
            if (f.sqrMagnitude < 1e-12f) return;
            t.Rotation = MathUtil.LookRotation(f, worldUp);
        }

        public static void TransformDirection_Injected(Transform self, ref Vector3 direction, out Vector3 ret) => ret = T(self).Rotation * direction;
        public static void InverseTransformDirection_Injected(Transform self, ref Vector3 direction, out Vector3 ret) => ret = MathUtil.Inverse(T(self).Rotation) * direction;
        public static void TransformVector_Injected(Transform self, ref Vector3 vector, out Vector3 ret) => ret = MathUtil.MulVector(T(self).LocalToWorld, vector);
        public static void InverseTransformVector_Injected(Transform self, ref Vector3 vector, out Vector3 ret) => ret = MathUtil.MulVector(T(self).WorldToLocal, vector);
        public static void TransformPoint_Injected(Transform self, ref Vector3 position, out Vector3 ret) => ret = MathUtil.MulPoint(T(self).LocalToWorld, position);
        public static void InverseTransformPoint_Injected(Transform self, ref Vector3 position, out Vector3 ret) => ret = MathUtil.MulPoint(T(self).WorldToLocal, position);
        public static void get_lossyScale_Injected(Transform self, out Vector3 ret) => ret = T(self).LossyScale;

        public static void RotateAround_Injected(Transform self, ref Vector3 axis, float angle)
        {
            var t = T(self);
            t.Rotation = MathUtil.Normalize(MathUtil.AngleAxis(angle * Mathf.Rad2Deg, axis) * t.Rotation);
        }

        public static void RotateAroundLocal_Injected(Transform self, ref Vector3 axis, float angle)
        {
            var t = T(self);
            t.LocalRotation = MathUtil.Normalize(t.LocalRotation * MathUtil.AngleAxis(angle * Mathf.Rad2Deg, axis));
        }
    }

    public static unsafe partial class UnityEngine_RectTransform
    {
        static NTransform T(RectTransform self) => Native.Self<NTransform>(self);

        public static UnityEngine.Object get_drivenByObject(RectTransform self) => T(self).DrivenBy;
        public static void set_drivenByObject(RectTransform self, UnityEngine.Object value) => T(self).DrivenBy = value;
        public static DrivenTransformProperties get_drivenProperties(RectTransform self) => T(self).DrivenProperties;
        public static void set_drivenProperties(RectTransform self, DrivenTransformProperties value) => T(self).DrivenProperties = value;
        public static void ForceUpdateRectTransforms(RectTransform self) => T(self).RectMaybeChanged();
        public static void get_rect_Injected(RectTransform self, out Rect ret) => ret = T(self).Rect;
        public static void get_anchorMin_Injected(RectTransform self, out Vector2 ret) => ret = T(self).AnchorMin;
        public static void set_anchorMin_Injected(RectTransform self, ref Vector2 value) { var t = T(self); if (t.AnchorMin != value) t.SetAnchorMin(value); }
        public static void get_anchorMax_Injected(RectTransform self, out Vector2 ret) => ret = T(self).AnchorMax;
        public static void set_anchorMax_Injected(RectTransform self, ref Vector2 value) { var t = T(self); if (t.AnchorMax != value) t.SetAnchorMax(value); }
        public static void get_anchoredPosition_Injected(RectTransform self, out Vector2 ret) => ret = T(self).AnchoredPosition;
        public static void set_anchoredPosition_Injected(RectTransform self, ref Vector2 value) { var t = T(self); if (t.AnchoredPosition != value) t.SetAnchoredPosition(value); }
        public static void get_sizeDelta_Injected(RectTransform self, out Vector2 ret) => ret = T(self).SizeDelta;
        public static void set_sizeDelta_Injected(RectTransform self, ref Vector2 value) { var t = T(self); if (t.SizeDelta != value) t.SetSizeDelta(value); }
        public static void get_pivot_Injected(RectTransform self, out Vector2 ret) => ret = T(self).Pivot;
        public static void set_pivot_Injected(RectTransform self, ref Vector2 value) { var t = T(self); if (t.Pivot != value) t.SetPivot(value); }
    }
}
