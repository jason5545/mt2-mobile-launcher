// Playables 的原生半邊：PlayableGraph、PlayableHandle、PlayableOutputHandle。Timeline（Unity.Timeline.dll）是 managed，原版照跑，這裡是它底下那層。
//
// 照原文（UnityCsReference 2022.3 的 Runtime/Export/Director）：
//   - ScriptPlayable.Create：managed 先建行為物件，再 CreatePlayableHandle、SetInputCount、SetScriptInstance；SetScriptInstance 之後發 OnPlayableCreate。
//   - GetPlayableType：ScriptPlayable 是行為物件的類別（ScriptPlayable<T> 的建構子拿它跟 T 比 IsAssignableFrom），一般 Playable 是 Playable。
//   - FrameData 的欄位與旗標（Evaluate、Loop、Hold、EffectivePlayStatePlaying）。
// 原生、沒有原始碼的部分照文件與 TS 版（unity-cs2ts/runtime/src/csport/unityPlayables.ts）推定：
//   - graph 求值：播放中的 playable 時間往前推 → 由 output 往輸入走 PrepareFrame（每個 playable 一次，Passthrough 只走 output port 對應的輸入）
//     → 每個 output 做 ProcessFrame（playerData 是 output 的 UserData）。暫停的 playable 和它的輸入不走。
//   - OnBehaviourPlay／OnBehaviourPause 在播放狀態改變後的下一次求值發；OnGraphStart 在第一次求值發。
//   - 新建的 playable 是 Playing，duration 預設正無限大。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

namespace UnityNative
{
    public sealed class PGraph
    {
        public IntPtr Id;
        public string Name = "";
        public readonly List<PNode> Nodes = new List<PNode>();
        public readonly List<POut> Outputs = new List<POut>();
        public IExposedPropertyTable Resolver;
        public DirectorUpdateMode Mode = DirectorUpdateMode.GameTime;
        public bool Playing, Destroyed;
        public ulong FrameId;
        public PlayableGraph Handle => new PlayableGraph { m_Handle = Id, m_Version = 1 };
    }

    public sealed class PNode
    {
        public IntPtr Id;
        public PGraph G;
        public object Behaviour;
        public readonly List<PNode> Inputs = new List<PNode>();
        public readonly List<float> Weights = new List<float>();
        public readonly List<PNode> Outputs = new List<PNode> { null };
        public double Time, PrevTime, Duration = double.PositiveInfinity, Speed = 1, Delay;
        public PlayState State = PlayState.Playing;
        public bool Done, PropagateSetTime, Started, Destroyed;
        public PlayableTraversalMode Traversal;
        public DirectorWrapMode WrapMode = DirectorWrapMode.Loop;
        public float LeadTime;
        public PlayState? Notified;
        public PlayableHandle Handle => new PlayableHandle { m_Handle = Id, m_Version = 1 };
        public Playable P => new Playable(Handle);
    }

    public sealed class POut
    {
        public IntPtr Id;
        public PGraph G;
        public Type Type = typeof(ScriptPlayableOutput);
        public string Name = "";
        public PNode Source;
        public int Port;
        public float Weight = 1;
        public UnityEngine.Object Reference, UserData;
        public readonly List<INotificationReceiver> Receivers = new List<INotificationReceiver>();
        public bool Destroyed;
        public PlayableOutputHandle Handle => new PlayableOutputHandle { m_Handle = Id, m_Version = 1 };
    }

    public static class Playables
    {
        static readonly Dictionary<IntPtr, object> table = new Dictionary<IntPtr, object>();
        static long seq;
        /// <summary>自己 Play 的 graph（不是 PlayableDirector 的）：每格照更新模式求值</summary>
        public static readonly HashSet<PGraph> ManualGraphs = new HashSet<PGraph>();

        static IntPtr NextId() => (IntPtr)(++seq);

        public static PGraph NewGraph(string name)
        {
            var g = new PGraph { Id = NextId(), Name = name ?? "" };
            table[g.Id] = g;
            return g;
        }

