// PlayableDirector 的原生半邊：建 graph（PlayableAsset.CreatePlayable）、照 WrapMode 每格推進、綁定與 exposed reference。
//
// 照原文：
//   - Stop 停止並銷毀 graph，time 回 initialTime；played／paused／stopped 由原生叫 managed 的 SendOnPlayableDirectorPlay／Pause／Stop。
//   - WrapMode None 到終點：在 duration 那一格求值一次再 Stop（Timeline 1.7.0 changelog：修好「最後一格不保證執行」，Unity 2021.2 以上）。
//   - 綁定照場景存檔的 m_SceneBindings（key 是 timeline 資產裡的 track），exposed reference 照 m_ExposedReferences（key 是 PropertyName 的 id）。
// 推定（未查證，照 TS 版）：
//   - 每格在 Update、協程之後推進（Update.DirectorUpdate），排在 Animator 之前。GameTime 用 Time.deltaTime，UnscaledGameTime 與 DSPClock 用不乘 timeScale 的時間。
//   - Play On Awake：第一次在階層裡啟用時開始播。停用的導演不推進，也不停止。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Playables;

namespace UnityNative
{
    public sealed class NPlayableDirector : NBehaviour, IRemap
    {
        public ScriptableObject Asset;
        public DirectorWrapMode Wrap = DirectorWrapMode.Hold;
        public DirectorUpdateMode Mode = DirectorUpdateMode.GameTime;
        public double InitialTime, Time;
        public bool PlayOnAwake, Playing, Awoken;
        public PGraph Graph;
        public PNode Root;
        List<(string guid, string fileId, UnityEngine.Object value)> sceneBindings = new List<(string, string, UnityEngine.Object)>();
        readonly Dictionary<UnityEngine.Object, UnityEngine.Object> bindings = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
        Dictionary<int, UnityEngine.Object> refs = new Dictionary<int, UnityEngine.Object>();
        bool sceneBindingsResolved;
        static readonly List<NPlayableDirector> playing = new List<NPlayableDirector>();

