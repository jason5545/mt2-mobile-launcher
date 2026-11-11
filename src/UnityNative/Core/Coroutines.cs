// 協程與 Invoke：Unity 的協程排程在原生那邊（StartCoroutine 是 internal call），這裡照 Unity 的時機推進：
//   yield null、WaitForSeconds、AsyncOperation、Invoke：每格 Update 之後
//   WaitForFixedUpdate：FixedUpdate 之後；WaitForEndOfFrame：畫完之後
//   yield 另一個協程（Coroutine）或 IEnumerator：等它跑完。IEnumerator 當成巢狀協程，CustomYieldInstruction 也走這條。
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public enum WaitKind { None, NextFrame, Seconds, Fixed, EndOfFrame, Coroutine, AsyncOp }

    public class NCoroutine
    {
        public int Id;
        public NMonoBehaviour Owner;
        public IEnumerator E;
        public WaitKind Kind;
        public double ResumeAt;
        public NCoroutine WaitingOn;
        public NAsyncOperation WaitingOp;
        public List<NCoroutine> Waiters;
        public bool Done;
        public Coroutine Handle;
    }

    public static class Coroutines
    {
        static int nextId = 1;
        static readonly Dictionary<IntPtr, NCoroutine> byPtr = new Dictionary<IntPtr, NCoroutine>();
        static readonly List<NCoroutine> active = new List<NCoroutine>();
        static readonly List<NCoroutine> iter = new List<NCoroutine>();

        public static Coroutine Start(NMonoBehaviour owner, IEnumerator e)
        {
            var co = new NCoroutine { Id = nextId++, Owner = owner, E = e };
            var h = (Coroutine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Coroutine));
            h.m_Ptr = (IntPtr)co.Id;
            co.Handle = h;
            byPtr[h.m_Ptr] = co;
            owner?.Coroutines.Add(co);
            active.Add(co);
            Step(co);
            // 第一步（同步跑到第一個 yield）就跑完的協程：回 null。Unity 文件沒寫明（未查證），依原版遊戲的程式推定：
            // MT2 的 CoroutineController 寫 _coroutine = StartCoroutine(Run())，Run 跑完會先呼叫 Stop() 把欄位清掉；
            // 回非 null 的話 _coroutine 留著、_monoBehaviour 是 null，下一次 Stop() 就 NullReferenceException（原版不會）
            return co.Done ? null : h;
        }

        public static NCoroutine Of(Coroutine h) => h != null && byPtr.TryGetValue(h.m_Ptr, out var c) ? c : null;

        static void Finish(NCoroutine co)
        {
            if (co.Done) return;
            co.Done = true;
            co.Owner?.Coroutines.Remove(co);
            byPtr.Remove((IntPtr)co.Id);
            if (co.Waiters != null)
            {
                var ws = co.Waiters;
                co.Waiters = null;
                foreach (var w in ws)
                    if (!w.Done && w.WaitingOn == co) { w.WaitingOn = null; w.Kind = WaitKind.None; Step(w); }
            }
        }

        static void Step(NCoroutine co)
        {
            if (co.Done) return;
            bool more;
            try { more = co.E.MoveNext(); }
            catch (Exception e)
            {
                Lifecycle.Log(e, co.Owner);
                more = false;
            }
            if (co.Done) return;
            if (!more) { Finish(co); return; }
            var cur = co.E.Current;
            switch (cur)
            {
                case null:
                    co.Kind = WaitKind.NextFrame; break;
                case WaitForSeconds w:
                    co.Kind = WaitKind.Seconds; co.ResumeAt = Engine.Time.Time + w.m_Seconds; break;
                case WaitForFixedUpdate _:
                    co.Kind = WaitKind.Fixed; break;
                case WaitForEndOfFrame _:
                    co.Kind = WaitKind.EndOfFrame; break;
                case Coroutine c:
                    {
                        var inner = Of(c);
                        if (inner == null || inner.Done) { co.Kind = WaitKind.NextFrame; break; }
                        co.Kind = WaitKind.Coroutine; co.WaitingOn = inner;
                        (inner.Waiters ??= new List<NCoroutine>()).Add(co);
                        break;
                    }
                case AsyncOperation op:
                    {
                        var n = NAsyncOperation.Of(op);
                        if (n == null || n.IsDone) { co.Kind = WaitKind.NextFrame; break; }
                        co.Kind = WaitKind.AsyncOp; co.WaitingOp = n; break;
                    }
                case IEnumerator ie:
                    {
                        var h = Start(co.Owner, ie);
                        var inner = Of(h);
                        if (inner == null || inner.Done) { co.Kind = WaitKind.NextFrame; break; }
                        co.Kind = WaitKind.Coroutine; co.WaitingOn = inner;
                        (inner.Waiters ??= new List<NCoroutine>()).Add(co);
                        break;
                    }
                default:
                    co.Kind = WaitKind.NextFrame; break;
            }
        }

        static void Pump(Func<NCoroutine, bool> ready)
        {
            iter.Clear();
            iter.AddRange(active);
            foreach (var co in iter)
            {
                if (co.Done) continue;
                if (ready(co)) { co.Kind = WaitKind.None; Step(co); }
            }
            active.RemoveAll(c => c.Done);
        }

        /// <summary>Update 之後：yield null、WaitForSeconds、AsyncOperation</summary>
        public static void AfterUpdate()
        {
            double now = Engine.Time.Time;
            Pump(c => c.Kind == WaitKind.NextFrame || (c.Kind == WaitKind.Seconds && now >= c.ResumeAt) || (c.Kind == WaitKind.AsyncOp && c.WaitingOp.IsDone));
        }

        public static void AfterFixedUpdate() => Pump(c => c.Kind == WaitKind.Fixed);
        public static void EndOfFrame() => Pump(c => c.Kind == WaitKind.EndOfFrame);

        public static void Stop(NCoroutine co)
        {
            if (co == null || co.Done) return;
            // 停掉的協程不會通知等它的協程（Unity 也是：外層會一直等）
            co.Waiters = null;
            co.Done = true;
            co.Owner?.Coroutines.Remove(co);
            byPtr.Remove((IntPtr)co.Id);
        }

        public static void StopAll(NMonoBehaviour mb)
        {
            foreach (var co in mb.Coroutines.ToArray()) Stop(co);
            mb.Coroutines.Clear();
        }

        public static void StopByEnumerator(NMonoBehaviour mb, IEnumerator e)
        {
            foreach (var co in mb.Coroutines.ToArray()) if (co.E == e) Stop(co);
        }

        public static int ActiveCount => active.Count;
    }

    public static class Invokes
    {
        sealed class Entry { public NMonoBehaviour Mb; public string Method; public double At; public float Repeat; public bool Dead; }
        static readonly List<Entry> list = new List<Entry>();

        public static void Add(NMonoBehaviour mb, string method, float time, float repeat)
        {
            list.Add(new Entry { Mb = mb, Method = method, At = Engine.Time.Time + Math.Max(0f, time), Repeat = repeat });
        }

        public static void Cancel(NMonoBehaviour mb, string method)
        {
            foreach (var e in list) if (e.Mb == mb && (method == null || e.Method == method)) e.Dead = true;
        }

        public static void CancelAll(NMonoBehaviour mb) => Cancel(mb, null);

        public static bool IsInvoking(NMonoBehaviour mb, string method)
        {
            foreach (var e in list) if (!e.Dead && e.Mb == mb && (method == null || e.Method == method)) return true;
            return false;
        }

        public static void Run()
        {
            if (list.Count == 0) return;
            double now = Engine.Time.Time;
            foreach (var e in list.ToArray())
            {
                if (e.Dead || now < e.At) continue;
                if (!e.Mb.Alive) { e.Dead = true; continue; }
                // Unity：腳本停用（或 GameObject 停用）時 Invoke 照樣會叫
                foreach (var mi in e.Mb.Info.ByName(e.Method))
                {
                    if (mi.GetParameters().Length != 0) continue;
                    try { mi.Invoke(e.Mb.Managed, null); } catch (Exception ex) { Lifecycle.Log(ex, e.Mb); }
                    break;
                }
                if (e.Repeat > 0f) e.At += e.Repeat; else e.Dead = true;
            }
            list.RemoveAll(e => e.Dead);
        }
    }
}