        public static PNode NewNode(PGraph g)
        {
            var n = new PNode { Id = NextId(), G = g };
            table[n.Id] = n;
            g.Nodes.Add(n);
            return n;
        }

        public static POut NewOutput(PGraph g, string name, Type type)
        {
            var o = new POut { Id = NextId(), G = g, Name = name ?? "", Type = type };
            table[o.Id] = o;
            g.Outputs.Add(o);
            return o;
        }

        public static PGraph Graph(in PlayableGraph h) => h.m_Handle != IntPtr.Zero && table.TryGetValue(h.m_Handle, out var o) && o is PGraph g && !g.Destroyed ? g : null;
        public static PNode Node(in PlayableHandle h) => h.m_Handle != IntPtr.Zero && table.TryGetValue(h.m_Handle, out var o) && o is PNode n && !n.Destroyed ? n : null;
        public static POut Out(in PlayableOutputHandle h) => h.m_Handle != IntPtr.Zero && table.TryGetValue(h.m_Handle, out var o) && o is POut x && !x.Destroyed && !x.G.Destroyed ? x : null;

        public static PGraph NeedGraph(in PlayableGraph h) => Graph(h) ?? throw new InvalidOperationException("This PlayableGraph is invalid. It may have been destroyed.");
        public static PNode NeedNode(in PlayableHandle h) => Node(h) ?? throw new InvalidOperationException("This PlayableHandle is invalid. It may have been destroyed.");
        public static POut NeedOut(in PlayableOutputHandle h) => Out(h) ?? throw new InvalidOperationException("This PlayableOutputHandle is invalid. It may have been destroyed.");

        // ───────── 行為回呼 ─────────

        static void Call(PNode n, Action<IPlayableBehaviour> f, string what)
        {
            if (n.Behaviour is not IPlayableBehaviour b) return;
            try { f(b); }
            catch (Exception e) { Lifecycle.Log(e); }
        }

        public static FrameData Frame(PGraph g, double dt, bool evaluate, bool loop, bool hold, float weight, float effWeight, float parentSpeed, float speed, bool playing, POut o)
        {
            var f = new FrameData
            {
                m_FrameID = g.FrameId,
                m_DeltaTime = dt,
                m_Weight = weight,
                m_EffectiveWeight = effWeight,
                m_EffectiveParentSpeed = parentSpeed,
                m_EffectiveSpeed = speed,
                m_Output = o != null ? new PlayableOutput(o.Handle) : PlayableOutput.Null,
            };
            FrameData.Flags fl = 0;
            if (evaluate) fl |= FrameData.Flags.Evaluate;
            if (loop) fl |= FrameData.Flags.Loop;
            if (hold) fl |= FrameData.Flags.Hold;
            if (playing) fl |= FrameData.Flags.EffectivePlayStatePlaying;
            f.m_Flags = fl;
            return f;
        }

        public static void SetTime(PNode n, double t)
        {
            n.PrevTime = n.Time;
            n.Time = t;
            n.Done = false;
            if (n.PropagateSetTime) foreach (var i in n.Inputs) if (i != null && !i.Destroyed) SetTime(i, t);
        }

