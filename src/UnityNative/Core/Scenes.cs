// SceneManager 的原生半邊：場景表（匯入器的 scenes/index.json）、同步與非同步載入、卸載、DontDestroyOnLoad。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityNative
{
    /// <summary>AsyncOperation 的原生半邊（AsyncOperation 不是 UnityEngine.Object，m_Ptr 自己管）</summary>
    public class NAsyncOperation
    {
        static readonly Dictionary<IntPtr, NAsyncOperation> byPtr = new Dictionary<IntPtr, NAsyncOperation>();
        static int next = 1;

        public IntPtr Ptr;
        public AsyncOperation Managed;
        public bool IsDone;
        public float Progress;
        public int Priority;
        public bool AllowSceneActivation = true;
        public Action Work;
        public UnityEngine.Object Asset;
        /// <summary>AssetBundleRequest.allAssets（LoadAssetWithSubAssetsAsync 的子資產）；null 時就是 Asset 一個</summary>
        public UnityEngine.Object[] AllAssets;

        public static T New<T>(Type managedType = null) where T : NAsyncOperation, new()
        {
            var n = new T { Ptr = (IntPtr)next++ };
            n.Managed = (AsyncOperation)RuntimeHelpers.GetUninitializedObject(managedType ?? typeof(AsyncOperation));
            n.Managed.m_Ptr = n.Ptr;
            byPtr[n.Ptr] = n;
            return n;
        }

        public static NAsyncOperation Of(AsyncOperation op) => op != null && byPtr.TryGetValue(op.m_Ptr, out var n) ? n : null;
        public static void Release(IntPtr p) => byPtr.Remove(p);

        public void Complete()
        {
            if (IsDone) return;
            IsDone = true;
            Progress = 1f;
            try { Managed.InvokeCompletionEvent(); } catch (Exception e) { Engine.LogException(e); }
        }
    }

    public sealed class SceneLoadOp : NAsyncOperation
    {
        public string Scene;
        public LoadSceneMode Mode;
        public bool Unload;
        public NScene Target;
    }

    public static class Scenes
    {
        sealed class Entry { public string Name, File, Guid, Path; public int BuildIndex; }
        static readonly Dictionary<string, Entry> byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly SortedDictionary<int, Entry> byIndex = new SortedDictionary<int, Entry>();
        public static readonly List<NScene> Loaded = new List<NScene>();
        static readonly Dictionary<int, NScene> byHandle = new Dictionary<int, NScene>();
        static readonly List<SceneLoadOp> queue = new List<SceneLoadOp>();
        static int nextHandle = 2;
        public static NScene Active;
        public static NScene DontDestroy;
        public static bool IsLoading => queue.Count > 0;

        public static void Init()
        {
            var idx = Assets.ReadJson("scenes/index.json");
            foreach (var kv in (JsonObject)idx["scenes"])
            {
                var o = (JsonObject)kv.Value;
                var e = new Entry
                {
                    Name = o["name"]?.ToString() ?? kv.Key,
                    File = o["file"]?.ToString(),
                    Guid = o["guid"]?.ToString(),
                    BuildIndex = o["buildIndex"] != null ? (int)Serializer.D(o["buildIndex"]) : -1,
                    Path = "Assets/" + (o["src"]?.ToString() ?? ("Scenes/" + kv.Key + ".unity")),
                };
                byName[e.Name] = e;
                if (e.BuildIndex >= 0) byIndex[e.BuildIndex] = e;
                SceneRefs.AddScene(e.Guid);
            }
            DontDestroy = new NScene { Handle = nextHandle++, Name = "DontDestroyOnLoad", IsLoaded = true, BuildIndex = -1, LoadingState = Scene.LoadingState.Loaded };
            byHandle[DontDestroy.Handle] = DontDestroy;
        }

        public static string ByBuildIndex(int i) => byIndex.TryGetValue(i, out var e) ? e.Name : null;
        public static int BuildCount => byIndex.Count == 0 ? 0 : byIndex.Keys is var k ? Max(k) + 1 : 0;
        static int Max(IEnumerable<int> xs) { int m = -1; foreach (var x in xs) m = Math.Max(m, x); return m; }
        public static string PathOf(int buildIndex) => byIndex.TryGetValue(buildIndex, out var e) ? e.Path : "";
        public static int IndexOfPath(string path)
        {
            foreach (var e in byIndex.Values) if (string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name, path, StringComparison.OrdinalIgnoreCase)) return e.BuildIndex;
            return -1;
        }

        public static NScene ByHandle(int h) => byHandle.TryGetValue(h, out var s) ? s : null;
        public static NScene Get(Scene s) => ByHandle(s.m_Handle);

        static Entry Resolve(string name, int buildIndex)
        {
            if (!string.IsNullOrEmpty(name))
            {
                if (byName.TryGetValue(name, out var e)) return e;
                // 路徑（Assets/Scenes/x.unity）或只有檔名
                var n = System.IO.Path.GetFileNameWithoutExtension(name);
                if (byName.TryGetValue(n, out e)) return e;
                return null;
            }
            return byIndex.TryGetValue(buildIndex, out var b) ? b : null;
        }

        public static bool CanLoad(string name) => Resolve(name, -1) != null;

        // ───────── 載入 ─────────

        public static NScene LoadNow(string name, LoadSceneMode mode) => LoadNow(Resolve(name, -1), mode, name);

        static NScene LoadNow(Entry e, LoadSceneMode mode, string what)
        {
            if (e == null) { Engine.Log(LogType.Error, "Scene '" + what + "' couldn't be loaded because it has not been added to the build settings or the AssetBundle has not been loaded."); return null; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (mode == LoadSceneMode.Single)
                foreach (var s in Loaded.ToArray()) UnloadNow(s);

            var scene = new NScene { Handle = nextHandle++, Name = e.Name, Path = e.Path, Guid = e.Guid, BuildIndex = e.BuildIndex, LoadingState = Scene.LoadingState.Loading };
            byHandle[scene.Handle] = scene;
            Loaded.Add(scene);
            var prevActive = Active;
            if (Active == null || mode == LoadSceneMode.Single) Active = scene;

            var doc = Assets.ParseDocFile(System.IO.Path.Combine(Assets.Dir, e.File));
            var lf = Loader.Load(doc, false, scene, e.Guid, "scene:" + e.Name);
            scene.LoadCtx = lf.Ctx;
            // 另外複製一份留著（JsonObject.Create 會讓整份場景的 JsonDocument 一直活著）
            Render.SceneSettings(scene, doc.TryGetProperty("settings", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.Object ? (JsonObject)JsonNode.Parse(st.GetRawText()) : null);
            scene.IsLoaded = true;
            scene.LoadingState = Scene.LoadingState.Loaded;
            Engine.Time.TimeSinceLevelLoadStart = Engine.Time.Time;

            var active = lf.All.FindAll(g => g.ActiveInHierarchy);
            Lifecycle.Activate(active, byFileOrder: true);
            Engine.Log(LogType.Log, "場景 " + e.Name + " 載入：" + lf.All.Count + " 個 GameObject，" + sw.ElapsedMilliseconds + " ms");

            try { SceneManager.Internal_SceneLoaded(scene.ToScene(), mode); } catch (Exception ex) { Engine.LogException(ex); }
            if (Active != prevActive)
            {
                try { SceneManager.Internal_ActiveSceneChanged(prevActive?.ToScene() ?? default, Active.ToScene()); } catch (Exception ex) { Engine.LogException(ex); }
            }
            return scene;
        }

        public static void UnloadNow(NScene s)
        {
            if (s == null || !Loaded.Contains(s)) return;
            s.LoadingState = Scene.LoadingState.Unloading;
            SceneRefs.SceneUnloaded(s.Guid, s.LoadCtx);
            foreach (var root in s.Roots.ToArray()) Destroyer.DestroyNow(root);
            s.Roots.Clear();
            s.IsLoaded = false;
            s.LoadingState = Scene.LoadingState.NotLoaded;
            Loaded.Remove(s);
            Render.Settings.Remove(s);
            if (Active == s) Active = Loaded.Count > 0 ? Loaded[0] : null;
            try { SceneManager.Internal_SceneUnloaded(s.ToScene()); } catch (Exception ex) { Engine.LogException(ex); }
        }

        public static AsyncOperation LoadAsync(string name, int buildIndex, LoadSceneMode mode)
        {
            var e = Resolve(name, buildIndex);
            if (e == null)
            {
                Engine.Log(LogType.Error, "Scene '" + (name ?? buildIndex.ToString()) + "' couldn't be loaded because it has not been added to the build settings or the AssetBundle has not been loaded.");
                return null;
            }
            var op = NAsyncOperation.New<SceneLoadOp>();
            op.Scene = e.Name;
            op.Mode = mode;
            queue.Add(op);
            return op.Managed;
        }

        public static AsyncOperation UnloadAsync(NScene s)
        {
            var op = NAsyncOperation.New<SceneLoadOp>();
            op.Unload = true;
            op.Target = s;
            queue.Add(op);
            return op.Managed;
        }

        /// <summary>每格 Update 之後：排隊中的非同步載入一格做一個（Unity 也是一個接一個）</summary>
        public static void Pump()
        {
            if (queue.Count == 0) return;
            var op = queue[0];
            if (op.Unload)
            {
                queue.RemoveAt(0);
                UnloadNow(op.Target);
                op.Complete();
                return;
            }
            if (!op.AllowSceneActivation) { op.Progress = 0.9f; return; }
            queue.RemoveAt(0);
            LoadNow(Resolve(op.Scene, -1), op.Mode, op.Scene);
            op.Complete();
        }

        public static void MarkDontDestroy(NGameObject go)
        {
            // 只有根物件能 DontDestroyOnLoad；子物件 Unity 會警告，這裡照樣搬它的根
            var root = go.Transform.Root.Go;
            if (root.Scene == DontDestroy) return;
            root.Scene?.Roots.Remove(root);
            DontDestroy.Roots.Add(root);
            Hierarchy.MoveToScene(root, DontDestroy, false);
            Hierarchy.Walk(root, g => g.DontDestroyOnLoad = true);
        }
    }
}
