// PlayerLoop：Unity 每格的更新順序（UnityEngine.LowLevel.PlayerLoop）。
//
// 引擎自己的每格流程寫死在 Engine.Frame，原生的子系統（ScriptRunBehaviourUpdate…）不會照這份清單一個一個叫；
// 這份清單只是讓 managed 端看得到、改得了。遊戲或套件用 SetPlayerLoop 插進來的 managed 子系統（有 updateDelegate、沒有 updateFunction，
// 例如 UniTask 的 PlayerLoopHelper 在每一段頭尾各插一對 runner），引擎照它在那一段的位置叫：
//   - 在那一段第一個原生子系統之前的 → 那一段開頭（Engine.Frame 的對應位置）
//   - 其他（最後一個原生子系統之後，或夾在原生子系統之間）→ 那一段結尾
// 預設的清單是 UnityEngine.PlayerLoop 底下各段的巢狀型別（反射照中繼資料的順序，即原始碼宣告的順序），updateFunction 給非 0 的假指標表示原生。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;

namespace UnityNative
{
    public static class PlayerLoops
    {
        /// <summary>引擎叫 managed 子系統的時機：Unity 2022.3 預設迴圈的八段</summary>
        public enum Phase { TimeUpdate, Initialization, EarlyUpdate, FixedUpdate, PreUpdate, Update, PreLateUpdate, PostLateUpdate }

        static readonly Type[] PhaseTypes =
        {
            typeof(UnityEngine.PlayerLoop.TimeUpdate), typeof(UnityEngine.PlayerLoop.Initialization), typeof(UnityEngine.PlayerLoop.EarlyUpdate),
            typeof(UnityEngine.PlayerLoop.FixedUpdate), typeof(UnityEngine.PlayerLoop.PreUpdate), typeof(UnityEngine.PlayerLoop.Update),
            typeof(UnityEngine.PlayerLoop.PreLateUpdate), typeof(UnityEngine.PlayerLoop.PostLateUpdate),
        };

        static readonly IntPtr NativeFn = (IntPtr)1;
        static PlayerLoopSystemInternal[] current;
        static readonly List<PlayerLoopSystem.UpdateFunction>[] head = NewLists(), tail = NewLists();

        static List<PlayerLoopSystem.UpdateFunction>[] NewLists()
        {
            var a = new List<PlayerLoopSystem.UpdateFunction>[8];
            for (int i = 0; i < a.Length; i++) a[i] = new List<PlayerLoopSystem.UpdateFunction>();
            return a;
        }

        public static PlayerLoopSystemInternal[] Default()
        {
            var list = new List<PlayerLoopSystemInternal> { default };
            int total = 0;
            foreach (var pt in PhaseTypes)
            {
                var subs = pt.GetNestedTypes();
                list.Add(new PlayerLoopSystemInternal { type = pt, numSubSystems = subs.Length });
                foreach (var s in subs) list.Add(new PlayerLoopSystemInternal { type = s, updateFunction = NativeFn });
                total += 1 + subs.Length;
            }
            list[0] = new PlayerLoopSystemInternal { numSubSystems = total };
            return list.ToArray();
        }

        public static PlayerLoopSystemInternal[] Current => current ??= Default();

        public static void Set(PlayerLoopSystemInternal[] loop)
        {
            current = loop;
            foreach (var l in head) l.Clear();
            foreach (var l in tail) l.Clear();
            if (loop == null || loop.Length == 0) return;
            // 根的直接子系統是各段；段的直接子系統裡找 managed 的
            int i = 1;
            while (i < loop.Length)
            {
                var seg = loop[i];
                int end = i + seg.numSubSystems;
                int phase = Array.IndexOf(PhaseTypes, seg.type);
                bool seenNative = false;
                int j = i + 1;
                while (j <= end && j < loop.Length)
                {
                    var s = loop[j];
                    if (s.updateDelegate != null && s.updateFunction == IntPtr.Zero)
                    {
                        if (phase < 0) Engine.Log(LogType.Warning, "[PlayerLoop] 不認得的段 " + seg.type + "，" + s.type + " 不會被叫");
                        else (seenNative ? tail : head)[phase].Add(s.updateDelegate);
                    }
                    else if (s.updateFunction != IntPtr.Zero) seenNative = true;
                    j += 1 + s.numSubSystems;
                }
                i = end + 1;
            }
        }

        public static void Run(Phase phase, bool atEnd)
        {
            var list = (atEnd ? tail : head)[(int)phase];
            for (int i = 0; i < list.Count; i++)
            {
                try { list[i](); }
                catch (Exception e) { Engine.LogException(e); }
            }
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_LowLevel_PlayerLoop
    {
        public static PlayerLoopSystemInternal[] GetDefaultPlayerLoopInternal() => PlayerLoops.Default();
        public static PlayerLoopSystemInternal[] GetCurrentPlayerLoopInternal() => (PlayerLoopSystemInternal[])PlayerLoops.Current.Clone();
        public static void SetPlayerLoopInternal(PlayerLoopSystemInternal[] loop) => PlayerLoops.Set(loop);
    }
}
