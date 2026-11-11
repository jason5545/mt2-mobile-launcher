// NoAllocHelpers（List<T> 的內部陣列：uGUI 的 VertexHelper、Mesh.SetVertices(List) 都靠它）與 RectOffset（原生結構，m_Ptr 自己管）。
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_NoAllocHelpers
    {
        sealed class ListFields { public FieldInfo Items, Size, Version; }
        static readonly Dictionary<Type, ListFields> fields = new Dictionary<Type, ListFields>();

        static ListFields F(Type t)
        {
            if (fields.TryGetValue(t, out var f)) return f;
            const BindingFlags B = BindingFlags.Instance | BindingFlags.NonPublic;
            f = new ListFields { Items = t.GetField("_items", B), Size = t.GetField("_size", B), Version = t.GetField("_version", B) };
            fields[t] = f;
            return f;
        }

        public static Array ExtractArrayFromList(object list) => list == null ? null : (Array)F(list.GetType()).Items.GetValue(list);

        public static void Internal_ResizeList(object list, int size)
        {
            var f = F(list.GetType());
            var items = (Array)f.Items.GetValue(list);
            int old = (int)f.Size.GetValue(list);
            // 縮小時把多出來的清掉（跟 List.RemoveRange 一樣，不留參照）
            if (size < old) Array.Clear(items, size, old - size);
            f.Size.SetValue(list, size);
            if (f.Version != null) f.Version.SetValue(list, (int)f.Version.GetValue(list) + 1);
        }
    }

    public static unsafe partial class UnityEngine_RectOffset
    {
        sealed class R { public int L, Rt, T, B; }
        static readonly Dictionary<IntPtr, R> map = new Dictionary<IntPtr, R>();
        static long next = 1;
        static readonly object gate = new object();
        static R G(RectOffset o) { lock (gate) return map.TryGetValue(o.m_Ptr, out var r) ? r : (map[o.m_Ptr] = new R()); }

        public static IntPtr InternalCreate() { lock (gate) { var p = (IntPtr)next++; map[p] = new R(); return p; } }
        public static void InternalDestroy(IntPtr ptr) { lock (gate) map.Remove(ptr); }
        public static int get_left(RectOffset self) => G(self).L;
        public static void set_left(RectOffset self, int value) => G(self).L = value;
        public static int get_right(RectOffset self) => G(self).Rt;
        public static void set_right(RectOffset self, int value) => G(self).Rt = value;
        public static int get_top(RectOffset self) => G(self).T;
        public static void set_top(RectOffset self, int value) => G(self).T = value;
        public static int get_bottom(RectOffset self) => G(self).B;
        public static void set_bottom(RectOffset self, int value) => G(self).B = value;
        public static int get_horizontal(RectOffset self) { var r = G(self); return r.L + r.Rt; }
        public static int get_vertical(RectOffset self) { var r = G(self); return r.T + r.B; }
        public static void Add_Injected(RectOffset self, ref Rect rect, out Rect ret) { var r = G(self); ret = new Rect(rect.x - r.L, rect.y - r.T, rect.width + r.L + r.Rt, rect.height + r.T + r.B); }
        public static void Remove_Injected(RectOffset self, ref Rect rect, out Rect ret) { var r = G(self); ret = new Rect(rect.x + r.L, rect.y + r.T, rect.width - r.L - r.Rt, rect.height - r.T - r.B); }
    }
}
