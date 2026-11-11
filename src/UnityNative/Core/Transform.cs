// Transform 與 RectTransform 的原生半邊：階層、本地 TRS、世界矩陣快取、RectTransform 的 rect 與錨點。
// RectTransform 的 localPosition.xy 不另外存：由父物件的 rect、錨點、pivot、anchoredPosition 推出來
//   localPosition.xy = lerp(錨點在父 rect 的最小點, 最大點, pivot) + anchoredPosition
// 父物件不是 RectTransform 時，父 rect 當成原點上大小 0 的矩形，localPosition.xy 就等於 anchoredPosition。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public class NTransform : NComponent
    {
        public NTransform Parent;
        public readonly List<NTransform> Children = new List<NTransform>();

        Vector3 localPos;
        Quaternion localRot = Quaternion.identity;
        Vector3 localScale = Vector3.one;

        public bool IsRect;
        public Vector2 AnchorMin = new Vector2(0.5f, 0.5f), AnchorMax = new Vector2(0.5f, 0.5f);
        public Vector2 AnchoredPosition, SizeDelta = new Vector2(100, 100), Pivot = new Vector2(0.5f, 0.5f);
        public UnityEngine.Object DrivenBy;
        public DrivenTransformProperties DrivenProperties;
        Rect lastRect;
        bool lastRectValid;

        public bool HasChanged = true;
        bool worldDirty = true;
        Matrix4x4 l2w;
        Quaternion worldRot;
        public int HierarchyCapacity = 1;

        public Transform T => (Transform)Managed;

        // ───────── 本地 ─────────

        public Vector3 LocalPosition
        {
            get
            {
                if (!IsRect) return localPos;
                var r = AnchorReference();
                return new Vector3(r.x + AnchoredPosition.x, r.y + AnchoredPosition.y, localPos.z);
            }
            set
            {
                if (IsRect)
                {
                    var r = AnchorReference();
                    AnchoredPosition = new Vector2(value.x - r.x, value.y - r.y);
                }
                localPos = value;
                MarkDirty();
            }
        }

        public Quaternion LocalRotation
        {
            get => localRot;
            set { localRot = value; MarkDirty(); }
        }

        public Vector3 LocalScale
        {
            get => localScale;
            set { localScale = value; MarkDirty(); }
        }

        /// <summary>直接設值，不送通知。子物件也標成 dirty：MarkDirty 看到自己已經 dirty 就不往下傳（前提是子物件一定也 dirty），
        /// 只標自己的話，SetParent（worldPositionStays）搬過去之後子物件的世界座標會一直停在舊父物件那裡（10/19 對話立繪停在畫面外）</summary>
        public void InitLocal(Vector3 p, Quaternion r, Vector3 s)
        {
            localPos = p; localRot = r; localScale = s;
            worldDirty = true;
            for (int i = 0; i < Children.Count; i++) Children[i].MarkDirty();
        }

        public void SetAnchoredPosition(Vector2 v) { AnchoredPosition = v; MarkDirty(); }
        public void SetSizeDelta(Vector2 v) { SizeDelta = v; RectMaybeChanged(); }
        public void SetAnchorMin(Vector2 v) { AnchorMin = v; RectMaybeChanged(); }
        public void SetAnchorMax(Vector2 v) { AnchorMax = v; RectMaybeChanged(); }
        public void SetPivot(Vector2 v) { Pivot = v; RectMaybeChanged(); }

        // ───────── RectTransform ─────────

        public Vector2 ParentSize => Parent != null && Parent.IsRect ? Parent.RectSize : Vector2.zero;

        public Vector2 RectSize
        {
            get
            {
                var ps = ParentSize;
                return new Vector2(ps.x * (AnchorMax.x - AnchorMin.x) + SizeDelta.x, ps.y * (AnchorMax.y - AnchorMin.y) + SizeDelta.y);
            }
        }

        public Rect Rect
        {
            get
            {
                var s = RectSize;
                return new Rect(-Pivot.x * s.x, -Pivot.y * s.y, s.x, s.y);
            }
        }

        /// <summary>錨點在父物件本地座標裡、照 pivot 內插的參考點</summary>
        Vector2 AnchorReference()
        {
            if (Parent == null || !Parent.IsRect) return Vector2.zero;
            var pr = Parent.Rect;
            float ax = Mathf.LerpUnclamped(AnchorMin.x, AnchorMax.x, Pivot.x);
            float ay = Mathf.LerpUnclamped(AnchorMin.y, AnchorMax.y, Pivot.y);
            return new Vector2(pr.x + pr.width * ax, pr.y + pr.height * ay);
        }

        /// <summary>rect 改了：子物件的位置跟著動；大小有變的送 OnRectTransformDimensionsChange（uGUI 的排版、Graphic 靠它重建）</summary>
        public void RectMaybeChanged()
        {
            MarkDirty();
            if (!IsRect) return;
            CheckRect(true);
        }

        void CheckRect(bool notify)
        {
            var r = Rect;
            if (lastRectValid && r == lastRect) return;
            lastRect = r;
            lastRectValid = true;
            if (notify && Go != null && Go.ActiveInHierarchy) Lifecycle.SendToScripts(Go, Messages.OnRectTransformDimensionsChange);
            foreach (var c in Children.ToArray())
                if (c.IsRect) c.CheckRect(notify);
        }

        /// <summary>載入完、換父物件之後，把 rect 的基準記下來（不送通知）</summary>
        public void SyncRect()
        {
            if (!IsRect) { foreach (var c in Children) c.SyncRect(); return; }
            lastRect = Rect;
            lastRectValid = true;
            foreach (var c in Children) c.SyncRect();
        }

        // ───────── 世界 ─────────

        public void MarkDirty()
        {
            if (worldDirty && HasChanged) return;
            worldDirty = true;
            HasChanged = true;
            for (int i = 0; i < Children.Count; i++) Children[i].MarkDirty();
        }

        void UpdateWorld()
        {
            var local = MathUtil.TRS(LocalPosition, localRot, localScale);
            if (Parent == null) { l2w = local; worldRot = localRot; }
            else
            {
                if (Parent.worldDirty) Parent.UpdateWorld();
                l2w = MathUtil.Mul(Parent.l2w, local);
                worldRot = Parent.worldRot * localRot;
            }
            worldDirty = false;
        }

        public Matrix4x4 LocalToWorld { get { if (worldDirty) UpdateWorld(); return l2w; } }

        public Matrix4x4 WorldToLocal
        {
            get
            {
                MathUtil.Inverse(LocalToWorld, out var inv);
                return inv;
            }
        }

        public Vector3 Position
        {
            get { var m = LocalToWorld; return new Vector3(m.m03, m.m13, m.m23); }
            set
            {
                if (Parent == null) LocalPosition = value;
                else LocalPosition = MathUtil.MulPoint(Parent.WorldToLocal, value);
            }
        }

        public Quaternion Rotation
        {
            get { if (worldDirty) UpdateWorld(); return worldRot; }
            set
            {
                if (Parent == null) LocalRotation = value;
                else LocalRotation = MathUtil.Inverse(Parent.Rotation) * value;
            }
        }

        public Vector3 LossyScale
        {
            get
            {
                // 跟 Unity 一樣是近似值：把旋轉拿掉之後矩陣的對角線
                var m = LocalToWorld;
                var inv = MathUtil.Inverse(Rotation);
                var rs = MathUtil.Mul(MathUtil.TRS(Vector3.zero, inv, Vector3.one), m);
                return new Vector3(rs.m00, rs.m11, rs.m22);
            }
        }

        // ───────── 階層 ─────────

        public bool IsChildOf(NTransform p)
        {
            for (var t = this; t != null; t = t.Parent) if (t == p) return true;
            return false;
        }

        public NTransform Root
        {
            get { var t = this; while (t.Parent != null) t = t.Parent; return t; }
        }

        public int SiblingIndex
        {
            get
            {
                if (Parent != null) return Parent.Children.IndexOf(this);
                return Go?.Scene != null ? Go.Scene.Roots.IndexOf(Go) : 0;
            }
        }

        public void SetSiblingIndex(int index)
        {
            if (Parent != null)
            {
                var list = Parent.Children;
                list.Remove(this);
                index = Math.Clamp(index, 0, list.Count);
                list.Insert(index, this);
                Lifecycle.SendToScripts(Parent.Go, Messages.OnTransformChildrenChanged);
            }
            else if (Go?.Scene != null)
            {
                var list = Go.Scene.Roots;
                list.Remove(Go);
                index = Math.Clamp(index, 0, list.Count);
                list.Insert(index, Go);
            }
            Hierarchy.SiblingOrderChanged(this);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NTransform)src;
            IsRect = s.IsRect;
            AnchorMin = s.AnchorMin; AnchorMax = s.AnchorMax; AnchoredPosition = s.AnchoredPosition; SizeDelta = s.SizeDelta; Pivot = s.Pivot;
            localPos = s.localPos; localRot = s.localRot; localScale = s.localScale;
            DrivenBy = s.DrivenBy; DrivenProperties = s.DrivenProperties;
            worldDirty = true;
        }
    }
}
