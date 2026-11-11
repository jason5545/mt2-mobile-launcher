// Mesh 的原生半邊：頂點的每個通道（位置、法線、顏色、UV…）存成 float 陣列，子網格存索引。
// uGUI 的 VertexHelper、TMP、Spine 都是走 SetArrayForChannel／SetIndices 這幾個入口；繪製端照 Version 判斷要不要重新上傳。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public sealed class NMesh : NObject, ICloneableAsset, IDestroyable
    {
        public sealed class Channel { public float[] Data; public int Dim; public VertexAttributeFormat Format; }
        public sealed class Sub { public int[] Indices = Array.Empty<int>(); public MeshTopology Topology = MeshTopology.Triangles; public int BaseVertex; }

        public readonly Channel[] Channels = new Channel[14];
        public readonly List<Sub> Subs = new List<Sub> { new Sub() };
        public int VertexCount;
        public Bounds Bounds;
        public IndexFormat IndexFormat = IndexFormat.UInt16;
        public bool Readable = true;
        public int Version;
        public Matrix4x4[] Bindposes = Array.Empty<Matrix4x4>();
        public BoneWeight[] BoneWeights;
        public object Gpu;
        public static Action<NMesh> GpuRelease;
        public void OnDestroyNative() => GpuRelease?.Invoke(this);

        public UnityEngine.Object CloneAsset()
        {
            var m = (Mesh)Native.NewShell(typeof(Mesh));
            var n = Native.Bind(new NMesh { Name = Name }, m);
            n.CopyFrom(this);
            return m;
        }

        public void CopyFrom(NMesh o)
        {
            for (int i = 0; i < Channels.Length; i++)
                Channels[i] = o.Channels[i] == null ? null : new Channel { Data = (float[])o.Channels[i].Data.Clone(), Dim = o.Channels[i].Dim, Format = o.Channels[i].Format };
            Subs.Clear();
            foreach (var s in o.Subs) Subs.Add(new Sub { Indices = (int[])s.Indices.Clone(), Topology = s.Topology, BaseVertex = s.BaseVertex });
            VertexCount = o.VertexCount; Bounds = o.Bounds; IndexFormat = o.IndexFormat;
            Bindposes = (Matrix4x4[])o.Bindposes.Clone();
            Version++;
        }

        public void Clear(bool keepLayout)
        {
            for (int i = 0; i < Channels.Length; i++) Channels[i] = keepLayout && Channels[i] != null ? new Channel { Data = Array.Empty<float>(), Dim = Channels[i].Dim, Format = Channels[i].Format } : null;
            Subs.Clear();
            Subs.Add(new Sub());
            VertexCount = 0;
            Bounds = default;
            Version++;
        }

        public void RecalculateBounds()
        {
            var p = Channels[(int)VertexAttribute.Position];
            if (p == null || VertexCount == 0) { Bounds = default; return; }
            int d = p.Dim;
            Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), mx = -mn;
            for (int i = 0; i < VertexCount; i++)
            {
                float x = p.Data[i * d], y = d > 1 ? p.Data[i * d + 1] : 0, z = d > 2 ? p.Data[i * d + 2] : 0;
                if (x < mn.x) mn.x = x; if (y < mn.y) mn.y = y; if (z < mn.z) mn.z = z;
                if (x > mx.x) mx.x = x; if (y > mx.y) mx.y = y; if (z > mx.z) mx.z = z;
            }
            Bounds = new Bounds((mn + mx) * 0.5f, mx - mn);
        }

        // ───────── 通道 ─────────

        public void SetChannel(VertexAttribute ch, VertexAttributeFormat format, int dim, Array values, int start, int count)
        {
            if (values == null || count <= 0)
            {
                Channels[(int)ch] = null;
                if (ch == VertexAttribute.Position) VertexCount = 0;
                Version++;
                return;
            }
            var data = new float[count * dim];
            switch (values)
            {
                case Vector3[] v3: for (int i = 0; i < count; i++) { var v = v3[start + i]; data[i * dim] = v.x; if (dim > 1) data[i * dim + 1] = v.y; if (dim > 2) data[i * dim + 2] = v.z; } break;
                case Vector2[] v2: for (int i = 0; i < count; i++) { var v = v2[start + i]; data[i * dim] = v.x; if (dim > 1) data[i * dim + 1] = v.y; } break;
                case Vector4[] v4: for (int i = 0; i < count; i++) { var v = v4[start + i]; data[i * dim] = v.x; if (dim > 1) data[i * dim + 1] = v.y; if (dim > 2) data[i * dim + 2] = v.z; if (dim > 3) data[i * dim + 3] = v.w; } break;
                case Color[] c: for (int i = 0; i < count; i++) { var v = c[start + i]; data[i * 4] = v.r; data[i * 4 + 1] = v.g; data[i * 4 + 2] = v.b; data[i * 4 + 3] = v.a; } break;
                case Color32[] c32: for (int i = 0; i < count; i++) { var v = c32[start + i]; data[i * 4] = v.r / 255f; data[i * 4 + 1] = v.g / 255f; data[i * 4 + 2] = v.b / 255f; data[i * 4 + 3] = v.a / 255f; } break;
                case float[] f: Array.Copy(f, start * dim, data, 0, count * dim); break;
                default:
                    // 其他結構陣列（自訂頂點格式）：照記憶體逐個 float 讀
                    var h = GCHandle.Alloc(values, GCHandleType.Pinned);
                    try
                    {
                        int esz = Marshal.SizeOf(values.GetType().GetElementType());
                        unsafe
                        {
                            byte* b = (byte*)h.AddrOfPinnedObject() + start * esz;
                            for (int i = 0; i < count; i++) for (int k = 0; k < dim; k++) data[i * dim + k] = format == VertexAttributeFormat.UNorm8 ? b[i * esz + k] / 255f : ((float*)(b + i * esz))[k];
                        }
                    }
                    finally { h.Free(); }
                    break;
            }
            Channels[(int)ch] = new Channel { Data = data, Dim = dim, Format = format };
            if (ch == VertexAttribute.Position) VertexCount = count;
            Version++;
        }

        public Array GetChannel(VertexAttribute ch, VertexAttributeFormat format, int dim)
        {
            var c = Channels[(int)ch];
            int n = VertexCount;
            if (format == VertexAttributeFormat.UNorm8 && dim == 4)
            {
                var r = new Color32[c == null ? 0 : n];
                for (int i = 0; i < r.Length; i++) r[i] = new Color32(B(c, i, 0), B(c, i, 1), B(c, i, 2), B(c, i, 3));
                return r;
            }
            if (c == null) return dim switch { 2 => Array.Empty<Vector2>(), 3 => Array.Empty<Vector3>(), 4 => ch == VertexAttribute.Color ? Array.Empty<Color>() : Array.Empty<Vector4>(), _ => Array.Empty<float>() };
            switch (dim)
            {
                case 2: { var r = new Vector2[n]; for (int i = 0; i < n; i++) r[i] = new Vector2(F(c, i, 0), F(c, i, 1)); return r; }
                case 3: { var r = new Vector3[n]; for (int i = 0; i < n; i++) r[i] = new Vector3(F(c, i, 0), F(c, i, 1), F(c, i, 2)); return r; }
                case 4:
                    if (ch == VertexAttribute.Color) { var r = new Color[n]; for (int i = 0; i < n; i++) r[i] = new Color(F(c, i, 0), F(c, i, 1), F(c, i, 2), F(c, i, 3)); return r; }
                    else { var r = new Vector4[n]; for (int i = 0; i < n; i++) r[i] = new Vector4(F(c, i, 0), F(c, i, 1), F(c, i, 2), F(c, i, 3)); return r; }
                default: { var r = new float[n]; for (int i = 0; i < n; i++) r[i] = F(c, i, 0); return r; }
            }
        }

        static float F(Channel c, int i, int k) => k < c.Dim && i * c.Dim + k < c.Data.Length ? c.Data[i * c.Dim + k] : (k == 3 ? 1f : 0f);
        static byte B(Channel c, int i, int k) => (byte)Math.Clamp((int)(F(c, i, k) * 255f + 0.5f), 0, 255);

        public void SetIndices(int sub, MeshTopology topo, Array indices, int start, int size, int baseVertex)
        {
            // submesh -1（mesh.triangles = …）：整個網格只剩一個子網格
            if (sub < 0) { Subs.Clear(); sub = 0; }
            while (Subs.Count <= sub) Subs.Add(new Sub());
            var arr = new int[size];
            switch (indices)
            {
                case int[] ia: Array.Copy(ia, start, arr, 0, size); break;
                case ushort[] ua: for (int i = 0; i < size; i++) arr[i] = ua[start + i]; break;
                case uint[] uia: for (int i = 0; i < size; i++) arr[i] = (int)uia[start + i]; break;
                case null: break;
                default: for (int i = 0; i < size; i++) arr[i] = Convert.ToInt32(indices.GetValue(start + i)); break;
            }
            Subs[sub] = new Sub { Indices = arr, Topology = topo, BaseVertex = baseVertex };
            Version++;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Mesh
    {
        static NMesh M(Mesh m) => Native.Self<NMesh>(m);

        public static void Internal_Create(Mesh mono) => Native.Bind(new NMesh(), mono);
        public static Mesh FromInstanceID(int id) => Native.ById(id)?.Managed as Mesh;
        public static IndexFormat get_indexFormat(Mesh self) => M(self).IndexFormat;
        public static void set_indexFormat(Mesh self, IndexFormat value) => M(self).IndexFormat = value;
        public static uint GetTotalIndexCount(Mesh self) { uint n = 0; foreach (var s in M(self).Subs) n += (uint)s.Indices.Length; return n; }
        public static uint GetIndexStartImpl(Mesh self, int submesh) { uint n = 0; var m = M(self); for (int i = 0; i < submesh && i < m.Subs.Count; i++) n += (uint)m.Subs[i].Indices.Length; return n; }
        public static uint GetIndexCountImpl(Mesh self, int submesh) => (uint)M(self).Subs[submesh].Indices.Length;
        public static uint GetTrianglesCountImpl(Mesh self, int submesh) => (uint)M(self).Subs[submesh].Indices.Length;
        public static uint GetBaseVertexImpl(Mesh self, int submesh) => (uint)M(self).Subs[submesh].BaseVertex;

        static int[] Indices(NMesh m, int submesh, bool applyBaseVertex)
        {
            if (submesh < 0)
            {
                // 全部子網格接起來
                var all = new List<int>();
                for (int i = 0; i < m.Subs.Count; i++) all.AddRange(Indices(m, i, applyBaseVertex));
                return all.ToArray();
            }
            if ((uint)submesh >= (uint)m.Subs.Count) return Array.Empty<int>();
            var s = m.Subs[submesh];
            var r = (int[])s.Indices.Clone();
            if (applyBaseVertex && s.BaseVertex != 0) for (int i = 0; i < r.Length; i++) r[i] += s.BaseVertex;
            return r;
        }

        public static int[] GetTrianglesImpl(Mesh self, int submesh, bool applyBaseVertex) => Indices(M(self), submesh, applyBaseVertex);
        public static int[] GetIndicesImpl(Mesh self, int submesh, bool applyBaseVertex) => Indices(M(self), submesh, applyBaseVertex);
        public static void SetIndicesImpl(Mesh self, int submesh, MeshTopology topology, IndexFormat indicesFormat, Array indices, int arrayStart, int arraySize, bool calculateBounds, int baseVertex)
        {
            var m = M(self);
            m.SetIndices(submesh, topology, indices, arrayStart, arraySize, baseVertex);
            if (calculateBounds) m.RecalculateBounds();
        }
        public static void GetTrianglesNonAllocImpl(Mesh self, int[] values, int submesh, bool applyBaseVertex) => Array.Copy(Indices(M(self), submesh, applyBaseVertex), values, Math.Min(values.Length, Indices(M(self), submesh, applyBaseVertex).Length));
        public static void GetIndicesNonAllocImpl(Mesh self, int[] values, int submesh, bool applyBaseVertex) => GetTrianglesNonAllocImpl(self, values, submesh, applyBaseVertex);
        public static void GetTrianglesNonAllocImpl16(Mesh self, ushort[] values, int submesh, bool applyBaseVertex)
        {
            var a = Indices(M(self), submesh, applyBaseVertex);
            for (int i = 0; i < a.Length && i < values.Length; i++) values[i] = (ushort)a[i];
        }
        public static void GetIndicesNonAllocImpl16(Mesh self, ushort[] values, int submesh, bool applyBaseVertex) => GetTrianglesNonAllocImpl16(self, values, submesh, applyBaseVertex);
        public static void PrintErrorCantAccessChannel(Mesh self, VertexAttribute ch) => Engine.Log(LogType.Error, "Not allowed to access " + ch + " on mesh '" + M(self).Name + "'");
        public static bool HasVertexAttribute(Mesh self, VertexAttribute attr) => M(self).Channels[(int)attr] != null;
        public static int GetVertexAttributeDimension(Mesh self, VertexAttribute attr) => M(self).Channels[(int)attr]?.Dim ?? 0;
        public static VertexAttributeFormat GetVertexAttributeFormat(Mesh self, VertexAttribute attr) => M(self).Channels[(int)attr]?.Format ?? VertexAttributeFormat.Float32;
        public static int GetVertexAttributeStream(Mesh self, VertexAttribute attr) => 0;
        public static int GetVertexAttributeOffset(Mesh self, VertexAttribute attr) => 0;
        public static void SetArrayForChannelImpl(Mesh self, VertexAttribute channel, VertexAttributeFormat format, int dim, Array values, int arraySize, int valuesStart, int valuesCount, MeshUpdateFlags flags)
        {
            var m = M(self);
            m.SetChannel(channel, format, dim, values, valuesStart, valuesCount);
            if (channel == VertexAttribute.Position && (flags & MeshUpdateFlags.DontRecalculateBounds) == 0) m.RecalculateBounds();
        }
        public static Array GetAllocArrayFromChannelImpl(Mesh self, VertexAttribute channel, VertexAttributeFormat format, int dim) => M(self).GetChannel(channel, format, dim);
        public static void GetArrayFromChannelImpl(Mesh self, VertexAttribute channel, VertexAttributeFormat format, int dim, Array values)
        {
            var src = M(self).GetChannel(channel, format, dim);
            Array.Copy(src, values, Math.Min(src.Length, values.Length));
        }
        public static int get_vertexBufferCount(Mesh self) => 1;
        public static int get_blendShapeCount(Mesh self) => 0;
        public static bool HasBoneWeights(Mesh self) => M(self).BoneWeights != null;
        public static BoneWeight[] GetBoneWeightsImpl(Mesh self) => M(self).BoneWeights ?? Array.Empty<BoneWeight>();
        public static void SetBoneWeightsImpl(Mesh self, BoneWeight[] weights) { M(self).BoneWeights = weights; M(self).Version++; }
        public static int get_bindposeCount(Mesh self) => M(self).Bindposes.Length;
        public static Matrix4x4[] get_bindposes(Mesh self) => (Matrix4x4[])M(self).Bindposes.Clone();
        public static void set_bindposes(Mesh self, Matrix4x4[] value) => M(self).Bindposes = value ?? Array.Empty<Matrix4x4>();
        public static bool get_isReadable(Mesh self) => true;
        public static bool get_canAccess(Mesh self) => true;
        public static int get_vertexCount(Mesh self) => M(self).VertexCount;
        public static int get_subMeshCount(Mesh self) => M(self).Subs.Count;
        public static void set_subMeshCount(Mesh self, int value)
        {
            var m = M(self);
            while (m.Subs.Count < value) m.Subs.Add(new NMesh.Sub());
            while (m.Subs.Count > value && m.Subs.Count > 0) m.Subs.RemoveAt(m.Subs.Count - 1);
            m.Version++;
        }
        public static void ClearImpl(Mesh self, bool keepVertexLayout) => M(self).Clear(keepVertexLayout);
        public static void RecalculateBoundsImpl(Mesh self, MeshUpdateFlags flags) => M(self).RecalculateBounds();
        public static void RecalculateNormalsImpl(Mesh self, MeshUpdateFlags flags) { }
        public static void RecalculateTangentsImpl(Mesh self, MeshUpdateFlags flags) { }
        public static void MarkDynamicImpl(Mesh self) { }
        public static void MarkModified(Mesh self) => M(self).Version++;
        public static void UploadMeshDataImpl(Mesh self, bool markNoLongerReadable) => M(self).Version++;
        public static MeshTopology GetTopologyImpl(Mesh self, int submesh) => M(self).Subs[submesh].Topology;
        public static void OptimizeImpl(Mesh self) { }
        public static void OptimizeIndexBuffersImpl(Mesh self) { }
        public static void OptimizeReorderVertexBufferImpl(Mesh self) { }
        public static void SetSubMesh_Injected(Mesh self, int index, ref SubMeshDescriptor desc, MeshUpdateFlags flags)
        {
            var m = M(self);
            while (m.Subs.Count <= index) m.Subs.Add(new NMesh.Sub());
            m.Subs[index].Topology = desc.topology;
            m.Subs[index].BaseVertex = desc.baseVertex;
            // SetIndexBufferParams／SetIndexBufferData 寫的索引緩衝：子網格是其中一段（MeshBuffers.cs）
            if (MeshLayouts.IndexBuffer(m) is { } buf)
            {
                int start = Math.Clamp(desc.indexStart, 0, buf.Length), count = Math.Clamp(desc.indexCount, 0, buf.Length - start);
                var idx = new int[count];
                Array.Copy(buf, start, idx, 0, count);
                m.Subs[index].Indices = idx;
            }
            m.Version++;
        }
        public static void GetSubMesh_Injected(Mesh self, int index, out SubMeshDescriptor ret)
        {
            var s = M(self).Subs[index];
            ret = new SubMeshDescriptor(0, s.Indices.Length, s.Topology) { baseVertex = s.BaseVertex };
        }
        public static void get_bounds_Injected(Mesh self, out Bounds ret) => ret = M(self).Bounds;
        public static void set_bounds_Injected(Mesh self, ref Bounds value) => M(self).Bounds = value;

        public static void CombineMeshesImpl(Mesh self, CombineInstance[] combine, bool mergeSubMeshes, bool useMatrices, bool hasLightmapData)
        {
            var m = M(self);
            var pos = new List<Vector3>(); var uv = new List<Vector2>(); var col = new List<Color>(); var idx = new List<int>();
            foreach (var ci in combine)
            {
                var src = Native.Of<NMesh>(ci.mesh);
                if (src == null) continue;
                int b = pos.Count;
                var p = (Vector3[])src.GetChannel(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
                foreach (var v in p) pos.Add(useMatrices ? ci.transform.MultiplyPoint3x4(v) : v);
                uv.AddRange((Vector2[])src.GetChannel(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
                col.AddRange((Color[])src.GetChannel(VertexAttribute.Color, VertexAttributeFormat.Float32, 4));
                foreach (var i in Indices(src, ci.subMeshIndex, true)) idx.Add(b + i);
            }
            m.Clear(false);
            m.SetChannel(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, pos.ToArray(), 0, pos.Count);
            if (uv.Count == pos.Count) m.SetChannel(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, uv.ToArray(), 0, uv.Count);
            if (col.Count == pos.Count) m.SetChannel(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, col.ToArray(), 0, col.Count);
            m.SetIndices(0, MeshTopology.Triangles, idx.ToArray(), 0, idx.Count, 0);
            m.RecalculateBounds();
        }
    }
}

namespace UnityNative
{
    /// <summary>網格素材：匯入器的 meshes/&lt;guid&gt;.json 與 builtin.json 的內建網格（channels：通道名 → {dimension, values}）</summary>
    public static class MeshAssets
    {
        static readonly Dictionary<string, UnityEngine.Rendering.VertexAttribute> names = new Dictionary<string, UnityEngine.Rendering.VertexAttribute>
        {
            ["position"] = UnityEngine.Rendering.VertexAttribute.Position, ["normal"] = UnityEngine.Rendering.VertexAttribute.Normal,
            ["tangent"] = UnityEngine.Rendering.VertexAttribute.Tangent, ["color"] = UnityEngine.Rendering.VertexAttribute.Color,
            ["uv0"] = UnityEngine.Rendering.VertexAttribute.TexCoord0, ["uv"] = UnityEngine.Rendering.VertexAttribute.TexCoord0,
            ["uv1"] = UnityEngine.Rendering.VertexAttribute.TexCoord1, ["uv2"] = UnityEngine.Rendering.VertexAttribute.TexCoord2,
            ["uv3"] = UnityEngine.Rendering.VertexAttribute.TexCoord3, ["uv4"] = UnityEngine.Rendering.VertexAttribute.TexCoord4,
            ["uv5"] = UnityEngine.Rendering.VertexAttribute.TexCoord5, ["uv6"] = UnityEngine.Rendering.VertexAttribute.TexCoord6,
            ["uv7"] = UnityEngine.Rendering.VertexAttribute.TexCoord7,
        };

        public static void Register()
        {
            Assets.Loaders["mesh"] = (g, f, r, w) => Load(Assets.ReadAssetJson("meshes/" + g + ".json"), g);
            Assets.Loaders["builtin:Mesh"] = (g, f, r, w) => Load(r?["meshAsset"] as System.Text.Json.Nodes.JsonObject, g + ":" + f);
        }

        static Mesh Load(System.Text.Json.Nodes.JsonObject d, string src)
        {
            if (d == null) return null;
            var m = (Mesh)Native.NewShell(typeof(Mesh));
            var n = Native.Bind(new NMesh { Name = d["m_Name"]?.ToString() ?? "", Source = src }, m, true);
            int vc = (int)Serializer.D(d["vertexCount"]);
            if (d["channels"] is System.Text.Json.Nodes.JsonObject chs)
                foreach (var kv in chs)
                {
                    if (!names.TryGetValue(kv.Key, out var attr) || kv.Value?["values"] is not System.Text.Json.Nodes.JsonArray vals) continue;
                    int dim = (int)Serializer.D(kv.Value["dimension"]);
                    var data = new float[vals.Count];
                    for (int i = 0; i < data.Length; i++) data[i] = (float)Serializer.D(vals[i]);
                    n.Channels[(int)attr] = new NMesh.Channel { Data = data, Dim = dim, Format = UnityEngine.Rendering.VertexAttributeFormat.Float32 };
                }
            n.VertexCount = vc;
            var idx = d["indices"] as System.Text.Json.Nodes.JsonArray;
            var all = new int[idx?.Count ?? 0];
            for (int i = 0; i < all.Length; i++) all[i] = (int)Serializer.D(idx[i]);
            n.Subs.Clear();
            if (d["m_SubMeshes"] is System.Text.Json.Nodes.JsonArray subs && subs.Count > 0)
            {
                bool u32 = Serializer.D(d["m_IndexFormat"]) == 1;
                foreach (var s in subs)
                {
                    int first = (int)Serializer.D(s["firstByte"]) / (u32 ? 4 : 2), count = (int)Serializer.D(s["indexCount"]);
                    var ia = new int[Math.Max(0, Math.Min(count, all.Length - first))];
                    Array.Copy(all, first, ia, 0, ia.Length);
                    n.Subs.Add(new NMesh.Sub { Indices = ia, Topology = (MeshTopology)(int)Serializer.D(s["topology"]), BaseVertex = (int)Serializer.D(s["baseVertex"]) });
                }
            }
            else n.Subs.Add(new NMesh.Sub { Indices = all });
            if (d["m_LocalAABB"] is System.Text.Json.Nodes.JsonObject aabb)
                n.Bounds = (Bounds)Serializer.Convert(aabb, typeof(Bounds), null);
            else n.RecalculateBounds();
            return m;
        }
    }
}