        /// <summary>graph 求值一次：時間往前推 → PrepareFrame → ProcessFrame</summary>
        public static void Evaluate(PGraph g, double dt, bool evaluate, bool loop = false, bool hold = false, PNode skipAdvance = null)
        {
            g.FrameId++;
            if (dt > 0)
                foreach (var n in g.Nodes.ToArray())
                    if (!n.Destroyed && n.State == PlayState.Playing && n != skipAdvance)
                    {
                        n.PrevTime = n.Time;
                        n.Time += dt * n.Speed;
                        if (n.Time >= n.Duration) n.Done = true;
                    }
            var prepared = new HashSet<PNode>();
            void Prepare(PNode n, int port, float w, float ew, float ps, POut o)
            {
                if (n == null || n.Destroyed || !prepared.Add(n)) return;
                float sp = ps * (float)n.Speed;
                bool playing = n.State == PlayState.Playing;
                var fd = Frame(g, dt, evaluate, loop, hold, w, ew, ps, sp, playing, o);
                var p = n.P;
                if (!n.Started) { n.Started = true; Call(n, b => b.OnGraphStart(p), "OnGraphStart"); }
                if (!playing)
                {
                    if (n.Notified == PlayState.Playing) Call(n, b => b.OnBehaviourPause(p, fd), "OnBehaviourPause");
                    n.Notified = PlayState.Paused;
                    return;
                }
                if (n.Notified != PlayState.Playing) Call(n, b => b.OnBehaviourPlay(p, fd), "OnBehaviourPlay");
                n.Notified = PlayState.Playing;
                Call(n, b => b.PrepareFrame(p, fd), "PrepareFrame");
                if (n.Traversal == PlayableTraversalMode.Passthrough)
                {
                    if (port < n.Inputs.Count) Prepare(n.Inputs[port], 0, n.Weights[port], ew * n.Weights[port], sp, o);
                }
                else for (int i = 0; i < n.Inputs.Count; i++) Prepare(n.Inputs[i], 0, n.Weights[i], ew * n.Weights[i], sp, o);
            }
            var outs = g.Outputs.ToArray();
            foreach (var o in outs) if (!o.Destroyed) Prepare(o.Source, o.Port, o.Weight, o.Weight, 1, o);
            foreach (var o in outs)
            {
                if (o.Destroyed) continue;
                var seen = new HashSet<PNode>();
                void Process(PNode n, int port, float w, float ew, float ps)
                {
                    if (n == null || n.Destroyed || n.State != PlayState.Playing || !seen.Add(n)) return;
                    float sp = ps * (float)n.Speed;
                    var fd = Frame(g, dt, evaluate, loop, hold, w, ew, ps, sp, true, o);
                    var p = n.P;
                    object data = o.UserData;
                    Call(n, b => b.ProcessFrame(p, fd, data), "ProcessFrame");
                    if (n.Traversal == PlayableTraversalMode.Passthrough)
                    {
                        if (port < n.Inputs.Count) Process(n.Inputs[port], 0, n.Weights[port], ew * n.Weights[port], sp);
                    }
                    else for (int i = 0; i < n.Inputs.Count; i++) Process(n.Inputs[i], 0, n.Weights[i], ew * n.Weights[i], sp);
                }
                Process(o.Source, o.Port, o.Weight, o.Weight, 1);
            }
        }

        public static void DestroyNode(PNode n)
        {
            if (n.Destroyed) return;
            var p = n.P;
            if (n.Notified == PlayState.Playing)
            {
                var fd = Frame(n.G, 0, false, false, false, 1, 1, 1, (float)n.Speed, false, null);
                Call(n, b => b.OnBehaviourPause(p, fd), "OnBehaviourPause");
            }
            if (n.Started) Call(n, b => b.OnGraphStop(p), "OnGraphStop");
            Call(n, b => b.OnPlayableDestroy(p), "OnPlayableDestroy");
            n.Destroyed = true;
            table.Remove(n.Id);
            foreach (var m in n.G.Nodes)
            {
                for (int i = 0; i < m.Inputs.Count; i++) if (m.Inputs[i] == n) m.Inputs[i] = null;
                for (int i = 0; i < m.Outputs.Count; i++) if (m.Outputs[i] == n) m.Outputs[i] = null;
            }
            foreach (var o in n.G.Outputs) if (o.Source == n) o.Source = null;
        }

        public static void DestroySubgraph(PNode n)
        {
            if (n == null || n.Destroyed) return;
            var ins = n.Inputs.ToArray();
            DestroyNode(n);
            foreach (var i in ins) DestroySubgraph(i);
        }

        public static void DestroyGraph(PGraph g)
        {
            if (g.Destroyed) return;
            foreach (var n in g.Nodes.ToArray()) DestroyNode(n);
            foreach (var o in g.Outputs) { o.Destroyed = true; table.Remove(o.Id); }
            g.Destroyed = true;
            g.Playing = false;
            table.Remove(g.Id);
            ManualGraphs.Remove(g);
        }

