// 腳本訊息（Awake、Update、OnEnable…）：Unity 照方法名稱找，任何存取層級，從最底層的子類別往上找第一個同名的。
// 每個型別建一次開放實例委派（open instance delegate），之後每格呼叫不走反射。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace UnityNative
{
    public enum Messages
    {
        Awake, OnEnable, Start, Update, LateUpdate, FixedUpdate, OnDisable, OnDestroy,
        OnApplicationQuit,
        OnRectTransformDimensionsChange, OnBeforeTransformParentChanged, OnTransformParentChanged, OnTransformChildrenChanged,
        OnCanvasGroupChanged, OnCanvasHierarchyChanged, OnDidApplyAnimationProperties, OnRectTransformRemoved,
        OnBecameVisible, OnBecameInvisible, OnWillRenderObject, OnPreCull, OnPreRender, OnPostRender, OnRenderObject,
        OnAnimatorMove, OnAnimatorIK, OnParticleSystemStopped, OnParticleCollision, OnParticleTrigger, OnValidate, Reset,
        OnGUI,
        Count
    }

    public sealed class ScriptInfo
    {
        static readonly Dictionary<Type, ScriptInfo> cache = new Dictionary<Type, ScriptInfo>();
        static readonly MethodInfo wrapAction = typeof(ScriptInfo).GetMethod(nameof(WrapAction), BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo wrapFunc = typeof(ScriptInfo).GetMethod(nameof(WrapFunc), BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo wrapBool = typeof(ScriptInfo).GetMethod(nameof(WrapBool), BindingFlags.NonPublic | BindingFlags.Static);

        public readonly Type Type;
        /// <summary>沒有回傳值的訊息</summary>
        public readonly Action<object>[] Calls = new Action<object>[(int)Messages.Count];
        /// <summary>回傳 IEnumerator 的（Start、Awake…可以寫成協程）</summary>
        public readonly Func<object, IEnumerator>[] Coroutines = new Func<object, IEnumerator>[(int)Messages.Count];
        public readonly Action<object, bool> OnApplicationPause, OnApplicationFocus;
        /// <summary>OnRenderImage(RenderTexture src, RenderTexture dst)：攝影機上的影像效果（一格每台攝影機叫一次，用反射叫就夠）</summary>
        public readonly MethodInfo OnRenderImage;
        public readonly int ExecutionOrder;
        public readonly bool HasUpdate, HasLateUpdate, HasFixedUpdate;
        readonly Dictionary<string, MethodInfo[]> byName = new Dictionary<string, MethodInfo[]>();

        ScriptInfo(Type t)
        {
            Type = t;
            for (int i = 0; i < (int)Messages.Count; i++)
            {
                var mi = Find(t, ((Messages)i).ToString(), 0);
                if (mi == null) continue;
                if (mi.ReturnType == typeof(void)) Calls[i] = MakeAction(mi);
                else if (typeof(IEnumerator).IsAssignableFrom(mi.ReturnType)) Coroutines[i] = MakeFunc(mi);
            }
            OnApplicationPause = MakeBool(Find(t, "OnApplicationPause", 1, typeof(bool)));
            OnApplicationFocus = MakeBool(Find(t, "OnApplicationFocus", 1, typeof(bool)));
            OnRenderImage = Find(t, "OnRenderImage", 2, typeof(UnityEngine.RenderTexture));
            HasUpdate = Calls[(int)Messages.Update] != null;
            HasLateUpdate = Calls[(int)Messages.LateUpdate] != null;
            HasFixedUpdate = Calls[(int)Messages.FixedUpdate] != null;
            var order = t.GetCustomAttribute<UnityEngine.DefaultExecutionOrder>(true);
            ExecutionOrder = order?.order ?? 0;
            ExecutionOrder = ScriptOrder.Override(t, ExecutionOrder);
        }

        public static ScriptInfo Of(Type t)
        {
            if (!cache.TryGetValue(t, out var s)) cache[t] = s = new ScriptInfo(t);
            return s;
        }

        public bool Has(Messages m) => Calls[(int)m] != null || Coroutines[(int)m] != null;

        static MethodInfo Find(Type t, string name, int nparams, Type p0 = null)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var c = t; c != null && c != typeof(UnityEngine.MonoBehaviour) && c != typeof(UnityEngine.Behaviour) && c != typeof(object); c = c.BaseType)
            {
                foreach (var m in c.GetMethods(F))
                {
                    if (m.Name != name || m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != nparams) continue;
                    if (p0 != null && ps[0].ParameterType != p0) continue;
                    return m;
                }
            }
            return null;
        }

        static Action<object> MakeAction(MethodInfo mi)
        {
            var dt = typeof(Action<>).MakeGenericType(mi.DeclaringType);
            var d = mi.CreateDelegate(dt);
            return (Action<object>)wrapAction.MakeGenericMethod(mi.DeclaringType).Invoke(null, new object[] { d });
        }

        static Func<object, IEnumerator> MakeFunc(MethodInfo mi)
        {
            var dt = typeof(Func<,>).MakeGenericType(mi.DeclaringType, mi.ReturnType);
            var d = mi.CreateDelegate(dt);
            return (Func<object, IEnumerator>)wrapFunc.MakeGenericMethod(mi.DeclaringType, mi.ReturnType).Invoke(null, new object[] { d });
        }

        static Action<object, bool> MakeBool(MethodInfo mi)
        {
            if (mi == null || mi.ReturnType != typeof(void)) return null;
            var dt = typeof(Action<,>).MakeGenericType(mi.DeclaringType, typeof(bool));
            var d = mi.CreateDelegate(dt);
            return (Action<object, bool>)wrapBool.MakeGenericMethod(mi.DeclaringType).Invoke(null, new object[] { d });
        }

        static Action<object> WrapAction<T>(Action<T> a) => o => a((T)o);
        static Func<object, IEnumerator> WrapFunc<T, R>(Func<T, R> f) where R : IEnumerator => o => f((T)o);
        static Action<object, bool> WrapBool<T>(Action<T, bool> a) => (o, b) => a((T)o, b);

        /// <summary>SendMessage、Invoke 用：照名字找，0 或 1 個參數</summary>
        public MethodInfo[] ByName(string name)
        {
            if (byName.TryGetValue(name, out var ms)) return ms;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var list = new List<MethodInfo>();
            for (var c = Type; c != null && c != typeof(object); c = c.BaseType)
                foreach (var m in c.GetMethods(F))
                    if (m.Name == name && m.GetParameters().Length <= 1 && !m.IsGenericMethodDefinition) list.Add(m);
            byName[name] = ms = list.ToArray();
            return ms;
        }
    }

    /// <summary>腳本執行順序。原版存在 globalgamemanagers 的 MonoScript 裡；拿不到時用 [DefaultExecutionOrder] 加這裡的表</summary>
    public static class ScriptOrder
    {
        public static readonly Dictionary<string, int> Table = new Dictionary<string, int>();
        public static int Override(Type t, int fallback) => Table.TryGetValue(t.FullName, out var o) ? o : fallback;
    }
}
