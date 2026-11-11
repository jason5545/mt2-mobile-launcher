// 資產裡指向場景物件的參照（同 TS 版 csport/core.ts 的 collectSceneRefs／resolveSceneRefs）。
// 原版嵌在場景裡的 ScriptableObject（I2 的 LocalizeTarget_TextMeshPro_UGUI 這類），AssetRipper 匯出成獨立資產，
// 欄位指回場景裡的物件：{guid: 場景, fileID}。素材庫裡沒有場景，以前照素材找，全部落空（每個場景的 TMP 一條「素材庫找不到」）。
// 場景已經載入（通常是場景自己載入時順便載到這個資產）：當場從那個場景的 fileID 表拿；還沒載入的記下欄位，場景載入時（Awake 之前）補接。
// 只補直接的欄位；陣列、List 裡的元素只當場解（MT2 用到的都是單一欄位）。
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityNative
{
    public static class SceneRefs
    {
        sealed class Slot
        {
            public WeakReference<object> Target;
            public FieldInfo Field;
            public string FileId;
            public Type Want;
        }

        static readonly HashSet<string> sceneGuids = new HashSet<string>();
        static readonly Dictionary<string, LoadContext> loaded = new Dictionary<string, LoadContext>();
        static readonly Dictionary<string, List<Slot>> slots = new Dictionary<string, List<Slot>>();

        public static void AddScene(string guid) { if (!string.IsNullOrEmpty(guid)) sceneGuids.Add(guid); }
        public static bool IsScene(string guid) => guid != null && sceneGuids.Contains(guid);

        /// <summary>場景的物件都建好、欄位還沒讀之前：登記它的 fileID 表，補接等著這個場景的欄位</summary>
        public static void SceneLoaded(string guid, LoadContext ctx)
        {
            if (!IsScene(guid)) return;
            loaded[guid] = ctx;
            if (!slots.TryGetValue(guid, out var list)) return;
            list.RemoveAll(s => !s.Target.TryGetTarget(out _));
            foreach (var s in list)
                if (s.Target.TryGetTarget(out var t))
                    s.Field.SetValue(t, Serializer.Fit(ctx.Local(s.FileId), s.Want));
        }

        public static void SceneUnloaded(string guid, LoadContext ctx)
        {
            if (guid != null && loaded.TryGetValue(guid, out var c) && c == ctx) loaded.Remove(guid);
        }

        /// <summary>參照的場景載入了就回那個物件，沒有回 null</summary>
        public static UnityEngine.Object Resolve(string guid, string fileId) => loaded.TryGetValue(guid, out var ctx) ? ctx.Local(fileId) : null;

        /// <summary>欄位（target.field）指向場景物件：記下來，那個場景（再）載入時接上</summary>
        public static void Track(object target, FieldInfo field, string guid, string fileId)
        {
            if (!slots.TryGetValue(guid, out var list)) slots[guid] = list = new List<Slot>();
            list.Add(new Slot { Target = new WeakReference<object>(target), Field = field, FileId = fileId, Want = field.FieldType });
        }
    }
}