        public static void Connect(PNode src, int srcPort, PNode dst, int dstPort)
        {
            while (src.Outputs.Count <= srcPort) src.Outputs.Add(null);
            src.Outputs[srcPort] = dst;
            dst.Inputs[dstPort] = src;
        }

        public static IEnumerable<PNode> Roots(PGraph g)
        {
            foreach (var n in g.Nodes)
                if (!n.Destroyed && n.Outputs.TrueForAll(o => o == null)) yield return n;
        }
    }
}

namespace UnityNative.Icalls
{
    using UnityNative;
    using static UnityNative.Playables;

    public static unsafe partial class UnityEngine_Playables_PlayableGraph
    {
        public static void Create_Injected(string name, out PlayableGraph ret) => ret = NewGraph(name).Handle;
        public static void Destroy_Injected(ref PlayableGraph self) { if (Graph(self) is PGraph g) DestroyGraph(g); }
        public static bool IsValid_Injected(ref PlayableGraph self) => Graph(self) != null;
        public static bool IsPlaying_Injected(ref PlayableGraph self) => Graph(self)?.Playing ?? false;
        public static bool IsDone_Injected(ref PlayableGraph self)
        {
            var g = Graph(self);
            if (g == null) return true;
            foreach (var o in g.Outputs) if (!o.Destroyed && o.Source != null && !o.Source.Done) return false;
            return true;
        }
        public static void Play_Injected(ref PlayableGraph self) { var g = NeedGraph(self); g.Playing = true; ManualGraphs.Add(g); }
        public static void Stop_Injected(ref PlayableGraph self) { if (Graph(self) is PGraph g) { g.Playing = false; ManualGraphs.Remove(g); } }
        public static void Evaluate_Injected(ref PlayableGraph self, float deltaTime) => Evaluate(NeedGraph(self), deltaTime, true);
        public static DirectorUpdateMode GetTimeUpdateMode_Injected(ref PlayableGraph self) => NeedGraph(self).Mode;
        public static void SetTimeUpdateMode_Injected(ref PlayableGraph self, DirectorUpdateMode value) => NeedGraph(self).Mode = value;
        public static IExposedPropertyTable GetResolver_Injected(ref PlayableGraph self) => Graph(self)?.Resolver;
        public static void SetResolver_Injected(ref PlayableGraph self, IExposedPropertyTable value) => NeedGraph(self).Resolver = value;
        public static int GetPlayableCount_Injected(ref PlayableGraph self) => Graph(self)?.Nodes.FindAll(n => !n.Destroyed).Count ?? 0;
        public static int GetRootPlayableCount_Injected(ref PlayableGraph self) { int c = 0; if (Graph(self) is PGraph g) foreach (var _ in Roots(g)) c++; return c; }
        public static void GetRootPlayableInternal_Injected(ref PlayableGraph self, int index, out PlayableHandle ret)
        {
            ret = default;
            int i = 0;
            foreach (var n in Roots(NeedGraph(self))) if (i++ == index) { ret = n.Handle; return; }
        }
        public static void SynchronizeEvaluation_Injected(ref PlayableGraph self, ref PlayableGraph playable) { }
        public static int GetOutputCount_Injected(ref PlayableGraph self) => Graph(self)?.Outputs.FindAll(o => !o.Destroyed).Count ?? 0;
        public static bool GetOutputInternal_Injected(ref PlayableGraph self, int index, out PlayableOutputHandle ret)
        {
            ret = default;
            var list = NeedGraph(self).Outputs.FindAll(o => !o.Destroyed);
            if (index < 0 || index >= list.Count) return false;
            ret = list[index].Handle;
            return true;
        }
        public static int GetOutputCountByTypeInternal_Injected(ref PlayableGraph self, Type type) => Graph(self)?.Outputs.FindAll(o => !o.Destroyed && o.Type == type).Count ?? 0;
        public static bool GetOutputByTypeInternal_Injected(ref PlayableGraph self, Type type, int index, out PlayableOutputHandle ret)
        {
            ret = default;
            var list = NeedGraph(self).Outputs.FindAll(o => !o.Destroyed && o.Type == type);
            if (index < 0 || index >= list.Count) return false;
            ret = list[index].Handle;
            return true;
        }
        public static void CreatePlayableHandle_Injected(ref PlayableGraph self, out PlayableHandle ret) => ret = NewNode(NeedGraph(self)).Handle;
        public static bool CreateScriptOutputInternal_Injected(ref PlayableGraph self, string name, out PlayableOutputHandle ret)
        {
            ret = NewOutput(NeedGraph(self), name, typeof(ScriptPlayableOutput)).Handle;
            return true;
        }
        public static void DestroyOutputInternal_Injected(ref PlayableGraph self, ref PlayableOutputHandle output)
        {
            if (Out(output) is POut o) { o.Destroyed = true; o.G.Outputs.Remove(o); }
        }
        public static bool ConnectInternal_Injected(ref PlayableGraph self, ref PlayableHandle source, int sourceOutputPort, ref PlayableHandle destination, int destinationInputPort)
        {
            var g = NeedGraph(self);
            var a = Node(source);
            var b = Node(destination);
            if (a == null || b == null || a.G != g || b.G != g) return false;
            if (destinationInputPort < 0 || destinationInputPort >= b.Inputs.Count) return false;
            Connect(a, sourceOutputPort, b, destinationInputPort);
            return true;
        }
        public static void DisconnectInternal_Injected(ref PlayableGraph self, ref PlayableHandle playable, int inputPort)
        {
            var n = NeedNode(playable);
            if (inputPort < 0 || inputPort >= n.Inputs.Count) return;
            var i = n.Inputs[inputPort];
            if (i != null) for (int k = 0; k < i.Outputs.Count; k++) if (i.Outputs[k] == n) i.Outputs[k] = null;
            n.Inputs[inputPort] = null;
        }
        public static void DestroyPlayableInternal_Injected(ref PlayableGraph self, ref PlayableHandle playable) { if (Node(playable) is PNode n) DestroyNode(n); }
        public static void DestroySubgraphInternal_Injected(ref PlayableGraph self, ref PlayableHandle playable) => DestroySubgraph(Node(playable));
        public static bool IsMatchFrameRateEnabled_Injected(ref PlayableGraph self) => false;
        public static void EnableMatchFrameRate_Injected(ref PlayableGraph self, ref FrameRate frameRate) { }
        public static void DisableMatchFrameRate_Injected(ref PlayableGraph self) { }
        public static void GetFrameRate_Injected(ref PlayableGraph self, out FrameRate ret) => ret = default;
        public static string GetEditorName_Injected(ref PlayableGraph self) => Graph(self)?.Name ?? "";
    }

