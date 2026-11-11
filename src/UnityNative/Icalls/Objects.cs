// UnityEngine.Object、GameObject、Component、Behaviour、MonoBehaviour、ScriptableObject 的 internal call。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Object
    {
        public static int MainThread;

        public static void Destroy(UObject obj, float t) => Destroyer.Destroy(Native.Of(obj), t);
        public static void DestroyImmediate(UObject obj, bool allowDestroyingAssets) => Destroyer.DestroyNow(Native.Of(obj));

        public static UObject[] FindObjectsOfType(Type type, bool includeInactive) => Finder.Find(type, includeInactive, false);
        public static UObject[] FindObjectsByType(Type type, FindObjectsInactive findObjectsInactive, FindObjectsSortMode sortMode) =>
            Finder.Find(type, findObjectsInactive == FindObjectsInactive.Include, false);
        public static UObject[] FindObjectsOfTypeIncludingAssets(Type type) => Finder.Find(type, true, true);

        public static void DontDestroyOnLoad(UObject target)
        {
            var n = Native.Of(target);
            var go = n as NGameObject ?? (n as NComponent)?.Go;
            if (go != null) Scenes.MarkDontDestroy(go);
        }

        public static HideFlags get_hideFlags(UObject self) => Native.Self<NObject>(self).HideFlags;
        public static void set_hideFlags(UObject self, HideFlags value) => Native.Self<NObject>(self).HideFlags = value;
        public static int GetOffsetOfInstanceIDInCPlusPlusObject() => Native.InstanceIdOffset;
        public static bool CurrentThreadIsMainThread() => System.Threading.Thread.CurrentThread.ManagedThreadId == MainThread;

        public static UObject Internal_CloneSingle(UObject data) => Finder.Clone(data, null, false, null, null);
        public static UObject Internal_CloneSingleWithParent(UObject data, Transform parent, bool worldPositionStays) => Finder.Clone(data, parent, worldPositionStays, null, null);
        public static UObject Internal_CloneSingleWithScene_Injected(UObject data, ref Scene scene) => Finder.Clone(data, null, false, null, null, Scenes.Get(scene));
#if !NO_InstantiateParameters
        // InstantiateParameters 是 2022.3 後期才加的（MT2 的 2022.3.62 有，Diceomancer 的 2022.3.3 沒有；build.sh 偵測）
        public static UObject Internal_CloneSingleWithParams_Injected(UObject data, ref InstantiateParameters p) =>
            Finder.Clone(data, p.parent, p.worldSpace, null, null, p.parent == null ? Scenes.Get(p.scene) : null);
        public static UObject Internal_InstantiateSingleWithParams_Injected(UObject data, ref Vector3 position, ref Quaternion rotation, ref InstantiateParameters p) =>
            Finder.Clone(data, p.parent, true, position, rotation, p.parent == null ? Scenes.Get(p.scene) : null);
#endif
        public static UObject Internal_InstantiateSingle_Injected(UObject data, ref Vector3 pos, ref Quaternion rot) => Finder.Clone(data, null, true, pos, rot);
        public static UObject Internal_InstantiateSingleWithParent_Injected(UObject data, Transform parent, ref Vector3 pos, ref Quaternion rot) => Finder.Clone(data, parent, true, pos, rot);

        public static string ToString(UObject obj)
        {
            var n = Native.Of(obj);
            return n == null ? "null" : Finder.NameOf(n) + " (" + obj.GetType().FullName + ")";
        }

        public static string GetName(UObject obj) => Finder.NameOf(Native.Self<NObject>(obj));
        public static void SetName(UObject obj, string name)
        {
            var n = Native.Self<NObject>(obj);
            if (n is NComponent c && c.Go != null) c.Go.Name = name ?? "";
            else n.Name = name ?? "";
        }
        public static bool IsPersistent(UObject obj) => Native.Of(obj)?.Persistent ?? false;
        public static bool DoesObjectWithInstanceIDExist(int instanceID) => Native.ById(instanceID) != null || LazyRefs.Has(instanceID);
        public static UObject FindObjectFromInstanceID(int instanceID) => Native.ById(instanceID)?.Managed ?? LazyRefs.Loaded(instanceID);
        public static UObject ForceLoadFromInstanceID(int instanceID) => Native.ById(instanceID)?.Managed ?? LazyRefs.Load(instanceID);
        public static void MarkDirty(UObject self) { }
    }

    /// <summary>Object 的共用邏輯（找物件、Instantiate）</summary>
    public static class Finder
    {
        public static string NameOf(NObject n) => n is NComponent c && c.Go != null ? c.Go.Name : n.Name;

        public static UObject[] Find(Type type, bool includeInactive, bool includeAssets)
        {
            var list = new List<UObject>();
            foreach (var n in Native.All)
            {
                if (!n.Alive || (object)n.Managed == null || !type.IsInstanceOfType(n.Managed)) continue;
                if (n.Persistent && !includeAssets) continue;
                if (!includeInactive)
                {
                    var go = n as NGameObject ?? (n as NComponent)?.Go;
                    if (go != null && !go.ActiveInHierarchy) continue;
                }
                list.Add(n.Managed);
            }
            var arr = Array.CreateInstance(type, list.Count);
            for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i);
            return (UObject[])arr;
        }

        public static UObject Clone(UObject data, Transform parent, bool worldStays, Vector3? pos, Quaternion? rot, NScene scene = null)
        {
            if ((object)data == null) throw new ArgumentException("The Object you want to instantiate is null.");
            var n = Native.Of(data);
            if (n == null) throw new MissingReferenceException("The Object you want to instantiate has been destroyed.");
            var pt = Native.Of<NTransform>(parent);
            switch (n)
            {
                case NGameObject go:
                    return Cloner.CloneGameObject(go, pt, worldStays, pos, rot, scene);
                case NComponent c:
                    {
                        int index = c.Go.Components.IndexOf(c);
                        var path = PathFrom(c.Go.Transform.Root == c.Go.Transform ? c.Go : c.Go, c.Go);
                        var clone = Native.Of<NGameObject>(Cloner.CloneGameObject(c.Go, pt, worldStays, pos, rot, scene));
                        return index >= 0 && index < clone.Components.Count ? clone.Components[index].Managed : null;
                    }
                default:
                    {
                        var r = Cloner.CloneSingle(data);
                        return r;
                    }
            }
        }

        static string PathFrom(NGameObject root, NGameObject go) => go.Name;
    }

    public static unsafe partial class UnityEngine_GameObject
    {
        static NGameObject G(GameObject self) => Native.Self<NGameObject>(self);

        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            var go = Hierarchy.NewGameObject(type.ToString());
            return go.GO;
        }

        public static Component GetComponent(GameObject self, Type type) => Comps.First(G(self), type);
        public static void GetComponentFastPath(GameObject self, Type type, IntPtr oneFurtherThanResultValue) =>
            Comps.WriteResult(oneFurtherThanResultValue, Comps.First(G(self), type));
        public static Component GetComponentByName(GameObject self, string type) => Comps.ByName(G(self), type, true);
        public static Component GetComponentByNameWithCase(GameObject self, string type, bool caseSensitive) => Comps.ByName(G(self), type, caseSensitive);
        public static Component GetComponentInChildren(GameObject self, Type type, bool includeInactive) => Comps.InChildren(G(self), type, includeInactive);
        public static Component GetComponentInParent(GameObject self, Type type, bool includeInactive) => Comps.InParent(G(self), type, includeInactive);
        public static Array GetComponentsInternal(GameObject self, Type type, bool useSearchTypeAsArrayReturnType, bool recursive, bool includeInactive, bool reverse, object resultList) =>
            Comps.Many(G(self), type, useSearchTypeAsArrayReturnType, recursive, includeInactive, reverse, resultList);
        public static Component TryGetComponentInternal(GameObject self, Type type) => Comps.First(G(self), type);
        public static void TryGetComponentFastPath(GameObject self, Type type, IntPtr oneFurtherThanResultValue) =>
            Comps.WriteResult(oneFurtherThanResultValue, Comps.First(G(self), type));

        public static Component AddComponentInternal(GameObject self, string className)
        {
            var t = Types.Engine(className) ?? Loader.ByClassName(className);
            if (t == null) { Engine.Log(LogType.Error, "AddComponent: no script or engine class named '" + className + "'"); return null; }
            return Comps.Add(G(self), t);
        }

        public static Component Internal_AddComponentWithType(GameObject self, Type componentType) => Comps.Add(G(self), componentType);
        public static int GetComponentCount(GameObject self) => G(self).Components.Count;
        public static Component QueryComponentAtIndex(GameObject self, int index) => (Component)G(self).Components[index].Managed;
        public static int GetComponentIndex(GameObject self, Component component) => G(self).Components.IndexOf(Native.Of<NComponent>(component));

        public static Transform get_transform(GameObject self) => (Transform)G(self).Transform.Managed;
        public static int get_layer(GameObject self) => G(self).Layer;
        public static void set_layer(GameObject self, int value) => G(self).Layer = value;
        public static bool get_active(GameObject self) => G(self).ActiveInHierarchy;
        public static void set_active(GameObject self, bool value) => Hierarchy.SetActive(G(self), value);
        public static void SetActive(GameObject self, bool value) => Hierarchy.SetActive(G(self), value);
        public static bool get_activeSelf(GameObject self) => G(self).ActiveSelf;
        public static bool get_activeInHierarchy(GameObject self) => G(self).ActiveInHierarchy;
        public static void SetActiveRecursively(GameObject self, bool state)
        {
            Hierarchy.Walk(G(self), g => g.ActiveSelf = state);
            Hierarchy.UpdateActive(G(self));
        }
        public static bool get_isStatic(GameObject self) => G(self).IsStatic;
        public static void set_isStatic(GameObject self, bool value) => G(self).IsStatic = value;
        public static bool get_isStaticBatchable(GameObject self) => false;
        public static string get_tag(GameObject self) => G(self).Tag;
        public static void set_tag(GameObject self, string value) => G(self).Tag = value ?? "Untagged";
        public static bool CompareTag(GameObject self, string tag) => G(self).Tag == tag;

        public static GameObject FindGameObjectWithTag(string tag)
        {
            foreach (var n in Native.All)
                if (n is NGameObject g && g.Alive && !g.Persistent && g.ActiveInHierarchy && g.Tag == tag) return g.GO;
            return null;
        }

        public static GameObject[] FindGameObjectsWithTag(string tag)
        {
            var list = new List<GameObject>();
            foreach (var n in Native.All)
                if (n is NGameObject g && g.Alive && !g.Persistent && g.ActiveInHierarchy && g.Tag == tag) list.Add(g.GO);
            return list.ToArray();
        }

        public static void SendMessageUpwards(GameObject self, string methodName, object value, SendMessageOptions options)
        {
            bool any = false;
            for (var g = G(self); g != null; g = g.Parent) any |= Comps.Send(g, methodName, value);
            if (!any && options == SendMessageOptions.RequireReceiver) Engine.Log(LogType.Error, "SendMessage " + methodName + " has no receiver!");
        }

        public static void SendMessage(GameObject self, string methodName, object value, SendMessageOptions options)
        {
            bool any = Comps.Send(G(self), methodName, value);
            if (!any && options == SendMessageOptions.RequireReceiver) Engine.Log(LogType.Error, "SendMessage " + methodName + " has no receiver!");
        }

        public static void BroadcastMessage(GameObject self, string methodName, object parameter, SendMessageOptions options)
        {
            bool any = false;
            Hierarchy.Walk(G(self), g => any |= Comps.Send(g, methodName, parameter));
            if (!any && options == SendMessageOptions.RequireReceiver) Engine.Log(LogType.Error, "BroadcastMessage " + methodName + " has no receiver!");
        }

        public static void Internal_CreateGameObject(GameObject self, string name) => Hierarchy.NewGameObject(name, self);

        public static GameObject Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.Contains('/'))
            {
                var parts = name.TrimStart('/').Split('/');
                foreach (var s in AllScenes())
                    foreach (var root in s.Roots)
                    {
                        if (root.Name != parts[0] || !root.ActiveInHierarchy) continue;
                        var t = Comps.FindPath(root.Transform, parts, 1, true);
                        if (t != null) return t.Go.GO;
                    }
                return null;
            }
            foreach (var s in AllScenes())
                foreach (var root in s.Roots)
                {
                    GameObject hit = null;
                    Hierarchy.Walk(root, g => { if (hit == null && g.ActiveInHierarchy && g.Name == name) hit = g.GO; });
                    if (hit != null) return hit;
                }
            return null;
        }

        static IEnumerable<NScene> AllScenes()
        {
            foreach (var s in Scenes.Loaded) yield return s;
            yield return Scenes.DontDestroy;
        }

        public static void SetGameObjectsActive(IntPtr instanceIds, int instanceCount, bool active)
        {
            var ids = (int*)instanceIds;
            for (int i = 0; i < instanceCount; i++) if (Native.ById(ids[i]) is NGameObject g) Hierarchy.SetActive(g, active);
        }

        public static ulong get_sceneCullingMask(GameObject self) => ulong.MaxValue;

        public static void InstantiateGameObjects_Injected(int sourceInstanceID, IntPtr newInstanceIDs, IntPtr newTransformInstanceIDs, int count, ref Scene destinationScene)
        {
            var src = Native.ById(sourceInstanceID) as NGameObject;
            var ids = (int*)newInstanceIDs; var tids = (int*)newTransformInstanceIDs;
            for (int i = 0; i < count; i++)
            {
                var clone = Native.Of<NGameObject>(Cloner.CloneGameObject(src, null, false, null, null, Scenes.Get(destinationScene)));
                ids[i] = clone.InstanceId;
                tids[i] = clone.Transform.InstanceId;
            }
        }

        public static void GetScene_Injected(int instanceID, out Scene ret)
        {
            var g = Native.ById(instanceID) as NGameObject;
            ret = g?.Scene?.ToScene() ?? default;
        }

        public static void get_scene_Injected(GameObject self, out Scene ret) => ret = G(self).Scene?.ToScene() ?? default;
    }

    /// <summary>找元件、加元件的共用邏輯</summary>
    public static unsafe class Comps
    {
        public static void WriteResult(IntPtr oneFurther, Component c)
        {
            // CastHelper<T>：t 就在 onePointerFurtherThanT 前一個指標的位置
            Unsafe.AsRef<object>((void*)(oneFurther - IntPtr.Size)) = c;
        }

        public static Component First(NGameObject g, Type type)
        {
            var cs = g.Components;
            for (int i = 0; i < cs.Count; i++)
            {
                var c = cs[i];
                if (c.Alive && type.IsInstanceOfType(c.Managed)) return (Component)c.Managed;
            }
            return null;
        }

        public static Component ByName(NGameObject g, string name, bool caseSensitive)
        {
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var c in g.Components)
                for (var t = c.Managed.GetType(); t != null && t != typeof(Component); t = t.BaseType)
                    if (string.Equals(t.Name, name, cmp)) return (Component)c.Managed;
            return null;
        }

        /// <summary>搜尋元件時算不算啟用：場景裡的照 activeInHierarchy；prefab 素材（不在場景、ActiveInHierarchy 一直是 false，免得跑 Awake）
        /// 當作根在啟用的場景裡，照 activeSelf 一路往上算（同 TS 版 unity/scene.ts 的 dormant 節點）。不這樣的話 prefab.GetComponentInChildren 永遠是 null：
        /// 續關重播出牌時 RoomTargetingUI.CharacterPlacementPreview 拿角色 prefab 的 CharacterUI，拿到 null 就 NullReferenceException（10/19）。
        /// 現在只有往上找（InParent、反向的 Many）用；往下找改用「呼叫的物件一律搜、子物件看 activeSelf」，prefab 這個情況也涵蓋</summary>
        static bool SearchActive(NGameObject g)
        {
            if (!g.Persistent) return g.ActiveInHierarchy;
            for (var x = g; x != null; x = x.Parent) if (!x.ActiveSelf) return false;
            return true;
        }

        /// <summary>同 Many：呼叫的那個物件不管有沒有啟用都搜，子物件看自己有沒有被關（activeSelf）。
        /// MT2 拖召喚卡到樓層時，預覽角色在 Setup 前先 SetVisible(false) 關掉 CharacterUI，CharacterUI.characterMesh 再對自己 GetComponentInChildren 找 SpineMeshes 的 mesh；
        /// 照 activeInHierarchy 判斷會拿到 null，預覽卡在半路、手牌停在預覽狀態（10/24）</summary>
        public static Component InChildren(NGameObject g, Type type, bool includeInactive) => InChildren(g, type, includeInactive, true);

        static Component InChildren(NGameObject g, Type type, bool includeInactive, bool root)
        {
            if (!includeInactive && !root && !g.ActiveSelf) return null;
            var r = First(g, type);
            if (r != null) return r;
            foreach (var ch in g.Transform.Children)
            {
                if (ch.Go == null) continue;
                r = InChildren(ch.Go, type, includeInactive, false);
                if (r != null) return r;
            }
            return null;
        }

        public static Component InParent(NGameObject g, Type type, bool includeInactive)
        {
            for (var x = g; x != null; x = x.Parent)
            {
                if (!includeInactive && !SearchActive(x)) continue;
                var r = First(x, type);
                if (r != null) return r;
            }
            return null;
        }

        public static Array Many(NGameObject g, Type type, bool useSearchType, bool recursive, bool includeInactive, bool reverse, object resultList)
        {
            var found = new List<UObject>();
            void Self(NGameObject x)
            {
                foreach (var c in x.Components) if (c.Alive && type.IsInstanceOfType(c.Managed)) found.Add(c.Managed);
            }
            if (!recursive) Self(g);
            else if (!reverse)
            {
                // 呼叫的那個物件不管有沒有啟用都搜（Unity 文件：includeInactive 只管子物件）；子物件看自己有沒有被關（activeSelf）。
                // 根物件啟用時這跟 activeInHierarchy 一樣；根物件關著時（MT2 的設定對話框在 Initialize 時是關的，原版照樣抓得到分頁）才有差
                void Walk(NGameObject x, bool root)
                {
                    if (!includeInactive && !root && !x.ActiveSelf) return;
                    Self(x);
                    foreach (var ch in x.Transform.Children) if (ch.Go != null) Walk(ch.Go, false);
                }
                Walk(g, true);
            }
            else
            {
                for (var x = g; x != null; x = x.Parent)
                {
                    if (!includeInactive && !SearchActive(x)) continue;
                    Self(x);
                }
            }
            if (resultList is IList list)
            {
                list.Clear();
                foreach (var c in found) list.Add(c);
                return null;
            }
            var arr = Array.CreateInstance(useSearchType ? type : typeof(Component), found.Count);
            for (int i = 0; i < found.Count; i++) arr.SetValue(found[i], i);
            return arr;
        }

        public static Component Add(NGameObject g, Type t)
        {
            if (t == null) return null;
            if (t == typeof(Transform)) return (Component)g.Transform.Managed;
            if (t == typeof(RectTransform)) return Rectify(g);
            // [RequireComponent] 先補
            foreach (var rc in t.GetCustomAttributes<RequireComponent>(true))
                foreach (var req in new[] { rc.m_Type0, rc.m_Type1, rc.m_Type2 })
                    if (req != null && First(g, req) == null) Add(g, req);
            if (t.IsAbstract) { Engine.Log(LogType.Error, "Cannot add component of abstract type " + t.Name); return null; }
            if (typeof(MonoBehaviour).IsAssignableFrom(t))
            {
                if (t.IsDefined(typeof(DisallowMultipleComponent), true) && First(g, t) != null) return First(g, t);
                var mb = Loader.NewBehaviour(g, t, g.Persistent);
                if (g.ActiveInHierarchy) Lifecycle.Activate(new List<NGameObject> { g });
                return mb.MB;
            }
            var n = Loader.Factories.TryGetValue(t.Name, out var fac) ? fac() : Loader.Fallback(t);
            n.Go = g;
            n.Name = g.Name;
            Native.Bind(n, Native.NewShell(t), g.Persistent);
            g.Components.Add(n);
            n.OnActiveChanged();
            return (Component)n.Managed;
        }

        /// <summary>AddComponent&lt;RectTransform&gt;()：Transform 換成 RectTransform（Unity 也是整個換掉）</summary>
        static Component Rectify(NGameObject g)
        {
            var old = g.Transform;
            if (old.IsRect) return (Component)old.Managed;
            var tr = Native.Bind(new NTransform { Go = g, Name = g.Name, IsRect = true }, Native.NewShell(typeof(RectTransform)), g.Persistent);
            tr.InitLocal(old.LocalPosition, old.LocalRotation, old.LocalScale);
            tr.AnchoredPosition = new Vector2(old.LocalPosition.x, old.LocalPosition.y);
            tr.Parent = old.Parent;
            if (old.Parent != null) old.Parent.Children[old.Parent.Children.IndexOf(old)] = tr;
            foreach (var c in old.Children) { c.Parent = tr; tr.Children.Add(c); }
            old.Children.Clear();
            g.Transform = tr;
            g.Components[g.Components.IndexOf(old)] = tr;
            Native.Release(old);
            tr.MarkDirty();
            return (Component)tr.Managed;
        }

        public static bool Send(NGameObject g, string method, object value)
        {
            if (g == null || !g.ActiveInHierarchy) return false;
            bool any = false;
            foreach (var c in g.Components.ToArray())
            {
                if (c is not NMonoBehaviour mb || !mb.Alive) continue;
                foreach (var mi in mb.Info.ByName(method))
                {
                    var ps = mi.GetParameters();
                    try
                    {
                        object r;
                        if (ps.Length == 0) r = mi.Invoke(mb.Managed, null);
                        else if (value == null || ps[0].ParameterType.IsInstanceOfType(value)) r = mi.Invoke(mb.Managed, new[] { value });
                        else continue;
                        if (r is IEnumerator e) Coroutines.Start(mb, e);
                    }
                    catch (Exception e) { Lifecycle.Log(e, mb); }
                    any = true;
                    break;
                }
            }
            return any;
        }

        public static NTransform FindPath(NTransform t, string[] parts, int i, bool activeOnly)
        {
            if (i >= parts.Length) return t;
            var p = parts[i];
            if (p == "" ) return FindPath(t, parts, i + 1, activeOnly);
            if (p == "..") return t.Parent == null ? null : FindPath(t.Parent, parts, i + 1, activeOnly);
            foreach (var c in t.Children)
            {
                if (c.Go == null || c.Go.Name != p) continue;
                if (activeOnly && !c.Go.ActiveInHierarchy) continue;
                var r = FindPath(c, parts, i + 1, activeOnly);
                if (r != null) return r;
            }
            return null;
        }
    }

    public static unsafe partial class UnityEngine_Component
    {
        static NComponent C(Component self) => Native.Self<NComponent>(self);
        public static Transform get_transform(Component self) => (Transform)C(self).Go.Transform.Managed;
        public static GameObject get_gameObject(Component self) => C(self).Go.GO;
        public static void GetComponentFastPath(Component self, Type type, IntPtr oneFurtherThanResultValue) => Comps.WriteResult(oneFurtherThanResultValue, Comps.First(C(self).Go, type));
        public static Component GetComponent(Component self, string type) => Comps.ByName(C(self).Go, type, true);
        public static void GetComponentsForListInternal(Component self, Type searchType, object resultList) => Comps.Many(C(self).Go, searchType, true, false, true, false, resultList);
        public static int GetComponentIndex(Component self) => C(self).Go.Components.IndexOf(C(self));
        public static void SendMessageUpwards(Component self, string methodName, object value, SendMessageOptions options) => UnityEngine_GameObject.SendMessageUpwards(C(self).Go.GO, methodName, value, options);
        public static void SendMessage(Component self, string methodName, object value, SendMessageOptions options) => UnityEngine_GameObject.SendMessage(C(self).Go.GO, methodName, value, options);
        public static void BroadcastMessage(Component self, string methodName, object parameter, SendMessageOptions options) => UnityEngine_GameObject.BroadcastMessage(C(self).Go.GO, methodName, parameter, options);
    }

    public static unsafe partial class UnityEngine_Behaviour
    {
        public static bool get_enabled(Behaviour self) => Native.Self<NBehaviour>(self).Enabled;
        public static void set_enabled(Behaviour self, bool value) => Lifecycle.SetEnabled(Native.Self<NBehaviour>(self), value);
        public static bool get_isActiveAndEnabled(Behaviour self) => Native.Self<NBehaviour>(self).IsActiveAndEnabled;
    }

    public static unsafe partial class UnityEngine_MonoBehaviour
    {
        static NMonoBehaviour M(MonoBehaviour self) => Native.Self<NMonoBehaviour>(self);
        static readonly ConditionalWeakTable<NCoroutine, string> names = new ConditionalWeakTable<NCoroutine, string>();

        public static void StopCoroutine(MonoBehaviour self, string methodName)
        {
            foreach (var co in M(self).Coroutines.ToArray())
                if (names.TryGetValue(co, out var n) && n == methodName) Coroutines.Stop(co);
        }
        public static void StopAllCoroutines(MonoBehaviour self) => Coroutines.StopAll(M(self));
        public static bool get_useGUILayout(MonoBehaviour self) => true;
        public static void set_useGUILayout(MonoBehaviour self, bool value) { }
        public static void Internal_CancelInvokeAll(MonoBehaviour self) => Invokes.CancelAll(M(self));
        public static bool Internal_IsInvokingAll(MonoBehaviour self) => Invokes.IsInvoking(M(self), null);
        public static void InvokeDelayed(MonoBehaviour self, string methodName, float time, float repeatRate) => Invokes.Add(M(self), methodName, time, repeatRate);
        public static void CancelInvoke(MonoBehaviour self, string methodName) => Invokes.Cancel(M(self), methodName);
        public static bool IsInvoking(MonoBehaviour self, string methodName) => Invokes.IsInvoking(M(self), methodName);
        public static bool IsObjectMonoBehaviour(UObject obj) => Native.Of(obj) is NMonoBehaviour;

        static bool CanStart(NMonoBehaviour mb)
        {
            if (mb.Go != null && !mb.Go.ActiveInHierarchy)
            {
                Engine.Log(LogType.Error, "Coroutine couldn't be started because the the game object '" + mb.Go.Name + "' is inactive!");
                return false;
            }
            return true;
        }

        public static Coroutine StartCoroutineManaged(MonoBehaviour self, string methodName, object value)
        {
            var mb = M(self);
            if (!CanStart(mb)) return null;
            foreach (var mi in mb.Info.ByName(methodName))
            {
                if (!typeof(IEnumerator).IsAssignableFrom(mi.ReturnType)) continue;
                var ps = mi.GetParameters();
                var e = (IEnumerator)mi.Invoke(self, ps.Length == 0 ? null : new[] { value });
                var h = Coroutines.Start(mb, e);
                var co = Coroutines.Of(h);
                if (co != null) names.AddOrUpdate(co, methodName);
                return h;
            }
            Engine.Log(LogType.Error, "Coroutine '" + methodName + "' couldn't be started!");
            return null;
        }

        public static Coroutine StartCoroutineManaged2(MonoBehaviour self, IEnumerator enumerator)
        {
            var mb = M(self);
            if (!CanStart(mb)) return null;
            return Coroutines.Start(mb, enumerator);
        }

        public static void StopCoroutineManaged(MonoBehaviour self, Coroutine routine) => Coroutines.Stop(Coroutines.Of(routine));
        public static void StopCoroutineFromEnumeratorManaged(MonoBehaviour self, IEnumerator routine) => Coroutines.StopByEnumerator(M(self), routine);
        public static string GetScriptClassName(MonoBehaviour self) => self.GetType().Name;
        public static void OnCancellationTokenCreated(MonoBehaviour self) { }
    }

    public static unsafe partial class UnityEngine_Coroutine
    {
        public static void ReleaseCoroutine(IntPtr ptr) { }
    }

    public static unsafe partial class UnityEngine_ScriptableObject
    {
        public static void SetDirty(ScriptableObject self) { }
        public static void CreateScriptableObject(ScriptableObject self) => ScriptableObjects.Bind(self);

        public static ScriptableObject CreateScriptableObjectInstanceFromName(string className)
        {
            var t = Loader.ByClassName(className);
            return t == null ? null : CreateScriptableObjectInstanceFromType(t, true);
        }

        public static ScriptableObject CreateScriptableObjectInstanceFromType(Type type, bool applyDefaultsAndReset)
        {
            var so = (ScriptableObject)RuntimeHelpers.GetUninitializedObject(type);
            var n = ScriptableObjects.Bind(so);
            n.Name = "";
            Loader.RunCtor(so);
            Lifecycle.AwakeScriptableObject(n);
            return so;
        }

        public static void ResetAndApplyDefaultInstances(UObject obj) { }
    }
}
