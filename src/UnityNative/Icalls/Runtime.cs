// Time、Debug、Application、SceneManager、Resources、AsyncOperation、PlayerPrefs、Screen 的 internal call。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Time
    {
        static TimeState T => Engine.Time;
        public static float get_time() => (float)(T.InFixedTimeStep ? T.FixedTime : T.Time);
        public static double get_timeAsDouble() => T.InFixedTimeStep ? T.FixedTime : T.Time;
        public static float get_timeSinceLevelLoad() => (float)(T.Time - T.TimeSinceLevelLoadStart);
        public static double get_timeSinceLevelLoadAsDouble() => T.Time - T.TimeSinceLevelLoadStart;
        public static float get_deltaTime() => T.InFixedTimeStep ? T.FixedDeltaTime : T.DeltaTime;
        public static float get_fixedTime() => (float)T.FixedTime;
        public static double get_fixedTimeAsDouble() => T.FixedTime;
        public static float get_unscaledTime() => (float)T.UnscaledTime;
        public static double get_unscaledTimeAsDouble() => T.UnscaledTime;
        public static float get_fixedUnscaledTime() => (float)T.FixedUnscaledTime;
        public static double get_fixedUnscaledTimeAsDouble() => T.FixedUnscaledTime;
        public static float get_unscaledDeltaTime() => T.UnscaledDeltaTime;
        public static float get_fixedUnscaledDeltaTime() => T.TimeScale > 0 ? T.FixedDeltaTime / T.TimeScale : T.FixedDeltaTime;
        public static float get_fixedDeltaTime() => T.FixedDeltaTime;
        public static void set_fixedDeltaTime(float value) => T.FixedDeltaTime = Math.Max(value, 0.0001f);
        public static float get_maximumDeltaTime() => T.MaximumDeltaTime;
        public static void set_maximumDeltaTime(float value) => T.MaximumDeltaTime = value;
        public static float get_smoothDeltaTime() => T.SmoothDeltaTime;
        public static float get_maximumParticleDeltaTime() => T.MaximumParticleDeltaTime;
        public static void set_maximumParticleDeltaTime(float value) => T.MaximumParticleDeltaTime = value;
        public static float get_timeScale() => T.TimeScale;
        public static void set_timeScale(float value) => T.TimeScale = Math.Max(0f, value);
        public static int get_frameCount() => T.FrameCount;
        public static int get_renderedFrameCount() => T.RenderedFrameCount;
        public static float get_realtimeSinceStartup() => (float)(T.Realtime - T.RealtimeAtStart);
        public static double get_realtimeSinceStartupAsDouble() => T.Realtime - T.RealtimeAtStart;
        public static float get_captureDeltaTime() => T.CaptureDeltaTime;
        public static void set_captureDeltaTime(float value) => T.CaptureDeltaTime = value;
        public static bool get_inFixedTimeStep() => T.InFixedTimeStep;
    }

    public static unsafe partial class UnityEngine_DebugLogHandler
    {
        [ThreadStatic] static bool inLog;
        public static void Internal_Log(LogType level, LogOption options, string msg, UnityEngine.Object obj)
        {
            if (inLog) { Engine.Config?.LogSink?.Invoke(msg); return; }
            inLog = true;
            try { Engine.Log(level, msg, Native.Of(obj)); }
            finally { inLog = false; }
        }
        public static void Internal_LogException(Exception ex, UnityEngine.Object obj)
        {
            if (inLog) { Engine.Config?.LogSink?.Invoke(ex.ToString()); return; }
            inLog = true;
            try { Engine.LogException(ex, Native.Of(obj)); }
            finally { inLog = false; }
        }
    }

    public static unsafe partial class UnityEngine_Debug
    {
        public static int ExtractStackTraceNoAlloc(byte* buffer, int bufferMax, string projectFolder) => 0;
        public static bool get_isDebugBuild() => false;
        public static void Break() { }
        public static void DebugBreak() { }
    }

    public static unsafe partial class UnityEngine_Application
    {
        static EngineConfig C => Engine.Config;
        public static void Quit(int exitCode) { Engine.Quitting = true; Engine.Log(LogType.Log, "Application.Quit(" + exitCode + ")"); }
        public static bool get_isPlaying() => true;
        public static bool IsPlaying(UnityEngine.Object obj) => true;
        public static bool get_isFocused() => true;
        public static bool get_isLoadingLevel() => Scenes.IsLoading;
        public static bool CanStreamedLevelBeLoaded(string levelName) => Scenes.CanLoad(levelName);
        public static string get_dataPath() => C.DataDir;
        public static string get_streamingAssetsPath() => Path.Combine(C.DataDir, "StreamingAssets");
        public static string get_persistentDataPath() => C.PersistentDir;
        public static string get_temporaryCachePath() => Path.Combine(C.PersistentDir, "cache");
        public static string get_unityVersion() => C.UnityVersion;
        public static string get_version() => C.Version;
        public static string get_productName() => C.ProductName;
        public static string get_companyName() => C.CompanyName;
        public static string get_identifier() => "com." + C.CompanyName + "." + C.ProductName;
        public static RuntimePlatform get_platform() => C.Platform;
        public static SystemLanguage get_systemLanguage() => C.Language;
        public static NetworkReachability get_internetReachability() => NetworkReachability.NotReachable;
        public static int get_targetFrameRate() => Engine.TargetFrameRate;
        public static void set_targetFrameRate(int value) => Engine.TargetFrameRate = value;
        public static bool get_runInBackground() => true;
        public static void set_runInBackground(bool value) { }
        public static bool get_isBatchMode() => false;
        public static bool get_isTestRun() => false;
        public static bool get_isHumanControllingUs() => true;
        public static bool HasARGV(string name) => false;
        public static string GetValueForARGV(string name) => null;
        public static bool get_genuine() => true;
        public static bool get_genuineCheckAvailable() => false;
        public static void SetLogCallbackDefined(bool defined) { }
        public static void OpenURL(string url) => Engine.Log(LogType.Log, "OpenURL：" + url);
    }

    public static unsafe partial class UnityEngine_SceneManagement_SceneManager
    {
        public static int get_sceneCount() => Scenes.Loaded.Count;
        public static int get_loadedSceneCount() => Scenes.Loaded.FindAll(s => s.IsLoaded).Count;
        public static bool CanSetAsActiveScene_Injected(ref Scene scene) => Scenes.Get(scene)?.IsLoaded ?? false;
        public static void GetActiveScene_Injected(out Scene ret) => ret = Scenes.Active?.ToScene() ?? default;
        public static bool SetActiveScene_Injected(ref Scene scene)
        {
            var s = Scenes.Get(scene);
            if (s == null || !s.IsLoaded) return false;
            var prev = Scenes.Active;
            Scenes.Active = s;
            if (prev != s) SceneManager.Internal_ActiveSceneChanged(prev?.ToScene() ?? default, s.ToScene());
            return true;
        }
        public static void GetSceneByPath_Injected(string scenePath, out Scene ret)
        {
            ret = default;
            foreach (var s in Scenes.Loaded) if (string.Equals(s.Path, scenePath, StringComparison.OrdinalIgnoreCase)) { ret = s.ToScene(); return; }
        }
        public static void GetSceneByName_Injected(string name, out Scene ret)
        {
            ret = default;
            foreach (var s in Scenes.Loaded) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) { ret = s.ToScene(); return; }
        }
        public static void GetSceneAt_Injected(int index, out Scene ret) => ret = (uint)index < (uint)Scenes.Loaded.Count ? Scenes.Loaded[index].ToScene() : default;
        public static bool UnloadSceneInternal_Injected(ref Scene scene, UnloadSceneOptions options) { Scenes.UnloadNow(Scenes.Get(scene)); return true; }
        public static AsyncOperation UnloadSceneAsyncInternal_Injected(ref Scene scene, UnloadSceneOptions options) => Scenes.UnloadAsync(Scenes.Get(scene));
        public static void MoveGameObjectToScene_Injected(GameObject go, ref Scene scene)
        {
            var g = Native.Self<NGameObject>(go);
            Hierarchy.MoveToScene(g, Scenes.Get(scene), true);
        }
    }

    public static unsafe partial class UnityEngine_SceneManagement_SceneManagerAPIInternal
    {
        public static int GetNumScenesInBuildSettings() => Scenes.BuildCount;
        public static void GetSceneByBuildIndex_Injected(int buildIndex, out Scene ret)
        {
            ret = default;
            foreach (var s in Scenes.Loaded) if (s.BuildIndex == buildIndex) { ret = s.ToScene(); return; }
        }
        public static AsyncOperation LoadSceneAsyncNameIndexInternal_Injected(string sceneName, int sceneBuildIndex, ref LoadSceneParameters parameters, bool mustCompleteNextFrame)
        {
            if (mustCompleteNextFrame)
            {
                // SceneManager.LoadScene（同步）：Unity 在這一格結束前載好；這裡排在這一格的 Update 之後
                var op = Scenes.LoadAsync(sceneName, sceneBuildIndex, parameters.loadSceneMode);
                return op;
            }
            return Scenes.LoadAsync(sceneName, sceneBuildIndex, parameters.loadSceneMode);
        }
        public static AsyncOperation UnloadSceneNameIndexInternal(string sceneName, int sceneBuildIndex, bool immediately, UnloadSceneOptions options, out bool outSuccess)
        {
            NScene target = null;
            foreach (var s in Scenes.Loaded)
                if ((!string.IsNullOrEmpty(sceneName) && (s.Name == sceneName || s.Path == sceneName)) || (string.IsNullOrEmpty(sceneName) && s.BuildIndex == sceneBuildIndex)) { target = s; break; }
            outSuccess = target != null;
            if (target == null) return null;
            if (immediately) { Scenes.UnloadNow(target); return null; }
            return Scenes.UnloadAsync(target);
        }
    }

    public static unsafe partial class UnityEngine_SceneManagement_SceneUtility
    {
        public static string GetScenePathByBuildIndex(int buildIndex) => Scenes.PathOf(buildIndex);
        public static int GetBuildIndexByScenePath(string scenePath) => Scenes.IndexOfPath(scenePath);
    }

    public static unsafe partial class UnityEngine_SceneManagement_Scene
    {
        static NScene S(int h) => Scenes.ByHandle(h);
        public static bool IsValidInternal(int sceneHandle) => S(sceneHandle) != null;
        public static string GetPathInternal(int sceneHandle) => S(sceneHandle)?.Path ?? "";
        public static string GetNameInternal(int sceneHandle) => S(sceneHandle)?.Name ?? "";
        public static void SetNameInternal(int sceneHandle, string name) { var s = S(sceneHandle); if (s != null) s.Name = name; }
        public static string GetGUIDInternal(int sceneHandle) => S(sceneHandle)?.Guid ?? "";
        public static bool IsSubScene(int sceneHandle) => false;
        public static void SetIsSubScene(int sceneHandle, bool value) { }
        public static bool GetIsLoadedInternal(int sceneHandle) => S(sceneHandle)?.IsLoaded ?? false;
        public static Scene.LoadingState GetLoadingStateInternal(int sceneHandle) => S(sceneHandle)?.LoadingState ?? Scene.LoadingState.NotLoaded;
        public static bool GetIsDirtyInternal(int sceneHandle) => false;
        public static int GetDirtyID(int sceneHandle) => 0;
        public static int GetBuildIndexInternal(int sceneHandle) => S(sceneHandle)?.BuildIndex ?? -1;
        public static int GetRootCountInternal(int sceneHandle) => S(sceneHandle)?.Roots.Count ?? 0;
        public static void GetRootGameObjectsInternal(int sceneHandle, object resultRootList)
        {
            var list = (IList)resultRootList;
            list.Clear();
            var s = S(sceneHandle);
            if (s == null) return;
            foreach (var r in s.Roots) list.Add(r.GO);
        }
    }

    public static unsafe partial class UnityEngine_Resources
    {
        public static UnityEngine.Object InstanceIDToObject(int instanceID) => Native.ById(instanceID)?.Managed;
        public static void InstanceIDToObjectList(IntPtr instanceIDs, int instanceCount, List<UnityEngine.Object> objects)
        {
            objects.Clear();
            var ids = (int*)instanceIDs;
            for (int i = 0; i < instanceCount; i++) objects.Add(Native.ById(ids[i])?.Managed);
        }
        public static bool InstanceIDIsValid(int instanceId) => Native.ById(instanceId) != null;
        public static UnityEngine.Object GetBuiltinResource(Type type, string path)
        {
            Assets.Warn("Resources.GetBuiltinResource：" + type.Name + " " + path);
            return null;
        }
        public static void UnloadAssetImplResourceManager(UnityEngine.Object assetToUnload) { }
        public static AsyncOperation UnloadUnusedAssets()
        {
            var op = NAsyncOperation.New<NAsyncOperation>();
            op.Complete();
            return op.Managed;
        }
    }

    public static unsafe partial class UnityEngine_ResourcesAPIInternal
    {
        public static UnityEngine.Object Load(string path, Type systemTypeInstance) => Assets.LoadResource(path, systemTypeInstance ?? typeof(UnityEngine.Object));

        public static UnityEngine.Object[] LoadAll(string path, Type systemTypeInstance) => Assets.LoadAllResources(path, systemTypeInstance ?? typeof(UnityEngine.Object));

        public static ResourceRequest LoadAsyncInternal(string path, Type type)
        {
            var op = NAsyncOperation.New<NAsyncOperation>(typeof(ResourceRequest));
            var rr = (ResourceRequest)op.Managed;
            rr.m_Path = path;
            rr.m_Type = type;
            op.Asset = Assets.LoadResource(path, type ?? typeof(UnityEngine.Object));
            op.Complete();
            return rr;
        }

        public static UnityEngine.Object[] FindObjectsOfTypeAll(Type type) => Finder.Find(type, true, true);
        public static Shader FindShaderByName(string name) => Shaders.Find(name);
    }

    public static unsafe partial class UnityEngine_AsyncOperation
    {
        public static void InternalDestroy(IntPtr ptr) => NAsyncOperation.Release(ptr);
        public static bool get_isDone(AsyncOperation self) => NAsyncOperation.Of(self)?.IsDone ?? true;
        public static float get_progress(AsyncOperation self) => NAsyncOperation.Of(self)?.Progress ?? 1f;
        public static int get_priority(AsyncOperation self) => NAsyncOperation.Of(self)?.Priority ?? 0;
        public static void set_priority(AsyncOperation self, int value) { var n = NAsyncOperation.Of(self); if (n != null) n.Priority = value; }
        public static bool get_allowSceneActivation(AsyncOperation self) => NAsyncOperation.Of(self)?.AllowSceneActivation ?? true;
        public static void set_allowSceneActivation(AsyncOperation self, bool value) { var n = NAsyncOperation.Of(self); if (n != null) n.AllowSceneActivation = value; }
    }

    public static unsafe partial class UnityEngine_PlayerPrefs
    {
        public static bool TrySetInt(string key, int value) { Prefs.Set(key, value); return true; }
        public static bool TrySetFloat(string key, float value) { Prefs.Set(key, value); return true; }
        public static bool TrySetSetString(string key, string value) { Prefs.Set(key, value); return true; }
        public static int GetInt(string key, int defaultValue) => Prefs.Get(key) is long l ? (int)l : Prefs.Get(key) is double d ? (int)d : defaultValue;
        public static float GetFloat(string key, float defaultValue) => Prefs.Get(key) is double d ? (float)d : Prefs.Get(key) is long l ? l : defaultValue;
        public static string GetString(string key, string defaultValue) => Prefs.Get(key) as string ?? defaultValue;
        public static bool HasKey(string key) => Prefs.Get(key) != null;
        public static void DeleteKey(string key) => Prefs.Delete(key);
        public static void DeleteAll() => Prefs.Clear();
        public static void Save() => Prefs.Save();
    }

    public static unsafe partial class UnityEngine_Screen
    {
        public static int get_width() => Engine.Config.ScreenWidth;
        public static int get_height() => Engine.Config.ScreenHeight;
        public static float get_dpi() => Engine.Config.Dpi;
        public static bool get_fullScreen() => true;
        public static void set_fullScreen(bool value) { }
        public static FullScreenMode get_fullScreenMode() => FullScreenMode.FullScreenWindow;
        public static void set_fullScreenMode(FullScreenMode value) { }
        public static Rect[] get_cutouts() => Array.Empty<Rect>();
        public static Resolution[] get_resolutions() => new[] { CurrentResolution() };
        public static void get_currentResolution_Injected(out Resolution ret) => ret = CurrentResolution();
        public static void get_safeArea_Injected(out Rect ret) => ret = new Rect(0, 0, Engine.Config.ScreenWidth, Engine.Config.ScreenHeight);
        /// <summary>記下要求的大小，畫面比例由 ScreenFit 照它補黑邊（遊戲有打開的話）</summary>
        public static void SetResolution_Injected(int width, int height, FullScreenMode fullscreenMode, ref RefreshRate preferredRefreshRate)
        {
            ScreenFit.RequestedWidth = width; ScreenFit.RequestedHeight = height;
            ScreenFit.Update();
        }
        /// <summary>螢幕本身（視窗）的大小</summary>
        static Resolution CurrentResolution() => new Resolution { m_Width = Engine.Config.WindowWidth > 0 ? Engine.Config.WindowWidth : Engine.Config.ScreenWidth, m_Height = Engine.Config.WindowHeight > 0 ? Engine.Config.WindowHeight : Engine.Config.ScreenHeight, m_RefreshRate = new RefreshRate { numerator = (uint)Math.Round(Engine.Config.RefreshRate * 1000), denominator = 1000 } };
    }

    public static unsafe partial class UnityEngine_Hash128
    {
        public static void Parse_Injected(string hashString, out Hash128 ret) => ret = HashUtil.Parse(hashString);
        public static string Hash128ToStringImpl_Injected(ref Hash128 hash) => HashUtil.ToString(hash);
        public static void ComputeFromString(string data, ref Hash128 hash) => hash = HashUtil.Compute(System.Text.Encoding.UTF8.GetBytes(data ?? ""), hash);
        public static void ComputeFromArray(Array data, int start, int count, int elemSize, ref Hash128 hash)
        {
            var bytes = new byte[count * elemSize];
            Buffer.BlockCopy(data, start * elemSize, bytes, 0, bytes.Length);
            hash = HashUtil.Compute(bytes, hash);
        }
        public static void ComputeFromPtr(IntPtr data, int start, int count, int elemSize, ref Hash128 hash)
        {
            var bytes = new byte[count * elemSize];
            System.Runtime.InteropServices.Marshal.Copy(data + start * elemSize, bytes, 0, bytes.Length);
            hash = HashUtil.Compute(bytes, hash);
        }
    }
}
