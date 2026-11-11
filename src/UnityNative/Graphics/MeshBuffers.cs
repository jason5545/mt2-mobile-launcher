// Mesh 的進階 API（Mesh.SetVertexBufferParams／SetVertexBufferData、SetIndexBufferParams／SetIndexBufferData、NativeArray 的 SetIndices／SetVertices）：
// URP 的 LightUtility 用它產 2D 光的網格。引擎裡網格照舊一個通道一個 float 陣列，這裡把交錯的頂點資料拆進通道。
// 版面照 Unity 的規則（Mesh.SetVertexBufferParams 手冊）：同一個 stream 裡的屬性照 VertexAttribute 列舉的順序排、緊密排列，
// 跟傳進來的描述順序無關；每個屬性的大小是 4 位元組的倍數（格式 × 維度）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public static unsafe class MeshLayouts
    {
        public sealed class Attr { public VertexAttribute A; public VertexAttributeFormat F; public int Dim, Stream, Offset; }

        public static int FormatSize(VertexAttributeFormat f) => f switch
        {
            VertexAttributeFormat.Float32 or VertexAttributeFormat.UInt32 or VertexAttributeFormat.SInt32 => 4,
            VertexAttributeFormat.Float16 or VertexAttributeFormat.UNorm16 or VertexAttributeFormat.SNorm16 or VertexAttributeFormat.UInt16 or VertexAttributeFormat.SInt16 => 2,
            _ => 1,
        };

        public static float Read(byte* p, VertexAttributeFormat f) => f switch
        {
            VertexAttributeFormat.Float32 => *(float*)p,
            VertexAttributeFormat.Float16 => (float)*(Half*)p,
            VertexAttributeFormat.UNorm8 => *p / 255f,
            VertexAttributeFormat.SNorm8 => Math.Max(*(sbyte*)p / 127f, -1f),
            VertexAttributeFormat.UNorm16 => *(ushort*)p / 65535f,
            VertexAttributeFormat.SNorm16 => Math.Max(*(short*)p / 32767f, -1f),
            VertexAttributeFormat.UInt8 => *p,
            VertexAttributeFormat.SInt8 => *(sbyte*)p,
            VertexAttributeFormat.UInt16 => *(ushort*)p,
            VertexAttributeFormat.SInt16 => *(short*)p,
            VertexAttributeFormat.UInt32 => *(uint*)p,
            VertexAttributeFormat.SInt32 => *(int*)p,
            _ => 0,
        };

        static readonly Dictionary<NMesh, List<Attr>> layouts = new Dictionary<NMesh, List<Attr>>();
        static readonly Dictionary<NMesh, int[]> indexBuffers = new Dictionary<NMesh, int[]>();

        public static List<Attr> Of(NMesh m) => layouts.TryGetValue(m, out var l) ? l : null;
        public static int[] IndexBuffer(NMesh m) => indexBuffers.TryGetValue(m, out var b) ? b : null;
        public static void Forget(NMesh m) { layouts.Remove(m); indexBuffers.Remove(m); }

        public static void SetParams(NMesh m, int vertexCount, VertexAttributeDescriptor* attrs, int count)
        {
            var list = new List<Attr>();
            for (int i = 0; i < count; i++) list.Add(new Attr { A = attrs[i].attribute, F = attrs[i].format, Dim = attrs[i].dimension, Stream = attrs[i].stream });
            list.Sort((a, b) => a.Stream != b.Stream ? a.Stream.CompareTo(b.Stream) : a.A.CompareTo(b.A));
            var offs = new Dictionary<int, int>();
            foreach (var a in list)
            {
                offs.TryGetValue(a.Stream, out var o);
                a.Offset = o;
                offs[a.Stream] = o + FormatSize(a.F) * a.Dim;
            }
            layouts[m] = list;
            for (int i = 0; i < m.Channels.Length; i++) m.Channels[i] = null;
            foreach (var a in list) m.Channels[(int)a.A] = new NMesh.Channel { Data = new float[vertexCount * a.Dim], Dim = a.Dim, Format = a.F };
            m.VertexCount = vertexCount;
            m.Version++;
        }

        public static int Stride(NMesh m, int stream)
        {
            int s = 0;
            if (Of(m) is { } l) foreach (var a in l) if (a.Stream == stream) s = Math.Max(s, a.Offset + FormatSize(a.F) * a.Dim);
            return s;
        }

        /// <summary>SetVertexBufferData：data 的第 dataStart 個元素起 count 個，寫到這個 stream 的第 meshStart 個頂點起</summary>
        public static void SetData(NMesh m, int stream, byte* data, int dataStart, int meshStart, int count, int elemSize)
        {
            var l = Of(m);
            if (l == null) return;
            int stride = Stride(m, stream);
            if (elemSize <= 0) elemSize = stride;
            byte* src = data + (long)dataStart * elemSize;
            // 元素大小跟 stride 不同時（例如傳 byte 陣列），照位元組連續讀
            long total = (long)count * elemSize;
            int verts = stride > 0 ? (int)(total / stride) : 0;
            foreach (var a in l)
            {
                if (a.Stream != stream) continue;
                var ch = m.Channels[(int)a.A];
                if (ch == null) continue;
                int fs = FormatSize(a.F);
                for (int v = 0; v < verts && meshStart + v < m.VertexCount; v++)
                {
                    byte* p = src + (long)v * stride + a.Offset;
                    for (int k = 0; k < a.Dim; k++) ch.Data[(meshStart + v) * a.Dim + k] = Read(p + k * fs, a.F);
                }
            }
            m.Version++;
        }

        public static void SetIndexParams(NMesh m, int count, IndexFormat format)
        {
            indexBuffers[m] = new int[Math.Max(0, count)];
            m.IndexFormat = format;
        }

        public static void SetIndexData(NMesh m, byte* data, int dataStart, int meshStart, int count, int elemSize)
        {
            if (!indexBuffers.TryGetValue(m, out var buf)) return;
            for (int i = 0; i < count && meshStart + i < buf.Length; i++)
            {
                byte* p = data + (long)(dataStart + i) * elemSize;
                buf[meshStart + i] = elemSize == 2 ? *(ushort*)p : elemSize == 1 ? *p : *(int*)p;
            }
            m.Version++;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Mesh
    {
        public static void SetVertexBufferParamsFromArray(Mesh self, int vertexCount, VertexAttributeDescriptor[] attributes)
        {
            fixed (VertexAttributeDescriptor* p = attributes) MeshLayouts.SetParams(M(self), vertexCount, p, attributes?.Length ?? 0);
        }

        public static void SetVertexBufferParamsFromPtr(Mesh self, int vertexCount, IntPtr attributesPtr, int attributesCount)
            => MeshLayouts.SetParams(M(self), vertexCount, (VertexAttributeDescriptor*)attributesPtr, attributesCount);

        public static void InternalSetVertexBufferData(Mesh self, int stream, IntPtr data, int dataStart, int meshBufferStart, int count, int elemSize, MeshUpdateFlags flags)
        {
            var m = M(self);
            MeshLayouts.SetData(m, stream, (byte*)data, dataStart, meshBufferStart, count, elemSize);
            if ((flags & MeshUpdateFlags.DontRecalculateBounds) == 0) m.RecalculateBounds();
        }

        public static void InternalSetVertexBufferDataFromArray(Mesh self, int stream, Array data, int dataStart, int meshBufferStart, int count, int elemSize, MeshUpdateFlags flags)
        {
            if (data == null) return;
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try { InternalSetVertexBufferData(self, stream, h.AddrOfPinnedObject(), dataStart, meshBufferStart, count, elemSize, flags); }
            finally { h.Free(); }
        }

        public static int GetVertexBufferStride(Mesh self, int stream) => MeshLayouts.Stride(M(self), stream);

        public static int GetVertexAttributeCountImpl(Mesh self)
        {
            int n = 0;
            foreach (var c in M(self).Channels) if (c != null) n++;
            return n;
        }

        static VertexAttributeDescriptor[] Descriptors(NMesh m)
        {
            var list = new List<VertexAttributeDescriptor>();
            var l = MeshLayouts.Of(m);
            if (l != null) foreach (var a in l) list.Add(new VertexAttributeDescriptor(a.A, a.F, a.Dim, a.Stream));
            else for (int i = 0; i < m.Channels.Length; i++) if (m.Channels[i] is { } c) list.Add(new VertexAttributeDescriptor((VertexAttribute)i, c.Format, c.Dim, 0));
            return list.ToArray();
        }

        public static Array GetVertexAttributesAlloc(Mesh self) => Descriptors(M(self));
        public static int GetVertexAttributesArray(Mesh self, VertexAttributeDescriptor[] attributes)
        {
            var d = Descriptors(M(self));
            int n = Math.Min(d.Length, attributes?.Length ?? 0);
            Array.Copy(d, attributes, n);
            return d.Length;
        }
        public static int GetVertexAttributesList(Mesh self, List<VertexAttributeDescriptor> attributes)
        {
            var d = Descriptors(M(self));
            attributes.Clear();
            attributes.AddRange(d);
            return d.Length;
        }

        public static void SetIndexBufferParams(Mesh self, int indexCount, IndexFormat format) => MeshLayouts.SetIndexParams(M(self), indexCount, format);

        public static void InternalSetIndexBufferData(Mesh self, IntPtr data, int dataStart, int meshBufferStart, int count, int elemSize, MeshUpdateFlags flags)
            => MeshLayouts.SetIndexData(M(self), (byte*)data, dataStart, meshBufferStart, count, elemSize);

        public static void InternalSetIndexBufferDataFromArray(Mesh self, Array data, int dataStart, int meshBufferStart, int count, int elemSize, MeshUpdateFlags flags)
        {
            if (data == null) return;
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try { InternalSetIndexBufferData(self, h.AddrOfPinnedObject(), dataStart, meshBufferStart, count, elemSize, flags); }
            finally { h.Free(); }
        }

        /// <summary>Mesh.SetIndices(NativeArray)</summary>
        public static void SetIndicesNativeArrayImpl(Mesh self, int submesh, MeshTopology topology, IndexFormat indicesFormat, IntPtr indices, int arrayStart, int arraySize, bool calculateBounds, int baseVertex)
        {
            var m = M(self);
            var arr = new int[Math.Max(0, arraySize)];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = indicesFormat == IndexFormat.UInt16 ? ((ushort*)indices)[arrayStart + i] : ((int*)indices)[arrayStart + i];
            m.SetIndices(submesh, topology, arr, 0, arr.Length, baseVertex);
            if (calculateBounds) m.RecalculateBounds();
        }

        /// <summary>Mesh.SetVertices／SetUVs／SetColors(NativeArray)</summary>
        public static void SetNativeArrayForChannelImpl(Mesh self, VertexAttribute channel, VertexAttributeFormat format, int dim, IntPtr values, int arraySize, int valuesStart, int valuesCount, MeshUpdateFlags flags)
        {
            var m = M(self);
            int fs = MeshLayouts.FormatSize(format);
            var data = new float[valuesCount * dim];
            byte* p = (byte*)values + (long)valuesStart * fs * dim;
            for (int i = 0; i < valuesCount; i++) for (int k = 0; k < dim; k++) data[i * dim + k] = MeshLayouts.Read(p + (i * dim + k) * fs, format);
            MeshLayouts.Forget(m);
            m.SetChannel(channel, VertexAttributeFormat.Float32, dim, data, 0, valuesCount);
            if (channel == VertexAttribute.Position && (flags & MeshUpdateFlags.DontRecalculateBounds) == 0) m.RecalculateBounds();
        }
    }
}
