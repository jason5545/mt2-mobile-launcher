// prefab（scenes/prefabs/<guid>.json）與 ScriptableObject（assets/objects/<guid>.json）。
// prefab 是素材：GameObject 樹建出來但不在任何場景、不 Awake；Instantiate 從它複製。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace UnityNative
{
    public sealed class PrefabFile
    {
        public readonly string Guid;
        public LoadedFile File;
        public PrefabFile(string guid) { Guid = guid; }

        public void Load()
        {
            var doc = Assets.ParseDocFile(Path.Combine(Assets.Dir, "scenes/prefabs/" + Guid + ".json"));
            File = Loader.Load(doc, true, null, Guid, "prefab:" + (Serializer.Str(doc, "src") ?? Guid));
        }

        public UnityEngine.Object Get(string fileId, Type want)
        {
            if (File.Ctx.ByFileId.TryGetValue(fileId, out var n)) return Serializer.Fit(n.Managed, want ?? typeof(UnityEngine.Object));
            // prefab 本身（fileID 100100000 這類）或找不到：回根物件
            var root = File.Roots.Count > 0 ? File.Roots[0] : null;
            return root == null ? null : Serializer.Fit(root.Managed, want ?? typeof(GameObject));
        }
    }

    public static class ScriptableObjects
    {
        sealed class SoFile { public LoadContext Ctx; public string MainFileId; }
        static readonly Dictionary<string, SoFile> files = new Dictionary<string, SoFile>();

        public static UnityEngine.Object Load(string guid, string fileId, JsonObject rec)
        {
            if (!files.TryGetValue(guid, out var f))
            {
                var doc = Assets.ParseDocFile(Assets.AssetPath(rec?["file"]?.ToString() ?? ("objects/" + guid + ".json")));
                var mainId = Serializer.Str(doc, "fileID") ?? "11400000";
                f = new SoFile { Ctx = new LoadContext { Guid = guid, Source = "asset:" + (rec?["src"]?.ToString() ?? guid) }, MainFileId = mainId };
                files[guid] = f;
                // 主物件與子物件（Timeline 的 track、clip 這類同檔物件）先全部建出來，再填欄位
                var items = new List<(NScriptableObject n, JsonElement data)>();
                var main = Create(doc, rec?["script"]?.ToString(), f.Ctx, mainId);
                if (main.n != null) items.Add(main);
                if (doc.TryGetProperty("$subObjects", out var subs) && subs.ValueKind == JsonValueKind.Object)
                    foreach (var kv in subs.EnumerateObject())
                    {
                        if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                        var it = Create(kv.Value, Serializer.Str(kv.Value, "$script"), f.Ctx, kv.Name);
                        if (it.n != null) items.Add(it);
                    }
                foreach (var (n, data) in items)
                {
                    Assets.Remember(guid, n.Source.Substring(n.Source.LastIndexOf('#') + 1), n.Managed);
                    try { Serializer.Populate(n.Managed, data, f.Ctx); } catch (Exception e) { Engine.LogException(e, n); }
                }
                Loader.AfterDeserialize(f.Ctx);
                // 素材的 ScriptableObject 載入時 Awake、OnEnable（Unity 也是）
                foreach (var (n, _) in items) Lifecycle.AwakeScriptableObject(n);
            }
            return f.Ctx.Local(fileId) ?? f.Ctx.Local(f.MainFileId);
        }

        static (NScriptableObject n, JsonElement data) Create(JsonElement data, string cls, LoadContext ctx, string fileId)
        {
            var t = Loader.ScriptType(data) ?? Loader.ByClassName(cls);
            if (t == null || !typeof(ScriptableObject).IsAssignableFrom(t))
            {
                Assets.Warn("ScriptableObject 找不到腳本 " + cls + "（" + ctx.Source + "）");
                return (null, default);
            }
            var so = (ScriptableObject)RuntimeHelpers.GetUninitializedObject(t);
            var n = Native.Bind(new NScriptableObject { Name = Serializer.Str(data, "m_Name") ?? "", Info = ScriptInfo.Of(t), Source = ctx.Source + "#" + fileId }, so, true);
            ctx.ByFileId[fileId] = n;
            Loader.RunCtor(so);
            return (n, data);
        }

        /// <summary>ScriptableObject.CreateInstance、new：執行期建的，不是素材</summary>
        public static NScriptableObject Bind(ScriptableObject so)
        {
            var n = Native.Of<NScriptableObject>(so);
            if (n != null) return n;
            return Native.Bind(new NScriptableObject { Info = ScriptInfo.Of(so.GetType()) }, so, false);
        }
    }
}
