// IMGUI 的事件（UnityEngine.Event）與 OnGUI。
// Unity 每格在 LateUpdate 之後把輸入事件一個一個設成 Event.current、對啟用中有 OnGUI 的腳本各叫一次（另外還有 Layout／Repaint，畫 GUI 用）。
// 這裡只送滾輪（EventType.ScrollWheel）：遊戲讀滾輪的地方有的只在 OnGUI 讀 Event.current（MT2 的 CoreInputDriverMouse），
// Layout／Repaint 沒送：還沒有 IMGUI 的繪製，送了只會讓畫 GUI 的腳本跑一堆替身。
// Event 的狀態在原生端（m_Ptr）：這裡用一張表存，欄位照 Unity 的 Event（type、delta、mousePosition…）。
// 一格滾輪的 delta：Event.delta.y ＝ −滾輪格數 × 3（往下為正；跟 Input.mouseScrollDelta 相反）。一格 3 跟 TS 版 main.ts 的 WHEEL_NOTCH 一樣，
// Unity 在桌面一格給多少未查證。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public static class ImGui
    {
        public const float WheelNotch = 3f;
        static Event wheel;

        public static void Register() => Engine.PostLateUpdate.Add(Dispatch);

        static void Dispatch()
        {
            var s = InputState.Scroll;
            if (s == Vector2.zero) return;
            wheel ??= new Event();
            wheel.type = EventType.ScrollWheel;
            wheel.delta = new Vector2(s.x * WheelNotch, -s.y * WheelNotch);
            wheel.mousePosition = new Vector2(InputState.MousePosition.x, Screen.height - InputState.MousePosition.y); // GUI 座標：左上原點
            Event.current = wheel;
            Lifecycle.OnGUI();
            wheel.type = EventType.Used;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Event
    {
        sealed class State
        {
            public EventType Type = EventType.Ignore;
            public PointerType PointerType;
            public int Button, ClickCount, DisplayIndex;
            public EventModifiers Modifiers;
            public float Pressure, Twist;
            public PenStatus PenStatus;
            public char Character;
            public KeyCode KeyCode;
            public string CommandName = "";
            public Vector2 MousePosition, Delta, Tilt;
            public State Copy() => (State)MemberwiseClone();
        }

        static readonly Dictionary<IntPtr, State> states = new Dictionary<IntPtr, State>();
        static long next = 1;
        static State S(Event e) => e == null ? new State() : states.TryGetValue(e.m_Ptr, out var s) ? s : (states[e.m_Ptr] = new State());

        public static IntPtr Internal_Create(int displayIndex) { var p = (IntPtr)next++; states[p] = new State { DisplayIndex = displayIndex }; return p; }
        public static void Internal_Destroy(IntPtr ptr) => states.Remove(ptr);
        public static IntPtr Internal_Copy(IntPtr ptr) { var p = (IntPtr)next++; states[p] = states.TryGetValue(ptr, out var s) ? s.Copy() : new State(); return p; }
        public static void CopyFromPtr(Event self, IntPtr ptr) { if (states.TryGetValue(ptr, out var s)) states[self.m_Ptr] = s.Copy(); }
        public static void Internal_SetNativeEvent(IntPtr ptr) { }
        public static void Internal_Use(Event self) => S(self).Type = EventType.Used;
        public static EventType GetTypeForControl(Event self, int controlID) => S(self).Type;
        public static bool PopEvent(Event outEvent) => false;
        public static void QueueEvent(Event outEvent) { }
        public static int GetEventCount() => 0;
        public static void ClearEvents() { }
        public static int GetDoubleClickTime() => 500;

        public static EventType get_rawType(Event self) => S(self).Type;
        public static EventType get_type(Event self) => S(self).Type;
        public static void set_type(Event self, EventType value) => S(self).Type = value;
        public static PointerType get_pointerType(Event self) => S(self).PointerType;
        public static void set_pointerType(Event self, PointerType value) => S(self).PointerType = value;
        public static int get_button(Event self) => S(self).Button;
        public static void set_button(Event self, int value) => S(self).Button = value;
        public static EventModifiers get_modifiers(Event self) => S(self).Modifiers;
        public static void set_modifiers(Event self, EventModifiers value) => S(self).Modifiers = value;
        public static float get_pressure(Event self) => S(self).Pressure;
        public static void set_pressure(Event self, float value) => S(self).Pressure = value;
        public static float get_twist(Event self) => S(self).Twist;
        public static void set_twist(Event self, float value) => S(self).Twist = value;
        public static PenStatus get_penStatus(Event self) => S(self).PenStatus;
        public static void set_penStatus(Event self, PenStatus value) => S(self).PenStatus = value;
        public static int get_clickCount(Event self) => S(self).ClickCount;
        public static void set_clickCount(Event self, int value) => S(self).ClickCount = value;
        public static char get_character(Event self) => S(self).Character;
        public static void set_character(Event self, char value) => S(self).Character = value;
        public static KeyCode get_keyCode(Event self) => S(self).KeyCode;
        public static void set_keyCode(Event self, KeyCode value) => S(self).KeyCode = value;
        public static int get_displayIndex(Event self) => S(self).DisplayIndex;
        public static void set_displayIndex(Event self, int value) => S(self).DisplayIndex = value;
        public static string get_commandName(Event self) => S(self).CommandName;
        public static void set_commandName(Event self, string value) => S(self).CommandName = value ?? "";
        public static void get_mousePosition_Injected(Event self, out Vector2 ret) => ret = S(self).MousePosition;
        public static void set_mousePosition_Injected(Event self, ref Vector2 value) => S(self).MousePosition = value;
        public static void get_delta_Injected(Event self, out Vector2 ret) => ret = S(self).Delta;
        public static void set_delta_Injected(Event self, ref Vector2 value) => S(self).Delta = value;
        public static void get_tilt_Injected(Event self, out Vector2 ret) => ret = S(self).Tilt;
        public static void set_tilt_Injected(Event self, ref Vector2 value) => S(self).Tilt = value;
    }
}
