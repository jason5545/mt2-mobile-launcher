// ParticleSystem 元件的原生半邊與管理：啟用與停用、playOnAwake、每格推進、stopAction、子發射器、腳本 API。
// 照 TS 版（unity-cs2ts/runtime/src/particles/manager.ts），依據 Unity 2022.3 手冊 Main module 與 ParticleSystem 腳本 API：
//   - playOnAwake：物件啟用時開始播。物件停用時粒子清掉、停止（再啟用照 playOnAwake 從頭播）。
//   - 子發射器（Sub Emitters module 指到的子系統）自己不發射，也不照 playOnAwake 播；由父系統的觸發驅動。
//   - 每格在全部腳本的 LateUpdate 之後推進（PostLateUpdate，排在 Canvas 重建之前：UIParticle 在 willRenderCanvases 烘網格）。
//     dt：useUnscaledTime 用 unscaledDeltaTime，乘 simulationSpeed。
//   - stopAction（系統停下、粒子死光時）：1 Disable、2 Destroy、3 Callback（SendMessage OnParticleSystemStopped）。
//   - 發射器的姿態照 scalingMode（Hierarchy 0：整條父物件鏈的縮放；Local 1：只有自己的 localScale；Shape 2：只縮放發射形狀）。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public sealed class NParticleSystem : NComponent, IRemap
    {
        /// <summary>元件的模組資料。載入時只留自己那一段原始 JSON（compRaw，UTF-8），第一次用到才解析：
        /// MT2 開機時 prefab 裡有近千個粒子系統，大多不會播，全部當場解析要好幾百 ms</summary>
        public PMod Comp
        {
            get => comp ??= compRaw != null ? PMod.Parse(System.Text.Json.JsonDocument.Parse(compRaw).RootElement) : new PMod();
            set => comp = value;
        }
        PMod comp;
        byte[] compRaw;
        public ParticleSim Sim;
        public bool IsSubEmitter;
        bool active;
        public List<(ParticleSystem target, int type, int properties, float probability)> SubRefs = new List<(ParticleSystem, int, int, float)>();
        public static readonly List<NParticleSystem> All = new List<NParticleSystem>();
        public static readonly XorShift GlobalRng = new XorShift(0x5eed1234);

        public ParticleSystem PS => (ParticleSystem)Managed;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            if (El.ValueKind == System.Text.Json.JsonValueKind.Object) { compRaw = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(El).ToArray(); comp = null; ReadSubRefs(El, ctx); }
            else { Comp = PMod.Parse(d); ReadSubRefs(d, ctx); }
            All.Add(this);
        }

        void ReadSubRefs(System.Text.Json.JsonElement d, LoadContext ctx)
        {
            SubRefs.Clear();
            var sm = PJ.Get(d, "SubModule");
            if (sm.ValueKind != System.Text.Json.JsonValueKind.Object || PJ.F(sm, "enabled") != 1) return;
            if (PJ.Get(sm, "subEmitters") is not { ValueKind: System.Text.Json.JsonValueKind.Array } subs) return;
            foreach (var s in subs.EnumerateArray())
            {
                if (PJ.Get(s, "emitter") is not { ValueKind: System.Text.Json.JsonValueKind.Object } e) continue;
                var target = Serializer.Ref(e, typeof(ParticleSystem), ctx) as ParticleSystem;
                if (target == null) continue;
                SubRefs.Add((target, (int)PJ.F(s, "type"), (int)PJ.F(s, "properties"), PJ.Has(s, "emitProbability") ? PJ.F(s, "emitProbability") : 1));
                if (Native.Of<NParticleSystem>(target) is NParticleSystem t && t != this) t.IsSubEmitter = true;
            }
        }

        void ReadSubRefs(JsonObject d, LoadContext ctx)
        {
            SubRefs.Clear();
            var sm = d["SubModule"] as JsonObject;
            if (sm == null || AnimationAssets.F(sm["enabled"]) != 1) return;
            foreach (var s in sm["subEmitters"] as JsonArray ?? new JsonArray())
            {
                if (s?["emitter"] is not JsonObject e) continue;
                var target = Serializer.Ref(e, typeof(ParticleSystem), ctx) as ParticleSystem;
                if (target == null) continue;
                SubRefs.Add((target, (int)AnimationAssets.F(s["type"]), (int)AnimationAssets.F(s["properties"]), s["emitProbability"] != null ? AnimationAssets.F(s["emitProbability"]) : 1));
                if (Native.Of<NParticleSystem>(target) is NParticleSystem t && t != this) t.IsSubEmitter = true;
            }
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NParticleSystem)src;
            // 來源還沒解析：共用它那份 JSON（不會被改），一樣用到才解析；解析過（可能被腳本改過）就複製解析好的
            if (s.comp != null) Comp = s.comp.Clone();
            else { compRaw = s.compRaw; comp = null; }
            SubRefs = new List<(ParticleSystem, int, int, float)>(s.SubRefs);
            IsSubEmitter = s.IsSubEmitter;
            All.Add(this);
        }

        public void Remap(Func<UnityEngine.Object, UnityEngine.Object> f)
        {
            for (int i = 0; i < SubRefs.Count; i++)
            {
                var (t, a, b, c) = SubRefs[i];
                var nt = f(t) as ParticleSystem ?? t;
                SubRefs[i] = (nt, a, b, c);
                if (Native.Of<NParticleSystem>(nt) is NParticleSystem x && x != this) x.IsSubEmitter = true;
            }
        }

        public override void OnDestroyNative() { All.Remove(this); Sim = null; }

        public ParticleSim SimOf()
        {
            if (Sim != null) return Sim;
            Sim = new ParticleSim(this, Comp);
            foreach (var (target, type, props, prob) in SubRefs)
            {
                var t = Native.Of<NParticleSystem>(target);
                if (t == null || t == this) continue;
                var child = t.SimOf();
                child.SubEmitterOf = Sim;
                Sim.Subs.Add(new ParticleSim.Sub { Sim = child, Type = type, Properties = props, Probability = prob });
            }
            return Sim;
        }

        public override void OnActiveChanged()
        {
            bool now = Alive && Go != null && Go.ActiveInHierarchy;
            if (now == active) return;
            active = now;
            if (!now) { Sim?.Stop(true); return; }
            if (Comp.F("playOnAwake", 1) == 1 && !IsSubEmitter)
            {
                var s = SimOf();
                SetPose();
                s.Play(GlobalRng);
            }
        }

        /// <summary>發射器的姿態（照 scalingMode）</summary>
        public void SetPose()
        {
            var s = SimOf();
            var tr = Tr;
            var pos = tr.Position;
            var rot = tr.Rotation;
            var lossy = tr.LossyScale;
            var local = tr.LocalScale;
            int mode = (int)Comp.F("scalingMode", 1);
            var p = new EmitterPose { Position = pos, Rotation = rot };
            if (mode == 0) { p.ToWorld = tr.LocalToWorld; p.SizeScale = lossy; }
            else if (mode == 2) { p.ToWorld = MathUtil.TRS(pos, rot, Vector3.one); p.SizeScale = Vector3.one; p.ShapeScale = lossy; }
            else { p.ToWorld = MathUtil.TRS(pos, rot, local); p.SizeScale = local; }
            MathUtil.Inverse(p.ToWorld, out p.Inv);
            s.Pose = p;
        }

        // ── 腳本 API ──

        public List<NParticleSystem> Family(bool withChildren)
        {
            var list = new List<NParticleSystem>();
            if (!withChildren || Go == null) { list.Add(this); return list; }
            Hierarchy.Walk(Go, g => { foreach (var c in g.Components) if (c is NParticleSystem p && p.Alive) list.Add(p); });
            return list;
        }

        public void Play(bool withChildren)
        {
            foreach (var c in Family(withChildren))
            {
                if (c.Go == null || !c.Go.ActiveInHierarchy) continue;
                c.active = true;
                if (c.IsSubEmitter && c != this) continue;
                c.SetPose();
                c.SimOf().Play(GlobalRng);
            }
        }

        /// <summary>Simulate(t, withChildren, restart, fixedTimeStep)：往前快轉 t 秒後暫停（手冊：Simulate 之後系統是暫停的）</summary>
        public void Simulate(float t, bool withChildren, bool restart, bool fixedTimeStep)
        {
            foreach (var c in Family(withChildren))
            {
                var s = c.SimOf();
                if (restart) { s.Stop(true); s.Time = 0; }
                c.SetPose();
                if (!s.Playing) s.Play(GlobalRng);
                s.Paused = false;
                float step = fixedTimeStep ? 1 / 50f : t, left = t;
                while (left > 1e-9f) { float d = Math.Min(step, left); s.Step(d); left -= d; }
                s.Paused = true;
            }
        }

        // ── 每格 ──

        public static void UpdateAll()
        {
            var t = Engine.Time;
            foreach (var c in All.ToArray())
            {
                if (!c.Alive || c.Go == null || !c.Go.ActiveInHierarchy) continue;
                var s = c.Sim;
                if (s == null || (!s.Playing && !s.IsAlive)) continue;
                try
                {
                    var prev = s.Pose?.Position;
                    c.SetPose();
                    float d = (c.Comp.F("useUnscaledTime") == 1 ? t.UnscaledDeltaTime : t.DeltaTime) * c.Comp.F("simulationSpeed", 1);
                    if (prev.HasValue && d > 0) s.EmitterVel = (s.Pose.Value.Position - prev.Value) / d;
                    s.Step(d);
                    if (!s.Playing && !s.IsAlive && !s.StopNotified)
                    {
                        s.StopNotified = true;
                        c.StopAction();
                    }
                }
                catch (Exception e) { Engine.LogException(e, c); }
            }
        }

        void StopAction()
        {
            switch ((int)Comp.F("stopAction"))
            {
                case 1: Hierarchy.SetActive(Go, false); break;
                case 2: Destroyer.Destroy(Go, 0); break;
                case 3: Icalls.Comps.Send(Go, "OnParticleSystemStopped", null); break;
            }
        }

        public static void Register()
        {
            Loader.Factories["ParticleSystem"] = () => new NParticleSystem();
            Loader.Factories["ParticleSystemRenderer"] = () => new NParticleSystemRenderer();
            Engine.PostLateUpdate.Insert(0, UpdateAll);
        }
    }
}

