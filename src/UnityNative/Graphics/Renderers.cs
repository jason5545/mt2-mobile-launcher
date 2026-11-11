// Renderer 系列的原生半邊：Renderer（材質、排序、MaterialPropertyBlock）、SpriteRenderer、MeshFilter、MeshRenderer、
// SortingLayer、SortingGroup。繪製端每格從 NRenderer.All 收集要畫的東西。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public class NRenderer : NBehaviour, IRemap
    {
        public Material[] Shared = Array.Empty<Material>();
        public int SortingLayerID, SortingOrder;
        public PropertySheet Block;
        public readonly Dictionary<int, PropertySheet> BlockPerMaterial = new Dictionary<int, PropertySheet>();
        public Bounds? BoundsOverride, LocalBoundsOverride;
        public bool Visible = true;
        public int SortingGroupID = -1, SortingGroupOrder;
        /// <summary>烘焙光照圖：LightmapSettings.lightmaps 的索引（−1 沒有）與光照圖座標的 scale／offset（Renderer.lightmapIndex／lightmapScaleOffset）。
        /// 序列化的 m_LightmapIndex 沒有光照圖時是 65535（0xFFFF），讀成 −1（同 TS 版）</summary>
        public int LightmapIndex = -1;
        public Vector4 LightmapST = new Vector4(1, 1, 0, 0);
        public static readonly List<NRenderer> All = new List<NRenderer>();

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Enabled = d["m_Enabled"] == null || Serializer.D(d["m_Enabled"]) != 0;
            if (d["m_Materials"] is JsonArray ms)
            {
                Shared = new Material[ms.Count];
                for (int i = 0; i < ms.Count; i++) Shared[i] = Serializer.Ref(ms[i], typeof(Material), ctx) as Material;
            }
            SortingLayerID = (int)Serializer.D(d["m_SortingLayerID"]);
            SortingOrder = (int)Serializer.D(d["m_SortingOrder"]);
            if (d["m_LightmapIndex"] != null) { int li = (int)Serializer.D(d["m_LightmapIndex"]); LightmapIndex = li >= 0xFFFE ? -1 : li; }
            if (d["m_LightmapTilingOffset"] != null) LightmapST = (Vector4)Serializer.Convert(d["m_LightmapTilingOffset"], typeof(Vector4), null);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NRenderer)src;
            Shared = (Material[])s.Shared.Clone();
            SortingLayerID = s.SortingLayerID; SortingOrder = s.SortingOrder;
            LightmapIndex = s.LightmapIndex; LightmapST = s.LightmapST;
            if (s.Block != null) { Block = new PropertySheet(); Block.CopyFrom(s.Block); }
        }

        public virtual void Remap(Func<UnityEngine.Object, UnityEngine.Object> f) { }

        public override void OnActiveChanged()
        {
            bool on = Alive && Enabled && Go != null && Go.ActiveInHierarchy;
            if (on && !All.Contains(this)) All.Add(this);
            else if (!on) All.Remove(this);
        }
        public override void OnDestroyNative() => All.Remove(this);

        public virtual Bounds LocalBounds => default;

        public Bounds WorldBounds
        {
            get
            {
                if (BoundsOverride.HasValue) return BoundsOverride.Value;
                var lb = LocalBoundsOverride ?? LocalBounds;
                var m = Tr.LocalToWorld;
                var c = MathUtil.MulPoint(m, lb.center);
                var e = lb.extents;
                var ex = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                return new Bounds(c, ex * 2f);
            }
        }
    }

    public sealed class NSpriteRenderer : NRenderer
    {
        public Sprite Sprite;
        public Color Color = Color.white;
        public bool FlipX, FlipY;
        public SpriteDrawMode DrawMode;
        public Vector2 Size = Vector2.one;
        public float AdaptiveThreshold = 0.5f;
        public SpriteTileMode TileMode;
        public SpriteMaskInteraction MaskInteraction;
        public SpriteSortPoint SortPoint;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            base.Load(d, ctx);
            Sprite = Serializer.Ref(d["m_Sprite"], typeof(Sprite), ctx) as Sprite;
            if (d["m_Color"] != null) Color = (Color)Serializer.Convert(d["m_Color"], typeof(Color), null);
            FlipX = Serializer.D(d["m_FlipX"]) != 0;
            FlipY = Serializer.D(d["m_FlipY"]) != 0;
            DrawMode = (SpriteDrawMode)(int)Serializer.D(d["m_DrawMode"]);
            if (d["m_Size"] != null) Size = (Vector2)Serializer.Convert(d["m_Size"], typeof(Vector2), null);
            TileMode = (SpriteTileMode)(int)Serializer.D(d["m_SpriteTileMode"]);
            MaskInteraction = (SpriteMaskInteraction)(int)Serializer.D(d["m_MaskInteraction"]);
            SortPoint = (SpriteSortPoint)(int)Serializer.D(d["m_SpriteSortPoint"]);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NSpriteRenderer)src;
            Sprite = s.Sprite; Color = s.Color; FlipX = s.FlipX; FlipY = s.FlipY; DrawMode = s.DrawMode; Size = s.Size; TileMode = s.TileMode;
            MaskInteraction = s.MaskInteraction; SortPoint = s.SortPoint;
        }

        public override Bounds LocalBounds
        {
            get
            {
                var s = Native.Of<NSprite>(Sprite);
                if (s == null) return default;
                // Sliced／Tiled：size 照 pivot 擺（同繪製端的九宮格）
                if (DrawMode != SpriteDrawMode.Simple) return new Bounds(new Vector3((0.5f - s.Pivot.x) * Size.x, (0.5f - s.Pivot.y) * Size.y, 0), new Vector3(Math.Abs(Size.x), Math.Abs(Size.y), 0));
                float ppu = s.PixelsPerUnit <= 0 ? 100 : s.PixelsPerUnit;
                var size = new Vector3(s.Rect.width / ppu, s.Rect.height / ppu, 0);
                return new Bounds(new Vector3((0.5f - s.Pivot.x) * size.x, (0.5f - s.Pivot.y) * size.y, 0), size);
            }
        }
    }

    public sealed class NMeshFilter : NComponent, IRemap
    {
        public Mesh Mesh;
        public override void Load(JsonObject d, LoadContext ctx) => Mesh = Serializer.Ref(d["m_Mesh"], typeof(Mesh), ctx) as Mesh;
        public override void CopyFrom(NComponent src) { base.CopyFrom(src); Mesh = ((NMeshFilter)src).Mesh; }
        public void Remap(Func<UnityEngine.Object, UnityEngine.Object> f) => Mesh = f(Mesh) as Mesh ?? Mesh;
    }

    public sealed class NMeshRenderer : NRenderer
    {
        public Mesh AdditionalStreams;
        public override Bounds LocalBounds => Native.Of<NMesh>(Go?.Get<NMeshFilter>()?.Mesh)?.Bounds ?? default;
    }

    public sealed class NSortingGroup : NBehaviour
    {
        public int SortingLayerID, SortingOrder;
        public bool SortAtRoot;
        public override void Load(JsonObject d, LoadContext ctx)
        {
            SortingLayerID = (int)Serializer.D(d["m_SortingLayerID"]);
            SortingOrder = (int)Serializer.D(d["m_SortingOrder"]);
            SortAtRoot = Serializer.D(d["m_SortAtRoot"]) != 0;
        }
        public override void CopyFrom(NComponent src) { base.CopyFrom(src); var s = (NSortingGroup)src; SortingLayerID = s.SortingLayerID; SortingOrder = s.SortingOrder; SortAtRoot = s.SortAtRoot; }
    }

    public static class RendererModule
    {
        public static void Register()
        {
            Loader.Factories["SpriteRenderer"] = () => new NSpriteRenderer();
            Loader.Factories["MeshFilter"] = () => new NMeshFilter();
            Loader.Factories["MeshRenderer"] = () => new NMeshRenderer();
            Loader.Factories["SortingGroup"] = () => new NSortingGroup();
            Loader.Factories["SpriteMask"] = () => new NSpriteMask();
            Loader.Factories["LineRenderer"] = () => new NLineRenderer();
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Renderer
    {
        static NRenderer R(Renderer r) => Native.Self<NRenderer>(r);

        public static void ResetBounds(Renderer self) => R(self).BoundsOverride = null;
        public static void ResetLocalBounds(Renderer self) => R(self).LocalBoundsOverride = null;

        /// <summary>renderer.material：第一次要的時候把共用材質複製一份換上（Unity 也是）</summary>
        public static Material GetMaterial(Renderer self)
        {
            var r = R(self);
            if (r.Shared.Length == 0 || r.Shared[0] == null) return null;
            var m = r.Shared[0];
            var n = Native.Of<NMaterial>(m);
            if (n != null && n.Source != null && !n.Source.EndsWith("(Instance)"))
            {
                var inst = (Material)n.CloneAsset();
                var ni = Native.Of<NMaterial>(inst);
                ni.Name = n.Name + " (Instance)";
                ni.Source = (n.Source ?? "") + "(Instance)";
                r.Shared[0] = inst;
                return inst;
            }
            return m;
        }

        public static Material GetSharedMaterial(Renderer self) { var r = R(self); return r.Shared.Length > 0 ? r.Shared[0] : null; }
        public static void SetMaterial(Renderer self, Material m)
        {
            var r = R(self);
            if (r.Shared.Length == 0) r.Shared = new Material[1];
            r.Shared[0] = m;
        }

        public static Material[] GetMaterialArray(Renderer self)
        {
            var r = R(self);
            for (int i = 0; i < r.Shared.Length; i++)
            {
                var n = Native.Of<NMaterial>(r.Shared[i]);
                if (n != null && (n.Source == null || !n.Source.EndsWith("(Instance)")))
                {
                    var inst = (Material)n.CloneAsset();
                    var ni = Native.Of<NMaterial>(inst);
                    ni.Name = n.Name + " (Instance)";
                    ni.Source = (n.Source ?? "") + "(Instance)";
                    r.Shared[i] = inst;
                }
            }
            return (Material[])r.Shared.Clone();
        }

        public static void CopyMaterialArray(Renderer self, Material[] m) => Array.Copy(GetMaterialArray(self), m, Math.Min(m.Length, R(self).Shared.Length));
        public static void CopySharedMaterialArray(Renderer self, Material[] m) => Array.Copy(R(self).Shared, m, Math.Min(m.Length, R(self).Shared.Length));
        /// <summary>2022.3.3 的簽名（沒有 length；Diceomancer）</summary>
        public static void SetMaterialArray(Renderer self, Material[] m) => SetMaterialArray(self, m, m?.Length ?? 0);

        public static void SetMaterialArray(Renderer self, Material[] m, int length)
        {
            var r = R(self);
            r.Shared = new Material[length];
            if (m != null) Array.Copy(m, r.Shared, Math.Min(length, m.Length));
        }
        public static Material[] GetSharedMaterialArray(Renderer self) => (Material[])R(self).Shared.Clone();
        public static int GetMaterialCount(Renderer self) => R(self).Shared.Length;

        public static void Internal_SetPropertyBlock(Renderer self, MaterialPropertyBlock properties)
        {
            var r = R(self);
            if (properties == null) { r.Block = null; return; }
            r.Block ??= new PropertySheet();
            r.Block.CopyFrom(UnityEngine_MaterialPropertyBlock.Of(properties));
        }
        public static void Internal_GetPropertyBlock(Renderer self, MaterialPropertyBlock dest)
        {
            var d = UnityEngine_MaterialPropertyBlock.Of(dest);
            var r = R(self);
            if (r.Block != null) d.CopyFrom(r.Block); else d.Clear();
        }
        public static void Internal_SetPropertyBlockMaterialIndex(Renderer self, MaterialPropertyBlock properties, int materialIndex)
        {
            var r = R(self);
            if (properties == null) { r.BlockPerMaterial.Remove(materialIndex); return; }
            if (!r.BlockPerMaterial.TryGetValue(materialIndex, out var b)) r.BlockPerMaterial[materialIndex] = b = new PropertySheet();
            b.CopyFrom(UnityEngine_MaterialPropertyBlock.Of(properties));
        }
        public static void Internal_GetPropertyBlockMaterialIndex(Renderer self, MaterialPropertyBlock dest, int materialIndex)
        {
            var d = UnityEngine_MaterialPropertyBlock.Of(dest);
            if (R(self).BlockPerMaterial.TryGetValue(materialIndex, out var b)) d.CopyFrom(b); else d.Clear();
        }
        public static bool HasPropertyBlock(Renderer self) => R(self).Block != null || R(self).BlockPerMaterial.Count > 0;
        public static bool get_enabled(Renderer self) => R(self).Enabled;
        public static void set_enabled(Renderer self, bool value) { var r = R(self); if (r.Enabled == value) return; r.Enabled = value; r.OnActiveChanged(); }
        public static bool get_isVisible(Renderer self) => R(self).Visible;
        public static string get_sortingLayerName(Renderer self) => SortingLayers.Name(R(self).SortingLayerID);
        public static void set_sortingLayerName(Renderer self, string value) => R(self).SortingLayerID = SortingLayers.IdOf(value);
        public static int get_sortingLayerID(Renderer self) => R(self).SortingLayerID;
        public static void set_sortingLayerID(Renderer self, int value) => R(self).SortingLayerID = value;
        public static int get_sortingOrder(Renderer self) => R(self).SortingOrder;
        public static void set_sortingOrder(Renderer self, int value) => R(self).SortingOrder = value;
        public static int get_sortingGroupID(Renderer self) => R(self).SortingGroupID;

        // 光照圖：只有靜態的（烘焙）；動態（realtime GI）的沒做，讀回 −1 與 (1,1,0,0)
        public static int GetLightmapIndex(Renderer self, UnityEngineInternal.LightmapType lt) => lt == UnityEngineInternal.LightmapType.StaticLightmap ? R(self).LightmapIndex : -1;
        public static void SetLightmapIndex(Renderer self, int index, UnityEngineInternal.LightmapType lt)
        {
            if (lt == UnityEngineInternal.LightmapType.StaticLightmap) R(self).LightmapIndex = index >= 0xFFFE || index < 0 ? -1 : index;
        }
        public static void SetStaticLightmapST_Injected(Renderer self, ref Vector4 st) => R(self).LightmapST = st;
        public static void GetLightmapST_Injected(Renderer self, UnityEngineInternal.LightmapType lt, out Vector4 st) =>
            st = lt == UnityEngineInternal.LightmapType.StaticLightmap ? R(self).LightmapST : new Vector4(1, 1, 0, 0);
        public static void SetLightmapST_Injected(Renderer self, ref Vector4 st, UnityEngineInternal.LightmapType lt)
        {
            if (lt == UnityEngineInternal.LightmapType.StaticLightmap) R(self).LightmapST = st;
        }
        public static void set_sortingGroupID(Renderer self, int value) => R(self).SortingGroupID = value;
        public static int get_sortingGroupOrder(Renderer self) => R(self).SortingGroupOrder;
        public static void set_sortingGroupOrder(Renderer self, int value) => R(self).SortingGroupOrder = value;
        public static void get_bounds_Injected(Renderer self, out Bounds ret) => ret = R(self).WorldBounds;
        public static void set_bounds_Injected(Renderer self, ref Bounds value) => R(self).BoundsOverride = value;
        public static void get_localBounds_Injected(Renderer self, out Bounds ret) => ret = R(self).LocalBoundsOverride ?? R(self).LocalBounds;
        public static void set_localBounds_Injected(Renderer self, ref Bounds value) => R(self).LocalBoundsOverride = value;
        public static void get_worldToLocalMatrix_Injected(Renderer self, out Matrix4x4 ret) => ret = R(self).Tr.WorldToLocal;
        public static void get_localToWorldMatrix_Injected(Renderer self, out Matrix4x4 ret) => ret = R(self).Tr.LocalToWorld;
    }

    public static unsafe partial class UnityEngine_SpriteRenderer
    {
        static NSpriteRenderer S(SpriteRenderer s) => Native.Self<NSpriteRenderer>(s);
        public static bool get_shouldSupportTiling(SpriteRenderer self) => S(self).DrawMode != SpriteDrawMode.Simple;
        public static bool get_hasSpriteChangeEvents(SpriteRenderer self) => false;
        public static void set_hasSpriteChangeEvents(SpriteRenderer self, bool value) { }
        public static Sprite get_sprite(SpriteRenderer self) => S(self).Sprite;
        public static void set_sprite(SpriteRenderer self, Sprite value) => S(self).Sprite = value;
        public static SpriteDrawMode get_drawMode(SpriteRenderer self) => S(self).DrawMode;
        public static void set_drawMode(SpriteRenderer self, SpriteDrawMode value) => S(self).DrawMode = value;
        public static float get_adaptiveModeThreshold(SpriteRenderer self) => S(self).AdaptiveThreshold;
        public static void set_adaptiveModeThreshold(SpriteRenderer self, float value) => S(self).AdaptiveThreshold = value;
        public static SpriteTileMode get_tileMode(SpriteRenderer self) => S(self).TileMode;
        public static void set_tileMode(SpriteRenderer self, SpriteTileMode value) => S(self).TileMode = value;
        public static SpriteMaskInteraction get_maskInteraction(SpriteRenderer self) => S(self).MaskInteraction;
        public static void set_maskInteraction(SpriteRenderer self, SpriteMaskInteraction value) => S(self).MaskInteraction = value;
        public static bool get_flipX(SpriteRenderer self) => S(self).FlipX;
        public static void set_flipX(SpriteRenderer self, bool value) => S(self).FlipX = value;
        public static bool get_flipY(SpriteRenderer self) => S(self).FlipY;
        public static void set_flipY(SpriteRenderer self, bool value) => S(self).FlipY = value;
        public static SpriteSortPoint get_spriteSortPoint(SpriteRenderer self) => S(self).SortPoint;
        public static void set_spriteSortPoint(SpriteRenderer self, SpriteSortPoint value) => S(self).SortPoint = value;
        public static void get_size_Injected(SpriteRenderer self, out Vector2 ret) => ret = S(self).Size;
        public static void set_size_Injected(SpriteRenderer self, ref Vector2 value) => S(self).Size = value;
        public static void get_color_Injected(SpriteRenderer self, out Color ret) => ret = S(self).Color;
        public static void set_color_Injected(SpriteRenderer self, ref Color value) => S(self).Color = value;
        public static void Internal_GetSpriteBounds_Injected(SpriteRenderer self, SpriteDrawMode mode, out Bounds ret) => ret = S(self).LocalBounds;
    }

    public static unsafe partial class UnityEngine_MeshFilter
    {
        static NMeshFilter F(MeshFilter m) => Native.Self<NMeshFilter>(m);
        public static Mesh get_sharedMesh(MeshFilter self) => F(self).Mesh;
        public static void set_sharedMesh(MeshFilter self, Mesh value) => F(self).Mesh = value;
        /// <summary>meshFilter.mesh：素材網格第一次取用時複製一份（Unity 也是）</summary>
        public static Mesh get_mesh(MeshFilter self)
        {
            var f = F(self);
            var n = Native.Of<NMesh>(f.Mesh);
            if (n == null)
            {
                var m = (Mesh)Native.NewShell(typeof(Mesh));
                Native.Bind(new NMesh(), m);
                f.Mesh = m;
                return m;
            }
            if (n.Persistent)
            {
                var c = (Mesh)n.CloneAsset();
                Native.Of<NMesh>(c).Name = n.Name + " Instance";
                f.Mesh = c;
            }
            return f.Mesh;
        }
        public static void set_mesh(MeshFilter self, Mesh value) => F(self).Mesh = value;
    }

    public static unsafe partial class UnityEngine_MeshRenderer
    {
        public static Mesh get_additionalVertexStreams(MeshRenderer self) => Native.Self<NMeshRenderer>(self).AdditionalStreams;
        public static void set_additionalVertexStreams(MeshRenderer self, Mesh value) => Native.Self<NMeshRenderer>(self).AdditionalStreams = value;
        public static int get_subMeshStartIndex(MeshRenderer self) => 0;
    }

    public static unsafe partial class UnityEngine_SortingLayer
    {
        public static int[] GetSortingLayerIDsInternal()
        {
            var r = new int[SortingLayers.Count];
            for (int i = 0; i < r.Length; i++) r[i] = SortingLayers.At(i).id;
            return r;
        }
        public static int GetLayerValueFromID(int id) => SortingLayers.Value(id);
        public static int GetLayerValueFromName(string name) => SortingLayers.Value(SortingLayers.IdOf(name));
        public static int NameToID(string name) => SortingLayers.IdOf(name);
        public static string IDToName(int id) => SortingLayers.Name(id);
        public static bool IsValid(int id) { for (int i = 0; i < SortingLayers.Count; i++) if (SortingLayers.At(i).id == id) return true; return false; }
    }

    public static unsafe partial class UnityEngine_Rendering_SortingGroup
    {
        static NSortingGroup G(SortingGroup s) => Native.Self<NSortingGroup>(s);
        public static int get_invalidSortingGroupID() => -1;
        public static void UpdateAllSortingGroups() { }
        public static string get_sortingLayerName(SortingGroup self) => SortingLayers.Name(G(self).SortingLayerID);
        public static void set_sortingLayerName(SortingGroup self, string value) => G(self).SortingLayerID = SortingLayers.IdOf(value);
        public static int get_sortingLayerID(SortingGroup self) => G(self).SortingLayerID;
        public static void set_sortingLayerID(SortingGroup self, int value) => G(self).SortingLayerID = value;
        public static int get_sortingOrder(SortingGroup self) => G(self).SortingOrder;
        public static void set_sortingOrder(SortingGroup self, int value) => G(self).SortingOrder = value;
        public static bool get_sortAtRoot(SortingGroup self) => G(self).SortAtRoot;
        public static void set_sortAtRoot(SortingGroup self, bool value) => G(self).SortAtRoot = value;
    }

    /// <summary>烘焙光照圖的清單（Renderer.lightmapIndex 指進來）。遊戲執行期設的才有（MT2 的 PrefabLightmapData 把 prefab 帶的光照圖加進來）；
    /// 場景自己的清單（LightingData）沒有讀，從空的開始（同 TS 版）。讀寫都是複製一份（Unity 也是）</summary>
    public static unsafe partial class UnityEngine_LightmapSettings
    {
        public static LightmapData[] Lightmaps = Array.Empty<LightmapData>();
        static LightmapsMode mode = LightmapsMode.CombinedDirectional;
        public static LightmapData[] get_lightmaps() => (LightmapData[])Lightmaps.Clone();
        public static void set_lightmaps(LightmapData[] value) => Lightmaps = value == null ? Array.Empty<LightmapData>() : (LightmapData[])value.Clone();
        public static LightmapsMode get_lightmapsMode() => mode;
        public static void set_lightmapsMode(LightmapsMode value) => mode = value;
        public static void Reset() { Lightmaps = Array.Empty<LightmapData>(); mode = LightmapsMode.CombinedDirectional; }

        /// <summary>繪製端用：這個 Renderer 的光照圖（沒有就 null）</summary>
        public static Texture2D ColorOf(NRenderer r)
        {
            int i = r.LightmapIndex;
            if (i < 0 || i >= Lightmaps.Length) return null;
            var t = Lightmaps[i]?.lightmapColor;
            return t ? t : null;
        }
    }
}