    public static unsafe partial class UnityEngine_Playables_PlayableHandle
    {
        public static bool IsNull_Injected(ref PlayableHandle self) => self.m_Handle == IntPtr.Zero;
        public static bool IsValid_Injected(ref PlayableHandle self) => Node(self) != null;
        public static Type GetPlayableType_Injected(ref PlayableHandle self)
        {
            var n = Node(self);
            if (n == null) return null;
            return n.Behaviour?.GetType() ?? typeof(Playable);
        }
        public static Type GetJobType_Injected(ref PlayableHandle self) => null;
        public static void SetScriptInstance_Injected(ref PlayableHandle self, object scriptInstance)
        {
            var n = NeedNode(self);
            n.Behaviour = scriptInstance;
            var p = n.P;
            if (scriptInstance is IPlayableBehaviour b)
            {
                try { b.OnPlayableCreate(p); }
                catch (Exception e) { Lifecycle.Log(e); }
            }
        }
        public static object GetScriptInstance_Injected(ref PlayableHandle self) => Node(self)?.Behaviour;
        public static bool CanChangeInputs_Injected(ref PlayableHandle self) => Node(self) != null;
        public static bool CanSetWeights_Injected(ref PlayableHandle self) => Node(self) != null;
        public static bool CanDestroy_Injected(ref PlayableHandle self) => Node(self) != null;
        public static PlayState GetPlayState_Injected(ref PlayableHandle self) => NeedNode(self).State;
        public static void Play_Injected(ref PlayableHandle self) => NeedNode(self).State = PlayState.Playing;
        public static void Pause_Injected(ref PlayableHandle self) => NeedNode(self).State = PlayState.Paused;
        public static double GetSpeed_Injected(ref PlayableHandle self) => NeedNode(self).Speed;
        public static void SetSpeed_Injected(ref PlayableHandle self, double value) => NeedNode(self).Speed = value;
        public static double GetTime_Injected(ref PlayableHandle self) => NeedNode(self).Time;
        public static void SetTime_Injected(ref PlayableHandle self, double value) => SetTime(NeedNode(self), value);
        public static bool IsDone_Injected(ref PlayableHandle self) => NeedNode(self).Done;
        public static void SetDone_Injected(ref PlayableHandle self, bool value) => NeedNode(self).Done = value;
        public static double GetDuration_Injected(ref PlayableHandle self) => NeedNode(self).Duration;
        public static void SetDuration_Injected(ref PlayableHandle self, double value) => NeedNode(self).Duration = value;
        public static bool GetPropagateSetTime_Injected(ref PlayableHandle self) => NeedNode(self).PropagateSetTime;
        public static void SetPropagateSetTime_Injected(ref PlayableHandle self, bool value) => NeedNode(self).PropagateSetTime = value;
        public static void GetGraph_Injected(ref PlayableHandle self, out PlayableGraph ret) => ret = NeedNode(self).G.Handle;
        public static int GetInputCount_Injected(ref PlayableHandle self) => NeedNode(self).Inputs.Count;
        public static void SetInputCount_Injected(ref PlayableHandle self, int value)
        {
            var n = NeedNode(self);
            while (n.Inputs.Count < value) { n.Inputs.Add(null); n.Weights.Add(0); }
            while (n.Inputs.Count > value)
            {
                var i = n.Inputs[^1];
                if (i != null) for (int k = 0; k < i.Outputs.Count; k++) if (i.Outputs[k] == n) i.Outputs[k] = null;
                n.Inputs.RemoveAt(n.Inputs.Count - 1);
                n.Weights.RemoveAt(n.Weights.Count - 1);
            }
        }
        public static int GetOutputCount_Injected(ref PlayableHandle self) => NeedNode(self).Outputs.Count;
        public static void SetOutputCount_Injected(ref PlayableHandle self, int value)
        {
            var n = NeedNode(self);
            while (n.Outputs.Count < value) n.Outputs.Add(null);
            while (n.Outputs.Count > value) n.Outputs.RemoveAt(n.Outputs.Count - 1);
        }
        public static void SetInputWeight_Injected(ref PlayableHandle self, ref PlayableHandle input, float weight)
        {
            var n = NeedNode(self);
            var x = Node(input);
            int i = x == null ? -1 : n.Inputs.IndexOf(x);
            if (i >= 0) n.Weights[i] = weight;
        }
        public static void SetInputWeightFromIndex_Injected(ref PlayableHandle self, int index, float weight)
        {
            var n = NeedNode(self);
            if (index >= 0 && index < n.Weights.Count) n.Weights[index] = weight;
        }
        public static float GetInputWeightFromIndex_Injected(ref PlayableHandle self, int index)
        {
            var n = NeedNode(self);
            return index >= 0 && index < n.Weights.Count ? n.Weights[index] : 0;
        }
        public static void GetInputHandle_Injected(ref PlayableHandle self, int index, out PlayableHandle ret)
        {
            var n = NeedNode(self);
            ret = index >= 0 && index < n.Inputs.Count && n.Inputs[index] != null ? n.Inputs[index].Handle : default;
        }
        public static void GetOutputHandle_Injected(ref PlayableHandle self, int index, out PlayableHandle ret)
        {
            var n = NeedNode(self);
            ret = index >= 0 && index < n.Outputs.Count && n.Outputs[index] != null ? n.Outputs[index].Handle : default;
        }
        public static void SetDelay_Injected(ref PlayableHandle self, double delay) => NeedNode(self).Delay = delay;
        public static double GetDelay_Injected(ref PlayableHandle self) => NeedNode(self).Delay;
        public static bool IsDelayed_Injected(ref PlayableHandle self) => NeedNode(self).Delay > 0;
        public static double GetPreviousTime_Injected(ref PlayableHandle self) => NeedNode(self).PrevTime;
        public static void SetLeadTime_Injected(ref PlayableHandle self, float value) => NeedNode(self).LeadTime = value;
        public static float GetLeadTime_Injected(ref PlayableHandle self) => NeedNode(self).LeadTime;
        public static PlayableTraversalMode GetTraversalMode_Injected(ref PlayableHandle self) => NeedNode(self).Traversal;
        public static void SetTraversalMode_Injected(ref PlayableHandle self, PlayableTraversalMode mode) => NeedNode(self).Traversal = mode;
        public static IntPtr GetJobData_Injected(ref PlayableHandle self) => IntPtr.Zero;
        public static DirectorWrapMode GetTimeWrapMode_Injected(ref PlayableHandle self) => NeedNode(self).WrapMode;
        public static void SetTimeWrapMode_Injected(ref PlayableHandle self, DirectorWrapMode mode) => NeedNode(self).WrapMode = mode;
    }

