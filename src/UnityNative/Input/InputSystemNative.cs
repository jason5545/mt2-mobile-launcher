// 新版 Input System（com.unity.inputsystem）的原生半邊：UnityEngineInternal.Input.NativeInputSystem。
// 原版的原生層回報裝置（JSON 描述）、每格把輸入事件寫進一塊緩衝交給 managed 端（NotifyUpdate）。這裡照做：
//   - 裝置：滑鼠與鍵盤。描述的 type（deviceClass）就是 layout 名稱（"Mouse"、"Keyboard"）；沒有 matcher 對到時，
//     InputManager.TryFindMatchingControlLayout 會拿 deviceClass 當 layout（Unity.InputSystem 的 InputManager）。
//   - 每格（EarlyUpdate，在 InputState.BeginFrame 之後）：managed 端 QueueInputEvent 排進來的事件，加上滑鼠（MOUS）、鍵盤（KEYS）的 StateEvent，
//     照 Unity 的格式：InputEvent 檔頭 20 位元組（UnityEngine.InputModule 的 NativeInputEvent：type FourCC @0、sizeInBytes @4、deviceId @6、
//     time（double）@8、eventId @16），StateEvent 再加 4 位元組的狀態格式，
//     事件之間 4 位元組對齊（InputEvent.kAlignment）。
//   - 舊版 Input（InputState）是同一份宿主事件，兩套同時能用（Diceomancer 的主選單用新版 UI 模組、戰鬥場景與遊戲碼用舊版）。
// 觸控照舊轉成滑鼠送（宿主那邊），不另外回報 Touchscreen。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngineInternal.Input;

namespace UnityNative
{
    public static unsafe class InputSystemNative
    {
        const int Dynamic = 1; // NativeInputUpdateType.Dynamic
        const int HeaderSize = 20;

        static bool hasCallback, reported;
        static int nextDeviceId = 1, mouseId, keyboardId;
        static readonly List<byte[]> queued = new List<byte[]>();
        static byte[] buffer = new byte[4096];
        static bool updating;

        static int FourCC(char a, char b, char c, char d) => (a << 24) | (b << 16) | (c << 8) | d;
        static readonly int STAT = FourCC('S', 'T', 'A', 'T'), MOUS = FourCC('M', 'O', 'U', 'S'), KEYS = FourCC('K', 'E', 'Y', 'S');

        public static void Register() => Engine.EarlyUpdate.Add(() => RunUpdate(Dynamic));

        public static double Now => Engine.Time.Realtime - Engine.Time.RealtimeAtStart;

        public static void SetHasCallback(bool value) => hasCallback = value;
        public static int AllocateDeviceId() => nextDeviceId++;

        public static void Queue(IntPtr ev)
        {
            if (ev == IntPtr.Zero) return;
            int size = *(ushort*)((byte*)ev + 4);
            if (size < HeaderSize) return;
            var copy = new byte[size];
            Marshal.Copy(ev, copy, 0, size);
            lock (queued) queued.Add(copy);
        }

        // ───────── 每格 ─────────

        public static void RunUpdate(int type)
        {
            if (updating) return; // managed 端在處理事件時又叫 Update：原版也不會重入
            updating = true;
            try
            {
                if (hasCallback && !reported)
                {
                    reported = true;
                    mouseId = AllocateDeviceId();
                    keyboardId = AllocateDeviceId();
                    NativeInputSystem.NotifyDeviceDiscovered(mouseId, "{\"interface\":\"UnityNative\",\"type\":\"Mouse\",\"product\":\"Mouse\",\"manufacturer\":\"\",\"serial\":\"\",\"version\":\"\",\"capabilities\":\"\"}");
                    NativeInputSystem.NotifyDeviceDiscovered(keyboardId, "{\"interface\":\"UnityNative\",\"type\":\"Keyboard\",\"product\":\"Keyboard\",\"manufacturer\":\"\",\"serial\":\"\",\"version\":\"\",\"capabilities\":\"\"}");
                }
                var nt = (NativeInputUpdateType)type;
                NativeInputSystem.NotifyBeforeUpdate(nt);
                NativeInputSystem.ShouldRunUpdate(nt, out bool run);
                if (!run) return;

                int used = 0, count = 0;
                List<byte[]> evs;
                lock (queued) { evs = new List<byte[]>(queued); queued.Clear(); }
                foreach (var e in evs) Append(e, ref used, ref count);
                if (reported)
                {
                    Append(MouseEvent(), ref used, ref count);
                    Append(KeyboardEvent(), ref used, ref count);
                }
                fixed (byte* p = buffer)
                {
                    var nb = new NativeInputEventBuffer { eventBuffer = p, eventCount = count, sizeInBytes = used, capacityInBytes = buffer.Length };
                    NativeInputSystem.NotifyUpdate(nt, (IntPtr)(&nb));
                }
            }
            catch (Exception e) { Engine.LogException(e); }
            finally { updating = false; }
        }

        static void Append(byte[] ev, ref int used, ref int count)
        {
            int aligned = (ev.Length + 3) & ~3;
            if (used + aligned > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, used + aligned));
            Buffer.BlockCopy(ev, 0, buffer, used, ev.Length);
            used += aligned;
            count++;
        }

