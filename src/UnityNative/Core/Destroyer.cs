// Object.Destroy：Unity 等到這一格結束才真的摧毀（之前 == null 還是 false）；帶秒數的照時間。DestroyImmediate 馬上做。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public static class Destroyer
    {
        sealed class Pending { public NObject N; public double At; }
        static readonly List<Pending> pending = new List<Pending>();
        static readonly HashSet<NObject> queued = new HashSet<NObject>();

        public static void Destroy(NObject n, float t)
        {
            if (n == null || !n.Alive) return;
            if (t <= 0 && queued.Contains(n)) return;
            if (t <= 0) queued.Add(n);
            pending.Add(new Pending { N = n, At = t > 0 ? Engine.Time.Time + t : 0 });
        }

        public static void Flush()
        {
            if (pending.Count == 0) return;
            double now = Engine.Time.Time;
            var due = new List<NObject>();
            for (int i = pending.Count - 1; i >= 0; i--)
                if (pending[i].At <= now) { due.Add(pending[i].N); pending.RemoveAt(i); }
            due.Reverse();
            foreach (var n in due) { queued.Remove(n); DestroyNow(n); }
        }

        public static void DestroyNow(NObject n)
        {
            if (n == null || !n.Alive) return;
            switch (n)
            {
                case NGameObject go: DestroyGameObject(go); break;
                case NTransform tr:
                    Engine.Log(LogType.Error, "Can't destroy Transform component of '" + tr.Go?.Name + "'. If you want to destroy the game object, please call 'Destroy' on the game object instead.");
                    break;
                case NComponent c: DestroyComponent(c, true); break;
                case NScriptableObject so:
                    if (so.Activated) { so.Activated = false; Lifecycle.CallSO(so, Messages.OnDisable); }
                    Lifecycle.CallSO(so, Messages.OnDestroy);
                    Native.Release(so);
                    break;
                default:
                    if (n is IDestroyable d) d.OnDestroyNative();
                    Native.Release(n);
                    break;
            }
        }

        static void DestroyComponent(NComponent c, bool detach)
        {
            if (!c.Alive) return;
            if (c is NMonoBehaviour mb) Lifecycle.DestroyBehaviour(mb);
            else
            {
                if (c is NBehaviour b && b.Enabled) { b.Enabled = false; }
                c.OnDestroyNative();
            }
            if (detach) c.Go?.Components.Remove(c);
            Native.Release(c);
        }

        static void DestroyGameObject(NGameObject go)
        {
            var all = Hierarchy.Collect(go);
            // 先全部停用（OnDisable），再 OnDestroy
            var active = all.FindAll(g => g.ActiveInHierarchy);
            foreach (var g in active) g.ActiveInHierarchy = false;
            if (active.Count > 0) Lifecycle.Deactivate(active);
            foreach (var g in all)
                foreach (var c in g.Components.ToArray())
                    if (c is NMonoBehaviour mb) Lifecycle.DestroyBehaviour(mb);

            var tr = go.Transform;
            var parent = tr.Parent;
            if (parent != null)
            {
                parent.Children.Remove(tr);
                tr.Parent = null;
                Lifecycle.SendToScripts(parent.Go, Messages.OnTransformChildrenChanged);
            }
            else go.Scene?.Roots.Remove(go);

            foreach (var g in all)
            {
                foreach (var c in g.Components.ToArray()) DestroyComponent(c, false);
                g.Components.Clear();
                Native.Release(g);
            }
        }
    }

    /// <summary>素材類的原生物件（貼圖、網格…）摧毀時要收回 GPU 資源的實作這個</summary>
    public interface IDestroyable { void OnDestroyNative(); }
}