        PlayableDirector D => (PlayableDirector)Managed;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Asset = Serializer.Ref(d["m_PlayableAsset"], typeof(ScriptableObject), ctx) as ScriptableObject;
            Wrap = (DirectorWrapMode)(int)Serializer.D(d["m_WrapMode"]);
            Mode = d["m_DirectorUpdateMode"] != null ? (DirectorUpdateMode)(int)Serializer.D(d["m_DirectorUpdateMode"]) : DirectorUpdateMode.GameTime;
            InitialTime = Serializer.D(d["m_InitialTime"]);
            Time = InitialTime;
            PlayOnAwake = Serializer.D(d["m_InitialState"]) == 1;
            foreach (var b in d["m_SceneBindings"] as JsonArray ?? new JsonArray())
            {
                var k = b?["key"] as JsonObject;
                if (k?["guid"] == null) continue;
                sceneBindings.Add((k["guid"].ToString(), k["fileID"]?.ToString() ?? "11400000", Serializer.Ref(b["value"], typeof(UnityEngine.Object), ctx)));
            }
            foreach (var e in d["m_ExposedReferences"]?["m_References"] as JsonArray ?? new JsonArray())
                if (e is JsonObject o)
                    foreach (var kv in o)
                        if (int.TryParse(kv.Key, out var id)) refs[id] = Serializer.Ref(kv.Value, typeof(UnityEngine.Object), ctx);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NPlayableDirector)src;
            Asset = s.Asset; Wrap = s.Wrap; Mode = s.Mode; InitialTime = s.InitialTime; Time = s.InitialTime; PlayOnAwake = s.PlayOnAwake;
            sceneBindings = new List<(string, string, UnityEngine.Object)>(s.sceneBindings);
            refs = new Dictionary<int, UnityEngine.Object>(s.refs);
            foreach (var kv in s.bindings) bindings[kv.Key] = kv.Value;
        }

        public void Remap(Func<UnityEngine.Object, UnityEngine.Object> f)
        {
            for (int i = 0; i < sceneBindings.Count; i++)
            {
                var (g, id, v) = sceneBindings[i];
                sceneBindings[i] = (g, id, f(v) ?? v);
            }
            foreach (var k in new List<int>(refs.Keys)) refs[k] = f(refs[k]) ?? refs[k];
            foreach (var k in new List<UnityEngine.Object>(bindings.Keys)) bindings[k] = f(bindings[k]) ?? bindings[k];
        }

        public override void OnActiveChanged()
        {
            if (!(Alive && Enabled && Go != null && Go.ActiveInHierarchy) || Awoken) return;
            Awoken = true;
            if (PlayOnAwake) Play();
        }

        public override void OnDestroyNative()
        {
            if (Graph != null) Playables.DestroyGraph(Graph);
            Graph = null;
            playing.Remove(this);
        }

        // ───────── 綁定 ─────────

        void ResolveSceneBindings()
        {
            if (sceneBindingsResolved) return;
            sceneBindingsResolved = true;
            foreach (var (g, id, v) in sceneBindings)
            {
                var key = Assets.Load(g, id, typeof(UnityEngine.Object));
                if ((object)key != null && !bindings.ContainsKey(key)) bindings[key] = v;
            }
        }

        public UnityEngine.Object GetBinding(UnityEngine.Object key)
        {
            if ((object)key == null) return null;
            ResolveSceneBindings();
            return bindings.TryGetValue(key, out var v) ? v : null;
        }
        public bool HasBinding(UnityEngine.Object key) { ResolveSceneBindings(); return (object)key != null && bindings.ContainsKey(key); }
        public void SetBinding(UnityEngine.Object key, UnityEngine.Object value) { ResolveSceneBindings(); if ((object)key != null) bindings[key] = value; }
        public void ClearBinding(UnityEngine.Object key) { ResolveSceneBindings(); if ((object)key != null) bindings.Remove(key); }

        public UnityEngine.Object GetRef(int id, out bool valid) { valid = refs.TryGetValue(id, out var v); return v; }
        public void SetRef(int id, UnityEngine.Object v) => refs[id] = v;
        public void ClearRef(int id) => refs.Remove(id);

        // ───────── 播放 ─────────

        public double Duration => Asset is PlayableAsset pa ? pa.duration : 0;

        /// <summary>建 graph：PlayableAsset.CreatePlayable(graph, 導演的 GameObject)，output 照綁定接上目標</summary>
        public void Rebuild()
        {
            if (Graph != null) Playables.DestroyGraph(Graph);
            Graph = null;
            Root = null;
            if (Asset is not PlayableAsset pa) return;
            var g = Playables.NewGraph(Go?.Name ?? "");
            g.Resolver = D;
            g.Mode = Mode;
            Graph = g;
            try
            {
                var p = pa.CreatePlayable(g.Handle, Go?.GO);
                Root = Playables.Node(p.GetHandle());
            }
            catch (Exception e) { Lifecycle.Log(e, this); }
            RebindOutputs();
            if (Root != null) Playables.SetTime(Root, Time);
        }

        public void RebindOutputs()
        {
            if (Graph == null) return;
            foreach (var o in Graph.Outputs)
                if (!o.Destroyed && o.Type == typeof(ScriptPlayableOutput)) o.UserData = GetBinding(o.Reference);
        }

        void EvaluateNow(double dt, bool evaluate, bool loop = false, bool hold = false)
        {
            if (Graph == null || Root == null) return;
            Playables.SetTime(Root, Time);
            Playables.Evaluate(Graph, dt, evaluate, loop, hold, Root);
        }

        void Send(Action<PlayableDirector> f)
        {
            try { f(D); }
            catch (Exception e) { Lifecycle.Log(e, this); }
        }

        static readonly bool debug = Environment.GetEnvironmentVariable("UN_ANIM_DEBUG") == "1";

        public void Play()
        {
            if (debug) Console.WriteLine("[Director] Play " + Go?.Name + "（" + Asset?.name + "，" + Duration.ToString("0.###") + " 秒，第 " + Engine.Time.FrameCount + " 格）");
            if (Graph == null) Rebuild();
            if (Graph == null) return;
            Graph.Playing = true;
            if (!playing.Contains(this)) playing.Add(this);
            if (!Playing)
            {
                Playing = true;
                Send(d => d.SendOnPlayableDirectorPlay());
            }
        }

        public void Stop()
        {
            bool was = Playing;
            if (debug && was) Console.WriteLine("[Director] Stop " + Go?.Name + "（第 " + Engine.Time.FrameCount + " 格，輸出 " + (Graph?.Outputs.Count ?? 0) + " 個）");
            if (Graph != null) Playables.DestroyGraph(Graph);
            Graph = null;
            Root = null;
            Playing = false;
            Time = InitialTime;
            playing.Remove(this);
            if (was) Send(d => d.SendOnPlayableDirectorStop());
        }

        public void Pause()
        {
            if (!Playing) return;
            Playing = false;
            if (Graph != null) Graph.Playing = false;
            playing.Remove(this);
            Send(d => d.SendOnPlayableDirectorPause());
        }

        public void Resume()
        {
            if (Playing || Graph == null) return;
            Play();
        }

        public void Evaluate()
        {
            if (Graph == null) Rebuild();
            EvaluateNow(0, true);
        }

        public void SetTime(double t)
        {
            Time = t;
            if (Root != null) Playables.SetTime(Root, t);
        }

        public void SetAsset(ScriptableObject a)
        {
            Asset = a;
            if (Graph != null) Playables.DestroyGraph(Graph);
            Graph = null;
            Root = null;
        }

        static float ModeDelta(DirectorUpdateMode m)
        {
            var t = Engine.Time;
            return m == DirectorUpdateMode.GameTime ? t.DeltaTime : t.UnscaledDeltaTime;
        }

        /// <summary>每格（Update.DirectorUpdate）：播放中的導演往前推、照 WrapMode 處理終點；自己 Play 的 graph 照更新模式求值</summary>
        public static void UpdateAll()
        {
            foreach (var s in playing.ToArray())
            {
                if (!s.Alive || !s.Playing || s.Graph == null) { playing.Remove(s); continue; }
                if (!s.Enabled || s.Go == null || !s.Go.ActiveInHierarchy) continue;
                if (s.Mode == DirectorUpdateMode.Manual) continue;
                try
                {
                    double dur = s.Duration;
                    double dt = ModeDelta(s.Mode);
                    double t = s.Time + dt;
                    bool loop = false, hold = false;
                    if (t >= dur)
                    {
                        if (s.Wrap == DirectorWrapMode.Loop && dur > 0) { t %= dur; loop = true; }
                        else if (s.Wrap == DirectorWrapMode.Hold) { t = dur; hold = true; }
                        else
                        {
                            s.Time = dur;
                            s.EvaluateNow(dt, false);
                            s.Stop();
                            continue;
                        }
                    }
                    s.Time = t;
                    s.EvaluateNow(dt, false, loop, hold);
                }
                catch (Exception e) { Lifecycle.Log(e, s); }
            }
            foreach (var g in new List<PGraph>(Playables.ManualGraphs))
            {
                if (g.Destroyed || !g.Playing) { Playables.ManualGraphs.Remove(g); continue; }
                if (g.Mode == DirectorUpdateMode.Manual) continue;
                try { Playables.Evaluate(g, ModeDelta(g.Mode), false); }
                catch (Exception e) { Lifecycle.Log(e); }
            }
        }

        public static void Register()
        {
            Loader.Factories["PlayableDirector"] = () => new NPlayableDirector();
            Engine.PreLateUpdate.Insert(0, UpdateAll);
        }
    }
}

