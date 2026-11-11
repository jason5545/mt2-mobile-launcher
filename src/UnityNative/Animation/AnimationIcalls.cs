// Animator、AnimationClip、RuntimeAnimatorController、Motion 的 internal call。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityNative;

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Animator
    {
        static NAnimator A(Animator self) => Native.Self<NAnimator>(self);

        /// <summary>還沒初始化（沒啟用過、沒有控制器）的 Animator：Unity 會警告「Animator is not playing an AnimatorController」並略過</summary>
        static AnimatorRuntime R(Animator self)
        {
            var a = A(self);
            if (a.Rt == null && a.Controller != null) a.Rebind();
            return a.Rt;
        }

        public static int StringToHash(string name) => (int)Crc32.Of(name ?? "");

        public static RuntimeAnimatorController get_runtimeAnimatorController(Animator self) => A(self).Controller;
        public static void set_runtimeAnimatorController(Animator self, RuntimeAnimatorController value)
        {
            var a = A(self);
            if (ReferenceEquals(a.Controller, value)) return;
            a.Controller = value;
            a.Rt = null;
            if (a.Enabled && a.Go != null && a.Go.ActiveInHierarchy) a.Rebind();
        }

        public static bool get_isInitialized(Animator self) => A(self).Rt != null;
        public static bool get_hasBoundPlayables(Animator self) => false;
        public static bool get_hasTransformHierarchy(Animator self) => true;
        public static bool get_isHuman(Animator self) => false;
        public static bool get_isOptimizable(Animator self) => false;
        public static bool get_hasRootMotion(Animator self) => false;
        public static float get_humanScale(Animator self) => 1;
        public static Avatar get_avatar(Animator self) => null;
        public static void set_avatar(Animator self, Avatar value) { }

        public static float get_speed(Animator self) => A(self).Speed;
        public static void set_speed(Animator self, float value) => A(self).Speed = value;
        public static AnimatorUpdateMode get_updateMode(Animator self) => A(self).UpdateMode;
        public static void set_updateMode(Animator self, AnimatorUpdateMode value) => A(self).UpdateMode = value;
        public static AnimatorCullingMode get_cullingMode(Animator self) => A(self).CullingMode;
        public static void set_cullingMode(Animator self, AnimatorCullingMode value) => A(self).CullingMode = value;
        public static bool get_applyRootMotion(Animator self) => A(self).ApplyRootMotion;
        public static void set_applyRootMotion(Animator self, bool value) => A(self).ApplyRootMotion = value;
        public static bool get_keepAnimatorStateOnDisable(Animator self) => A(self).KeepStateOnDisable;
        public static void set_keepAnimatorStateOnDisable(Animator self, bool value) => A(self).KeepStateOnDisable = value;
        public static bool get_writeDefaultValuesOnDisable(Animator self) => A(self).WriteDefaultsOnDisable;
        public static void set_writeDefaultValuesOnDisable(Animator self, bool value) => A(self).WriteDefaultsOnDisable = value;
        public static bool get_fireEvents(Animator self) => A(self).FireEvents;
        public static void set_fireEvents(Animator self, bool value) => A(self).FireEvents = value;
        public static bool get_logWarnings(Animator self) => A(self).LogWarnings;
        public static void set_logWarnings(Animator self, bool value) => A(self).LogWarnings = value;
        public static void OnUpdateModeChanged(Animator self) { }
        public static void OnCullingModeChanged(Animator self) { }

        public static void SetFloatString(Animator self, string name, float value) => R(self)?.SetFloat(name, value);
        public static void SetFloatID(Animator self, int id, float value) => R(self)?.SetFloat(id, value);
        public static float GetFloatString(Animator self, string name) => R(self)?.GetFloat(name) ?? 0;
        public static float GetFloatID(Animator self, int id) => R(self)?.GetFloat(id) ?? 0;
        /// <summary>SetFloat 的阻尼版：照 Unity 的 SmoothDamp 往目標靠（dampTime 是到達的大約時間）</summary>
        public static void SetFloatStringDamp(Animator self, string name, float value, float dampTime, float deltaTime)
        {
            var r = R(self);
            if (r == null) return;
            r.SetFloat(name, Damp(r.GetFloat(name), value, dampTime, deltaTime));
        }
        public static void SetFloatIDDamp(Animator self, int id, float value, float dampTime, float deltaTime)
        {
            var r = R(self);
            if (r == null) return;
            r.SetFloat(id, Damp(r.GetFloat(id), value, dampTime, deltaTime));
        }
        static float Damp(float cur, float target, float time, float dt)
        {
            if (time <= 0 || dt <= 0) return target;
            float v = 0;
            return Mathf.SmoothDamp(cur, target, ref v, time, float.PositiveInfinity, dt);
        }
        public static void SetBoolString(Animator self, string name, bool value) => R(self)?.SetBool(name, value);
        public static void SetBoolID(Animator self, int id, bool value) => R(self)?.SetBool(id, value);
        public static bool GetBoolString(Animator self, string name) => R(self)?.GetBool(name) ?? false;
        public static bool GetBoolID(Animator self, int id) => R(self)?.GetBool(id) ?? false;
        public static void SetIntegerString(Animator self, string name, int value) => R(self)?.SetInt(name, value);
        public static void SetIntegerID(Animator self, int id, int value) => R(self)?.SetInt(id, value);
        public static int GetIntegerString(Animator self, string name) => R(self)?.GetInt(name) ?? 0;
        public static int GetIntegerID(Animator self, int id) => R(self)?.GetInt(id) ?? 0;
        public static void SetTriggerString(Animator self, string name) => R(self)?.SetTrigger(name, true);
        public static void SetTriggerID(Animator self, int id) => R(self)?.SetTrigger(id, true);
        public static void ResetTriggerString(Animator self, string name) => R(self)?.SetTrigger(name, false);
        public static void ResetTriggerID(Animator self, int id) => R(self)?.SetTrigger(id, false);
        public static bool IsParameterControlledByCurveString(Animator self, string name) => false;
        public static bool IsParameterControlledByCurveID(Animator self, int id) => false;

        public static AnimatorControllerParameter[] get_parameters(Animator self)
        {
            var c = Native.Of<NAnimatorController>(A(self).Controller)?.Root;
            if (c == null) return Array.Empty<AnimatorControllerParameter>();
            var r = new AnimatorControllerParameter[c.Params.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var p = c.Params[i];
                r[i] = new AnimatorControllerParameter { m_Name = p.Name, m_Type = p.Type, m_DefaultFloat = p.DefaultFloat, m_DefaultInt = p.DefaultInt, m_DefaultBool = p.DefaultBool };
            }
            return r;
        }
        public static int get_parameterCount(Animator self) => Native.Of<NAnimatorController>(A(self).Controller)?.Root.Params.Count ?? 0;
        public static AnimatorControllerParameter GetParameterInternal(Animator self, int index) => get_parameters(self)[index];

        public static int get_layerCount(Animator self) => R(self)?.LayerCount ?? 0;
        public static string GetLayerName(Animator self, int layer) => R(self)?.LayerName(layer) ?? "";
        public static int GetLayerIndex(Animator self, string name) => R(self)?.LayerIndex(name) ?? -1;
        public static float GetLayerWeight(Animator self, int layer) => R(self)?.LayerWeight(layer) ?? 0;
        public static void SetLayerWeight(Animator self, int layer, float weight) => R(self)?.SetLayerWeight(layer, weight);

        public static void GetAnimatorStateInfo(Animator self, int layer, StateInfoIndex index, out AnimatorStateInfo info)
            => info = R(self)?.StateInfo(layer, index == StateInfoIndex.NextState) ?? default;
        public static void GetAnimatorTransitionInfo(Animator self, int layer, out AnimatorTransitionInfo info) => info = R(self)?.TransitionInfo(layer) ?? default;
        public static bool IsInTransition(Animator self, int layer) => R(self)?.IsInTransition(layer) ?? false;
        public static AnimatorClipInfo[] GetCurrentAnimatorClipInfo(Animator self, int layer) => R(self)?.ClipInfo(layer, false) ?? Array.Empty<AnimatorClipInfo>();
        public static AnimatorClipInfo[] GetNextAnimatorClipInfo(Animator self, int layer) => R(self)?.ClipInfo(layer, true) ?? Array.Empty<AnimatorClipInfo>();
        public static int GetAnimatorClipInfoCount(Animator self, int layer, bool current) => (R(self)?.ClipInfo(layer, !current) ?? Array.Empty<AnimatorClipInfo>()).Length;
        public static void GetAnimatorClipInfoInternal(Animator self, int layer, bool isCurrent, object clips)
        {
            if (clips is not List<AnimatorClipInfo> list) return;
            list.Clear();
            list.AddRange(R(self)?.ClipInfo(layer, !isCurrent) ?? Array.Empty<AnimatorClipInfo>());
        }
        public static string GetAnimatorStateName(Animator self, int layer, bool current) => R(self)?.StateName(layer, !current) ?? "";
        public static string ResolveHash(Animator self, int hash) => "";
        public static bool HasState(Animator self, int layer, int stateID) => R(self)?.FindState(layer, stateID) != null;

        public static void Play(Animator self, int stateNameHash, int layer, float normalizedTime) => R(self)?.Play(stateNameHash, layer, normalizedTime, false);
        public static void PlayInFixedTime(Animator self, int stateNameHash, int layer, float fixedTime) => R(self)?.Play(stateNameHash, layer, fixedTime, true);
        public static void CrossFade(Animator self, int stateHashName, float normalizedTransitionDuration, int layer, float normalizedTimeOffset, float normalizedTransitionTime)
            => R(self)?.CrossFade(stateHashName, layer, normalizedTransitionDuration, normalizedTimeOffset, false);
        public static void CrossFadeInFixedTime(Animator self, int stateHashName, float fixedTransitionDuration, int layer, float fixedTimeOffset, float normalizedTransitionTime)
            => R(self)?.CrossFade(stateHashName, layer, fixedTransitionDuration, fixedTimeOffset, true);

        public static void Update(Animator self, float deltaTime) => R(self)?.Step(deltaTime);
        public static void Rebind(Animator self, bool writeDefaultValues)
        {
            var a = A(self);
            if (writeDefaultValues) a.Rt?.WriteDefaults();
            a.Rebind();
        }
        public static void WriteDefaultValues(Animator self) => R(self)?.WriteDefaults();
        public static void WriteDefaultPose(Animator self) => R(self)?.WriteDefaults();
        public static void EvaluateController(Animator self, float deltaTime) => R(self)?.Step(deltaTime);
        public static void ApplyBuiltinRootMotion(Animator self) { }
        public static void ClearInternalControllerPlayable(Animator self) { }
        public static string GetStats(Animator self) => "";
        public static ScriptableObject GetBehaviour(Animator self, Type type) => null;
        public static ScriptableObject[] InternalGetBehaviours(Animator self, Type type) => Array.Empty<ScriptableObject>();
        public static ScriptableObject[] InternalGetBehavioursByKey(Animator self, int fullPathHash, int layerIndex, Type type) => Array.Empty<ScriptableObject>();
    }

    public static unsafe partial class UnityEngine_AnimatorClipInfo
    {
        public static AnimationClip InstanceIDToAnimationClipPPtr(int instanceID) => Native.ById(instanceID)?.Managed as AnimationClip;
    }

    public static unsafe partial class UnityEngine_RuntimeAnimatorController
    {
        public static AnimationClip[] get_animationClips(RuntimeAnimatorController self)
        {
            var c = Native.Self<NAnimatorController>(self);
            var list = new List<AnimationClip>();
            foreach (var l in c.Root.Layers)
                foreach (var s in l.States)
                    if (c.ClipOf(s) is AnimationClip clip && !list.Contains(clip)) list.Add(clip);
            return list.ToArray();
        }
    }

    public static unsafe partial class UnityEngine_AnimationClip
    {
        static NAnimationClip C(AnimationClip self) => Native.Self<NAnimationClip>(self);

        public static void Internal_CreateAnimationClip(AnimationClip self) => Native.Bind(new NAnimationClip(), self);
        public static float get_length(AnimationClip self) => C(self).Length;
        public static float get_startTime(AnimationClip self) => C(self).StartTime;
        public static float get_stopTime(AnimationClip self) => C(self).StopTime;
        public static float get_frameRate(AnimationClip self) => C(self).SampleRate;
        public static void set_frameRate(AnimationClip self, float value) => C(self).SampleRate = value;
        public static WrapMode get_wrapMode(AnimationClip self) => C(self).WrapMode;
        public static void set_wrapMode(AnimationClip self, WrapMode value) => C(self).WrapMode = value;
        public static bool get_legacy(AnimationClip self) => C(self).Legacy;
        public static void set_legacy(AnimationClip self, bool value) => C(self).Legacy = value;
        public static bool get_humanMotion(AnimationClip self) => false;
        public static bool get_empty(AnimationClip self) => C(self).Bindings.Count == 0;
        public static bool get_hasGenericRootTransform(AnimationClip self) => false;
        public static bool get_hasMotionFloatCurves(AnimationClip self) => false;
        public static bool get_hasMotionCurves(AnimationClip self) => false;
        public static bool get_hasRootCurves(AnimationClip self) => false;
        public static bool get_hasRootMotion(AnimationClip self) => false;
        public static void EnsureQuaternionContinuity(AnimationClip self) { }
        public static void ClearCurves(AnimationClip self) => C(self).Bindings.Clear();

        public static Array GetEventsInternal(AnimationClip self)
        {
            var c = C(self);
            var r = new AnimationEvent[c.Events.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var e = c.Events[i];
                r[i] = new AnimationEvent
                {
                    m_Time = e.m_Time, m_FunctionName = e.m_FunctionName, m_StringParameter = e.m_StringParameter,
                    m_ObjectReferenceParameter = e.m_ObjectReferenceParameter, m_FloatParameter = e.m_FloatParameter,
                    m_IntParameter = e.m_IntParameter, m_MessageOptions = e.m_MessageOptions,
                };
            }
            return r;
        }
        public static void SetEventsInternal(AnimationClip self, Array events)
        {
            var c = C(self);
            c.Events.Clear();
            if (events != null) foreach (var o in events) if (o is AnimationEvent e) c.Events.Add(e);
            c.Events.Sort((a, b) => a.m_Time.CompareTo(b.m_Time));
        }
        public static void AddEventInternal(AnimationClip self, object evt)
        {
            if (evt is not AnimationEvent e) return;
            var c = C(self);
            c.Events.Add(e);
            c.Events.Sort((a, b) => a.m_Time.CompareTo(b.m_Time));
        }
    }

    public static unsafe partial class UnityEngine_Motion
    {
        public static bool get_isLooping(Motion self) => Native.Of<NAnimationClip>(self)?.Loop ?? false;
        public static bool get_legacy(Motion self) => Native.Of<NAnimationClip>(self)?.Legacy ?? false;
        public static bool get_isHumanMotion(Motion self) => false;
        public static float get_averageDuration(Motion self) => Native.Of<NAnimationClip>(self)?.Length ?? 0;
    }
}
