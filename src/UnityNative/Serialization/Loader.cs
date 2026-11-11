// 場景與 prefab 檔（匯入器的 scenes/*.json、scenes/prefabs/*.json）→ GameObject 樹。
// 照 Unity 的順序：先把檔裡所有物件建出來（腳本跑建構子），再填欄位（同檔參照這時才找得到），再 OnAfterDeserialize。
// 啟用（Awake／OnEnable）由呼叫的人決定：場景載入完馬上做，prefab 是素材不做。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public sealed class LoadedFile
    {
        public readonly LoadContext Ctx = new LoadContext();
        public readonly List<NGameObject> Roots = new List<NGameObject>();
        /// <summary>所有 GameObject，照階層的深度優先順序</summary>
        public readonly List<NGameObject> All = new List<NGameObject>();
    }

    public static class Loader
    {
        /// <summary>原生元件的工廠（Unity 類別名 → 原生物件）；沒登記的用 NGenericComponent</summary>
        public static readonly Dictionary<string, Func<NComponent>> Factories = new Dictionary<string, Func<NComponent>>();
        /// <summary>腳本參照（m_Script 的 guid:fileID）→ 類別：動畫曲線綁腳本欄位時只有這個參照</summary>
        public static readonly Dictionary<string, Type> ScriptByRef = new Dictionary<string, Type>();

        static JsonElement P(JsonElement o, string key) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) ? v : default;
        static bool Has(JsonElement o, string key) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;
        static IEnumerable<JsonElement> Arr(JsonElement o, string key) => P(o, key) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Array.Empty<JsonElement>();

        /// <summary>doc 直接用 JsonElement 讀（Assets.ParseDocFile）：腳本元件的欄位不建 JsonNode 樹，原生元件拿用到才展開的 JsonObject</summary>
        public static LoadedFile Load(JsonElement doc, bool persistent, NScene scene, string guid, string source)
        {
            var lf = new LoadedFile();
            var ctx = lf.Ctx;
            ctx.Guid = guid;
            ctx.Source = source;
            var gos = new List<(string id, JsonElement g, NGameObject go)>();
            var comps = P(doc, "components");

            // 1. GameObject
            var goById = new Dictionary<string, NGameObject>();
            if (P(doc, "gameObjects") is { ValueKind: JsonValueKind.Object } gosEl)
                foreach (var kv in gosEl.EnumerateObject())
                {
                    var g = kv.Value;
                    var managed = (GameObject)Native.NewShell(typeof(GameObject));
                    var go = Native.Bind(new NGameObject
                    {
                        Name = Serializer.Str(g, "m_Name") ?? "",
                        Layer = (int)Serializer.D(P(g, "m_Layer")),
                        Tag = Serializer.Str(g, "m_TagString") ?? "Untagged",
                        ActiveSelf = Serializer.D(P(g, "m_IsActive")) != 0,
                        Source = source,
                    }, managed, persistent);
                    goById[kv.Name] = go;
                    ctx.ByFileId[kv.Name] = go;
                    gos.Add((kv.Name, g, go));
                }

            // 2. 元件（照 GameObject 上的順序）
            var pending = new List<(NComponent n, JsonElement data)>();
            foreach (var (_, g, go) in gos)
            {
                foreach (var cidEl in Arr(g, "components"))
                {
                    var cid = Serializer.Str(cidEl);
                    if (cid == null || P(comps, cid) is not { ValueKind: JsonValueKind.Object } c) continue;
                    var n = CreateComponent(go, c, persistent, source);
                    if (n == null) continue;
                    if (n is NMonoBehaviour fm && long.TryParse(cid, out var fid)) fm.FileOrder = fid;
                    ctx.ByFileId[cid] = n;
                    pending.Add((n, c));
                }
                if (go.Transform == null)
                {
                    // 沒有 Transform 的 GameObject（不該發生）：補一個
                    var tr = Native.Bind(new NTransform { Go = go }, Native.NewShell(typeof(Transform)), persistent);
                    go.Transform = tr;
                    go.Components.Insert(0, tr);
                }
            }

            // 3. 階層：照 GameObject 的 children 順序
            foreach (var (_, g, go) in gos)
                foreach (var chEl in Arr(g, "children"))
                {
                    var chId = Serializer.Str(chEl);
                    if (chId == null || !goById.TryGetValue(chId, out var ch)) continue;
                    ch.Transform.Parent = go.Transform;
                    go.Transform.Children.Add(ch.Transform);
                }
            foreach (var r in Arr(doc, "roots"))
                if (Serializer.Str(r) is string rid && goById.TryGetValue(rid, out var root) && root.Transform.Parent == null) lf.Roots.Add(root);
            // roots 沒列到的根物件（保險）
            foreach (var go in goById.Values) if (go.Transform.Parent == null && !lf.Roots.Contains(go)) lf.Roots.Add(go);

            foreach (var root in lf.Roots) Hierarchy.Walk(root, lf.All.Add);
            foreach (var go in lf.All)
            {
                go.Scene = scene;
                if (!persistent) go.ActiveInHierarchy = go.ActiveSelf && (go.Parent == null || go.Parent.ActiveInHierarchy);
            }
            if (scene != null) scene.Roots.AddRange(lf.Roots);

            // 4. 欄位（之前先讓資產裡指向這個場景的參照接上：SceneRefs）
            if (scene != null) SceneRefs.SceneLoaded(guid, ctx);
            foreach (var (n, data) in pending)
            {
                try
                {
                    if (n is NMonoBehaviour mb) Serializer.Populate(mb.Managed, data, ctx);
                    else n.Load(n.Data, ctx);
                }
                catch (Exception e) { Engine.LogException(e, n); }
            }
            AfterDeserialize(ctx);
            foreach (var root in lf.Roots) root.Transform.SyncRect();
            // 欄位填完就放掉 JSON（JsonObject 包著整份檔的 JsonDocument，留著一個就整份檔一直活著）
            foreach (var (n, _) in pending) { n.Data = null; n.El = default; }
            return lf;
        }

        public static void AfterDeserialize(LoadContext ctx)
        {
            foreach (var o in ctx.AfterDeserialize)
            {
                try { ((ISerializationCallbackReceiver)o).OnAfterDeserialize(); }
                catch (Exception e) { Engine.LogException(e); }
            }
            ctx.AfterDeserialize.Clear();
        }

        static NComponent CreateComponent(NGameObject go, JsonElement c, bool persistent, string source)
        {
            var type = Serializer.Str(c, "type");
            if (type == "Transform" || type == "RectTransform")
            {
                bool rect = type == "RectTransform";
                var tr = Native.Bind(new NTransform { Go = go, IsRect = rect, Name = go.Name }, Native.NewShell(rect ? typeof(RectTransform) : typeof(Transform)), persistent);
                tr.InitLocal(
                    (Vector3)Serializer.Convert(P(c, "m_LocalPosition"), typeof(Vector3), null),
                    (Quaternion)Serializer.Convert(P(c, "m_LocalRotation"), typeof(Quaternion), null),
                    Has(c, "m_LocalScale") ? (Vector3)Serializer.Convert(P(c, "m_LocalScale"), typeof(Vector3), null) : Vector3.one);
                if (rect)
                {
                    tr.AnchorMin = (Vector2)Serializer.Convert(P(c, "m_AnchorMin"), typeof(Vector2), null);
                    tr.AnchorMax = (Vector2)Serializer.Convert(P(c, "m_AnchorMax"), typeof(Vector2), null);
                    tr.AnchoredPosition = (Vector2)Serializer.Convert(P(c, "m_AnchoredPosition"), typeof(Vector2), null);
                    tr.SizeDelta = (Vector2)Serializer.Convert(P(c, "m_SizeDelta"), typeof(Vector2), null);
                    tr.Pivot = (Vector2)Serializer.Convert(P(c, "m_Pivot"), typeof(Vector2), null);
                }
                tr.Data = JsonObject.Create(c);
                go.Transform = tr;
                go.Components.Insert(0, tr);
                return tr;
            }
            if (type == "MonoBehaviour")
            {
                var st = ScriptType(c);
                if (st == null)
                {
                    Assets.Warn("找不到腳本：" + (Raw(c, "script") ?? Raw(c, "m_Script")) + "（" + source + "）");
                    return null;
                }
                if (!typeof(MonoBehaviour).IsAssignableFrom(st))
                {
                    // 巢狀的腳本類別（GlobalMonoBehavior.GlobalMonoBehaviorInst）：匯入器只記到外層
                    var nested = Array.FindAll(st.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic), t => typeof(MonoBehaviour).IsAssignableFrom(t));
                    if (nested.Length == 1) st = nested[0];
                }
                if (!typeof(MonoBehaviour).IsAssignableFrom(st))
                {
                    Assets.Warn("腳本不是 MonoBehaviour：" + st.FullName);
                    return null;
                }
                if (P(c, "m_Script") is { ValueKind: JsonValueKind.Object } sref && Has(sref, "guid")) ScriptByRef[Serializer.Str(sref, "guid") + ":" + Serializer.Str(sref, "fileID")] = st;
                var mb = NewBehaviour(go, st, persistent);
                mb.Enabled = Serializer.D(P(c, "m_Enabled")) != 0;
                return mb;
            }
            var mt = Types.Engine(type);
            if (mt == null) { Assets.Warn("沒有對應的 Unity 類別：" + type); return null; }
            var n = Factories.TryGetValue(type, out var fac) ? fac() : Fallback(mt);
            n.Go = go;
            n.Name = go.Name;
            n.Data = JsonObject.Create(c);
            n.El = c;
            if (n is NBehaviour b && Has(c, "m_Enabled")) b.Enabled = Serializer.D(P(c, "m_Enabled")) != 0;
            Native.Bind(n, Native.NewShell(mt), persistent);
            go.Components.Add(n);
            return n;
        }

        static string Raw(JsonElement o, string key) => Has(o, key) ? P(o, key).GetRawText() : null;

        /// <summary>沒登記工廠的原生元件：Renderer 的子類別用 NRenderer（材質、排序那些共用），其他用 NGenericComponent</summary>
        public static NComponent Fallback(Type mt) => typeof(Renderer).IsAssignableFrom(mt) ? new NRenderer() : new NGenericComponent();

        /// <summary>建一個腳本元件：先綁原生物件再跑建構子（Unity 也是建構子跑的時候 m_CachedPtr 已經有了）</summary>
        public static NMonoBehaviour NewBehaviour(NGameObject go, Type st, bool persistent)
        {
            var managed = (MonoBehaviour)RuntimeHelpers.GetUninitializedObject(st);
            var n = Native.Bind(new NMonoBehaviour { Go = go, Name = go.Name, Info = ScriptInfo.Of(st), Seq = Lifecycle.NextSeq() }, managed, persistent);
            go.Components.Add(n);
            RunCtor(managed);
            return n;
        }

        static readonly Dictionary<Type, ConstructorInfo> ctors = new Dictionary<Type, ConstructorInfo>();

        public static void RunCtor(object o)
        {
            var t = o.GetType();
            if (!ctors.TryGetValue(t, out var ctor)) ctors[t] = ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor == null) return;
            try { ctor.Invoke(o, null); }
            catch (Exception e) { Lifecycle.Log(e); }
        }

        // ───────── 腳本型別 ─────────

        static Dictionary<string, List<Type>> byClassName;

        public static Type ScriptType(JsonElement c)
        {
            if (c.TryGetProperty("script", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                var cls = Serializer.Str(s, "class");
                var ns = Serializer.Str(s, "namespace");
                var full = string.IsNullOrEmpty(ns) ? cls : ns + "." + cls;
                var t = Types.Find(Serializer.Str(s, "assembly"), full);
                if (t != null) return t;
                return ByClassName(cls);
            }
            if (c.TryGetProperty("$script", out var ds) && ds.ValueKind != JsonValueKind.Null) return ByClassName(Serializer.Str(ds));
            return null;
        }

        public static Type ScriptType(JsonObject c)
        {
            if (c["script"] is JsonObject s)
            {
                var asm = s["assembly"]?.ToString();
                var ns = s["namespace"]?.ToString();
                var cls = s["class"]?.ToString();
                var full = string.IsNullOrEmpty(ns) ? cls : ns + "." + cls;
                var t = Types.Find(asm, full);
                if (t != null) return t;
                return ByClassName(cls);
            }
            if (c["$script"] != null) return ByClassName(c["$script"].ToString());
            return null;
        }

        /// <summary>只有類別名（ScriptableObject 的索引只記類別名）：在遊戲的組件裡找 MonoBehaviour／ScriptableObject 子類別</summary>
        public static Type ByClassName(string cls)
        {
            if (cls == null) return null;
            if (byClassName == null)
            {
                byClassName = new Dictionary<string, List<Type>>();
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = a.GetName().Name;
                    if (n.StartsWith("System") || n == "mscorlib" || n == "netstandard" || n.StartsWith("Microsoft.") || n == "UnityNative") continue;
                    foreach (var t in Types.SafeGetTypes(a))
                    {
                        if (!typeof(MonoBehaviour).IsAssignableFrom(t) && !typeof(ScriptableObject).IsAssignableFrom(t)) continue;
                        if (!byClassName.TryGetValue(t.Name, out var l)) byClassName[t.Name] = l = new List<Type>();
                        l.Add(t);
                    }
                }
            }
            if (!byClassName.TryGetValue(cls, out var list)) return null;
            if (list.Count == 1) return list[0];
            return list.Find(t => t.Assembly.GetName().Name == "Assembly-CSharp") ?? list[0];
        }
    }
}