    public static unsafe partial class UnityEngine_Playables_PlayableOutputHandle
    {
        public static bool IsNull_Injected(ref PlayableOutputHandle self) => self.m_Handle == IntPtr.Zero;
        public static bool IsValid_Injected(ref PlayableOutputHandle self) => Out(self) != null;
        public static Type GetPlayableOutputType_Injected(ref PlayableOutputHandle self) => Out(self)?.Type;
        public static UnityEngine.Object GetReferenceObject_Injected(ref PlayableOutputHandle self) => NeedOut(self).Reference;
        public static void SetReferenceObject_Injected(ref PlayableOutputHandle self, UnityEngine.Object target) => NeedOut(self).Reference = target;
        public static UnityEngine.Object GetUserData_Injected(ref PlayableOutputHandle self) => NeedOut(self).UserData;
        public static void SetUserData_Injected(ref PlayableOutputHandle self, UnityEngine.Object target) => NeedOut(self).UserData = target;
        public static void GetSourcePlayable_Injected(ref PlayableOutputHandle self, out PlayableHandle ret) => ret = NeedOut(self).Source?.Handle ?? default;
        public static void SetSourcePlayable_Injected(ref PlayableOutputHandle self, ref PlayableHandle target, int port)
        {
            var o = NeedOut(self);
            o.Source = Node(target);
            o.Port = port;
        }
        public static int GetSourceOutputPort_Injected(ref PlayableOutputHandle self) => NeedOut(self).Port;
        public static float GetWeight_Injected(ref PlayableOutputHandle self) => NeedOut(self).Weight;
        public static void SetWeight_Injected(ref PlayableOutputHandle self, float weight) => NeedOut(self).Weight = weight;
        public static void PushNotification_Injected(ref PlayableOutputHandle self, ref PlayableHandle origin, INotification notification, object context)
        {
            var o = NeedOut(self);
            var p = new Playable(origin);
            foreach (var r in o.Receivers.ToArray())
            {
                try { r.OnNotify(p, notification, context); }
                catch (Exception e) { Lifecycle.Log(e); }
            }
        }
        public static INotificationReceiver[] GetNotificationReceivers_Injected(ref PlayableOutputHandle self) => NeedOut(self).Receivers.ToArray();
        public static void AddNotificationReceiver_Injected(ref PlayableOutputHandle self, INotificationReceiver receiver)
        {
            var o = NeedOut(self);
            if (receiver != null && !o.Receivers.Contains(receiver)) o.Receivers.Add(receiver);
        }
        public static void RemoveNotificationReceiver_Injected(ref PlayableOutputHandle self, INotificationReceiver receiver) => NeedOut(self).Receivers.Remove(receiver);
    }
}
