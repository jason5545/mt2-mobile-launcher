// 引擎本體：時間、每格的 player loop、log、開機。
// 宿主（桌面版、Android）只做三件事：Engine.Boot(設定)、每格 Engine.Frame(真實經過的秒數)、把畫面交給繪製端。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace UnityNative
{
    public sealed class EngineConfig
    {
        /// <summary>匯入器的輸出（public/game）：scenes、assets、packs…</summary>
        public string GameDir;
        /// <summary>原版的 *_Data 資料夾（RuntimeInitializeOnLoads.json、StreamingAssets）</summary>
        public string DataDir;
        /// <summary>存檔、PlayerPrefs 放這裡（Application.persistentDataPath）</summary>
        public string PersistentDir;
        public string ProductName = "";
        public string CompanyName = "";
        public string Version = "";
        public string UnityVersion = "2022.3.62f2";
        public RuntimePlatform Platform = RuntimePlatform.WindowsPlayer;
        public int ScreenWidth = 1920, ScreenHeight = 1080;
        /// <summary>視窗（GL 畫布）的大小；0 ＝ 同 ScreenWidth／ScreenHeight。ScreenWidth／ScreenHeight 是遊戲看到的畫面大小，由 ScreenFit 每格算（黑邊、寬版）</summary>
        public int WindowWidth, WindowHeight;
        /// <summary>手機版面（Diceomancer 的寬版、介面縮放、觸控：Jason 要求的改動，網頁版的 APK 預設開）：Android 宿主開，桌面用 --wide 開</summary>
        public bool MobileLayout;
        public float Dpi = 96;
        public SystemLanguage Language = SystemLanguage.English;
        /// <summary>螢幕的更新率（Hz，Screen.currentResolution）；宿主照顯示模式填（Android 挑同解析度最高的）</summary>
        public float RefreshRate = 60f;
        /// <summary>螢幕圓角半徑（裝置像素，圓角與挖孔取大；0 是方角）。Unity 沒有這個 API，遊戲專屬的修正（mt2-web/native 的 glue）拿來讓開圓角</summary>
        public int CornerRadius;
        /// <summary>允許直向（遊戲的 glue 打開：Diceomancer 的手機直向版面）。Android 宿主看到才把方向從 sensorLandscape 換成 sensor（跟著感應器轉）；預設關，MT2 照舊只有橫向</summary>
        public bool AllowPortrait;
        /// <summary>畫面上緣被挖孔擋住的高（裝置像素，DisplayCutout.SafeInsetTop；橫拿時挖孔在側邊，通常是 0）。Screen.safeArea 照舊回整個畫面，遊戲專屬的 glue 讀這個</summary>
        public int SafeInsetTop;
        public Action<string> LogSink = Console.WriteLine;
        /// <summary>第一個場景（build index 0）</summary>
        public string FirstScene;
    }

    public sealed class TimeState
    {
        public double Time, UnscaledTime, FixedTime, FixedUnscaledTime, TimeSinceLevelLoadStart, RealtimeAtStart;
        public float DeltaTime = 1f / 60f, UnscaledDeltaTime = 1f / 60f, SmoothDeltaTime = 1f / 60f;
        public float TimeScale = 1f, FixedDeltaTime = 0.02f, MaximumDeltaTime = 1f / 3f, MaximumParticleDeltaTime = 0.03f, CaptureDeltaTime;
        public int FrameCount, RenderedFrameCount;
        public bool InFixedTimeStep;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public double Realtime => Clock.Elapsed.TotalSeconds;
    }

    public static class Engine
    {
        public static EngineConfig Config;
        public static readonly TimeState Time = new TimeState();
        public static int TargetFrameRate = -1;


        public static bool Quitting;

        /// <summary>各模組掛在 player loop 上的工作（動畫、Director、繪製、聲音）</summary>
        public static readonly List<Action> EarlyUpdate = new List<Action>();
        public static readonly List<Action> PreUpdate = new List<Action>();
        public static readonly List<Action> PreLateUpdate = new List<Action>();
        public static readonly List<Action> PostLateUpdate = new List<Action>();
        public static readonly List<Action> Render = new List<Action>();
        public static readonly List<Action> FrameEnd = new List<Action>();

        // ───────── log ─────────

        public static int ErrorCount, ExceptionCount;
        static readonly HashSet<string> missingSeen = new HashSet<string>();
        public static readonly Dictionary<string, int> Missing = new Dictionary<string, int>();

        public static void Log(LogType type, string msg, NObject ctx = null)
        {
            if (type == LogType.Error || type == LogType.Assert) ErrorCount++;
            var prefix = type switch { LogType.Error => "[錯誤] ", LogType.Assert => "[assert] ", LogType.Warning => "[警告] ", LogType.Exception => "[例外] ", _ => "" };
            Config?.LogSink?.Invoke(prefix + msg + (ctx != null ? "（" + ctx.Name + "）" : ""));
            try { Application.CallLogCallback(msg, "", type, true); } catch { }
        }

        /// <summary>替身被呼叫的次數（報告用）。第一次碰到的印一行</summary>
        public static readonly Dictionary<string, int> Stubs = new Dictionary<string, int>();
        public static bool LogStubs = true;

        public static void Stub(string what)
        {
            if (Stubs.TryGetValue(what, out var n)) { Stubs[what] = n + 1; return; }
            Stubs[what] = 1;
            if (LogStubs) Config?.LogSink?.Invoke("[替身] " + what);
        }

        public static void LogException(Exception e, NObject ctx = null)
        {
            ExceptionCount++;
            if (e is MissingMethodException mm && mm.Message.Contains("UnityNative"))
            {
                // 沒補的 internal call：同一個只印一次，數量記下來
                var key = mm.Message;
                Missing[key] = Missing.TryGetValue(key, out var n) ? n + 1 : 1;
                if (!missingSeen.Add(key)) return;
            }
            // TypeInitializationException、反射呼叫包的 TargetInvocationException：真正的原因在最裡層
            var inner = e;
            while (inner.InnerException != null) inner = inner.InnerException;
            var cause = inner != e ? "\n  原因：" + inner.GetType().Name + "：" + inner.Message + "\n" + Trim(inner.StackTrace) : "";
            Config?.LogSink?.Invoke("[例外] " + e.GetType().Name + "：" + e.Message + (ctx != null ? "（" + ctx.Name + "）" : "") + TopIL(inner) + "\n" + Trim(e.StackTrace) + cause);
            try { Application.CallLogCallback(e.GetType().Name + ": " + e.Message, e.StackTrace ?? "", LogType.Exception, true); } catch { }
        }

        /// <summary>最上層那格的 IL 位移：遊戲的 DLL 沒有 pdb，小 getter 又會被 inline 進呼叫端，NRE 只看得到外層方法，要靠位移對 IL 才知道是哪個參照</summary>
        static string TopIL(Exception e)
        {
            try
            {
                var f = new StackTrace(e, false).GetFrame(0);
                int il = f?.GetILOffset() ?? StackFrame.OFFSET_UNKNOWN;
                return f == null || il == StackFrame.OFFSET_UNKNOWN ? "" : $"［{f.GetMethod()?.DeclaringType?.Name}.{f.GetMethod()?.Name} IL_{il:x4}］";
            }
            catch { return ""; }
        }

        static string Trim(string st)
        {
            if (st == null) return "";
            var lines = st.Split('\n');
            return string.Join("\n", lines.Length > 12 ? lines[..12] : lines);
        }

        // ───────── 開機 ─────────

        public static void Boot(EngineConfig cfg)
        {
            Config = cfg;
            Time.RealtimeAtStart = Time.Realtime;
            Directory.CreateDirectory(cfg.PersistentDir);
            Assets.Init(cfg.GameDir);
            FillVersions(cfg);
            GraphicsAssets.Register();
            MeshAssets.Register();
            Mixers.Register();
            CanvasModule.Register();
            RendererModule.Register();
            NLight.Register();
            NCollider.Register();
            NCollider2D.Register();
            FrameRenderer.Register();
            InputState.Register();
            InputSystemNative.Register();
            MouseEvents.Register();
            ImGui.Register();
            NVideoPlayer.Register();
            FontFaces.Register();
            NAudioSource.Register();
            NParticleSystem.Register();
            NPlayableDirector.Register();
            NAnimator.Register();
            Scenes.Init();
            try { UnitySynchronizationContext.InitializeSynchronizationContext(); } catch (Exception e) { LogException(e); }
            RuntimeInit.Run(RuntimeInitializeLoadType.SubsystemRegistration);
            RuntimeInit.Run(RuntimeInitializeLoadType.AfterAssembliesLoaded);
            PreloadAssets();
            RuntimeInit.Run(RuntimeInitializeLoadType.BeforeSplashScreen);
            RuntimeInit.Run(RuntimeInitializeLoadType.BeforeSceneLoad);
            var first = cfg.FirstScene ?? Scenes.ByBuildIndex(0);
            Scenes.LoadNow(first, UnityEngine.SceneManagement.LoadSceneMode.Single);
            RuntimeInit.Run(RuntimeInitializeLoadType.AfterSceneLoad);
        }

        /// <summary>Application.version 與 unityVersion：宿主沒給就照原版資料。version 是 PlayerSettings.bundleVersion（project.json），
        /// unityVersion 是 globalgamemanagers 檔頭的版本字串（序列化格式 22 起在位移 48，之前在 20）</summary>
        static void FillVersions(EngineConfig cfg)
        {
            try
            {
                if (string.IsNullOrEmpty(cfg.Version))
                    cfg.Version = Assets.ReadAssetJson("project.json")?["ProjectSettings"]?["PlayerSettings"]?["bundleVersion"]?.ToString() ?? "";
                var ggm = cfg.DataDir == null ? null : Path.Combine(cfg.DataDir, "globalgamemanagers");
                if (ggm != null && File.Exists(ggm))
                {
                    var head = new byte[128];
                    using (var f = File.OpenRead(ggm)) f.ReadExactly(head, 0, Math.Min(head.Length, (int)f.Length));
                    int format = (head[8] << 24) | (head[9] << 16) | (head[10] << 8) | head[11];
                    int at = format >= 22 ? 48 : 20, end = at;
                    while (end < head.Length && head[end] != 0) end++;
                    var v = System.Text.Encoding.ASCII.GetString(head, at, end - at);
                    if (v.Length > 0 && char.IsDigit(v[0])) cfg.UnityVersion = v;
                }
            }
            catch (Exception e) { LogException(e); }
        }

        /// <summary>PlayerSettings 的 Preloaded Assets：原版開機時就載入、一直留著（Diceomancer 的 LocalizationSettings、Input System 的設定，
        /// ScriptableObject 的 OnEnable 把自己設成單例）。清單在 project.json 的 ProjectSettings.PlayerSettings.preloadedAssets</summary>
        public static readonly List<UnityEngine.Object> Preloaded = new List<UnityEngine.Object>();

        static void PreloadAssets()
        {
            if (Assets.ReadAssetJson("project.json")?["ProjectSettings"]?["PlayerSettings"]?["preloadedAssets"] is not System.Text.Json.Nodes.JsonArray list) return;
            foreach (var n in list)
            {
                var guid = n?["guid"]?.ToString();
                if (string.IsNullOrEmpty(guid)) continue;
                var o = Assets.Load(guid, n["fileID"]?.ToString() ?? "11400000");
                if (o != null) Preloaded.Add(o);
            }
        }

        // ───────── 每格 ─────────

        public static void Frame(double realDt)
        {
            ScreenFit.Update();
            var t = Time;
            t.FrameCount++;
            float udt = (float)Math.Min(realDt, 1.0);
            if (t.CaptureDeltaTime > 0) udt = t.CaptureDeltaTime;
            t.UnscaledDeltaTime = udt;
            t.UnscaledTime += udt;
            float dt = Math.Min(udt, t.MaximumDeltaTime) * t.TimeScale;
            t.DeltaTime = dt;
            t.Time += dt;
            t.SmoothDeltaTime = t.SmoothDeltaTime * 0.8f + dt * 0.2f;

            Lap(null);
            // PlayerLoop 插進來的 managed 子系統（UniTask 等）在各段的頭尾，見 PlayerLoop.cs
            PlayerLoops.Run(PlayerLoops.Phase.TimeUpdate, false);
            PlayerLoops.Run(PlayerLoops.Phase.TimeUpdate, true);
            PlayerLoops.Run(PlayerLoops.Phase.Initialization, false);
            PlayerLoops.Run(PlayerLoops.Phase.Initialization, true);
            PlayerLoops.Run(PlayerLoops.Phase.EarlyUpdate, false);
            Run(EarlyUpdate);
            PlayerLoops.Run(PlayerLoops.Phase.EarlyUpdate, true);
            Lap("EarlyUpdate");

            // FixedUpdate：照 fixedDeltaTime 補到現在的時間
            if (t.TimeScale > 0)
            {
                int guard = 0;
                while (t.FixedTime + t.FixedDeltaTime <= t.Time && guard++ < 8)
                {
                    t.FixedTime += t.FixedDeltaTime;
                    t.FixedUnscaledTime += t.FixedDeltaTime / t.TimeScale;
                    t.InFixedTimeStep = true;
                    PlayerLoops.Run(PlayerLoops.Phase.FixedUpdate, false);
                    Lifecycle.RunStarts();
                    Lifecycle.FixedUpdate();
                    Coroutines.AfterFixedUpdate();
                    PlayerLoops.Run(PlayerLoops.Phase.FixedUpdate, true);
                    t.InFixedTimeStep = false;
                }
                if (guard >= 8) t.FixedTime = t.Time;
            }
            Lap("FixedUpdate");

            PlayerLoops.Run(PlayerLoops.Phase.PreUpdate, false);
            Run(PreUpdate);
            PlayerLoops.Run(PlayerLoops.Phase.PreUpdate, true);
            Lap("PreUpdate");
            PlayerLoops.Run(PlayerLoops.Phase.Update, false);
            Lifecycle.RunStarts();
            Lifecycle.Update();
            Lap("腳本 Update");
            Invokes.Run();
            Coroutines.AfterUpdate();
            PlayerLoops.Run(PlayerLoops.Phase.Update, true);
            Lap("Invoke／協程");
            Scenes.Pump();
            Lap("場景載入");
            PlayerLoops.Run(PlayerLoops.Phase.PreLateUpdate, false);
            Run(PreLateUpdate);
            Lap("PreLateUpdate");
            Lifecycle.RunStarts();
            Lifecycle.LateUpdate();
            PlayerLoops.Run(PlayerLoops.Phase.PreLateUpdate, true);
            Lap("腳本 LateUpdate");
            PlayerLoops.Run(PlayerLoops.Phase.PostLateUpdate, false);
            Run(PostLateUpdate);
            Lap("PostLateUpdate");
            Run(Render);
            PlayerLoops.Run(PlayerLoops.Phase.PostLateUpdate, true);
            Lap("Render");
            t.RenderedFrameCount++;
            Coroutines.EndOfFrame();
            Destroyer.Flush();
            try { UnitySynchronizationContext.ExecuteTasks(); } catch (Exception e) { LogException(e); }
            Run(FrameEnd);
            Lap("EndOfFrame");
        }

        // ───────── 分段計時（宿主開 Profile；每段的毫秒累加在 PhaseMs，宿主自己印、自己清）─────────

        public static bool Profile;
        public static readonly Dictionary<string, double> PhaseMs = new Dictionary<string, double>();
        /// <summary>同 PhaseMs 的分段，這段在這條執行緒上配置的位元組（GC.GetAllocatedBytesForCurrentThread 的差）</summary>
        public static readonly Dictionary<string, double> PhaseAlloc = new Dictionary<string, double>();
        /// <summary>上一格各大段的毫秒（不管 Profile 都記；宿主的慢格紀錄用）</summary>
        public static readonly Dictionary<string, double> LastFrameLaps = new Dictionary<string, double>();
        static long lapMark, lapAlloc;

        static void Lap(string name)
        {
            long now = Stopwatch.GetTimestamp();
            if (name != null) LastFrameLaps[name] = Stopwatch.GetElapsedTime(lapMark, now).TotalMilliseconds;
            if (Profile)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                if (name != null) { Add(name, Stopwatch.GetElapsedTime(lapMark, now).TotalMilliseconds); AddAlloc(name, a - lapAlloc); }
                lapAlloc = a;
            }
            lapMark = now;
        }

        public static void Add(string name, double ms) => PhaseMs[name] = PhaseMs.TryGetValue(name, out var v) ? v + ms : ms;
        public static void AddAlloc(string name, long bytes) => PhaseAlloc[name] = PhaseAlloc.TryGetValue(name, out var v) ? v + bytes : bytes;

        static void Run(List<Action> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                long t0 = Profile ? Stopwatch.GetTimestamp() : 0, a0 = Profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
                try { list[i](); }
                catch (Exception e) { LogException(e); }
                if (Profile)
                {
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - a0;
                    var m = list[i].Method;
                    if (!runKeys.TryGetValue(m, out var key)) runKeys[m] = key = "  " + m.DeclaringType?.Name + "." + m.Name;
                    Add(key, ms);
                    AddAlloc(key, bytes);
                }
            }
        }

        /// <summary>Run 的分段名稱照方法記一份（不用每格每個回呼重新接字串）</summary>
        static readonly Dictionary<System.Reflection.MethodInfo, string> runKeys = new Dictionary<System.Reflection.MethodInfo, string>();
    }

    /// <summary>[RuntimeInitializeOnLoadMethod]：原版的清單在 *_Data/RuntimeInitializeOnLoads.json</summary>
    public static class RuntimeInit
    {
        sealed class Entry { public string assemblyName, nameSpace, className, methodName; public int loadTypes; public bool isUnityClass; }
        static List<Entry> entries;

        public static void Run(RuntimeInitializeLoadType when)
        {
            if (entries == null)
            {
                entries = new List<Entry>();
                var f = Path.Combine(Engine.Config.DataDir ?? "", "RuntimeInitializeOnLoads.json");
                if (File.Exists(f))
                {
                    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var e in doc.RootElement.GetProperty("root").EnumerateArray())
                        entries.Add(new Entry
                        {
                            assemblyName = e.GetProperty("assemblyName").GetString(),
                            nameSpace = e.GetProperty("nameSpace").GetString(),
                            className = e.GetProperty("className").GetString(),
                            methodName = e.GetProperty("methodName").GetString(),
                            loadTypes = e.GetProperty("loadTypes").GetInt32(),
                            isUnityClass = e.GetProperty("isUnityClass").GetBoolean(),
                        });
                }
            }
            foreach (var e in entries)
            {
                if (e.loadTypes != (int)when) continue;
                var full = (string.IsNullOrEmpty(e.nameSpace) ? "" : e.nameSpace + ".") + e.className.Replace('/', '+');
                var t = Types.Find(e.assemblyName, full);
                if (t == null) { Engine.Log(LogType.Warning, "RuntimeInitializeOnLoad 找不到型別：" + e.assemblyName + " " + full); continue; }
                var mi = t.GetMethod(e.methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mi == null) continue;
                try { mi.Invoke(null, null); }
                catch (Exception ex) { Lifecycle.Log(ex); }
            }
        }
    }
}
