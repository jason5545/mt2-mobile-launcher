// 階層操作：SetParent、SetActive、建立與摧毀 GameObject，以及它們引發的訊息與啟用狀態。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public static class Hierarchy
    {
        public static event Action<NTransform> OnSiblingOrderChanged;
        public static event Action<NTransform> OnParentChanged;
        public static void SiblingOrderChanged(NTransform t) => OnSiblingOrderChanged?.Invoke(t);

        // ───────── 建立 ─────────

        public static NGameObject NewGameObject(string name, GameObject managed = null, bool rect = false, NScene scene = null, bool persistent = false)
        {
            managed ??= (GameObject)Native.NewShell(typeof(GameObject));
            var go = Native.Bind(new NGameObject { Name = name ?? "" }, managed, persistent);
            var trType = rect ? typeof(RectTransform) : typeof(Transform);
            var tr = Native.Bind(new NTransform { IsRect = rect, Go = go }, Native.NewShell(trType), persistent);
            go.Transform = tr;
            go.Components.Add(tr);
            go.ActiveSelf = true;
            if (!persistent)
            {
                go.Scene = scene ?? Scenes.Active;
                go.Scene?.Roots.Add(go);
                go.ActiveInHierarchy = true;
            }
            return go;
        }

        // ───────── SetParent ─────────

        public static void SetParent(NTransform t, NTransform parent, bool worldPositionStays)
        {
            if (t.Parent == parent) return;
            if (parent != null && parent.IsChildOf(t)) { Debug.LogError("Cannot set parent: would create a cycle"); return; }
            var go = t.Go;
            Lifecycle.SendToScriptsRecursive(go, Messages.OnBeforeTransformParentChanged);

            Vector3 wp = default; Quaternion wr = default; Vector3 ws = default;
            if (worldPositionStays) { wp = t.Position; wr = t.Rotation; ws = t.LossyScale; }

            var oldParent = t.Parent;
            if (oldParent != null) oldParent.Children.Remove(t);
            else go.Scene?.Roots.Remove(go);
            t.Parent = parent;
            if (parent != null)
            {
                parent.Children.Add(t);
                // 換到別的場景的父物件底下，整棵子樹跟著搬
                if (parent.Go.Scene != go.Scene) MoveToScene(go, parent.Go.Scene, false);
            }
            else
            {
                go.Scene ??= Scenes.Active;
                go.Scene?.Roots.Add(go);
            }

            if (worldPositionStays)
            {
                if (parent == null) { t.InitLocal(wp, wr, ScaleDiv(ws, Vector3.one)); }
                else
                {
                    var lp = MathUtil.MulPoint(parent.WorldToLocal, wp);
                    var lr = MathUtil.Inverse(parent.Rotation) * wr;
                    var ps = parent.LossyScale;
                    t.InitLocal(t.IsRect ? lp : lp, lr, ScaleDiv(ws, ps));
                    if (t.IsRect) t.LocalPosition = lp;
                }
            }
            t.MarkDirty();
            t.SyncRect();

            if (oldParent != null) Lifecycle.SendToScripts(oldParent.Go, Messages.OnTransformChildrenChanged);
            if (parent != null) Lifecycle.SendToScripts(parent.Go, Messages.OnTransformChildrenChanged);
            Lifecycle.SendToScriptsRecursive(go, Messages.OnTransformParentChanged);
            Lifecycle.SendToScriptsRecursive(go, Messages.OnCanvasHierarchyChanged);
            OnParentChanged?.Invoke(t);

            UpdateActive(go);
        }

        static Vector3 ScaleDiv(Vector3 a, Vector3 b) =>
            new Vector3(b.x != 0 ? a.x / b.x : 0, b.y != 0 ? a.y / b.y : 0, b.z != 0 ? a.z / b.z : 0);

        public static void MoveToScene(NGameObject go, NScene scene, bool asRoot)
        {
            void Walk(NGameObject g)
            {
                g.Scene = scene;
                foreach (var c in g.Transform.Children) if (c.Go != null) Walk(c.Go);
            }
            if (asRoot && go.Parent == null)
            {
                go.Scene?.Roots.Remove(go);
                scene?.Roots.Add(go);
            }
            Walk(go);
        }

        // ───────── 啟用 ─────────

        public static void SetActive(NGameObject go, bool value)
        {
            if (go.ActiveSelf == value) return;
            go.ActiveSelf = value;
            UpdateActive(go);
            // 父物件收 OnTransformChildrenChanged：uGUI 的 LayoutGroup 靠它在子物件開關時重排（player 的 DrivenRectTransformTracker 不記驅動者，
            // 子物件的 LayoutElement.OnDisable 那時 isActiveAndEnabled 已經是 false，都標不到）。Unity 原生端怎麼通知未查證，結果照 TS 版：
            // battle_intro 沒有試煉時關掉的「Enemy relics container root」不佔位，「開始戰鬥」緊接說明文字（10/19）
            var parent = go.Parent;
            if (parent != null && parent.ActiveInHierarchy) Lifecycle.SendToScripts(parent, Messages.OnTransformChildrenChanged);
        }

        /// <summary>重算 go 子樹的 ActiveInHierarchy，變成啟用的叫 Awake／OnEnable，變成停用的叫 OnDisable</summary>
        public static void UpdateActive(NGameObject go)
        {
            if (go.Persistent) return;
            var turnedOn = new List<NGameObject>();
            var turnedOff = new List<NGameObject>();
            bool parentActive = go.Parent == null || go.Parent.ActiveInHierarchy;
            Recompute(go, parentActive, turnedOn, turnedOff);
            if (turnedOff.Count > 0) Lifecycle.Deactivate(turnedOff);
            if (turnedOn.Count > 0) Lifecycle.Activate(turnedOn);
        }

        static void Recompute(NGameObject go, bool parentActive, List<NGameObject> on, List<NGameObject> off)
        {
            bool now = parentActive && go.ActiveSelf;
            if (now != go.ActiveInHierarchy)
            {
                go.ActiveInHierarchy = now;
                (now ? on : off).Add(go);
            }
            var ch = go.Transform.Children;
            for (int i = 0; i < ch.Count; i++) if (ch[i].Go != null) Recompute(ch[i].Go, now, on, off);
        }

        // ───────── 走訪 ─────────

        public static void Walk(NGameObject go, Action<NGameObject> fn)
        {
            fn(go);
            var ch = go.Transform.Children;
            for (int i = 0; i < ch.Count; i++) if (ch[i].Go != null) Walk(ch[i].Go, fn);
        }

        public static List<NGameObject> Collect(NGameObject go)
        {
            var list = new List<NGameObject>();
            Walk(go, list.Add);
            return list;
        }
    }
}