namespace UnityNative.Icalls
{
    using UnityNative;

    public static unsafe partial class UnityEngine_Playables_PlayableDirector
    {
        static NPlayableDirector P(PlayableDirector self) => Native.Self<NPlayableDirector>(self);

        public static void set_timeUpdateMode(PlayableDirector self, DirectorUpdateMode value) { var p = P(self); p.Mode = value; if (p.Graph != null) p.Graph.Mode = value; }
        public static DirectorUpdateMode get_timeUpdateMode(PlayableDirector self) => P(self).Mode;
        public static void set_time(PlayableDirector self, double value) => P(self).SetTime(value);
        public static double get_time(PlayableDirector self) => P(self).Time;
        public static void set_initialTime(PlayableDirector self, double value) => P(self).InitialTime = value;
        public static double get_initialTime(PlayableDirector self) => P(self).InitialTime;
        public static double get_duration(PlayableDirector self) => P(self).Duration;
        public static void Evaluate(PlayableDirector self) => P(self).Evaluate();
        public static void EvaluateNextFrame(PlayableDirector self) => P(self).Evaluate();
        public static void Play(PlayableDirector self) => P(self).Play();
        public static void PlayOnFrame_Injected(PlayableDirector self, ref FrameRate frameRate) => P(self).Play();
        public static void Stop(PlayableDirector self) => P(self).Stop();
        public static void Pause(PlayableDirector self) => P(self).Pause();
        public static void Resume(PlayableDirector self) => P(self).Resume();
        public static void RebuildGraph(PlayableDirector self) => P(self).Rebuild();
        public static void RebindPlayableGraphOutputs(PlayableDirector self) => P(self).RebindOutputs();
        public static void ProcessPendingGraphChanges(PlayableDirector self) { }
        public static void ResetFrameTiming() { }
        public static UnityEngine.Object GetGenericBinding(PlayableDirector self, UnityEngine.Object key) => P(self).GetBinding(key);
        public static void ClearGenericBinding(PlayableDirector self, UnityEngine.Object key) => P(self).ClearBinding(key);
        public static bool HasGenericBinding(PlayableDirector self, UnityEngine.Object key) => P(self).HasBinding(key);
        public static void Internal_SetGenericBinding(PlayableDirector self, UnityEngine.Object key, UnityEngine.Object value) => P(self).SetBinding(key, value);
        public static PlayState GetPlayState(PlayableDirector self) => P(self).Playing ? PlayState.Playing : PlayState.Paused;
        public static void SetWrapMode(PlayableDirector self, DirectorWrapMode mode) => P(self).Wrap = mode;
        public static DirectorWrapMode GetWrapMode(PlayableDirector self) => P(self).Wrap;
        public static void SetPlayOnAwake(PlayableDirector self, bool on) => P(self).PlayOnAwake = on;
        public static bool GetPlayOnAwake(PlayableDirector self) => P(self).PlayOnAwake;
        public static void SetPlayableAsset(PlayableDirector self, ScriptableObject asset) => P(self).SetAsset(asset);
        public static ScriptableObject Internal_GetPlayableAsset(PlayableDirector self) => P(self).Asset;
        public static void ClearReferenceValue_Injected(PlayableDirector self, ref PropertyName id) => P(self).ClearRef(id.id);
        public static void SetReferenceValue_Injected(PlayableDirector self, ref PropertyName id, UnityEngine.Object value) => P(self).SetRef(id.id, value);
        public static UnityEngine.Object GetReferenceValue_Injected(PlayableDirector self, ref PropertyName id, out bool idValid) => P(self).GetRef(id.id, out idValid);
        public static void GetGraphHandle_Injected(PlayableDirector self, out PlayableGraph ret) => ret = P(self).Graph?.Handle ?? default;
    }
}
