// Profiler 的計數器（ProfilerRecorder、ProfilerRecorderHandle）：不量任何東西，只讓 handle 有效。
// 原版播放器裡 marker 都存在，Recorder.Get(名稱) 拿得到有效的 recorder；替身回 0 時 Recorder 變成 s_InvalidRecorder，
// 之後 enabled = false 會走到 ProfilerRecorder.Stop() 的 CheckInitializedAndThrow 丟例外（ProfilingSampler 的建構子就會）。
// 兩個結構的第一個欄位都是 ulong handle（ProfilerRecorderHandle 是 Explicit、Size 8）。
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace UnityNative.Icalls
{
    /// <summary>marker：名稱 → 非 0 的 id（不是真的指標，只給 Begin/EndSample 這些什麼都不做的 icall 用），反查得回名稱</summary>
    static class Markers
    {
        static readonly Dictionary<string, ulong> ids = new Dictionary<string, ulong>();
        static readonly List<string> names = new List<string>();

        public static ulong Id(string name)
        {
            name ??= "";
            if (!ids.TryGetValue(name, out var id)) { names.Add(name); ids[name] = id = (ulong)names.Count; }
            return id;
        }

        public static string Name(ulong id) => id >= 1 && id <= (ulong)names.Count ? names[(int)id - 1] : null;
    }

    // CustomSampler.Create、ProfilerMarker 用 CreateMarker 的回傳值當 handle；回 0 的話 Sampler.GetRecorder 一樣拿到無效的 recorder
    public static unsafe partial class Unity_Profiling_LowLevel_Unsafe_ProfilerUnsafeUtility
    {
        public static IntPtr CreateMarker(string a0, ushort a1, Unity.Profiling.LowLevel.MarkerFlags a2, int a3) => (IntPtr)(long)Markers.Id(a0);
        public static IntPtr CreateMarker__Unmanaged(byte* a0, int a1, ushort a2, Unity.Profiling.LowLevel.MarkerFlags a3, int a4) =>
            (IntPtr)(long)Markers.Id(System.Text.Encoding.UTF8.GetString(a0, a1));
        public static IntPtr CreateMarker_Unsafe(char* a0, int a1, ushort a2, Unity.Profiling.LowLevel.MarkerFlags a3, int a4) => (IntPtr)(long)Markers.Id(new string(a0, 0, a1));
        public static IntPtr GetMarker(string a0) => (IntPtr)(long)Markers.Id(a0);
        public static string Internal_GetName(IntPtr a0) => Markers.Name((ulong)(long)a0);
    }

    public static unsafe partial class Unity_Profiling_LowLevel_Unsafe_ProfilerRecorderHandle
    {
        static ProfilerRecorderHandle Of(string name)
        {
            ProfilerRecorderHandle h = default;
            Unsafe.As<ProfilerRecorderHandle, ulong>(ref h) = Markers.Id(name);
            return h;
        }

        public static void GetByName_Injected(ref ProfilerCategory a0, string a1, out ProfilerRecorderHandle a2) => a2 = Of(a1);
        public static void GetByName__Unmanaged_Injected(ref ProfilerCategory a0, byte* a1, int a2, out ProfilerRecorderHandle a3) =>
            a3 = Of(System.Text.Encoding.UTF8.GetString(a1, a2));
        public static void GetByName_Unsafe_Injected(ref ProfilerCategory a0, char* a1, int a2, out ProfilerRecorderHandle a3) => a3 = Of(new string(a1, 0, a2));
    }

    public static unsafe partial class Unity_Profiling_ProfilerRecorder
    {
        static ulong next;

        public static void Create_Injected(ref ProfilerRecorderHandle a0, int a1, ProfilerRecorderOptions a2, out ProfilerRecorder a3)
        {
            a3 = default;
            Unsafe.As<ProfilerRecorder, ulong>(ref a3) = ++next;
        }

        public static void Control_Injected(ref ProfilerRecorder a0, ProfilerRecorder.ControlOptions a1) { }
        public static bool GetValid_Injected(ref ProfilerRecorder a0) => true;
        public static bool GetRunning_Injected(ref ProfilerRecorder a0) => false;
        public static bool GetWrapped_Injected(ref ProfilerRecorder a0) => false;
        public static int GetCount_Injected(ref ProfilerRecorder a0, ProfilerRecorder.CountOptions a1) => 0;
        public static long GetCurrentValue_Injected(ref ProfilerRecorder a0) => 0;
        public static double GetCurrentValueAsDouble_Injected(ref ProfilerRecorder a0) => 0;
        public static long GetLastValue_Injected(ref ProfilerRecorder a0) => 0;
        public static double GetLastValueAsDouble_Injected(ref ProfilerRecorder a0) => 0;
    }
}
