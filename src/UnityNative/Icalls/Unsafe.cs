// Unity.Collections.LowLevel.Unsafe.UnsafeUtility 的 internal call：NativeArray 這類原生容器的記憶體（Input System 的事件緩衝、URP 2D 的 Light2D 頂點…）。
// 配置照 Allocator 都走同一個 aligned malloc（Temp、TempJob、Persistent 的生命週期由 managed 端自己管，原版的分池只是效能）；
// 洩漏偵測只在編輯器與開發版有，這裡永遠是關的。
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;

namespace UnityNative.Icalls
{
    public static unsafe partial class Unity_Collections_LowLevel_Unsafe_UnsafeUtility
    {
        static nuint Align(int alignment)
        {
            uint a = (uint)Math.Max(alignment, 16);
            return (nuint)(BitOperations.IsPow2(a) ? a : BitOperations.RoundUpToPowerOf2(a));
        }

        public static void* Malloc(long size, int alignment, Allocator allocator) => NativeMemory.AlignedAlloc((nuint)Math.Max(size, 1), Align(alignment));
        public static void Free(void* memory, Allocator allocator) { if (memory != null && allocator != Allocator.None) NativeMemory.AlignedFree(memory); }
        public static void* MallocTracked(long size, int alignment, Allocator allocator, int callstacksToSkip) => Malloc(size, alignment, allocator);
        public static void FreeTracked(void* memory, Allocator allocator) => Free(memory, allocator);

        public static void MemCpy(void* destination, void* source, long size) { if (size > 0) NativeMemory.Copy(source, destination, (nuint)size); }
        public static void MemMove(void* destination, void* source, long size) { if (size > 0) NativeMemory.Copy(source, destination, (nuint)size); }
        public static void MemSet(void* destination, byte value, long size) { if (size > 0) NativeMemory.Fill(destination, (nuint)size, value); }
        public static int MemCmp(void* ptr1, void* ptr2, long size) => new ReadOnlySpan<byte>(ptr1, (int)size).SequenceCompareTo(new ReadOnlySpan<byte>(ptr2, (int)size));

        public static void MemCpyReplicate(void* destination, void* source, int size, int count)
        {
            var d = (byte*)destination;
            for (int i = 0; i < count; i++, d += size) NativeMemory.Copy(source, d, (nuint)size);
        }

        public static void MemCpyStride(void* destination, int destinationStride, void* source, int sourceStride, int elementSize, int count)
        {
            byte* d = (byte*)destination, s = (byte*)source;
            for (int i = 0; i < count; i++, d += destinationStride, s += sourceStride) NativeMemory.Copy(s, d, (nuint)elementSize);
        }

        // ───────── 型別 ─────────

        static readonly Dictionary<Type, int> sizes = new Dictionary<Type, int>();
        static readonly Dictionary<Type, bool> unmanaged = new Dictionary<Type, bool>();
        static int SizeOfT<T>() => Unsafe.SizeOf<T>();
        static bool HasRefs<T>() => RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        static readonly MethodInfo sizeOfT = typeof(Unity_Collections_LowLevel_Unsafe_UnsafeUtility).GetMethod(nameof(SizeOfT), BindingFlags.NonPublic | BindingFlags.Static);
        static readonly MethodInfo hasRefs = typeof(Unity_Collections_LowLevel_Unsafe_UnsafeUtility).GetMethod(nameof(HasRefs), BindingFlags.NonPublic | BindingFlags.Static);

        public static int SizeOf(Type type)
        {
            if (!sizes.TryGetValue(type, out var s)) sizes[type] = s = (int)sizeOfT.MakeGenericMethod(type).Invoke(null, null);
            return s;
        }

        public static bool IsUnmanaged(Type type)
        {
            if (!unmanaged.TryGetValue(type, out var u)) unmanaged[type] = u = type.IsValueType && !(bool)hasRefs.MakeGenericMethod(type).Invoke(null, null);
            return u;
        }

        public static bool IsBlittable(Type type) => IsUnmanaged(type);
        public static bool IsValidNativeContainerElementType(Type type) => IsUnmanaged(type);
        public static int GetFieldOffsetInStruct(FieldInfo field) => (int)Marshal.OffsetOf(field.DeclaringType, field.Name);

        // ───────── 釘住 managed 物件 ─────────

        public static void* PinSystemArrayAndGetAddress(object target, out ulong gcHandle)
        {
            var h = GCHandle.Alloc(target, GCHandleType.Pinned);
            gcHandle = (ulong)(long)GCHandle.ToIntPtr(h);
            var a = (Array)target;
            return a.Length == 0 ? (void*)h.AddrOfPinnedObject() : (void*)Marshal.UnsafeAddrOfPinnedArrayElement(a, 0);
        }

        public static void* PinSystemObjectAndGetAddress(object target, out ulong gcHandle)
        {
            var h = GCHandle.Alloc(target, GCHandleType.Pinned);
            gcHandle = (ulong)(long)GCHandle.ToIntPtr(h);
            return (void*)h.AddrOfPinnedObject();
        }

        public static void ReleaseGCObject(ulong gcHandle) { if (gcHandle != 0) GCHandle.FromIntPtr((IntPtr)(long)gcHandle).Free(); }

        // ───────── 洩漏偵測（關著）─────────

        public static int CheckForLeaks() => 0;
        public static int ForgiveLeaks() => 0;
        public static NativeLeakDetectionMode GetLeakDetectionMode() => NativeLeakDetectionMode.Disabled;
        public static void SetLeakDetectionMode(NativeLeakDetectionMode value) { }
        public static int LeakRecord(IntPtr handle, LeakCategory category, int callstacksToSkip) => 0;
        public static int LeakErase(IntPtr handle, LeakCategory category) => 0;

        public static void LogError(string msg, string filename, int linenumber) => Engine.Log(LogType.Error, msg + "（" + filename + ":" + linenumber + "）");
    }
}
