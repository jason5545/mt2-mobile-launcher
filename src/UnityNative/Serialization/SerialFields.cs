// Unity 序列化的欄位規則（Serializer 讀檔、Cloner 複製都用這份）：
//   - 實例欄位，不是 static、const、readonly，沒有 [NonSerialized]
//   - public（UnityEngine 的型別被 patcher 改成 public 過，原本不是 public 的看 nonpublic-fields.txt），或有 [SerializeField]
//   - 型別可以序列化：基本型別、enum、string、UnityEngine.Object、Unity 的值型別（Vector3、Color…）、
//     [Serializable] 的類別或結構、上面這些的一維陣列或 List<T>
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace UnityNative
{
    public static class SerialFields
    {
        static readonly Dictionary<Type, FieldInfo[]> cache = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, bool> serializable = new Dictionary<Type, bool>();
        static HashSet<string> engineNonPublic;

        static readonly HashSet<Type> unityValueTypes = new HashSet<Type>
        {
            typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector4), typeof(UnityEngine.Vector2Int), typeof(UnityEngine.Vector3Int),
            typeof(UnityEngine.Quaternion), typeof(UnityEngine.Color), typeof(UnityEngine.Color32), typeof(UnityEngine.Rect), typeof(UnityEngine.RectInt),
            typeof(UnityEngine.Bounds), typeof(UnityEngine.BoundsInt), typeof(UnityEngine.Matrix4x4), typeof(UnityEngine.LayerMask), typeof(UnityEngine.Hash128),
        };
        static readonly HashSet<Type> unityClasses = new HashSet<Type>
        {
            typeof(UnityEngine.AnimationCurve), typeof(UnityEngine.Gradient), typeof(UnityEngine.RectOffset),
        };

        public static void LoadEngineNonPublic(string file)
        {
            engineNonPublic = new HashSet<string>();
            if (File.Exists(file)) foreach (var l in File.ReadAllLines(file)) if (l.Length > 0) engineNonPublic.Add(l);
        }

        static bool IsEngineAssembly(Assembly a)
        {
            var n = a.GetName().Name;
            return n == "UnityEngine" || (n.StartsWith("UnityEngine.") && n.EndsWith("Module"));
        }

        static bool WasPublic(FieldInfo f)
        {
            if (!f.IsPublic) return false;
            if (engineNonPublic != null && IsEngineAssembly(f.DeclaringType.Assembly))
                return !engineNonPublic.Contains(f.DeclaringType.FullName.Replace('+', '/') + "::" + f.Name);
            return true;
        }

        public static FieldInfo[] Of(Type t)
        {
            if (cache.TryGetValue(t, out var fs)) return fs;
            var list = new List<FieldInfo>();
            var chain = new List<Type>();
            for (var c = t; c != null && c != typeof(object) && c != typeof(UnityEngine.Object) && c != typeof(ValueType); c = c.BaseType) chain.Add(c);
            chain.Reverse();
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var c in chain)
                foreach (var f in c.GetFields(F))
                {
                    if (f.IsInitOnly || f.IsLiteral || f.IsNotSerialized) continue;
                    bool sref = f.IsDefined(typeof(UnityEngine.SerializeReference), false);
                    bool attr = sref || f.IsDefined(typeof(UnityEngine.SerializeField), false);
                    if (!attr && !WasPublic(f)) continue;
                    // [SerializeReference]：抽象類別、介面、object 都可以（實際型別存在資料裡），不能是值型別與 UnityEngine.Object
                    if (!(sref ? IsReferenceSerializable(f.FieldType) : IsSerializable(f.FieldType, true))) continue;
                    list.Add(f);
                }
            cache[t] = fs = list.ToArray();
            return fs;
        }

        static bool IsReferenceSerializable(Type t)
        {
            if (t.IsArray) t = t.GetElementType();
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) t = t.GetGenericArguments()[0];
            return !t.IsValueType && !t.ContainsGenericParameters && !typeof(UnityEngine.Object).IsAssignableFrom(t) && !typeof(Delegate).IsAssignableFrom(t);
        }

        public static bool IsSerializable(Type t, bool allowCollection)
        {
            if (serializable.TryGetValue(t, out var r) && allowCollection) return r;
            r = Compute(t, allowCollection);
            if (allowCollection) serializable[t] = r;
            return r;
        }

        static bool Compute(Type t, bool allowCollection)
        {
            if (t.IsPrimitive || t.IsEnum || t == typeof(string)) return t != typeof(IntPtr) && t != typeof(UIntPtr);
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return true;
            if (unityValueTypes.Contains(t) || unityClasses.Contains(t)) return true;
            if (t.IsArray) return allowCollection && t.GetArrayRank() == 1 && IsSerializable(t.GetElementType(), false);
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return allowCollection && IsSerializable(t.GetGenericArguments()[0], false);
            if (t.IsAbstract || t.IsInterface || t.IsPointer || t.IsByRef) return false;
            if (typeof(Delegate).IsAssignableFrom(t)) return false;
            if (t.ContainsGenericParameters) return false;
            if (!t.IsSerializable && !t.IsDefined(typeof(SerializableAttribute), false)) return false;
            // BCL 的 [Serializable]（Dictionary、Exception…）Unity 不序列化
            var asm = t.Assembly.GetName().Name;
            if (asm == "System.Private.CoreLib" || asm == "mscorlib" || asm.StartsWith("System.")) return false;
            return true;
        }

        public static FieldInfo Find(Type t, string name)
        {
            foreach (var f in Of(t)) if (f.Name == name) return f;
            return null;
        }
    }
}
