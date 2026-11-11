// 原生物件表：Unity 的每個物件（GameObject、元件、素材）在 C++ 那邊有一個原生物件，managed 那邊的 UnityEngine.Object
// 只帶一個 m_CachedPtr 指過去。這裡用 C# 物件（NObject 的子類別）當原生物件，m_CachedPtr 指向一小塊非受控記憶體：
//   offset 0：物件表的索引（int），offset 8：instance ID（int）。
// Object.GetInstanceID() 是 managed 程式直接讀 *(int*)(m_CachedPtr + GetOffsetOfInstanceIDInCPlusPlusObject())，
// 所以 m_CachedPtr 一定要是真的記憶體位址，不能放索引。
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnityNative
{
    public class NObject
    {
        public int Index;
        public int InstanceId;
        public IntPtr Ptr;
        public UnityEngine.Object Managed;
        public string Name = "";
        public UnityEngine.HideFlags HideFlags;
        public bool Alive = true;
        /// <summary>素材（存在檔案裡的物件）：Object.IsPersistent、FindObjectsOfType 不列</summary>
        public bool Persistent;
        public bool DontDestroyOnLoad;
        /// <summary>來源（素材的 guid、場景檔），除錯用</summary>
        public string Source;
    }

    public static unsafe class Native
    {
        public const int InstanceIdOffset = 8;
        const int BlockSize = 16;

        static readonly List<NObject> table = new List<NObject> { null };
        static readonly Dictionary<int, NObject> byId = new Dictionary<int, NObject>();
        static readonly Stack<int> freeIndex = new Stack<int>();
        static int nextAssetId = 2;
        static int nextRuntimeId = -2;

        public static int Count => byId.Count;

        /// <summary>先保留一個素材用的 instance ID（LazyLoadReference 指到還沒載入的素材，Serialization/LazyRefs.cs）</summary>
        public static int ReserveAssetId() => System.Threading.Interlocked.Add(ref nextAssetId, 2);
        public static IEnumerable<NObject> All => byId.Values;

        /// <summary>原生物件和 managed 物件綁在一起。managed 物件可以是剛用 GetUninitializedObject 建出來的，也可以是腳本 new 出來的</summary>
        public static T Bind<T>(T n, UnityEngine.Object managed, bool persistent = false) where T : NObject
        {
            int index;
            if (freeIndex.Count > 0) { index = freeIndex.Pop(); table[index] = n; }
            else { index = table.Count; table.Add(n); }
            n.Index = index;
            n.Persistent = persistent;
            // Unity：檔案裡載入的物件 instance ID 是正數，執行期建的是負數
            n.InstanceId = persistent ? System.Threading.Interlocked.Add(ref nextAssetId, 2) : (nextRuntimeId -= 2);
            n.Ptr = Marshal.AllocHGlobal(BlockSize);
            *(int*)n.Ptr = index;
            *(int*)((byte*)n.Ptr + InstanceIdOffset) = n.InstanceId;
            byId[n.InstanceId] = n;
            if ((object)managed != null)
            {
                n.Managed = managed;
                managed.m_CachedPtr = n.Ptr;
            }
            return n;
        }

        /// <summary>原生類別（Transform、Camera…）的 managed 外殼：Unity 也是不跑建構子直接配一個</summary>
        public static UnityEngine.Object NewShell(Type t) => (UnityEngine.Object)RuntimeHelpers.GetUninitializedObject(t);

        public static NObject Of(UnityEngine.Object o)
        {
            if ((object)o == null) return null;
            var p = o.m_CachedPtr;
            if (p == IntPtr.Zero) return null;
            return table[*(int*)p];
        }

        public static T Of<T>(UnityEngine.Object o) where T : NObject => Of(o) as T;

        /// <summary>給 internal call 用：self 是 null 或已經摧毀時照 Unity 丟例外</summary>
        public static T Self<T>(UnityEngine.Object o) where T : NObject
        {
            if ((object)o == null) throw new NullReferenceException();
            var n = Of(o);
            if (n == null)
                throw new UnityEngine.MissingReferenceException("The object of type '" + o.GetType().Name + "' has been destroyed but you are still trying to access it.");
            if (n is T t) return t;
            throw new InvalidCastException("UnityNative：" + o.GetType().FullName + " 的原生物件是 " + n.GetType().Name + "，不是 " + typeof(T).Name);
        }

        public static NObject ById(int id) => byId.TryGetValue(id, out var n) ? n : null;

        /// <summary>摧毀：managed 那邊的 m_CachedPtr 歸零（== null 從此成立），原生記憶體收回</summary>
        public static void Release(NObject n)
        {
            if (!n.Alive) return;
            n.Alive = false;
            if ((object)n.Managed != null) n.Managed.m_CachedPtr = IntPtr.Zero;
            byId.Remove(n.InstanceId);
            table[n.Index] = null;
            freeIndex.Push(n.Index);
            Marshal.FreeHGlobal(n.Ptr);
            n.Ptr = IntPtr.Zero;
        }

        public static T M<T>(NObject n) where T : UnityEngine.Object => n == null ? null : (T)n.Managed;
    }
}
