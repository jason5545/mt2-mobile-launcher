// 腳本的生命週期：Awake → OnEnable → Start（第一次 Update 之前）→ Update／LateUpdate → OnDisable → OnDestroy。
// 規則照 Unity：
//   - GameObject 在階層裡啟用時，上面每個腳本（就算 enabled 是 false）都會 Awake 一次；enabled 的接著 OnEnable。
//   - 同一批（載入場景、Instantiate、SetActive）照執行順序排，一個腳本 Awake 完馬上 OnEnable，再換下一個。
//   - Start 排進佇列，下一個 Update 階段開頭（或這一格還沒輪到 Update 的話就這一格）呼叫。
//   - SetActive(false) 停掉 GameObject 上所有協程；enabled = false 不停。
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public class NMonoBehaviour : NBehaviour
    {
        public ScriptInfo Info;
        public bool Awoken;
        public bool Activated;
        public bool Started;
        public bool StartQueued;
        public bool Destroying;
        public long Seq;
        /// <summary>場景檔裡的元件 fileID（原版 build 的 pathID）：場景載入時 Awake／OnEnable 照這個排（同 TS 版 world.start 的 fileOrder）。不是從檔案載的是 long.MaxValue</summary>
        public long FileOrder = long.MaxValue;
        public readonly List<NCoroutine> Coroutines = new List<NCoroutine>();
        public override bool IsActiveAndEnabled => Activated;
        public MonoBehaviour MB => (MonoBehaviour)Managed;
    }

    public class NScriptableObject : NObject
    {
        public ScriptInfo Info;
        public bool Awoken;
        public bool Activated;
    }

    public static class Lifecycle
    {
        static long seq;
        public static long NextSeq() => ++seq;

        // 每格要叫的腳本，照（執行順序, 建立順序）排好
        static readonly SortedSet<NMonoBehaviour> updaters = new SortedSet<NMonoBehaviour>(new OrderCmp());
        static readonly SortedSet<NMonoBehaviour> lateUpdaters = new SortedSet<NMonoBehaviour>(new OrderCmp());
        static readonly SortedSet<NMonoBehaviour> fixedUpdaters = new SortedSet<NMonoBehaviour>(new OrderCmp());
        static readonly SortedSet<NMonoBehaviour> guiers = new SortedSet<NMonoBehaviour>(new OrderCmp());
        static readonly List<NMonoBehaviour> startQueue = new List<NMonoBehaviour>();
        static readonly List<NMonoBehaviour> iter = new List<NMonoBehaviour>();

        sealed class OrderCmp : IComparer<NMonoBehaviour>
        {
            public int Compare(NMonoBehaviour a, NMonoBehaviour b)
            {
                int c = a.Info.ExecutionOrder.CompareTo(b.Info.ExecutionOrder);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            }
        }

        public static void Log(Exception e, NObject ctx = null)
        {
            while (e is System.Reflection.TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
            Engine.LogException(e, ctx);
        }

        static void Call(NMonoBehaviour mb, Messages m)
        {
            var info = mb.Info;
            var a = info.Calls[(int)m];
            try
            {
                if (a != null) a(mb.Managed);
                else
                {
                    var f = info.Coroutines[(int)m];
                    if (f != null)
                    {
                        var e = f(mb.Managed);
                        if (e != null) Coroutines.Start(mb, e);
                    }
                }
            }
            catch (Exception e) { Log(e, mb); }
        }

        /// <summary>OnApplicationPause(bool)：送給所有啟用中物件上的腳本（不管腳本 enabled；同 Unity 的 SendMessage 式廣播）。宿主在切到背景、回來時叫</summary>
        public static void SendApplicationPause(bool paused) => Broadcast(paused, i => i.OnApplicationPause);

        /// <summary>OnApplicationFocus(bool)</summary>
        public static void SendApplicationFocus(bool focus) => Broadcast(focus, i => i.OnApplicationFocus);

        static void Broadcast(bool value, Func<ScriptInfo, Action<object, bool>> pick)
        {
            foreach (var n in System.Linq.Enumerable.ToArray(Native.All))
            {
                if (n is not NMonoBehaviour mb || !mb.Alive || !mb.Awoken || mb.Go == null || !mb.Go.ActiveInHierarchy) continue;
                var f = pick(mb.Info);
                if (f == null) continue;
                try { f(mb.Managed, value); }
                catch (Exception e) { Log(e, mb); }
            }
        }

        /// <summary>只對腳本送（uGUI 的 OnRectTransformDimensionsChange 這類），不管 enabled</summary>
        public static void SendToScripts(NGameObject go, Messages m)
        {
            if (go == null) return;
            var comps = go.Components;
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i] is NMonoBehaviour mb && mb.Alive && mb.Awoken && mb.Info.Has(m)) Call(mb, m);
            }
        }

        /// <summary>攝影機的訊息（OnPreCull、OnPreRender、OnPostRender）：攝影機物件上啟用中的腳本</summary>
        public static void SendToEnabledScripts(NGameObject go, Messages m)
        {
            if (go == null) return;
            var comps = go.Components;
            for (int i = 0; i < comps.Count; i++)
                if (comps[i] is NMonoBehaviour mb && mb.Alive && mb.Awoken && mb.IsActiveAndEnabled && mb.Info.Has(m)) Call(mb, m);
        }

        /// <summary>攝影機物件上有 OnRenderImage 的啟用中腳本（影像效果，照元件順序）</summary>
        public static void RenderImageScripts(NGameObject go, List<NMonoBehaviour> into)
        {
            into.Clear();
            if (go == null) return;
            foreach (var c in go.Components)
                if (c is NMonoBehaviour mb && mb.Alive && mb.Awoken && mb.IsActiveAndEnabled && mb.Info.OnRenderImage != null) into.Add(mb);
        }

        public static void CallRenderImage(NMonoBehaviour mb, UnityEngine.RenderTexture src, UnityEngine.RenderTexture dst)
        {
            try { mb.Info.OnRenderImage.Invoke(mb.Managed, new object[] { src, dst }); }
            catch (System.Reflection.TargetInvocationException e) { Log(e.InnerException ?? e, mb); }
            catch (Exception e) { Log(e, mb); }
        }

        public static void SendToScriptsRecursive(NGameObject go, Messages m)
        {
            SendToScripts(go, m);
            var tr = go.Transform;
            if (tr == null) return;
            foreach (var c in tr.Children.ToArray()) if (c.Go != null) SendToScriptsRecursive(c.Go, m);
        }

        // ───────── 啟用與停用 ─────────

        /// <summary>一批 GameObject 剛在階層裡變成啟用（ActiveInHierarchy 已經更新）：照執行順序 Awake + OnEnable。
        /// byFileOrder：場景載入，執行順序相同的照元件在場景檔的 fileID（同 TS 版）；其他（SetActive、Instantiate）照階層順序</summary>
        public static void Activate(List<NGameObject> gos, bool byFileOrder = false)
        {
            var list = new List<NMonoBehaviour>();
            foreach (var go in gos)
                foreach (var c in go.Components)
                {
                    if (c is NMonoBehaviour mb && mb.Alive && (!mb.Awoken || (mb.Enabled && !mb.Activated))) list.Add(mb);
                    else if (c.Alive) c.OnActiveChanged();
                }
            // 穩定排序：執行順序相同的照階層順序
            var idx = new Dictionary<NMonoBehaviour, int>();
            for (int i = 0; i < list.Count; i++) idx[list[i]] = i;
            list.Sort((a, b) =>
            {
                int x = a.Info.ExecutionOrder.CompareTo(b.Info.ExecutionOrder);
                if (x == 0 && byFileOrder) x = a.FileOrder.CompareTo(b.FileOrder);
                return x != 0 ? x : idx[a].CompareTo(idx[b]);
            });
            foreach (var mb in list)
            {
                if (!mb.Alive || mb.Go == null || !mb.Go.ActiveInHierarchy) continue;
                if (!mb.Awoken)
                {
                    mb.Awoken = true;
                    Call(mb, Messages.Awake);
                    if (!mb.Alive || !mb.Go.ActiveInHierarchy) continue;
                }
                if (mb.Enabled && !mb.Activated) Enable(mb);
            }
        }

        public static void Enable(NMonoBehaviour mb)
        {
            if (mb.Activated) return;
            mb.Activated = true;
            Call(mb, Messages.OnEnable);
            if (!mb.Alive || !mb.Activated) return;
            Register(mb);
            if (!mb.Started && !mb.StartQueued && mb.Info.Has(Messages.Start)) { mb.StartQueued = true; startQueue.Add(mb); }
            else if (!mb.Started && !mb.Info.Has(Messages.Start)) mb.Started = true;
        }

        public static void Disable(NMonoBehaviour mb)
        {
            if (!mb.Activated) return;
            mb.Activated = false;
            Unregister(mb);
            Call(mb, Messages.OnDisable);
        }

        /// <summary>一批 GameObject 剛變成停用：OnDisable，停掉協程</summary>
        public static void Deactivate(List<NGameObject> gos)
        {
            foreach (var go in gos)
                foreach (var c in go.Components.ToArray())
                {
                    if (c is NMonoBehaviour mb && mb.Alive)
                    {
                        if (mb.Activated) Disable(mb);
                        Coroutines.StopAll(mb);
                    }
                    else if (c.Alive) c.OnActiveChanged();
                }
        }

        static void Register(NMonoBehaviour mb)
        {
            if (mb.Info.HasUpdate) updaters.Add(mb);
            if (mb.Info.HasLateUpdate) lateUpdaters.Add(mb);
            if (mb.Info.HasFixedUpdate) fixedUpdaters.Add(mb);
            if (mb.Info.Calls[(int)Messages.OnGUI] != null) guiers.Add(mb);
        }

        static void Unregister(NMonoBehaviour mb)
        {
            updaters.Remove(mb);
            lateUpdaters.Remove(mb);
            fixedUpdaters.Remove(mb);
            guiers.Remove(mb);
        }

        /// <summary>Behaviour.enabled 的 setter</summary>
        public static void SetEnabled(NBehaviour b, bool value)
        {
            if (b.Enabled == value) return;
            b.Enabled = value;
            if (b is NMonoBehaviour mb)
            {
                if (mb.Go == null || !mb.Go.ActiveInHierarchy) return;
                if (value)
                {
                    if (!mb.Awoken) { mb.Awoken = true; Call(mb, Messages.Awake); }
                    if (mb.Alive && mb.Enabled) Enable(mb);
                }
                else Disable(mb);
            }
            else b.OnActiveChanged();
        }

        // ───────── 摧毀 ─────────

        public static void DestroyBehaviour(NMonoBehaviour mb)
        {
            if (mb.Destroying) return;
            mb.Destroying = true;
            if (mb.Activated) Disable(mb);
            Coroutines.StopAll(mb);
            Invokes.CancelAll(mb);
            // OnDestroy 只有 Awake 過的才叫
            if (mb.Awoken) Call(mb, Messages.OnDestroy);
            try { ((MonoBehaviour)mb.Managed).RaiseCancellation(); } catch (Exception e) { Log(e, mb); }
        }

        // ───────── 每格 ─────────

        public static void RunStarts()
        {
            while (startQueue.Count > 0)
            {
                var batch = startQueue.ToArray();
                startQueue.Clear();
                // 執行順序相同的照排進佇列（OnEnable）的先後（同 TS 版 flushStarts）。以前照建立順序：hud 場景的 Hud.Start 比
                // GameSpeedUI.Start 早，GameSpeedUI 的圖示表還沒建，Hud.Initialize 丟 NullReferenceException（10/19）
                var pos = new Dictionary<NMonoBehaviour, int>();
                for (int i = 0; i < batch.Length; i++) pos[batch[i]] = i;
                Array.Sort(batch, (a, b) => { int x = a.Info.ExecutionOrder.CompareTo(b.Info.ExecutionOrder); return x != 0 ? x : pos[a].CompareTo(pos[b]); });
                foreach (var mb in batch)
                {
                    mb.StartQueued = false;
                    if (!mb.Alive || mb.Started) continue;
                    if (!mb.Activated) { continue; }
                    mb.Started = true;
                    Call(mb, Messages.Start);
                }
            }
        }

        static void Each(SortedSet<NMonoBehaviour> set, Messages m)
        {
            iter.Clear();
            iter.AddRange(set);
            for (int i = 0; i < iter.Count; i++)
            {
                var mb = iter[i];
                if (!mb.Activated || !mb.Alive) continue;
                // 還沒 Start 的（這一格才啟用）Unity 會先 Start
                if (!mb.Started) continue;
                var a = mb.Info.Calls[(int)m];
                try { a(mb.Managed); }
                catch (Exception e) { Log(e, mb); }
            }
        }

        public static void Update() => Each(updaters, Messages.Update);
        public static void LateUpdate() => Each(lateUpdaters, Messages.LateUpdate);
        public static void FixedUpdate() => Each(fixedUpdaters, Messages.FixedUpdate);
        /// <summary>IMGUI 的一個事件（Event.current 已經設好，ImGui.cs）</summary>
        public static void OnGUI() => Each(guiers, Messages.OnGUI);

        public static int UpdaterCount => updaters.Count;

        // ───────── ScriptableObject ─────────

        public static void AwakeScriptableObject(NScriptableObject so)
        {
            if (so.Awoken) return;
            so.Awoken = true;
            so.Activated = true;
            CallSO(so, Messages.Awake);
            CallSO(so, Messages.OnEnable);
        }

        public static void CallSO(NScriptableObject so, Messages m)
        {
            var a = so.Info.Calls[(int)m];
            if (a == null) return;
            try { a(so.Managed); } catch (Exception e) { Log(e, so); }
        }
    }
}
