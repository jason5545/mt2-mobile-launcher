// Animator（Mecanim）的原生半邊：照 AnimatorController 跑狀態機、取樣 clip、寫回場景。
//
// 照 Unity 的定義做的部分（Unity 2022.3 手冊 Animation State Machines、Animation transitions、Animator 的 Scripting API）：
//   - 預設狀態與入口轉換、狀態速度（含速度參數）與 cycle offset、clip 的 loop 與 start／stop time。
//   - 轉換條件（If／IfNot／Greater／Less／Equals／NotEqual），trigger 被轉換用掉就清。exit time、Any State（Can Transition To Self）、Solo／Mute。
//   - 轉換的淡入淡出：來源與目的兩個狀態都推進、都取樣，照經過的比例混合（fixed duration 是秒，否則是來源狀態長度的比例）。
//   - 多個 layer：照順序疊，第 2 層以後照 layer 權重蓋過前面（Override）。
//   - Write Defaults：目前狀態沒動到、控制器裡別的狀態會動的屬性，回到 bind 時記下的初始值。
//   - 動畫事件：播放時間經過事件時間就照 SendMessage 的規則叫 GameObject 上的腳本（參數照方法的型別帶）。
//   - 寫到腳本欄位之後叫 OnDidApplyAnimationProperties（UI 的 Graphic 靠它 SetAllDirty）。
//   - 每格在 Update、協程之後，LateUpdate 之前（PreLateUpdate.DirectorUpdateAnimationBegin／End）。
// 推定、未查證的寫在旁邊。還沒做：BlendTree、子狀態機、Additive layer、Synced layer、root motion、IK、轉換中斷（Interruption Source）。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public sealed class NAnimator : NBehaviour
    {
        public RuntimeAnimatorController Controller;
        public float Speed = 1;
        public AnimatorUpdateMode UpdateMode;
        public AnimatorCullingMode CullingMode;
        public bool ApplyRootMotion, KeepStateOnDisable, WriteDefaultsOnDisable, FireEvents = true, LogWarnings = true;
        public AnimatorRuntime Rt;
        public static readonly List<NAnimator> Active = new List<NAnimator>();
        static readonly List<NAnimator> iter = new List<NAnimator>();

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Controller = Serializer.Ref(d["m_Controller"], typeof(RuntimeAnimatorController), ctx) as RuntimeAnimatorController;
            UpdateMode = (AnimatorUpdateMode)(int)Serializer.D(d["m_UpdateMode"]);
            CullingMode = (AnimatorCullingMode)(int)Serializer.D(d["m_CullingMode"]);
            ApplyRootMotion = Serializer.D(d["m_ApplyRootMotion"]) != 0;
            KeepStateOnDisable = Serializer.D(d["m_KeepAnimatorStateOnDisable"]) != 0;
            WriteDefaultsOnDisable = Serializer.D(d["m_WriteDefaultValuesOnDisable"]) != 0;
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NAnimator)src;
            Controller = s.Controller; Speed = s.Speed; UpdateMode = s.UpdateMode; CullingMode = s.CullingMode;
            ApplyRootMotion = s.ApplyRootMotion; KeepStateOnDisable = s.KeepStateOnDisable; WriteDefaultsOnDisable = s.WriteDefaultsOnDisable;
            FireEvents = s.FireEvents;
        }

        public override void OnActiveChanged()
        {
            bool on = Alive && Enabled && Go != null && Go.ActiveInHierarchy;
            if (on)
            {
                if (Active.Contains(this)) return;
                Active.Add(this);
                if (Rt == null) Rebind();
            }
            else if (Active.Remove(this) && Rt != null)
            {
                if (WriteDefaultsOnDisable) Rt.WriteDefaults();
                // 停用時狀態機回到初始（參數也回預設）；開了 keepAnimatorStateOnDisable 才保留
                if (!KeepStateOnDisable) Rt.Reset();
            }
        }

        public override void OnDestroyNative() => Active.Remove(this);

        public void Rebind()
        {
            var c = Native.Of<NAnimatorController>(Controller);
            Rt = c != null ? new AnimatorRuntime(this, c) : null;
        }

        static string GoPath(NGameObject g) => g == null ? "" : g.Parent == null ? g.Name : GoPath(g.Parent) + "/" + g.Name;

        static readonly bool debug = Environment.GetEnvironmentVariable("UN_ANIM_DEBUG") == "1";

        public static void UpdateAll()
        {
            if (debug && Engine.Time.FrameCount % 300 == 0)
            {
                Console.WriteLine("[Animator] 第 " + Engine.Time.FrameCount + " 格：作用中 " + Active.Count + " 個");
                foreach (var a in Active)
                    Console.WriteLine("  " + GoPath(a.Go) + "：" + (Native.Of<NAnimatorController>(a.Controller)?.Name ?? "（沒有控制器）") + " → " + (a.Rt?.StateName(0, false) ?? "（未初始化）") + (a.Rt != null ? "  " + a.Rt.DebugProps() : ""));
            }
            iter.Clear();
            iter.AddRange(Active);
            var t = Engine.Time;
            foreach (var a in iter)
            {
                if (!a.Alive || a.Rt == null || !a.Enabled || a.Go == null || !a.Go.ActiveInHierarchy) continue;
                float dt = a.UpdateMode == AnimatorUpdateMode.UnscaledTime ? t.UnscaledDeltaTime : t.DeltaTime;
                try { a.Rt.Step(dt * a.Speed); }
                catch (Exception e) { Engine.LogException(e, a); }
            }
        }

        public static void Register()
        {
            Loader.Factories["Animator"] = () => new NAnimator();
            Engine.PreLateUpdate.Add(UpdateAll);
            AnimationAssets.Register();
        }
    }

    /// <summary>一個 Animator 綁上去的可動畫屬性（同一個屬性在不同 clip 共用一份，Write Defaults 與混合靠它）</summary>
    public sealed class AnimProp
    {
        public string Key;
        public int Dim;
        public bool IsRotation, IsPPtr;
        public Action<float[]> Get;
        public Action<float[]> Set;
        public Func<UnityEngine.Object> GetObj;
        public Action<UnityEngine.Object> SetObj;
        /// <summary>寫完要叫 OnDidApplyAnimationProperties 的腳本</summary>
        public NMonoBehaviour Notify;
        public float[] Default, A, B, Acc, Tmp, Mix, Lay;
        public UnityEngine.Object DefaultObj, AObj, BObj, AccObj;
        public int StampA, StampB, StampLayer, StampAcc;

        public void Init()
        {
            Default = new float[Dim]; A = new float[Dim]; B = new float[Dim]; Acc = new float[Dim]; Tmp = new float[Dim]; Mix = new float[Dim]; Lay = new float[Dim];
            if (IsPPtr) DefaultObj = GetObj();
            else Get(Default);
        }
    }

    sealed class AnimBound
    {
        public AnimProp P;
        public ClipCurve C;
    }

    public sealed class AnimatorRuntime
    {
        sealed class ParamValue { public AnimParam P; public float F; public int I; public bool B; }

        sealed class LayerState
        {
            public AnimLayer L;
            public float Weight;
            public AnimState Cur, Next;
            public float CurTime, NextTime;
            public bool CurFresh, NextFresh;
            public AnimTransition Tr;
            public float TrElapsed, TrDuration;
            public bool Advanced;
        }

        readonly NAnimator owner;
        public readonly NAnimatorController Ctl;
        readonly NAnimatorController root;
        readonly Dictionary<string, ParamValue> byName = new Dictionary<string, ParamValue>();
        readonly Dictionary<int, ParamValue> byHash = new Dictionary<int, ParamValue>();
        readonly LayerState[] layers;
        readonly Dictionary<string, AnimProp> props = new Dictionary<string, AnimProp>();
        readonly List<AnimProp> allProps = new List<AnimProp>();
        readonly Dictionary<NAnimationClip, AnimBound[]> bound = new Dictionary<NAnimationClip, AnimBound[]>();
        readonly List<(AnimationEvent ev, NAnimationClip clip, AnimState state, float weight)> fired = new List<(AnimationEvent, NAnimationClip, AnimState, float)>();
        readonly List<AnimProp> touched = new List<AnimProp>();
        readonly List<AnimProp> layerProps = new List<AnimProp>();
        readonly HashSet<NMonoBehaviour> notify = new HashSet<NMonoBehaviour>();
        int stamp;
        static readonly HashSet<string> warned = new HashSet<string>();

        public AnimatorRuntime(NAnimator owner, NAnimatorController ctl)
        {
            this.owner = owner;
            Ctl = ctl;
            root = ctl.Root;
            foreach (var p in root.Params)
            {
                var v = new ParamValue { P = p };
                byName[p.Name] = v;
                byHash[p.Hash] = v;
            }
            ResetParams();
            layers = new LayerState[root.Layers.Count];
            for (int i = 0; i < layers.Length; i++) layers[i] = new LayerState { L = root.Layers[i], Weight = root.Layers[i].DefaultWeight };
            // 控制器裡所有 clip 動到的屬性先綁好，記下初始值（Write Defaults 用）
            foreach (var l in root.Layers)
                foreach (var s in l.States)
                    if (Clip(s) is NAnimationClip c) Bind(c);
            Reset();
        }

        public int LayerCount => layers.Length;

        void ResetParams()
        {
            foreach (var v in byName.Values) { v.F = v.P.DefaultFloat; v.I = v.P.DefaultInt; v.B = v.P.DefaultBool; }
        }

        public void Reset()
        {
            ResetParams();
            foreach (var ls in layers) Enter(ls, EntryState(ls.L), 0);
        }

        NAnimationClip Clip(AnimState s) => Native.Of<NAnimationClip>(Ctl.ClipOf(s));

        static float Length(NAnimationClip c) => c?.Length ?? 0;

        void Enter(LayerState ls, AnimState s, float time)
        {
            ls.Cur = s;
            ls.CurTime = time;
            ls.CurFresh = true;
            ls.Next = null;
            ls.Tr = null;
        }

        /// <summary>入口：入口轉換有條件成立的走它，否則預設狀態</summary>
        AnimState EntryState(AnimLayer l)
        {
            foreach (var t in l.Entry)
                if (!t.Mute && t.Dst != null && t.Conditions.Length > 0 && ConditionsMet(t)) { ConsumeTriggers(t); return t.Dst; }
            return l.DefaultState;
        }

        // ───────── 參數 ─────────

        ParamValue Param(string name)
        {
            if (name != null && byName.TryGetValue(name, out var v)) return v;
            Warn("Parameter '" + name + "' does not exist.");
            return null;
        }
        ParamValue Param(int hash)
        {
            if (byHash.TryGetValue(hash, out var v)) return v;
            Warn("Parameter hash " + hash + " does not exist.");
            return null;
        }

        void Warn(string msg)
        {
            if (owner.LogWarnings && warned.Add(Ctl.Name + "|" + msg)) Engine.Log(LogType.Warning, msg, owner);
        }

        public void SetFloat(string n, float v) { if (Param(n) is ParamValue p) Set(p, v); }
        public void SetFloat(int h, float v) { if (Param(h) is ParamValue p) Set(p, v); }
        public void SetInt(string n, int v) { if (Param(n) is ParamValue p) Set(p, v); }
        public void SetInt(int h, int v) { if (Param(h) is ParamValue p) Set(p, v); }
        public void SetBool(string n, bool v) { if (Param(n) is ParamValue p) Set(p, v ? 1 : 0); }
        public void SetBool(int h, bool v) { if (Param(h) is ParamValue p) Set(p, v ? 1 : 0); }
        public void SetTrigger(string n, bool v) { if (Param(n) is ParamValue p) Set(p, v ? 1 : 0); }
        public void SetTrigger(int h, bool v) { if (Param(h) is ParamValue p) Set(p, v ? 1 : 0); }
        public float GetFloat(string n) => Param(n) is ParamValue p ? AsFloat(p) : 0;
        public float GetFloat(int h) => Param(h) is ParamValue p ? AsFloat(p) : 0;
        public int GetInt(string n) => Param(n) is ParamValue p ? (int)AsFloat(p) : 0;
        public int GetInt(int h) => Param(h) is ParamValue p ? (int)AsFloat(p) : 0;
        public bool GetBool(string n) => Param(n) is ParamValue p && AsFloat(p) != 0;
        public bool GetBool(int h) => Param(h) is ParamValue p && AsFloat(p) != 0;

        static void Set(ParamValue p, float v)
        {
            switch (p.P.Type)
            {
                case AnimatorControllerParameterType.Float: p.F = v; break;
                case AnimatorControllerParameterType.Int: p.I = (int)v; break;
                default: p.B = v != 0; break;
            }
        }
        static float AsFloat(ParamValue p) => p.P.Type switch
        {
            AnimatorControllerParameterType.Float => p.F,
            AnimatorControllerParameterType.Int => p.I,
            _ => p.B ? 1 : 0,
        };

        bool ConditionsMet(AnimTransition t)
        {
            foreach (var c in t.Conditions)
            {
                if (c.Param == null || !byName.TryGetValue(c.Param, out var p)) return false;
                float v = AsFloat(p);
                switch (c.Mode)
                {
                    case 1: if (v == 0) return false; break;
                    case 2: if (v != 0) return false; break;
                    case 3: if (!(v > c.Threshold)) return false; break;
                    case 4: if (!(v < c.Threshold)) return false; break;
                    case 6: if (v != c.Threshold) return false; break;
                    case 7: if (v == c.Threshold) return false; break;
                    default: return false;
                }
            }
            return true;
        }

        void ConsumeTriggers(AnimTransition t)
        {
            foreach (var c in t.Conditions)
                if (c.Param != null && byName.TryGetValue(c.Param, out var p) && p.P.Type == AnimatorControllerParameterType.Trigger) p.B = false;
        }

        // ───────── 推進 ─────────

        float StateSpeed(AnimState s)
        {
            float sp = s.Speed;
            if (s.SpeedParam != null && byName.TryGetValue(s.SpeedParam, out var p)) sp *= AsFloat(p);
            return sp;
        }

        float CycleOffset(AnimState s, NAnimationClip c)
        {
            float o = s.CycleOffset;
            if (s.CycleOffsetParam != null && byName.TryGetValue(s.CycleOffsetParam, out var p)) o = AsFloat(p);
            // clip 自己的 Cycle Offset（匯入設定）也加上（未查證：兩個是否疊加）
            return o + (c?.CycleOffset ?? 0);
        }

        /// <summary>狀態時間 → clip 時間（loop 的取餘數，不 loop 的夾在頭尾之間，再加 start time）</summary>
        float ClipTime(AnimState s, NAnimationClip c, float stateTime)
        {
            float len = Length(c);
            float t;
            if (s.TimeParam != null && byName.TryGetValue(s.TimeParam, out var p)) t = AsFloat(p) * len;
            else t = stateTime + CycleOffset(s, c) * len;
            if (c.Loop && len > 0) t = ((t % len) + len) % len;
            else t = Math.Clamp(t, 0, len);
            return t + c.StartTime;
        }

        public float NormalizedTime(AnimState s, float stateTime)
        {
            float len = Length(Clip(s));
            return len > 0 ? stateTime / len : stateTime > 0 ? 1 : 0;
        }

        /// <summary>Animator.Update(dt) 與每格的推進：狀態機往前走、取樣、寫回、發事件</summary>
        public void Step(float dt)
        {
            foreach (var ls in layers) StepLayer(ls, dt, true);
            Apply();
            DispatchEvents();
        }

        void StepLayer(LayerState ls, float dt, bool allowTransition)
        {
            var cur = ls.Cur;
            if (cur == null) return;
            var clip = Clip(cur);
            float len = Length(clip);
            float prev = ls.CurTime;
            float sp = StateSpeed(cur);
            ls.CurTime += dt * sp;
            ls.Advanced = ls.CurTime > prev;
            CollectEvents(cur, clip, prev, ls.CurTime, ref ls.CurFresh, ls.Next == null ? 1 : 1 - TransitionWeight(ls));
            if (ls.Next != null)
            {
                var nclip = Clip(ls.Next);
                float nprev = ls.NextTime;
                ls.NextTime += dt * StateSpeed(ls.Next);
                CollectEvents(ls.Next, nclip, nprev, ls.NextTime, ref ls.NextFresh, TransitionWeight(ls));
                ls.TrElapsed += ls.Tr.HasFixedDuration ? dt : dt * sp;
                if (ls.TrElapsed >= ls.TrDuration)
                {
                    ls.Cur = ls.Next;
                    ls.CurTime = ls.NextTime;
                    ls.CurFresh = ls.NextFresh;
                    ls.Next = null;
                    ls.Tr = null;
                }
                return;
            }
            if (!allowTransition) return;
            float Norm(float t) => len > 0 ? t / len : t > 0 ? 1 : 0;
            var loop = clip == null || !clip.Loop ? 0 : len > 0 ? 1 : 2;
            var t = FindTransition(ls, Norm(prev), Norm(ls.CurTime), loop);
            if (t == null) return;
            ConsumeTriggers(t);
            var dst = t.ToExit ? EntryState(ls.L) : t.Dst;
            if (dst == null) return;
            float dstLen = Length(Clip(dst));
            float dur = t.HasFixedDuration ? t.Duration : t.Duration * len;
            if (dur <= 0)
            {
                // 瞬間切換：新狀態從 offset 開始，這一格就套它（它的轉換下一格才判斷）
                Enter(ls, dst, t.Offset * dstLen);
                StepLayer(ls, 0, false);
                return;
            }
            ls.Next = dst;
            ls.NextTime = t.Offset * dstLen;
            ls.NextFresh = true;
            ls.Tr = t;
            ls.TrElapsed = 0;
            ls.TrDuration = dur;
        }

        static float TransitionWeight(LayerState ls) => ls.Next == null || ls.TrDuration <= 0 ? 0 : Math.Clamp(ls.TrElapsed / ls.TrDuration, 0, 1);

        /// <summary>
        /// Has Exit Time：正規化時間到達 exit time 的那一格成立。循環的 clip、exit time 小於 1 的每一圈都判斷（跨過 k + exitTime）；
        /// 大於等於 1 的只判斷一次。長度 0 的循環 clip，時間有前進的每一格都算到達（未查證，照 TS 版）
        /// </summary>
        static bool ExitTimeReached(float exit, float prevN, float n, int loop, bool advanced)
        {
            if (loop == 2) return advanced;
            if (loop == 0 || exit >= 1) return prevN < exit && n >= exit;
            int k = (int)MathF.Floor(n - exit);
            return k >= 0 && prevN < k + exit && n >= k + exit;
        }

        AnimTransition FindTransition(LayerState ls, float prevN, float n, int loop)
        {
            foreach (var t in ls.L.AnyState)
            {
                if (t.Mute) continue;
                if (!t.CanTransitionToSelf && t.Dst == ls.Cur) continue;
                if (Passes(t, ls, prevN, n, loop)) return t;
            }
            var list = ls.Cur.Transitions;
            bool solo = false;
            foreach (var t in list) if (t.Solo) { solo = true; break; }
            foreach (var t in list)
            {
                if (t.Mute || (solo && !t.Solo)) continue;
                if (Passes(t, ls, prevN, n, loop)) return t;
            }
            return null;
        }

        bool Passes(AnimTransition t, LayerState ls, float prevN, float n, int loop)
        {
            if (t.HasExitTime) { if (!ExitTimeReached(t.ExitTime, prevN, n, loop, ls.Advanced)) return false; }
            else if (t.Conditions.Length == 0) return false;
            return ConditionsMet(t);
        }

        // ───────── 事件 ─────────

        /// <summary>狀態時間 prev → cur 之間經過的事件：loop 的 clip 展開成連續時間一圈一圈找；剛進狀態的那一格含起點</summary>
        void CollectEvents(AnimState s, NAnimationClip c, float prev, float cur, ref bool fresh, float weight)
        {
            bool f = fresh;
            fresh = false;
            if (c == null || c.Events.Count == 0 || cur < prev) return;
            float len = c.Length;
            float off = CycleOffset(s, c) * len;
            float a = prev + off, b = cur + off;
            bool loop = c.Loop && len > 0;
            int k0 = loop ? (int)MathF.Floor(a / len) : 0, k1 = loop ? (int)MathF.Floor(b / len) : 0;
            for (int k = k0; k <= k1; k++)
                foreach (var ev in c.Events)
                {
                    float t = k * len + (ev.m_Time - c.StartTime);
                    if ((t > a || (f && t >= a)) && t <= b) fired.Add((ev, c, s, weight));
                }
        }

        static readonly Dictionary<(Type, string), MethodInfo> methodCache = new Dictionary<(Type, string), MethodInfo>();

        static MethodInfo EventMethod(Type t, string name)
        {
            if (methodCache.TryGetValue((t, name), out var m)) return m;
            for (var k = t; k != null && m == null; k = k.BaseType)
                foreach (var mi in k.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (mi.Name != name) continue;
                    var ps = mi.GetParameters();
                    if (ps.Length > 1) continue;
                    if (ps.Length == 1)
                    {
                        var pt = ps[0].ParameterType;
                        if (!(pt == typeof(float) || pt == typeof(int) || pt == typeof(string) || pt == typeof(AnimationEvent) || pt.IsEnum || typeof(UnityEngine.Object).IsAssignableFrom(pt))) continue;
                    }
                    m = mi;
                    break;
                }
            methodCache[(t, name)] = m;
            return m;
        }

        void DispatchEvents()
        {
            if (fired.Count == 0) return;
            var list = fired.ToArray();
            fired.Clear();
            if (!owner.FireEvents) return;
            foreach (var (ev, clip, state, weight) in list)
            {
                bool any = false;
                var e = new AnimationEvent
                {
                    m_Time = ev.m_Time, m_FunctionName = ev.m_FunctionName, m_StringParameter = ev.m_StringParameter,
                    m_ObjectReferenceParameter = ev.m_ObjectReferenceParameter, m_FloatParameter = ev.m_FloatParameter,
                    m_IntParameter = ev.m_IntParameter, m_MessageOptions = ev.m_MessageOptions, m_Source = AnimationEventSource.Animator,
                    m_AnimatorClipInfo = new AnimatorClipInfo { m_ClipInstanceID = clip.InstanceId, m_Weight = weight },
                };
                foreach (var comp in owner.Go.Components.ToArray())
                {
                    if (comp is not NMonoBehaviour mb || !mb.Alive) continue;
                    var m = EventMethod(mb.Managed.GetType(), ev.m_FunctionName);
                    if (m == null) continue;
                    any = true;
                    var ps = m.GetParameters();
                    object arg = null;
                    if (ps.Length == 1)
                    {
                        var pt = ps[0].ParameterType;
                        arg = pt == typeof(float) ? e.m_FloatParameter : pt == typeof(int) ? e.m_IntParameter : pt == typeof(string) ? e.m_StringParameter
                            : pt == typeof(AnimationEvent) ? e : pt.IsEnum ? Enum.ToObject(pt, e.m_IntParameter)
                            : pt.IsInstanceOfType(e.m_ObjectReferenceParameter) ? e.m_ObjectReferenceParameter : null;
                    }
                    try { m.Invoke(mb.Managed, ps.Length == 1 ? new[] { arg } : null); }
                    catch (Exception x) { Lifecycle.Log(x, mb); }
                }
                if (!any && e.m_MessageOptions == 0)
                    Engine.Log(LogType.Error, "'" + owner.Go.Name + "' AnimationEvent '" + ev.m_FunctionName + "' on animation '" + clip.Name + "' has no receiver! Are you missing a component?", owner);
            }
        }

        // ───────── 取樣與寫回 ─────────

        void Sample(NAnimationClip c, AnimState s, float stateTime, bool slotA, int st, int layerStamp)
        {
            if (c == null) return;
            float t = ClipTime(s, c, stateTime);
            foreach (var b in Bind(c))
            {
                var p = b.P;
                var dst = slotA ? p.A : p.B;
                var cc = b.C;
                if (p.IsPPtr)
                {
                    UnityEngine.Object o = null;
                    var keys = cc.PPtrKeys;
                    int i = -1;
                    for (int k = 0; k < keys.Length && keys[k].time <= t; k++) i = k;
                    if (i < 0 && keys.Length > 0) i = 0;
                    if (i >= 0 && keys[i].guid != null) o = Assets.Load(keys[i].guid, keys[i].fileId, PPtrType(p));
                    if (slotA) p.AObj = o; else p.BObj = o;
                }
                else if (cc.Kind == ClipCurve.Kinds.Euler)
                {
                    var q = Quaternion.Euler(cc.Curves[0].Evaluate(t), cc.Curves[1].Evaluate(t), cc.Curves[2].Evaluate(t));
                    dst[0] = q.x; dst[1] = q.y; dst[2] = q.z; dst[3] = q.w;
                }
                else
                {
                    for (int i = 0; i < cc.Curves.Length && i < dst.Length; i++) dst[i] = cc.Curves[i].Evaluate(t);
                    if (cc.Kind == ClipCurve.Kinds.Rotation) Normalize(dst);
                }
                if (slotA) p.StampA = st; else p.StampB = st;
                if (p.StampLayer != layerStamp)
                {
                    p.StampLayer = layerStamp;
                    layerProps.Add(p);
                }
            }
        }

        static readonly Dictionary<AnimProp, Type> pptrTypes = new Dictionary<AnimProp, Type>();
        static Type PPtrType(AnimProp p) => pptrTypes.TryGetValue(p, out var t) ? t : typeof(UnityEngine.Object);

        static void Normalize(float[] q)
        {
            float l = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (l > 0) for (int i = 0; i < 4; i++) q[i] /= l;
        }

        static void Blend(AnimProp p, float[] a, float[] b, float w, float[] dst)
        {
            if (p.IsRotation)
            {
                // 四元數：照最短路徑做 nlerp
                float dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
                float s = dot < 0 ? -1 : 1;
                for (int i = 0; i < 4; i++) dst[i] = a[i] * (1 - w) + b[i] * s * w;
                Normalize(dst);
                return;
            }
            for (int i = 0; i < dst.Length; i++) dst[i] = a[i] + (b[i] - a[i]) * w;
        }

        /// <summary>狀態沒動到這個屬性時的值：Write Defaults 開著是初始值，關著維持目前的值</summary>
        float[] Fallback(AnimProp p, AnimState s, int accStamp)
        {
            if (s != null && s.WriteDefaults) return p.Default;
            if (p.StampAcc == accStamp) return p.Acc;
            p.Get(p.Tmp);
            return p.Tmp;
        }

        void Apply()
        {
            int accStamp = ++stamp;
            touched.Clear();
            for (int li = 0; li < layers.Length; li++)
            {
                var ls = layers[li];
                if (ls.Cur == null) continue;
                float lw = li == 0 ? 1 : ls.Weight;
                if (lw <= 0) continue;
                layerProps.Clear();
                int layerStamp = ++stamp;
                int sa = ++stamp;
                Sample(Clip(ls.Cur), ls.Cur, ls.CurTime, true, sa, layerStamp);
                int sb = ++stamp;
                if (ls.Next != null) Sample(Clip(ls.Next), ls.Next, ls.NextTime, false, sb, layerStamp);
                float w = TransitionWeight(ls);
                foreach (var p in layerProps)
                {
                    bool first = p.StampAcc != accStamp;
                    if (p.IsPPtr)
                    {
                        var v = p.StampA == sa ? p.AObj : ls.Cur.WriteDefaults ? p.DefaultObj : p.GetObj();
                        if (ls.Next != null && w >= 0.5f) v = p.StampB == sb ? p.BObj : ls.Next.WriteDefaults ? p.DefaultObj : p.GetObj();
                        // 物件參照不能內插：權重大的那邊贏
                        if (first || lw >= 0.5f) p.AccObj = v;
                    }
                    else
                    {
                        // 這個 layer 的值：轉換中是來源與目的照比例混合
                        var va = p.StampA == sa ? p.A : Fallback(p, ls.Cur, accStamp);
                        if (ls.Next != null)
                        {
                            var vb = p.StampB == sb ? p.B : Fallback(p, ls.Next, accStamp);
                            Blend(p, va, vb, w, p.Lay);
                        }
                        else Array.Copy(va, p.Lay, p.Dim);
                        // 疊到前面 layer 的結果上（Override：照 layer 權重蓋過去）
                        if (lw >= 1) Array.Copy(p.Lay, p.Acc, p.Dim);
                        else
                        {
                            var under = first ? Fallback(p, layers[0].Cur, accStamp) : p.Acc;
                            Blend(p, under, p.Lay, lw, p.Mix);
                            Array.Copy(p.Mix, p.Acc, p.Dim);
                        }
                    }
                    if (first) { p.StampAcc = accStamp; touched.Add(p); }
                }
            }
            // Write Defaults：這一格沒有任何狀態動到、但控制器會動的屬性，基礎 layer 目前的狀態開著 Write Defaults 就回初始值
            var baseState = layers.Length > 0 ? layers[0].Cur : null;
            if (baseState != null && baseState.WriteDefaults)
                foreach (var p in allProps)
                    if (p.StampAcc != accStamp)
                    {
                        p.StampAcc = accStamp;
                        if (p.IsPPtr) p.AccObj = p.DefaultObj;
                        else Array.Copy(p.Default, p.Acc, p.Dim);
                        touched.Add(p);
                    }
            notify.Clear();
            foreach (var p in touched)
            {
                bool changed;
                if (p.IsPPtr)
                {
                    changed = !ReferenceEquals(p.GetObj(), p.AccObj);
                    if (changed) p.SetObj(p.AccObj);
                }
                else
                {
                    p.Get(p.Tmp);
                    changed = false;
                    for (int i = 0; i < p.Dim; i++) if (p.Tmp[i] != p.Acc[i]) { changed = true; break; }
                    if (changed) p.Set(p.Acc);
                }
                if (changed && p.Notify != null) notify.Add(p.Notify);
            }
            foreach (var mb in notify) CallDidApply(mb);
        }

        public void WriteDefaults()
        {
            notify.Clear();
            foreach (var p in allProps)
            {
                if (p.IsPPtr) p.SetObj(p.DefaultObj);
                else p.Set(p.Default);
                if (p.Notify != null) notify.Add(p.Notify);
            }
            foreach (var mb in notify) CallDidApply(mb);
        }

        static readonly Dictionary<Type, MethodInfo> didApply = new Dictionary<Type, MethodInfo>();

        static void CallDidApply(NMonoBehaviour mb)
        {
            if (!mb.Alive) return;
            var t = mb.Managed.GetType();
            if (!didApply.TryGetValue(t, out var m))
            {
                m = t.GetMethod("OnDidApplyAnimationProperties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                didApply[t] = m;
            }
            if (m == null) return;
            try { m.Invoke(mb.Managed, null); }
            catch (Exception e) { Lifecycle.Log(e, mb); }
        }

        // ───────── 綁定 ─────────

        AnimBound[] Bind(NAnimationClip c)
        {
            if (bound.TryGetValue(c, out var b)) return b;
            var list = new List<AnimBound>();
            foreach (var cc in c.Bindings)
            {
                var node = FindNode(cc.Path);
                AnimProp p = null;
                if (node != null)
                {
                    try { p = Resolve(node, cc); }
                    catch (Exception e) { Engine.LogException(e, owner); }
                }
                if (p == null)
                {
                    var what = node == null ? "找不到路徑 " + cc.Path : "綁不到 " + cc.Path + "（classID " + cc.ClassID + "）的 " + (cc.Attribute ?? cc.Kind.ToString());
                    if (warned.Add(c.Name + "|" + what)) Assets.Warn("Animator " + Ctl.Name + "：clip " + c.Name + " " + what);
                    continue;
                }
                list.Add(new AnimBound { P = p, C = cc });
            }
            b = list.ToArray();
            bound[c] = b;
            return b;
        }

        NGameObject FindNode(string path)
        {
            var n = owner.Go;
            if (string.IsNullOrEmpty(path)) return n;
            foreach (var name in path.Split('/'))
            {
                NGameObject hit = null;
                foreach (var ch in n.Transform.Children)
                    if (ch.Alive && ch.Go != null && ch.Go.Name == name) { hit = ch.Go; break; }
                if (hit == null) return null;
                n = hit;
            }
            return n;
        }

        AnimProp Prop(string key, Func<AnimProp> make)
        {
            if (props.TryGetValue(key, out var p)) return p;
            p = make();
            if (p == null) return null;
            p.Key = key;
            p.Init();
            props[key] = p;
            allProps.Add(p);
            return p;
        }

        static int Comp(string c) => c switch { "x" or "r" => 0, "y" or "g" => 1, "z" or "b" => 2, "w" or "a" => 3, _ => -1 };

        static AnimProp F(Func<float> get, Action<float> set) => new AnimProp { Dim = 1, Get = v => v[0] = get(), Set = v => set(v[0]) };

        static AnimProp VecComp(string attr, string field, Func<Vector4> get, Action<Vector4> set)
        {
            if (!attr.StartsWith(field + ".")) return null;
            int i = Comp(attr.Substring(field.Length + 1));
            if (i < 0) return null;
            return F(() => get()[i], v => { var x = get(); x[i] = v; set(x); });
        }

        AnimProp Resolve(NGameObject go, ClipCurve cc)
        {
            var tr = go.Transform;
            string id = go.InstanceId + "|";
            switch (cc.Kind)
            {
                case ClipCurve.Kinds.Position:
                    return Prop(id + "T|pos", () => new AnimProp { Dim = 3, Get = v => { var p = tr.LocalPosition; v[0] = p.x; v[1] = p.y; v[2] = p.z; }, Set = v => tr.LocalPosition = new Vector3(v[0], v[1], v[2]) });
                case ClipCurve.Kinds.Scale:
                    return Prop(id + "T|scale", () => new AnimProp { Dim = 3, Get = v => { var p = tr.LocalScale; v[0] = p.x; v[1] = p.y; v[2] = p.z; }, Set = v => tr.LocalScale = new Vector3(v[0], v[1], v[2]) });
                case ClipCurve.Kinds.Euler:
                case ClipCurve.Kinds.Rotation:
                    return Prop(id + "T|rot", () => new AnimProp { Dim = 4, IsRotation = true, Get = v => { var q = tr.LocalRotation; v[0] = q.x; v[1] = q.y; v[2] = q.z; v[3] = q.w; }, Set = v => tr.LocalRotation = new Quaternion(v[0], v[1], v[2], v[3]) });
            }
            var attr = cc.Attribute ?? "";
            if (cc.ClassID == 114) return Script(go, cc, id);
            if (cc.Kind == ClipCurve.Kinds.PPtr)
            {
                if (cc.ClassID == 212 && attr == "m_Sprite" && go.Get<NSpriteRenderer>() is NSpriteRenderer sr)
                    return PPtr(id + "212|m_Sprite", typeof(Sprite), () => sr.Sprite, o => sr.Sprite = o as Sprite, null);
                return null;
            }
            switch (cc.ClassID)
            {
                case 1:
                    if (attr != "m_IsActive") return null;
                    return Prop(id + "1|" + attr, () => F(() => go.ActiveSelf ? 1 : 0, v => { if (go.ActiveSelf != v > 0.5f) Hierarchy.SetActive(go, v > 0.5f); }));
                case 4:
                case 224:
                    return Prop(id + "4|" + attr, () => TransformFloat(tr, attr));
                case 225:
                    if (go.Get<NCanvasGroup>() is not NCanvasGroup g) return null;
                    if (attr == "m_Alpha") return Prop(id + "225|" + attr, () => F(() => g.Alpha, v => { if (g.Alpha != v) { g.Alpha = v; g.Changed(); } }));
                    if (attr == "m_Interactable") return Prop(id + "225|" + attr, () => F(() => g.Interactable ? 1 : 0, v => { g.Interactable = v > 0.5f; g.Changed(); }));
                    if (attr == "m_BlocksRaycasts") return Prop(id + "225|" + attr, () => F(() => g.BlocksRaycasts ? 1 : 0, v => { g.BlocksRaycasts = v > 0.5f; g.Changed(); }));
                    return null;
            }
            var comp = Component(go, cc.ClassID);
            if (comp == null) return null;
            string key = id + cc.ClassID + "|" + attr;
            if (attr == "m_Enabled" && comp is NBehaviour beh)
                return Prop(key, () => F(() => beh.Enabled ? 1 : 0, v => Lifecycle.SetEnabled(beh, v > 0.5f)));
            if (comp is NRenderer r)
            {
                if (attr.StartsWith("material.")) return Prop(key, () => MaterialProp(r, attr.Substring(9)));
                if (r is NSpriteRenderer s)
                {
                    if (VecComp(attr, "m_Color", () => s.Color, v => s.Color = v) is AnimProp pc) return Prop(key, () => pc);
                    if (VecComp(attr, "m_Size", () => s.Size, v => s.Size = v) is AnimProp ps) return Prop(key, () => ps);
                    if (attr == "m_FlipX") return Prop(key, () => F(() => s.FlipX ? 1 : 0, v => s.FlipX = v > 0.5f));
                    if (attr == "m_FlipY") return Prop(key, () => F(() => s.FlipY ? 1 : 0, v => s.FlipY = v > 0.5f));
                }
                if (attr == "m_SortingOrder") return Prop(key, () => F(() => r.SortingOrder, v => r.SortingOrder = (int)v));
            }
            return null;
        }

        static readonly Dictionary<int, string[]> classTypes = new Dictionary<int, string[]>
        {
            [8] = new[] { "Behaviour" }, [20] = new[] { "Camera" }, [23] = new[] { "MeshRenderer" }, [25] = new[] { "Renderer" },
            [95] = new[] { "Animator" }, [120] = new[] { "LineRenderer" }, [137] = new[] { "SkinnedMeshRenderer" },
            [198] = new[] { "ParticleSystem" }, [199] = new[] { "ParticleSystemRenderer" }, [212] = new[] { "SpriteRenderer" },
            [222] = new[] { "CanvasRenderer" }, [223] = new[] { "Canvas" }, [96] = new[] { "TrailRenderer" }, [82] = new[] { "AudioSource" },
            [108] = new[] { "Light" }, [61] = new[] { "BoxCollider2D" }, [58] = new[] { "CircleCollider2D" },
        };

        static NComponent Component(NGameObject go, int classId)
        {
            if (!classTypes.TryGetValue(classId, out var names)) return null;
            foreach (var c in go.Components)
            {
                if (!c.Alive || (object)c.Managed == null) continue;
                for (var t = c.Managed.GetType(); t != null && t != typeof(UnityEngine.Object); t = t.BaseType)
                    if (Array.IndexOf(names, t.Name) >= 0) return c;
            }
            return null;
        }

        static AnimProp TransformFloat(NTransform tr, string attr)
        {
            Vector4 V3(Vector3 v) => new Vector4(v.x, v.y, v.z, 0);
            return VecComp(attr, "m_LocalPosition", () => V3(tr.LocalPosition), v => tr.LocalPosition = v)
                ?? VecComp(attr, "m_LocalScale", () => V3(tr.LocalScale), v => tr.LocalScale = v)
                ?? VecComp(attr, "localEulerAnglesRaw", () => V3(tr.LocalRotation.eulerAngles), v => tr.LocalRotation = Quaternion.Euler(v.x, v.y, v.z))
                ?? VecComp(attr, "m_LocalRotation", () => { var q = tr.LocalRotation; return new Vector4(q.x, q.y, q.z, q.w); }, v => tr.LocalRotation = new Quaternion(v.x, v.y, v.z, v.w))
                ?? VecComp(attr, "m_AnchoredPosition", () => tr.AnchoredPosition, v => tr.SetAnchoredPosition(v))
                ?? VecComp(attr, "m_SizeDelta", () => tr.SizeDelta, v => tr.SetSizeDelta(v))
                ?? VecComp(attr, "m_AnchorMin", () => tr.AnchorMin, v => tr.SetAnchorMin(v))
                ?? VecComp(attr, "m_AnchorMax", () => tr.AnchorMax, v => tr.SetAnchorMax(v))
                ?? VecComp(attr, "m_Pivot", () => tr.Pivot, v => tr.SetPivot(v));
        }

        /// <summary>材質屬性動畫（material._Color.a、material._Alpha）：寫進 Renderer 的屬性區塊，初始值取第一個材質</summary>
        static AnimProp MaterialProp(NRenderer r, string name)
        {
            int dot = name.LastIndexOf('.');
            int comp = dot > 0 ? Comp(name.Substring(dot + 1)) : -1;
            var prop = comp >= 0 ? name.Substring(0, dot) : name;
            int pid = PropertyIds.Of(prop);
            NMaterial Mat() => r.Shared.Length > 0 ? Native.Of<NMaterial>(r.Shared[0]) : null;
            if (comp >= 0)
                return F(
                    () => r.Block != null && r.Block.Vectors.TryGetValue(pid, out var bv) ? bv[comp] : Mat()?.GetVector(pid)[comp] ?? 0,
                    v =>
                    {
                        r.Block ??= new PropertySheet();
                        var x = r.Block.Vectors.TryGetValue(pid, out var bv) ? bv : Mat()?.GetVector(pid) ?? Vector4.zero;
                        x[comp] = v;
                        r.Block.SetVector(pid, x);
                    });
            return F(
                () => r.Block != null && (r.Block.Floats.ContainsKey(pid) || r.Block.Ints.ContainsKey(pid)) ? r.Block.GetFloat(pid) : Mat()?.GetFloat(pid) ?? 0,
                v => { r.Block ??= new PropertySheet(); r.Block.SetFloat(pid, v); });
        }

        AnimProp PPtr(string key, Type type, Func<UnityEngine.Object> get, Action<UnityEngine.Object> set, NMonoBehaviour notify)
        {
            return Prop(key, () =>
            {
                var p = new AnimProp { Dim = 0, IsPPtr = true, GetObj = get, SetObj = set, Notify = notify };
                pptrTypes[p] = type;
                return p;
            });
        }

        // ───────── 腳本欄位（classID 114） ─────────

        AnimProp Script(NGameObject go, ClipCurve cc, string id)
        {
            Loader.ScriptByRef.TryGetValue(cc.Script ?? "", out var want);
            foreach (var c in go.Components)
            {
                if (c is not NMonoBehaviour mb || !mb.Alive) continue;
                var t = mb.Managed.GetType();
                if (want != null && !want.IsAssignableFrom(t)) continue;
                var attr = ResolveCrc(cc.Attribute ?? "", t);
                string key = id + "114|" + mb.InstanceId + "|" + attr;
                if (attr == "m_Enabled")
                    return Prop(key, () => F(() => mb.Enabled ? 1 : 0, v => Lifecycle.SetEnabled(mb, v > 0.5f)));
                var chain = FieldChain(t, attr);
                if (chain == null) { if (want != null) return null; continue; }
                var leaf = chain[^1].FieldType;
                var obj = mb.Managed;
                if (cc.Kind == ClipCurve.Kinds.PPtr)
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(leaf)) return null;
                    return PPtr(key, leaf, () => GetChain(obj, chain) as UnityEngine.Object, o => SetChain(obj, chain, 0, o), mb);
                }
                if (!(leaf == typeof(float) || leaf == typeof(double) || leaf == typeof(int) || leaf == typeof(uint) || leaf == typeof(bool) || leaf == typeof(byte) || leaf == typeof(short) || leaf.IsEnum)) return null;
                return Prop(key, () =>
                {
                    var p = F(() => ToFloat(GetChain(obj, chain)), v => SetChain(obj, chain, 0, FromFloat(v, leaf)));
                    p.Notify = mb;
                    return p;
                });
            }
            return null;
        }

        static float ToFloat(object o) => o switch
        {
            float f => f, double d => (float)d, int i => i, uint u => u, bool b => b ? 1 : 0, byte by => by, short s => s,
            Enum e => Convert.ToInt32(e), _ => 0,
        };

        /// <summary>曲線值寫回欄位：整數照四捨五入、bool 照 0.5 為界（未查證）</summary>
        static object FromFloat(float v, Type t)
        {
            if (t == typeof(float)) return v;
            if (t == typeof(double)) return (double)v;
            if (t == typeof(bool)) return v > 0.5f;
            int i = (int)MathF.Round(v);
            if (t == typeof(int)) return i;
            if (t == typeof(uint)) return (uint)Math.Max(0, i);
            if (t == typeof(byte)) return (byte)Math.Clamp(i, 0, 255);
            if (t == typeof(short)) return (short)i;
            if (t.IsEnum) return Enum.ToObject(t, i);
            return v;
        }

        static readonly Dictionary<(Type, string), FieldInfo[]> chains = new Dictionary<(Type, string), FieldInfo[]>();

        static FieldInfo FindField(Type t, string name)
        {
            for (var k = t; k != null; k = k.BaseType)
            {
                var f = k.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        static FieldInfo[] FieldChain(Type t, string attr)
        {
            if (chains.TryGetValue((t, attr), out var c)) return c;
            var list = new List<FieldInfo>();
            var cur = t;
            foreach (var part in attr.Split('.'))
            {
                var f = cur == null ? null : FindField(cur, part);
                if (f == null) { list = null; break; }
                list.Add(f);
                cur = f.FieldType;
            }
            c = list?.ToArray();
            chains[(t, attr)] = c;
            return c;
        }

        static object GetChain(object o, FieldInfo[] chain)
        {
            foreach (var f in chain)
            {
                if (o == null) return null;
                o = f.GetValue(o);
            }
            return o;
        }

        /// <summary>沿欄位鏈寫值：中間是 struct 的，改完要寫回上一層（FieldInfo.SetValue 對 boxed struct 是就地改）</summary>
        static void SetChain(object o, FieldInfo[] chain, int i, object leaf)
        {
            var f = chain[i];
            if (i == chain.Length - 1) { f.SetValue(o, leaf); return; }
            var child = f.GetValue(o);
            if (child == null) return;
            SetChain(child, chain, i + 1, leaf);
            if (f.FieldType.IsValueType) f.SetValue(o, child);
        }

        /// <summary>
        /// 綁到腳本欄位的曲線，build 只存屬性路徑的 CRC32，AssetRipper 認不出名稱時寫成 script_0x&lt;CRC32&gt;_&lt;亂碼&gt;
        ///（MT2 載入畫面火焰的 Image.m_Sprite 是 script_0x7822D856_UsmorHH）。照欄位名（含一層子欄位）算 CRC32 對回原名
        /// </summary>
        static string ResolveCrc(string attr, Type t)
        {
            if (!attr.StartsWith("script_0x") || attr.Length < 17) return attr;
            if (!uint.TryParse(attr.AsSpan(9, 8), System.Globalization.NumberStyles.HexNumber, null, out var want)) return attr;
            for (var k = t; k != null && k != typeof(MonoBehaviour); k = k.BaseType)
                foreach (var f in k.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (Crc32.Of(f.Name) == want) return f.Name;
                    if (f.FieldType.IsPrimitive || f.FieldType == typeof(string)) continue;
                    foreach (var sub in f.FieldType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (Crc32.Of(f.Name + "." + sub.Name) == want) return f.Name + "." + sub.Name;
                }
            return attr;
        }

        public string DebugProps()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in allProps)
            {
                if (sb.Length > 300) { sb.Append("…"); break; }
                sb.Append(p.Key.Substring(p.Key.IndexOf('|') + 1)).Append('=');
                if (p.IsPPtr) sb.Append(p.GetObj()?.name ?? "null");
                else { p.Get(p.Tmp); sb.Append(string.Join(",", Array.ConvertAll(p.Tmp, x => x.ToString("0.###")))); }
                sb.Append("  ");
            }
            return sb.ToString();
        }

        // ───────── 給 icall 的查詢 ─────────

        public AnimState FindState(int layer, int hash)
        {
            if (layer < 0 || layer >= layers.Length) return null;
            foreach (var s in layers[layer].L.States) if (s.NameHash == hash || s.FullPathHash == hash) return s;
            return null;
        }

        /// <summary>
        /// Animator.Play：直接切到狀態（取消進行中的轉換）。normalizedTime 是負無限大（預設）時，已經在這個狀態就不重頭播，
        /// 不在就從 0 開始（未查證：照常見用法推定）
        /// </summary>
        public void Play(int hash, int layer, float normalizedTime, bool fixedTime)
        {
            if (layer < 0)
            {
                for (int i = 0; i < layers.Length; i++) if (FindState(i, hash) != null) { layer = i; break; }
                if (layer < 0) { Warn("Animator.GotoState: State could not be found"); return; }
            }
            var s = FindState(layer, hash);
            if (s == null) { Warn("Animator.GotoState: State could not be found"); return; }
            var ls = layers[layer];
            float len = Length(Clip(s));
            if (float.IsNegativeInfinity(normalizedTime))
            {
                if (ls.Cur == s && ls.Next == null) return;
                normalizedTime = 0;
            }
            Enter(ls, s, fixedTime ? normalizedTime : normalizedTime * len);
        }

        public void CrossFade(int hash, int layer, float duration, float offset, bool fixedTime)
        {
            if (layer < 0) for (int i = 0; i < layers.Length; i++) if (FindState(i, hash) != null) { layer = i; break; }
            var s = FindState(Math.Max(layer, 0), hash);
            if (s == null) { Warn("Animator.CrossFade: State could not be found"); return; }
            var ls = layers[Math.Max(layer, 0)];
            float len = Length(Clip(s));
            if (float.IsNegativeInfinity(offset)) offset = 0;
            if (duration <= 0 || ls.Cur == null) { Enter(ls, s, fixedTime ? offset : offset * len); return; }
            ls.Next = s;
            ls.NextTime = fixedTime ? offset : offset * len;
            ls.NextFresh = true;
            ls.Tr = new AnimTransition { Duration = duration, HasFixedDuration = fixedTime };
            ls.TrElapsed = 0;
            ls.TrDuration = fixedTime ? duration : duration * Length(Clip(ls.Cur));
        }

        public bool IsInTransition(int layer) => layer >= 0 && layer < layers.Length && layers[layer].Next != null;

        public AnimatorStateInfo StateInfo(int layer, bool next)
        {
            var info = new AnimatorStateInfo();
            if (layer < 0 || layer >= layers.Length) return info;
            var ls = layers[layer];
            var s = next ? ls.Next : ls.Cur;
            if (s == null) return info;
            var c = Clip(s);
            float t = next ? ls.NextTime : ls.CurTime;
            info.m_Name = s.NameHash;
            info.m_Path = s.FullPathHash;
            info.m_FullPath = s.FullPathHash;
            info.m_NormalizedTime = NormalizedTime(s, t);
            info.m_Length = Length(c);
            info.m_Speed = s.Speed;
            info.m_SpeedMultiplier = s.SpeedParam != null && byName.TryGetValue(s.SpeedParam, out var p) ? AsFloat(p) : 1;
            info.m_Tag = s.TagHash;
            info.m_Loop = c != null && c.Loop ? 1 : 0;
            return info;
        }

        public AnimatorTransitionInfo TransitionInfo(int layer)
        {
            var info = new AnimatorTransitionInfo();
            if (!IsInTransition(layer)) return info;
            var ls = layers[layer];
            info.m_Name = ls.Tr.NameHash;
            info.m_HasFixedDuration = ls.Tr.HasFixedDuration;
            info.m_Duration = ls.Tr.Duration;
            info.m_NormalizedTime = TransitionWeight(ls);
            info.m_AnyState = ls.L.AnyState.Contains(ls.Tr);
            return info;
        }

        public AnimatorClipInfo[] ClipInfo(int layer, bool next)
        {
            if (layer < 0 || layer >= layers.Length) return Array.Empty<AnimatorClipInfo>();
            var ls = layers[layer];
            var s = next ? ls.Next : ls.Cur;
            var c = s == null ? null : Clip(s);
            if (c == null) return Array.Empty<AnimatorClipInfo>();
            float w = ls.Next == null ? 1 : next ? TransitionWeight(ls) : 1 - TransitionWeight(ls);
            return new[] { new AnimatorClipInfo { m_ClipInstanceID = c.InstanceId, m_Weight = w } };
        }

        public float LayerWeight(int i) => i == 0 ? 1 : i > 0 && i < layers.Length ? layers[i].Weight : 0;
        public void SetLayerWeight(int i, float w) { if (i > 0 && i < layers.Length) layers[i].Weight = w; }
        public string LayerName(int i) => i >= 0 && i < layers.Length ? layers[i].L.Name : "";
        public int LayerIndex(string name) { for (int i = 0; i < layers.Length; i++) if (layers[i].L.Name == name) return i; return -1; }
        public string StateName(int layer, bool next)
        {
            if (layer < 0 || layer >= layers.Length) return "";
            var s = next ? layers[layer].Next : layers[layer].Cur;
            return s?.Name ?? "";
        }
    }
}
