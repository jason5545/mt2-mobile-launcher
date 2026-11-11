// 舊版 Input（InputLegacyModule）的原生半邊。宿主把滑鼠、按鍵、滾輪、觸控事件丟進來（螢幕座標，左下原點），
// 每格開頭 BeginFrame 把「這一格按下／放開」整理好。手機的觸控照 TS 版照滑鼠送（MT2 原版就支援滑鼠點擊的觸控操作）。
// 虛擬軸（Horizontal、Submit、Mouse ScrollWheel…）照 project.json 的 InputManager。
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public static class InputState
    {
        static readonly object gate = new object();
        // 宿主丟進來的事件（可能在別的執行緒）
        static readonly List<(int kind, int a, float x, float y)> pending = new List<(int, int, float, float)>();
        static readonly HashSet<KeyCode> held = new HashSet<KeyCode>(), down = new HashSet<KeyCode>(), up = new HashSet<KeyCode>();
        public static Vector3 MousePosition;
        public static Vector2 Scroll;
        static Vector2 scrollAccum;
        public static readonly StringBuilder Typed = new StringBuilder();
        static string typedFrame = "";
        public static string InputString => typedFrame;
        public static Vector2 MouseDelta;
        static Vector3 lastMouse;

        // 事件種類
        const int MouseMove = 0, MouseDown = 1, MouseUp = 2, KeyDown = 3, KeyUp = 4, ScrollEv = 5, Char = 6;

        public static void PushMouse(float x, float y) { lock (gate) pending.Add((MouseMove, 0, x, y)); }
        public static void PushButton(int button, bool isDown, float x, float y) { lock (gate) { pending.Add((MouseMove, 0, x, y)); pending.Add((isDown ? MouseDown : MouseUp, button, x, y)); } }
        public static void PushKey(KeyCode k, bool isDown) { lock (gate) pending.Add((isDown ? KeyDown : KeyUp, (int)k, 0, 0)); }
        public static void PushScroll(float dx, float dy) { lock (gate) pending.Add((ScrollEv, 0, dx, dy)); }
        public static void PushChar(char c) { lock (gate) pending.Add((Char, c, 0, 0)); }

        public static void BeginFrame()
        {
            down.Clear(); up.Clear();
            Typed.Clear();
            scrollAccum = default;
            List<(int kind, int a, float x, float y)> evs;
            lock (gate) { evs = new List<(int, int, float, float)>(pending); pending.Clear(); }
            foreach (var (kind, a, x, y) in evs)
            {
                switch (kind)
                {
                    // 宿主給視窗座標：扣掉黑邊（ScreenFit）
                    case MouseMove: MousePosition = new Vector3(ScreenFit.MapX(x), ScreenFit.MapY(y), 0); break;
                    case MouseDown: { var k = KeyCode.Mouse0 + a; if (held.Add(k)) down.Add(k); break; }
                    case MouseUp: { var k = KeyCode.Mouse0 + a; if (held.Remove(k)) up.Add(k); break; }
                    case KeyDown: { var k = (KeyCode)a; if (held.Add(k)) down.Add(k); break; }
                    case KeyUp: { var k = (KeyCode)a; if (held.Remove(k)) up.Add(k); break; }
                    case ScrollEv: scrollAccum += new Vector2(x, y); break;
                    case Char: Typed.Append((char)a); break;
                }
            }
            Scroll = scrollAccum;
            typedFrame = Typed.ToString();
            MouseDelta = new Vector2(MousePosition.x - lastMouse.x, MousePosition.y - lastMouse.y);
            lastMouse = MousePosition;
        }

        public static bool Held(KeyCode k) => held.Contains(k);
        public static bool Down(KeyCode k) => down.Contains(k);
        public static bool Up(KeyCode k) => up.Contains(k);
        public static bool AnyHeld => held.Count > 0;
        public static bool AnyDown => down.Count > 0;

        public static void Register() => Engine.EarlyUpdate.Add(BeginFrame);

        // ───────── 虛擬軸 ─────────

        sealed class Axis
        {
            public string Name, Neg, Pos, AltNeg, AltPos;
            public float Gravity, Dead, Sensitivity;
            public bool Snap, Invert;
            public int Type, AxisIndex;
            public float Value;
        }
        static Dictionary<string, List<Axis>> axes;

        static Dictionary<string, List<Axis>> Axes
        {
            get
            {
                if (axes != null) return axes;
                axes = new Dictionary<string, List<Axis>>(StringComparer.Ordinal);
                var list = Assets.Index("project")["InputManager"]?["InputManager"]?["m_Axes"] as JsonArray;
                foreach (var a in list ?? new JsonArray())
                {
                    var ax = new Axis
                    {
                        Name = a?["m_Name"]?.ToString() ?? "",
                        Neg = a?["negativeButton"]?.ToString(), Pos = a?["positiveButton"]?.ToString(),
                        AltNeg = a?["altNegativeButton"]?.ToString(), AltPos = a?["altPositiveButton"]?.ToString(),
                        Gravity = (float)Serializer.D(a?["gravity"]), Dead = (float)Serializer.D(a?["dead"]), Sensitivity = (float)Serializer.D(a?["sensitivity"]),
                        Snap = Serializer.D(a?["snap"]) != 0, Invert = Serializer.D(a?["invert"]) != 0,
                        Type = (int)Serializer.D(a?["type"]), AxisIndex = (int)Serializer.D(a?["axis"]),
                    };
                    if (!axes.TryGetValue(ax.Name, out var l)) axes[ax.Name] = l = new List<Axis>();
                    l.Add(ax);
                }
                return axes;
            }
        }

        public static bool HasAxis(string name) => Axes.ContainsKey(name ?? "");

        static float Raw(Axis a)
        {
            switch (a.Type)
            {
                case 0:
                    {
                        float v = 0;
                        if (KeyHeld(a.Pos) || KeyHeld(a.AltPos)) v += 1;
                        if (KeyHeld(a.Neg) || KeyHeld(a.AltNeg)) v -= 1;
                        return a.Invert ? -v : v;
                    }
                case 1:
                    {
                        float v = a.AxisIndex == 0 ? MouseDelta.x * 0.1f : a.AxisIndex == 1 ? MouseDelta.y * 0.1f : a.AxisIndex == 2 ? Scroll.y : 0;
                        v *= a.Sensitivity == 0 ? 1 : a.Sensitivity;
                        return a.Invert ? -v : v;
                    }
                default: return 0;
            }
        }

        public static float AxisRaw(string name)
        {
            if (!Axes.TryGetValue(name ?? "", out var l)) throw new ArgumentException("Input Axis " + name + " is not setup.\n To change the input settings use: Edit -> Settings -> Input");
            float best = 0;
            foreach (var a in l) { var v = Raw(a); if (Mathf.Abs(v) > Mathf.Abs(best)) best = v; }
            return best;
        }

        public static float AxisValue(string name) => AxisRaw(name);

        public static bool Button(string name, int which)
        {
            if (!Axes.TryGetValue(name ?? "", out var l)) throw new ArgumentException("Input Button " + name + " is not setup.\n To change the input settings use: Edit -> Settings -> Input");
            foreach (var a in l)
            {
                if (a.Type != 0) continue;
                foreach (var b in new[] { a.Pos, a.AltPos })
                {
                    var k = KeyOf(b);
                    if (k == KeyCode.None) continue;
                    if (which == 0 && Held(k)) return true;
                    if (which == 1 && Down(k)) return true;
                    if (which == 2 && Up(k)) return true;
                }
            }
            return false;
        }

        static bool KeyHeld(string name) { var k = KeyOf(name); return k != KeyCode.None && Held(k); }

        static readonly Dictionary<string, KeyCode> keyNames = BuildKeyNames();
        static Dictionary<string, KeyCode> BuildKeyNames()
        {
            var d = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase)
            {
                ["space"] = KeyCode.Space, ["return"] = KeyCode.Return, ["enter"] = KeyCode.KeypadEnter, ["escape"] = KeyCode.Escape, ["backspace"] = KeyCode.Backspace,
                ["tab"] = KeyCode.Tab, ["delete"] = KeyCode.Delete, ["up"] = KeyCode.UpArrow, ["down"] = KeyCode.DownArrow, ["left"] = KeyCode.LeftArrow, ["right"] = KeyCode.RightArrow,
                ["left shift"] = KeyCode.LeftShift, ["right shift"] = KeyCode.RightShift, ["left ctrl"] = KeyCode.LeftControl, ["right ctrl"] = KeyCode.RightControl,
                ["left alt"] = KeyCode.LeftAlt, ["right alt"] = KeyCode.RightAlt, ["mouse 0"] = KeyCode.Mouse0, ["mouse 1"] = KeyCode.Mouse1, ["mouse 2"] = KeyCode.Mouse2,
                ["page up"] = KeyCode.PageUp, ["page down"] = KeyCode.PageDown, ["home"] = KeyCode.Home, ["end"] = KeyCode.End,
            };
            for (char c = 'a'; c <= 'z'; c++) d[c.ToString()] = KeyCode.A + (c - 'a');
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = KeyCode.Alpha0 + (c - '0');
            for (int i = 1; i <= 12; i++) d["f" + i] = KeyCode.F1 + (i - 1);
            for (int i = 0; i <= 19; i++) d["joystick button " + i] = KeyCode.JoystickButton0 + i;
            return d;
        }

        public static KeyCode KeyOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return KeyCode.None;
            if (keyNames.TryGetValue(name, out var k)) return k;
            if (Enum.TryParse<KeyCode>(name, true, out k)) return k;
            return KeyCode.None;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Input
    {
        static bool simulateMouse = true, multiTouch = true;
        public static bool GetKeyInt(KeyCode key) => InputState.Held(key);
        public static bool GetKeyUpInt(KeyCode key) => InputState.Up(key);
        public static bool GetKeyDownInt(KeyCode key) => InputState.Down(key);
        public static bool GetMouseButton(int button) => InputState.Held(KeyCode.Mouse0 + button);
        public static bool GetMouseButtonDown(int button) => InputState.Down(KeyCode.Mouse0 + button);
        public static bool GetMouseButtonUp(int button) => InputState.Up(KeyCode.Mouse0 + button);
        public static void ResetInputAxes() { }
        public static string[] GetJoystickNames() => Array.Empty<string>();
        public static bool get_simulateMouseWithTouches() => simulateMouse;
        public static void set_simulateMouseWithTouches(bool value) => simulateMouse = value;
        public static bool get_anyKey() => InputState.AnyHeld;
        public static bool get_anyKeyDown() => InputState.AnyDown;
        public static string get_inputString() => InputState.InputString;
        public static bool get_mousePresent() => true;
        public static int get_touchCount() => 0;
        public static bool get_touchSupported() => false;
        public static bool get_touchPressureSupported() => false;
        public static bool get_multiTouchEnabled() => multiTouch;
        public static void set_multiTouchEnabled(bool value) => multiTouch = value;
        public static bool CheckDisabled() => false;
        public static void GetTouch_Injected(int index, out Touch ret) => ret = default;
        public static void get_mousePosition_Injected(out Vector3 ret) => ret = InputState.MousePosition;
        public static void get_mouseScrollDelta_Injected(out Vector2 ret) => ret = InputState.Scroll;
        public static void get_acceleration_Injected(out Vector3 ret) => ret = Vector3.zero;
        public static bool GetKeyString(string name) => InputState.Held(InputState.KeyOf(name));
        public static bool GetKeyUpString(string name) => InputState.Up(InputState.KeyOf(name));
        public static bool GetKeyDownString(string name) => InputState.Down(InputState.KeyOf(name));
        public static float GetAxis(string axisName) => InputState.AxisValue(axisName);
        public static float GetAxisRaw(string axisName) => InputState.AxisRaw(axisName);
        public static bool GetButton(string buttonName) => InputState.Button(buttonName, 0);
        public static bool GetButtonDown(string buttonName) => InputState.Button(buttonName, 1);
        public static bool GetButtonUp(string buttonName) => InputState.Button(buttonName, 2);
    }

    public static unsafe partial class UnityEngine_Internal_InputUnsafeUtility
    {
        static string S(byte* p, int n) => n <= 0 || p == null ? "" : Encoding.UTF8.GetString(p, n);
        public static bool GetKeyString(string name) => InputState.Held(InputState.KeyOf(name));
        public static bool GetKeyString__Unmanaged(byte* name, int nameLen) => GetKeyString(S(name, nameLen));
        public static bool GetKeyUpString(string name) => InputState.Up(InputState.KeyOf(name));
        public static bool GetKeyUpString__Unmanaged(byte* name, int nameLen) => GetKeyUpString(S(name, nameLen));
        public static bool GetKeyDownString(string name) => InputState.Down(InputState.KeyOf(name));
        public static bool GetKeyDownString__Unmanaged(byte* name, int nameLen) => GetKeyDownString(S(name, nameLen));
        public static float GetAxis(string axisName) => InputState.AxisValue(axisName);
        public static float GetAxis__Unmanaged(byte* axisName, int axisNameLen) => GetAxis(S(axisName, axisNameLen));
        public static float GetAxisRaw(string axisName) => InputState.AxisRaw(axisName);
        public static float GetAxisRaw__Unmanaged(byte* axisName, int axisNameLen) => GetAxisRaw(S(axisName, axisNameLen));
        public static bool GetButton(string buttonName) => InputState.Button(buttonName, 0);
        public static bool GetButton__Unmanaged(byte* buttonName, int buttonNameLen) => GetButton(S(buttonName, buttonNameLen));
        public static bool GetButtonDown(string buttonName) => InputState.Button(buttonName, 1);
        public static byte GetButtonDown__Unmanaged(byte* buttonName, int buttonNameLen) => (byte)(GetButtonDown(S(buttonName, buttonNameLen)) ? 1 : 0);
        public static bool GetButtonUp(string buttonName) => InputState.Button(buttonName, 2);
        public static bool GetButtonUp__Unmanaged(byte* buttonName, int buttonNameLen) => GetButtonUp(S(buttonName, buttonNameLen));
    }

    public static unsafe partial class UnityEngine_Cursor
    {
        static bool visible = true;
        static CursorLockMode lockState;
        public static bool get_visible() => visible;
        public static void set_visible(bool value) => visible = value;
        public static CursorLockMode get_lockState() => lockState;
        public static void set_lockState(CursorLockMode value) => lockState = value;
        public static void SetCursor_Injected(Texture2D texture, ref Vector2 hotspot, CursorMode cursorMode) { }
    }
}
