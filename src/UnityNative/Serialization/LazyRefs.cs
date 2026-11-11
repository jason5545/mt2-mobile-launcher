// LazyLoadReference<T>（UnityEngine.CoreModule）：存檔裡是一般的物件參照（guid＋fileID），managed 端只存 m_InstanceID，
// 讀 .asset 時才 Object.ForceLoadFromInstanceID。Localization 的 GameObjectLocalizer 用它存各語系的字型、材質（UnityObjectProperty 的 m_VariantData）。
// 這裡先給素材一個 instance ID、不載入；ForceLoadFromInstanceID 第一次叫到時才 Assets.Load，之後同一個 ID 都回同一個物件。
// 同一份檔案裡的參照（沒有 guid、或是場景物件）直接解析，用物件本身的 instance ID。
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityNative
{
    public static class LazyRefs
    {
        static readonly Dictionary<(string guid, string fileId), int> ids = new Dictionary<(string, string), int>();
        static readonly Dictionary<int, (string guid, string fileId)> pending = new Dictionary<int, (string, string)>();
        static readonly Dictionary<int, UnityEngine.Object> loaded = new Dictionary<int, UnityEngine.Object>();
        static readonly Dictionary<Type, FieldInfo> idField = new Dictionary<Type, FieldInfo>();

        public static bool Is(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(UnityEngine.LazyLoadReference<>);

        /// <summary>參照（guid、fileID；同檔或場景的參照給 resolveNow）→ LazyLoadReference&lt;T&gt; 的值</summary>
        public static object Make(Type t, string guid, string fileId, Func<UnityEngine.Object> resolveNow)
        {
            var v = Activator.CreateInstance(t);
            int id = 0;
            if (!string.IsNullOrEmpty(fileId) && fileId != "0")
            {
                if (string.IsNullOrEmpty(guid) || resolveNow != null)
                {
                    var o = resolveNow?.Invoke();
                    id = (object)o != null ? Native.Of(o)?.InstanceId ?? 0 : 0;
                }
                else
                {
                    lock (ids)
                    {
                        if (!ids.TryGetValue((guid, fileId), out id))
                        {
                            id = Native.ReserveAssetId();
                            ids[(guid, fileId)] = id;
                            pending[id] = (guid, fileId);
                        }
                    }
                }
            }
            if (!idField.TryGetValue(t, out var f)) idField[t] = f = t.GetField("m_InstanceID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            f.SetValue(v, id);
            return v;
        }

        public static bool Has(int id) { lock (ids) return pending.ContainsKey(id); }

        /// <summary>ForceLoadFromInstanceID：還沒載入的保留 ID 這時才載入</summary>
        public static UnityEngine.Object Load(int id)
        {
            (string guid, string fileId) key;
            lock (ids)
            {
                if (loaded.TryGetValue(id, out var o) && (object)Native.Of(o) != null) return o;
                if (!pending.TryGetValue(id, out key)) return null;
            }
            var r = Assets.Load(key.guid, key.fileId, typeof(UnityEngine.Object));
            lock (ids) loaded[id] = r;
            return r;
        }

        public static UnityEngine.Object Loaded(int id) { lock (ids) return loaded.TryGetValue(id, out var o) && (object)Native.Of(o) != null ? o : null; }
    }
}