namespace UnityNative.Icalls
{
    using UnityNative;

    public static unsafe partial class UnityEngine_ParticleSystem
    {
        static NParticleSystem P(ParticleSystem ps) => Native.Self<NParticleSystem>(ps);

        public static bool get_isPlaying(ParticleSystem self) => P(self).Sim?.IsPlaying ?? false;
        public static bool get_isEmitting(ParticleSystem self) => P(self).Sim?.IsEmitting ?? false;
        public static bool get_isStopped(ParticleSystem self) => P(self).Sim?.IsStopped ?? true;
        public static bool get_isPaused(ParticleSystem self) => P(self).Sim?.Paused ?? false;
        public static int get_particleCount(ParticleSystem self) => P(self).Sim?.N ?? 0;
        public static float get_time(ParticleSystem self) => P(self).Sim?.Time ?? 0;
        public static void set_time(ParticleSystem self, float value) => P(self).SimOf().Time = Math.Clamp(value, 0, P(self).SimOf().Duration);
        public static float get_totalTime(ParticleSystem self) => P(self).Sim?.Clock ?? 0;
        public static uint get_randomSeed(ParticleSystem self) => (uint)P(self).Comp.F("randomSeed");
        public static void set_randomSeed(ParticleSystem self, uint value) => P(self).Comp.SetF("randomSeed", value);
        public static bool get_useAutoRandomSeed(ParticleSystem self) => P(self).Comp.F("autoRandomSeed", 1) == 1;
        public static void set_useAutoRandomSeed(ParticleSystem self, bool value) => P(self).Comp.SetB("autoRandomSeed", value);
        public static bool get_proceduralSimulationSupported(ParticleSystem self) => false;

