// 動畫素材的原生半邊：AnimationClip（曲線、事件、播放設定）與 AnimatorController（參數、layer、狀態、轉換）。
// 資料來源是匯入器的 assets/animations.json 與 animators.json（AssetRipper 讀出來的 YAML 原樣轉成 JSON）。
// 曲線求值共用 AnimationCurve 那套（Curves.Curve：Hermite，有權重的段用 Bézier；切線無限大是階梯）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    /// <summary>一條曲線綁定：路徑（相對 Animator 的 GameObject）、元件類別、屬性</summary>
    public sealed class ClipCurve
    {
        public enum Kinds { Position, Euler, Rotation, Scale, Float, PPtr }
        public Kinds Kind;
        public string Path = "";
        public int ClassID;
        public string Attribute;
        /// <summary>classID 114（腳本）的 m_Script：guid:fileID</summary>
        public string Script;
        /// <summary>Float 一條，Position／Euler／Scale 三條（x y z），Rotation 四條（x y z w）</summary>
        public Curves.Curve[] Curves;
        public (float time, string guid, string fileId)[] PPtrKeys;
    }

    public sealed class NAnimationClip : NObject
    {
        public float SampleRate = 60;
        public float StartTime, StopTime;
        public bool Loop;
        public float CycleOffset;
        public bool Legacy;
        public WrapMode WrapMode;
        public readonly List<ClipCurve> Bindings = new List<ClipCurve>();
        public readonly List<AnimationEvent> Events = new List<AnimationEvent>();
        public float Length => Math.Max(0, StopTime - StartTime);
        public AnimationClip Clip => (AnimationClip)Managed;
    }

    public sealed class AnimParam
    {
        public string Name;
        public int Hash;
        public AnimatorControllerParameterType Type;
        public float DefaultFloat;
        public int DefaultInt;
        public bool DefaultBool;
    }

    public sealed class AnimCondition
    {
        public int Mode; // 1 If、2 IfNot、3 Greater、4 Less、6 Equals、7 NotEqual
        public string Param;
        public float Threshold;
    }

    public sealed class AnimTransition
    {
        public AnimState Dst;
        /// <summary>目的地是 Exit（或狀態機）：回到入口重選</summary>
        public bool ToExit;
        public AnimCondition[] Conditions = Array.Empty<AnimCondition>();
        public float Duration, Offset, ExitTime;
        public bool HasExitTime, HasFixedDuration, Mute, Solo, CanTransitionToSelf;
        public int NameHash;
    }

    public sealed class AnimState
    {
        public string Name;
        public int NameHash, FullPathHash, TagHash;
        public float Speed = 1, CycleOffset;
        public bool WriteDefaults = true;
        public string SpeedParam, CycleOffsetParam, TimeParam;
        /// <summary>m_Motion 指到的 clip（guid）；BlendTree 是 null</summary>
        public string ClipGuid;
        public bool IsBlendTree;
        public readonly List<AnimTransition> Transitions = new List<AnimTransition>();
    }

    public sealed class AnimLayer
    {
        public string Name;
        public float DefaultWeight = 1;
        public int BlendingMode; // 0 Override、1 Additive
        public AnimState DefaultState;
        public readonly List<AnimState> States = new List<AnimState>();
        public readonly List<AnimTransition> AnyState = new List<AnimTransition>();
        public readonly List<AnimTransition> Entry = new List<AnimTransition>();
    }

    public sealed class NAnimatorController : NObject
    {
        public readonly List<AnimParam> Params = new List<AnimParam>();
        public readonly List<AnimLayer> Layers = new List<AnimLayer>();
        public readonly List<string> Notes = new List<string>();
        /// <summary>AnimatorOverrideController：底下的控制器與換掉的 clip（原 clip guid → 新 clip）</summary>
        public NAnimatorController Base;
        public readonly Dictionary<string, AnimationClip> Overrides = new Dictionary<string, AnimationClip>();

        public NAnimatorController Root => Base?.Root ?? this;

        public AnimationClip ClipOf(AnimState s)
        {
            if (s?.ClipGuid == null) return null;
            for (var c = this; c != null; c = c.Base)
                if (c.Overrides.TryGetValue(s.ClipGuid, out var o)) return o;
            return Assets.Load(s.ClipGuid, "7400000", typeof(AnimationClip)) as AnimationClip;
        }
    }

    public static class AnimationAssets
    {
        public static void Register()
        {
            Assets.Loaders["animation"] = (g, f, r, w) => LoadClip(g, r);
            Assets.Loaders["animator"] = (g, f, r, w) => LoadController(g, r);
        }

        public static float F(JsonNode n)
        {
            if (n == null) return 0;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<double>(out var d)) return (float)d;
                if (v.TryGetValue<string>(out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
                if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
            }
            return 0;
        }

        /// <summary>F 的 JsonElement 版（欄位不存在是 Undefined、JSON 的 null 是 Null，都回 0）</summary>
        public static float F(System.Text.Json.JsonElement n)
        {
            switch (n.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Number: return n.TryGetDouble(out var d) ? (float)d : 0;
                case System.Text.Json.JsonValueKind.String: return float.TryParse(n.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
                case System.Text.Json.JsonValueKind.True: return 1;
                default: return 0;
            }
        }

        static string S(JsonNode n) => n?.ToString();

        static readonly string[] Xyzw = { "x", "y", "z", "w" };

        static Curves.Curve Curve(JsonArray keys, string comp)
        {
            var c = new Curves.Curve();
            if (keys == null) return c;
            foreach (var kn in keys)
            {
                if (kn is not JsonObject k) continue;
                JsonNode P(string f) => comp == null ? k[f] : k[f]?[comp];
                var key = new Keyframe(F(k["time"]), F(P("value")), F(P("inSlope")), F(P("outSlope")), F(P("inWeight")), F(P("outWeight")))
                {
                    weightedMode = (WeightedMode)(int)F(k["weightedMode"]),
                };
                c.Keys.Add(key);
            }
            c.Keys.Sort((a, b) => a.time.CompareTo(b.time));
            return c;
        }

        static AnimationClip LoadClip(string guid, JsonObject r)
        {
            if (r == null) return null;
            var clip = (AnimationClip)Native.NewShell(typeof(AnimationClip));
            var n = Native.Bind(new NAnimationClip { Name = S(r["m_Name"]) ?? "", Source = guid }, clip, true);
            n.SampleRate = r["m_SampleRate"] != null ? F(r["m_SampleRate"]) : 60;
            n.Legacy = F(r["m_Legacy"]) != 0;
            n.WrapMode = (WrapMode)(int)F(r["m_WrapMode"]);
            var s = r["m_AnimationClipSettings"] as JsonObject;
            n.StartTime = F(s?["m_StartTime"]);
            n.StopTime = F(s?["m_StopTime"]);
            n.Loop = F(s?["m_LoopTime"]) != 0;
            n.CycleOffset = F(s?["m_CycleOffset"]);

            void Vec(string field, ClipCurve.Kinds kind, int dims)
            {
                foreach (var e in r[field] as JsonArray ?? new JsonArray())
                {
                    var keys = e?["curve"]?["m_Curve"] as JsonArray;
                    var cc = new ClipCurve { Kind = kind, Path = S(e?["path"]) ?? "", ClassID = 4, Curves = new Curves.Curve[dims] };
                    for (int i = 0; i < dims; i++) cc.Curves[i] = Curve(keys, Xyzw[i]);
                    n.Bindings.Add(cc);
                }
            }
            Vec("m_PositionCurves", ClipCurve.Kinds.Position, 3);
            Vec("m_EulerCurves", ClipCurve.Kinds.Euler, 3);
            Vec("m_RotationCurves", ClipCurve.Kinds.Rotation, 4);
            Vec("m_ScaleCurves", ClipCurve.Kinds.Scale, 3);

            static string ScriptKey(JsonNode sc) => sc is JsonObject o ? S(o["guid"]) + ":" + S(o["fileID"]) : null;
            foreach (var e in r["m_FloatCurves"] as JsonArray ?? new JsonArray())
                n.Bindings.Add(new ClipCurve
                {
                    Kind = ClipCurve.Kinds.Float,
                    Path = S(e?["path"]) ?? "",
                    ClassID = (int)F(e?["classID"]),
                    Attribute = S(e?["attribute"]),
                    Script = ScriptKey(e?["script"]),
                    Curves = new[] { Curve(e?["curve"]?["m_Curve"] as JsonArray, null) },
                });
            foreach (var e in r["m_PPtrCurves"] as JsonArray ?? new JsonArray())
            {
                var keys = new List<(float, string, string)>();
                foreach (var k in e?["curve"] as JsonArray ?? new JsonArray())
                    keys.Add((F(k?["time"]), S(k?["value"]?["guid"]), S(k?["value"]?["fileID"])));
                keys.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                n.Bindings.Add(new ClipCurve
                {
                    Kind = ClipCurve.Kinds.PPtr,
                    Path = S(e?["path"]) ?? "",
                    ClassID = (int)F(e?["classID"]),
                    Attribute = S(e?["attribute"]),
                    Script = ScriptKey(e?["script"]),
                    PPtrKeys = keys.ToArray(),
                });
            }
            foreach (var e in r["m_Events"] as JsonArray ?? new JsonArray())
            {
                if (e is not JsonObject o) continue;
                var ev = new AnimationEvent
                {
                    m_Time = F(o["time"]),
                    m_FunctionName = S(o["functionName"]) ?? "",
                    m_StringParameter = S(o["data"]) ?? "",
                    m_FloatParameter = F(o["floatParameter"]),
                    m_IntParameter = (int)F(o["intParameter"]),
                    m_MessageOptions = (int)F(o["messageOptions"]),
                };
                if (o["objectReferenceParameter"] is JsonObject or && S(or["guid"]) != null)
                    ev.m_ObjectReferenceParameter = Assets.Load(S(or["guid"]), S(or["fileID"]), typeof(UnityEngine.Object));
                n.Events.Add(ev);
            }
            n.Events.Sort((a, b) => a.m_Time.CompareTo(b.m_Time));
            return clip;
        }

        static RuntimeAnimatorController LoadController(string guid, JsonObject r)
        {
            if (r == null) return null;
            var objs = r["objects"] as JsonObject ?? new JsonObject();
            var rootId = S(r["root"]);
            var root = objs[rootId ?? ""] as JsonObject;
            if (S(r["type"]) == "AnimatorOverrideController")
            {
                var oc = (AnimatorOverrideController)Native.NewShell(typeof(AnimatorOverrideController));
                var on = Native.Bind(new NAnimatorController { Name = S(r["name"]) ?? "", Source = guid }, oc, true);
                var baseRef = root?["m_Controller"] as JsonObject;
                if (baseRef != null) on.Base = Native.Of<NAnimatorController>(Assets.Load(S(baseRef["guid"]), S(baseRef["fileID"]) ?? "9100000", typeof(RuntimeAnimatorController)));
                foreach (var c in root?["m_Clips"] as JsonArray ?? new JsonArray())
                {
                    var og = S(c?["m_OriginalClip"]?["guid"]);
                    var ov = c?["m_OverrideClip"] as JsonObject;
                    if (og != null && S(ov?["guid"]) != null) on.Overrides[og] = Assets.Load(S(ov["guid"]), S(ov["fileID"]) ?? "7400000", typeof(AnimationClip)) as AnimationClip;
                }
                return oc;
            }

            var ctl = (RuntimeAnimatorController)Native.NewShell(typeof(RuntimeAnimatorController));
            var n = Native.Bind(new NAnimatorController { Name = S(r["name"]) ?? "", Source = guid }, ctl, true);
            foreach (var p in root?["m_AnimatorParameters"] as JsonArray ?? new JsonArray())
                n.Params.Add(new AnimParam
                {
                    Name = S(p?["m_Name"]) ?? "",
                    Hash = (int)Crc32.Of(S(p?["m_Name"]) ?? ""),
                    Type = (AnimatorControllerParameterType)(int)F(p?["m_Type"]),
                    DefaultFloat = F(p?["m_DefaultFloat"]),
                    DefaultInt = (int)F(p?["m_DefaultInt"]),
                    DefaultBool = F(p?["m_DefaultBool"]) != 0,
                });

            var states = new Dictionary<string, AnimState>();
            AnimState StateOf(string id, string layerName)
            {
                if (id == null || id == "0") return null;
                if (states.TryGetValue(id, out var s)) return s;
                if (objs[id] is not JsonObject o || S(o["type"]) != "AnimatorState") return null;
                var name = S(o["m_Name"]) ?? "";
                s = new AnimState
                {
                    Name = name,
                    NameHash = (int)Crc32.Of(name),
                    FullPathHash = (int)Crc32.Of(layerName + "." + name),
                    TagHash = (int)Crc32.Of(S(o["m_Tag"]) ?? ""),
                    Speed = o["m_Speed"] != null ? F(o["m_Speed"]) : 1,
                    CycleOffset = F(o["m_CycleOffset"]),
                    WriteDefaults = o["m_WriteDefaultValues"] == null || F(o["m_WriteDefaultValues"]) != 0,
                    SpeedParam = F(o["m_SpeedParameterActive"]) != 0 ? S(o["m_SpeedParameter"]) : null,
                    CycleOffsetParam = F(o["m_CycleOffsetParameterActive"]) != 0 ? S(o["m_CycleOffsetParameter"]) : null,
                    TimeParam = F(o["m_TimeParameterActive"]) != 0 ? S(o["m_TimeParameter"]) : null,
                };
                states[id] = s;
                if (o["m_Motion"] is JsonObject m && S(m["guid"]) != null)
                {
                    if (S(m["fileID"]) == "7400000") s.ClipGuid = S(m["guid"]);
                    else { s.IsBlendTree = true; n.Notes.Add(name + "：BlendTree 還沒做"); }
                }
                else if (o["m_Motion"] is JsonObject lm && S(lm["fileID"]) is string fid && fid != "0" && objs[fid] is JsonObject bt && S(bt["type"]) == "BlendTree")
                {
                    s.IsBlendTree = true;
                    n.Notes.Add(name + "：BlendTree 還沒做");
                }
                foreach (var t in o["m_Transitions"] as JsonArray ?? new JsonArray())
                    if (Transition(S(t?["fileID"]), layerName) is AnimTransition tr) s.Transitions.Add(tr);
                return s;
            }

            AnimTransition Transition(string id, string layerName)
            {
                if (id == null || objs[id] is not JsonObject o) return null;
                var t = new AnimTransition
                {
                    Duration = F(o["m_TransitionDuration"]),
                    Offset = F(o["m_TransitionOffset"]),
                    ExitTime = F(o["m_ExitTime"]),
                    HasExitTime = F(o["m_HasExitTime"]) != 0,
                    HasFixedDuration = F(o["m_HasFixedDuration"]) != 0,
                    Mute = F(o["m_Mute"]) != 0,
                    Solo = F(o["m_Solo"]) != 0,
                    CanTransitionToSelf = F(o["m_CanTransitionToSelf"]) != 0,
                    NameHash = (int)Crc32.Of(S(o["m_Name"]) ?? ""),
                };
                var conds = new List<AnimCondition>();
                foreach (var c in o["m_Conditions"] as JsonArray ?? new JsonArray())
                    conds.Add(new AnimCondition { Mode = (int)F(c?["m_ConditionMode"]), Param = S(c?["m_ConditionEvent"]), Threshold = F(c?["m_EventTreshold"]) });
                t.Conditions = conds.ToArray();
                var dst = S(o["m_DstState"]?["fileID"]);
                if (F(o["m_IsExit"]) != 0 || dst == null || dst == "0")
                {
                    t.ToExit = true;
                    if (o["m_DstStateMachine"] is JsonObject dsm && S(dsm["fileID"]) is string f && f != "0") n.Notes.Add("轉到子狀態機還沒做");
                }
                else t.Dst = StateOf(dst, layerName);
                return t;
            }

            foreach (var l in root?["m_AnimatorLayers"] as JsonArray ?? new JsonArray())
            {
                var layer = new AnimLayer
                {
                    Name = S(l?["m_Name"]) ?? "",
                    DefaultWeight = n.Layers.Count == 0 ? 1 : F(l?["m_DefaultWeight"]),
                    BlendingMode = (int)F(l?["m_BlendingMode"]),
                };
                if (layer.BlendingMode != 0) n.Notes.Add(layer.Name + "：Additive layer 當成 Override");
                if (l?["m_SyncedLayerIndex"] != null && F(l["m_SyncedLayerIndex"]) >= 0) n.Notes.Add(layer.Name + "：Synced layer 還沒做");
                var smId = S(l?["m_StateMachine"]?["fileID"]);
                if (objs[smId ?? ""] is JsonObject sm)
                {
                    var smName = S(sm["m_Name"]) ?? layer.Name;
                    if ((sm["m_ChildStateMachines"] as JsonArray)?.Count > 0) n.Notes.Add(layer.Name + "：子狀態機還沒做");
                    foreach (var cs in sm["m_ChildStates"] as JsonArray ?? new JsonArray())
                        if (StateOf(S(cs?["m_State"]?["fileID"]), smName) is AnimState s && !layer.States.Contains(s)) layer.States.Add(s);
                    layer.DefaultState = StateOf(S(sm["m_DefaultState"]?["fileID"]), smName) ?? (layer.States.Count > 0 ? layer.States[0] : null);
                    foreach (var t in sm["m_AnyStateTransitions"] as JsonArray ?? new JsonArray())
                        if (Transition(S(t?["fileID"]), smName) is AnimTransition tr) layer.AnyState.Add(tr);
                    foreach (var t in sm["m_EntryTransitions"] as JsonArray ?? new JsonArray())
                        if (Transition(S(t?["fileID"]), smName) is AnimTransition tr) layer.Entry.Add(tr);
                }
                n.Layers.Add(layer);
            }
            foreach (var note in n.Notes) Assets.Warn("AnimatorController " + n.Name + "：" + note);
            return ctl;
        }
    }
}
