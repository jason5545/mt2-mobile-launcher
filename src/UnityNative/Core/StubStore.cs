// 替身（patcher 寫進 patch 過的 DLL 的 internal call，見 patcher/Stubs.cs）用的儲存：static 屬性存在字典，物件的屬性存在原生物件的屬性袋。
// patcher 照名稱與簽名呼叫這裡的 Get／Set，改簽名要一起改 Stubs.cs。
using System;
using System.Collections.Generic;

namespace UnityNative
{
    public static class StubStore
    {
        static readonly Dictionary<string, object> statics = new Dictionary<string, object>();
        static readonly Dictionary<(NObject, string), object> props = new Dictionary<(NObject, string), object>();

        /// <summary>預設值：有些替身的預設值不能是 0（例如 Time.timeScale），在這裡登記</summary>
        public static readonly Dictionary<string, object> Defaults = new Dictionary<string, object>();

        public static T Get<T>(string key)
        {
            if (statics.TryGetValue(key, out var v) && v is T t) return t;
            if (Defaults.TryGetValue(key, out var d) && d is T dt) return dt;
            return default;
        }

        public static void Set<T>(string key, T value) => statics[key] = value;

        public static T Get<T>(UnityEngine.Object self, string prop)
        {
            var n = Native.Of(self);
            if (n != null && props.TryGetValue((n, prop), out var v) && v is T t) return t;
            var type = self?.GetType();
            for (var c = type; c != null && c != typeof(object); c = c.BaseType)
                if (Defaults.TryGetValue(c.FullName + "." + prop, out var d) && d is T dt) return dt;
            return default;
        }

        public static void Set<T>(UnityEngine.Object self, string prop, T value)
        {
            var n = Native.Of(self);
            if (n != null) props[(n, prop)] = value;
        }
    }
}