        public static void Play(ParticleSystem self, bool withChildren) => P(self).Play(withChildren);
        public static void Pause(ParticleSystem self, bool withChildren) { foreach (var c in P(self).Family(withChildren)) c.Sim?.Pause(); }
        /// <summary>ParticleSystemStopBehavior：0 StopEmittingAndClear、1 StopEmitting</summary>
        public static void Stop(ParticleSystem self, bool withChildren, ParticleSystemStopBehavior stopBehavior)
        {
            foreach (var c in P(self).Family(withChildren)) c.Sim?.Stop(stopBehavior == ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        public static void Clear(ParticleSystem self, bool withChildren) { foreach (var c in P(self).Family(withChildren)) c.Sim?.Clear(); }
        public static bool IsAlive(ParticleSystem self, bool withChildren)
        {
            foreach (var c in P(self).Family(withChildren)) if (c.Sim != null && c.Sim.IsAlive) return true;
            return false;
        }
        public static void Simulate(ParticleSystem self, float t, bool withChildren, bool restart, bool fixedTimeStep) => P(self).Simulate(t, withChildren, restart, fixedTimeStep);
        public static void Emit_Internal(ParticleSystem self, int count)
        {
            var p = P(self);
            p.SetPose();
            p.SimOf().Emit(count);
        }
        /// <summary>Emit(EmitParams, count)：照起始設定發（EmitParams 的覆寫值沒做）</summary>
        public static void Emit_Injected(ParticleSystem self, ref ParticleSystem.EmitParams emitParams, int count) => Emit_Internal(self, count);
        public static bool get_has3DParticleRotations(ParticleSystem self) => P(self).Comp.M("InitialModule").F("rotation3D") == 1;
        public static bool get_hasNonUniformParticleSizes(ParticleSystem self) => P(self).Comp.M("InitialModule").F("size3D") == 1;
    }

    public static unsafe partial class UnityEngine_ParticleSystem__MainModule
    {
        static PMod C(ref ParticleSystem.MainModule m) => Native.Self<NParticleSystem>(m.m_ParticleSystem).Comp;
        static PMod I(ref ParticleSystem.MainModule m) => C(ref m).M("InitialModule");

        public static float get_duration_Injected(ref ParticleSystem.MainModule self) => C(ref self).F("lengthInSec", 5);
        public static void set_duration_Injected(ref ParticleSystem.MainModule self, float value) => C(ref self).SetF("lengthInSec", value);
        public static bool get_loop_Injected(ref ParticleSystem.MainModule self) => C(ref self).F("looping") == 1;
        public static void set_loop_Injected(ref ParticleSystem.MainModule self, bool value) => C(ref self).SetB("looping", value);
        public static bool get_playOnAwake_Injected(ref ParticleSystem.MainModule self) => C(ref self).F("playOnAwake", 1) == 1;
        public static void set_playOnAwake_Injected(ref ParticleSystem.MainModule self, bool value) => C(ref self).SetB("playOnAwake", value);
        public static float get_simulationSpeed_Injected(ref ParticleSystem.MainModule self) => C(ref self).F("simulationSpeed", 1);
        public static void set_simulationSpeed_Injected(ref ParticleSystem.MainModule self, float value) => C(ref self).SetF("simulationSpeed", value);
        public static ParticleSystemScalingMode get_scalingMode_Injected(ref ParticleSystem.MainModule self) => (ParticleSystemScalingMode)(int)C(ref self).F("scalingMode", 1);
        public static void set_scalingMode_Injected(ref ParticleSystem.MainModule self, ParticleSystemScalingMode value) => C(ref self).SetF("scalingMode", (int)value);
        /// <summary>simulationSpace：序列化是 moveWithTransform（0 Local、1 World、2 Custom）</summary>
        public static ParticleSystemSimulationSpace get_simulationSpace_Injected(ref ParticleSystem.MainModule self) => (ParticleSystemSimulationSpace)(int)C(ref self).F("moveWithTransform");
        public static void set_simulationSpace_Injected(ref ParticleSystem.MainModule self, ParticleSystemSimulationSpace value) => C(ref self).SetF("moveWithTransform", (int)value);
        public static int get_maxParticles_Injected(ref ParticleSystem.MainModule self) => (int)I(ref self).F("maxNumParticles", 1000);
        public static void set_maxParticles_Injected(ref ParticleSystem.MainModule self, int value) => I(ref self).SetF("maxNumParticles", value);
        public static ParticleSystemStopAction get_stopAction_Injected(ref ParticleSystem.MainModule self) => (ParticleSystemStopAction)(int)C(ref self).F("stopAction");
        public static void set_stopAction_Injected(ref ParticleSystem.MainModule self, ParticleSystemStopAction value) => C(ref self).SetF("stopAction", (int)value);
        public static bool get_useUnscaledTime_Injected(ref ParticleSystem.MainModule self) => C(ref self).F("useUnscaledTime") == 1;
        public static void set_useUnscaledTime_Injected(ref ParticleSystem.MainModule self, bool value) => C(ref self).SetB("useUnscaledTime", value);

        public static void get_startLifetime_Injected(ref ParticleSystem.MainModule self, out ParticleSystem.MinMaxCurve ret) => ret = I(ref self).C("startLifetime").ToManaged();
        public static void set_startLifetime_Injected(ref ParticleSystem.MainModule self, ref ParticleSystem.MinMaxCurve value) => I(ref self).Set("startLifetime", MMCurve.FromManaged(value));
        public static float get_startLifetimeMultiplier_Injected(ref ParticleSystem.MainModule self) => I(ref self).C("startLifetime").Scalar;
        public static void get_startSpeed_Injected(ref ParticleSystem.MainModule self, out ParticleSystem.MinMaxCurve ret) => ret = I(ref self).C("startSpeed").ToManaged();
        public static void set_startSpeed_Injected(ref ParticleSystem.MainModule self, ref ParticleSystem.MinMaxCurve value) => I(ref self).Set("startSpeed", MMCurve.FromManaged(value));
        public static void get_startSize_Injected(ref ParticleSystem.MainModule self, out ParticleSystem.MinMaxCurve ret) => ret = I(ref self).C("startSize").ToManaged();
        public static void set_startSize_Injected(ref ParticleSystem.MainModule self, ref ParticleSystem.MinMaxCurve value) => I(ref self).Set("startSize", MMCurve.FromManaged(value));
        public static void get_startColor_Injected(ref ParticleSystem.MainModule self, out ParticleSystem.MinMaxGradient ret) => ret = (I(ref self).G("startColor") ?? new MMGradient()).ToManaged();
        public static void set_startColor_Injected(ref ParticleSystem.MainModule self, ref ParticleSystem.MinMaxGradient value) => I(ref self).Set("startColor", MMGradient.FromManaged(value));
    }

    public static unsafe partial class UnityEngine_ParticleSystem__EmissionModule
    {
        static PMod E(ref ParticleSystem.EmissionModule m) => Native.Self<NParticleSystem>(m.m_ParticleSystem).Comp.M("EmissionModule");
        public static bool get_enabled_Injected(ref ParticleSystem.EmissionModule self) => E(ref self).On;
        public static void set_enabled_Injected(ref ParticleSystem.EmissionModule self, bool value) => E(ref self).SetB("enabled", value);
        public static void get_rateOverTime_Injected(ref ParticleSystem.EmissionModule self, out ParticleSystem.MinMaxCurve ret) => ret = E(ref self).C("rateOverTime").ToManaged();
        public static void set_rateOverTime_Injected(ref ParticleSystem.EmissionModule self, ref ParticleSystem.MinMaxCurve value) => E(ref self).Set("rateOverTime", MMCurve.FromManaged(value));
        public static float get_rateOverTimeMultiplier_Injected(ref ParticleSystem.EmissionModule self) => E(ref self).C("rateOverTime").Scalar;
        public static void set_rateOverTimeMultiplier_Injected(ref ParticleSystem.EmissionModule self, float value)
        {
            var c = E(ref self).C("rateOverTime");
            E(ref self).Set("rateOverTime", new MMCurve { State = c.State, Scalar = value, MinScalar = c.State == 0 ? value : c.MinScalar, Max = c.Max, Min = c.Min });
        }
        public static void get_rateOverDistance_Injected(ref ParticleSystem.EmissionModule self, out ParticleSystem.MinMaxCurve ret) => ret = E(ref self).C("rateOverDistance").ToManaged();
        public static void set_rateOverDistance_Injected(ref ParticleSystem.EmissionModule self, ref ParticleSystem.MinMaxCurve value) => E(ref self).Set("rateOverDistance", MMCurve.FromManaged(value));
    }

    public static unsafe partial class UnityEngine_ParticleSystem__TextureSheetAnimationModule
    {
        static PMod U(ref ParticleSystem.TextureSheetAnimationModule m) => Native.Self<NParticleSystem>(m.m_ParticleSystem).Comp.M("UVModule");
        public static bool get_enabled_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => U(ref self).On;
        public static void set_enabled_Injected(ref ParticleSystem.TextureSheetAnimationModule self, bool value) => U(ref self).SetB("enabled", value);
        public static ParticleSystemAnimationMode get_mode_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => (ParticleSystemAnimationMode)(int)U(ref self).F("mode");
        public static void set_mode_Injected(ref ParticleSystem.TextureSheetAnimationModule self, ParticleSystemAnimationMode value) => U(ref self).SetF("mode", (int)value);
        public static int get_numTilesX_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => (int)U(ref self).F("tilesX", 1);
        public static void set_numTilesX_Injected(ref ParticleSystem.TextureSheetAnimationModule self, int value) => U(ref self).SetF("tilesX", value);
        public static int get_numTilesY_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => (int)U(ref self).F("tilesY", 1);
        public static void set_numTilesY_Injected(ref ParticleSystem.TextureSheetAnimationModule self, int value) => U(ref self).SetF("tilesY", value);
        public static ParticleSystemAnimationType get_animation_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => (ParticleSystemAnimationType)(int)U(ref self).F("animationType");
        public static void set_animation_Injected(ref ParticleSystem.TextureSheetAnimationModule self, ParticleSystemAnimationType value) => U(ref self).SetF("animationType", (int)value);
        public static void get_frameOverTime_Injected(ref ParticleSystem.TextureSheetAnimationModule self, out ParticleSystem.MinMaxCurve ret) => ret = U(ref self).C("frameOverTime").ToManaged();
        public static void set_frameOverTime_Injected(ref ParticleSystem.TextureSheetAnimationModule self, ref ParticleSystem.MinMaxCurve value) => U(ref self).Set("frameOverTime", MMCurve.FromManaged(value));
        public static int get_cycleCount_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => (int)U(ref self).F("cycles", 1);
        public static void set_cycleCount_Injected(ref ParticleSystem.TextureSheetAnimationModule self, int value) => U(ref self).SetF("cycles", value);
        public static int get_rowIndex_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => (int)U(ref self).F("rowIndex");
        public static void set_rowIndex_Injected(ref ParticleSystem.TextureSheetAnimationModule self, int value) => U(ref self).SetF("rowIndex", value);
        public static int get_spriteCount_Injected(ref ParticleSystem.TextureSheetAnimationModule self) => U(ref self).L("sprites").Count;
    }

    public static unsafe partial class UnityEngine_ParticleSystem__TrailModule
    {
        static PMod T(ref ParticleSystem.TrailModule m) => Native.Self<NParticleSystem>(m.m_ParticleSystem).Comp.M("TrailModule");
        public static bool get_enabled_Injected(ref ParticleSystem.TrailModule self) => T(ref self).On;
        public static void set_enabled_Injected(ref ParticleSystem.TrailModule self, bool value) => T(ref self).SetB("enabled", value);
    }
}