        static byte[] StateEvent(int deviceId, int format, int stateSize, out int stateOffset)
        {
            int size = HeaderSize + 4 + stateSize;
            var b = new byte[size];
            fixed (byte* p = b)
            {
                *(int*)p = STAT;
                *(ushort*)(p + 4) = (ushort)size;
                *(ushort*)(p + 6) = (ushort)deviceId;
                *(double*)(p + 8) = Now;
                *(int*)(p + 16) = 0;
                *(int*)(p + 20) = format;
            }
            stateOffset = HeaderSize + 4;
            return b;
        }

        // MouseState（30 位元組）：position、delta、scroll（Vector2）、buttons、displayIndex、clickCount（ushort）
        static byte[] MouseEvent()
        {
            var b = StateEvent(mouseId, MOUS, 30, out int o);
            fixed (byte* p = b)
            {
                var s = p + o;
                var pos = InputState.MousePosition;
                *(Vector2*)s = new Vector2(pos.x, pos.y);
                *(Vector2*)(s + 8) = InputState.MouseDelta;
                // 舊版的滾輪一格 = Input System 的 120（Windows 的 WHEEL_DELTA）；舊版 Input.mouseScrollDelta 是一格 1
                *(Vector2*)(s + 16) = InputState.Scroll * 120f;
                ushort buttons = 0;
                if (InputState.Held(KeyCode.Mouse0)) buttons |= 1;
                if (InputState.Held(KeyCode.Mouse1)) buttons |= 2;
                if (InputState.Held(KeyCode.Mouse2)) buttons |= 4;
                if (InputState.Held(KeyCode.Mouse3)) buttons |= 16; // Back
                if (InputState.Held(KeyCode.Mouse4)) buttons |= 8;  // Forward
                *(ushort*)(s + 24) = buttons;
            }
            return b;
        }

        static int keyBytes;
        static (int bit, KeyCode code)[] keyMap;

        // KeyboardState：Key 列舉的位元陣列（Unity.InputSystem 的 KeyboardState.kSizeInBytes）
        static byte[] KeyboardEvent()
        {
            if (keyMap == null) BuildKeyMap();
            var b = StateEvent(keyboardId, KEYS, keyBytes, out int o);
            foreach (var (bit, code) in keyMap)
                if (InputState.Held(code)) b[o + bit / 8] |= (byte)(1 << (bit % 8));
            return b;
        }

        /// <summary>KeyCode（宿主送的）→ Input System 的 Key：名字一樣的直接對，不一樣的列在下面</summary>
        static void BuildKeyMap()
        {
            keyBytes = 14;
            var keyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            var st = Type.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState, Unity.InputSystem");
            var f = st?.GetField("kSizeInBytes", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (f != null) keyBytes = (int)f.GetValue(null);
            var list = new List<(int, KeyCode)>();
            if (keyType != null)
            {
                var rename = new Dictionary<string, string>
                {
                    ["Return"] = "Enter", ["BackQuote"] = "Backquote", ["LeftControl"] = "LeftCtrl", ["RightControl"] = "RightCtrl",
                    ["LeftCommand"] = "LeftMeta", ["RightCommand"] = "RightMeta", ["LeftWindows"] = "LeftMeta", ["RightWindows"] = "RightMeta",
                    ["KeypadEnter"] = "NumpadEnter", ["KeypadDivide"] = "NumpadDivide", ["KeypadMultiply"] = "NumpadMultiply", ["KeypadMinus"] = "NumpadMinus",
                    ["KeypadPlus"] = "NumpadPlus", ["KeypadPeriod"] = "NumpadPeriod", ["KeypadEquals"] = "NumpadEquals", ["Print"] = "PrintScreen", ["Menu"] = "ContextMenu",
                };
                for (int i = 0; i <= 9; i++) { rename["Alpha" + i] = "Digit" + i; rename["Keypad" + i] = "Numpad" + i; }
                foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
                {
                    var name = kc.ToString();
                    if (rename.TryGetValue(name, out var r)) name = r;
                    if (Enum.TryParse(keyType, name, false, out var key) && (int)key > 0) list.Add(((int)key, kc));
                }
            }
            keyMap = list.ToArray();
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngineInternal_Input_NativeInputSystem
    {
        public static void set_hasDeviceDiscoveredCallback(bool value) => InputSystemNative.SetHasCallback(value);
        public static double get_currentTime() => InputSystemNative.Now;
        public static double get_currentTimeOffsetToRealtimeSinceStartup() => 0;
        public static bool get_allowInputDeviceCreationFromEvents() => true;
        public static void set_allowInputDeviceCreationFromEvents(bool value) { }
        public static int AllocateDeviceId() => InputSystemNative.AllocateDeviceId();
        public static void QueueInputEvent(IntPtr inputEvent) => InputSystemNative.Queue(inputEvent);
        // 裝置命令（查鍵盤配置、按鍵名稱、開關裝置…）：都回「不支援」，managed 端照預設
        public static long IOCTL(int deviceId, int code, IntPtr data, int sizeInBytes) => -1;
        public static void SetPollingFrequency(float hertz) { }
        public static void Update(NativeInputUpdateType updateType) => InputSystemNative.RunUpdate((int)updateType);
        public static ulong GetBackgroundEventBufferSize() => 0;
    }
}
