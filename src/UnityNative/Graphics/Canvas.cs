// uGUI 的原生半邊：Canvas（根 Canvas 的 RectTransform 由它驅動）、CanvasRenderer（Graphic 交過來的網格、材質、貼圖、裁切）、
// CanvasGroup（alpha 與互動的繼承）、RectTransformUtility.PointInRectangle、Display。
// uGUI 本身（Graphic、Image、LayoutGroup、EventSystem…）是 managed，原版照跑；這裡只補它底下的 C++。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public sealed class NCanvas : NBehaviour
    {
        public RenderMode Mode;
        public Camera WorldCamera;
        public float PlaneDistance = 100, ScaleFactor = 1, ReferencePixelsPerUnit = 100, NormalizedSortingGridSize = 0.1f;
        public bool PixelPerfect, OverrideSorting, OverridePixelPerfect, VertexColorAlwaysGammaSpace;
        public int SortingLayerID, SortingOrder, TargetDisplay, RenderOrder;
        public AdditionalCanvasShaderChannels Channels;
        public StandaloneRenderResize UpdateRectForStandalone;
        public static readonly List<NCanvas> All = new List<NCanvas>();
        public Canvas C => (Canvas)Managed;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Mode = (RenderMode)(int)Serializer.D(d["m_RenderMode"]);
            WorldCamera = Serializer.Ref(d["m_Camera"], typeof(Camera), ctx) as Camera;
            if (d["m_PlaneDistance"] != null) PlaneDistance = (float)Serializer.D(d["m_PlaneDistance"]);
            PixelPerfect = Serializer.D(d["m_PixelPerfect"]) != 0;
            OverrideSorting = Serializer.D(d["m_OverrideSorting"]) != 0;
            OverridePixelPerfect = Serializer.D(d["m_OverridePixelPerfect"]) != 0;
            SortingLayerID = (int)Serializer.D(d["m_SortingLayerID"]);
            SortingOrder = (int)Serializer.D(d["m_SortingOrder"]);
            TargetDisplay = (int)Serializer.D(d["m_TargetDisplay"]);
            Channels = (AdditionalCanvasShaderChannels)(int)Serializer.D(d["m_AdditionalShaderChannelsFlag"]);
            VertexColorAlwaysGammaSpace = Serializer.D(d["m_VertexColorAlwaysGammaSpace"]) != 0;
            if (d["m_SortingBucketNormalizedSize"] != null) NormalizedSortingGridSize = (float)Serializer.D(d["m_SortingBucketNormalizedSize"]);
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NCanvas)src;
            Mode = s.Mode; WorldCamera = s.WorldCamera; PlaneDistance = s.PlaneDistance; ScaleFactor = s.ScaleFactor; ReferencePixelsPerUnit = s.ReferencePixelsPerUnit;
            PixelPerfect = s.PixelPerfect; OverrideSorting = s.OverrideSorting; SortingLayerID = s.SortingLayerID; SortingOrder = s.SortingOrder;
            TargetDisplay = s.TargetDisplay; Channels = s.Channels; VertexColorAlwaysGammaSpace = s.VertexColorAlwaysGammaSpace;
        }

        /// <summary>
        /// Instantiate 正在啟用複製品（Cloner.CloneGameObject）：這期間啟用的根 Canvas 先不驅動 RectTransform，等 UpdateAll（PreUpdate、繪製前）。
        /// Unity 不在 Instantiate 當下驅動：TMP_Dropdown.Show 先 Instantiate(範本)（這時清單沒有父物件、Canvas 是 SetupTemplate 補的 Screen Space Overlay），
        /// 再 SetParent 到下拉選單底下；當下就驅動的話清單的 RectTransform 會被改成全螢幕（錨點 0、大小＝螢幕），掛回去之後畫成一大塊（10/20，Diceomancer 的語言、介面縮放選單）
        /// </summary>
        public static int Cloning;

        public override void OnActiveChanged()
        {
            bool on = Alive && IsActiveAndEnabled;
            if (on && !All.Contains(this)) { All.Add(this); if (Cloning == 0) UpdateRect(); }
            else if (!on) All.Remove(this);
            CanvasTree.Dirty = true;
        }
        public override void OnDestroyNative() { All.Remove(this); CanvasTree.Dirty = true; }

        /// <summary>往上找啟用中的 Canvas；找不到就是根 Canvas</summary>
        public NCanvas Parent
        {
            get
            {
                for (var t = Tr?.Parent; t != null; t = t.Parent)
                {
                    var c = t.Go.Get<NCanvas>();
                    if (c != null && c.Alive && c.IsActiveAndEnabled) return c;
                }
                return null;
            }
        }

        public bool IsRoot => Parent == null;
        public NCanvas Root { get { var c = this; for (var p = c.Parent; p != null; p = p.Parent) c = p; return c; } }

        // 沒有攝影機的 Screen Space - Camera 照 Unity 退回 Overlay；攝影機已經摧毀也算沒有（Unity 存的是 PPtr，摧毀後解不出來）。
        // 換場景時攝影機比 Canvas 先摧毀，CanvasScaler.OnDisable 設 scaleFactor 時 UpdateRect 拿到 null 的 NCamera（10/19）
        public RenderMode EffectiveMode => Root.Mode == RenderMode.ScreenSpaceCamera && Native.Of<NCamera>(Root.WorldCamera) == null ? RenderMode.ScreenSpaceOverlay : Root.Mode;

        /// <summary>根 Canvas 的 RectTransform：螢幕空間的兩種模式由 Canvas 決定位置、大小、縮放（Unity 也是 driven）</summary>
        public void UpdateRect()
        {
            if (!IsRoot || Tr == null) return;
            var mode = EffectiveMode;
            if (mode == RenderMode.WorldSpace) return;
            float sw = Engine.Config.ScreenWidth, sh = Engine.Config.ScreenHeight, sf = ScaleFactor <= 0 ? 1 : ScaleFactor;
            var t = Tr;
            Vector3 pos; Quaternion rot = Quaternion.identity; float s;
            if (mode == RenderMode.ScreenSpaceCamera)
            {
                var cam = Native.Of<NCamera>(WorldCamera);
                var r = cam.PixelRect;
                sw = r.width; sh = r.height;
                float d = PlaneDistance;
                float unitsPerPixel = cam.Orthographic ? 2f * cam.OrthoSize / sh : 2f * d * MathF.Tan(cam.Fov * Mathf.Deg2Rad * 0.5f) / sh;
                s = unitsPerPixel * sf;
                rot = cam.Tr.Rotation;
                pos = cam.Tr.Position + rot * new Vector3(0, 0, d);
            }
            else
            {
                s = sf;
                pos = new Vector3(sw * 0.5f, sh * 0.5f, 0);
            }
            var size = new Vector2(sw / sf, sh / sf);
            if (t.SizeDelta != size || t.AnchorMin != Vector2.zero || t.AnchorMax != Vector2.zero || t.Pivot != new Vector2(0.5f, 0.5f))
            {
                t.AnchorMin = Vector2.zero; t.AnchorMax = Vector2.zero; t.Pivot = new Vector2(0.5f, 0.5f);
                t.SetSizeDelta(size);
            }
            var scale = new Vector3(s, s, s);
            if (t.LocalScale != scale) t.LocalScale = scale;
            if (t.Parent == null)
            {
                if ((t.LocalPosition - pos).sqrMagnitude > 1e-8f) t.LocalPosition = pos;
                if (t.LocalRotation != rot) t.LocalRotation = rot;
            }
            else
            {
                if ((t.Position - pos).sqrMagnitude > 1e-8f) t.Position = pos;
                if (t.Rotation != rot) t.Rotation = rot;
            }
        }

        public static void UpdateAll()
        {
            foreach (var c in All.ToArray()) if (c.Alive) c.UpdateRect();
        }
    }

    /// <summary>Canvas 樹有沒有變（繪製端重排 Canvas 的順序用）</summary>
    public static class CanvasTree { public static bool Dirty = true; }

    public sealed class UIGeometry
    {
        public int VertexCount;
        public float[] Pos = Array.Empty<float>();   // xyz
        public float[] Color = Array.Empty<float>(); // rgba 0～1
        public float[] UV0 = Array.Empty<float>(), UV1, UV2, UV3; // 每個 4 維（Unity 的 UI 頂點 uv 是 Vector4）
        public float[] Normal, Tangent;
        public int[] Indices = Array.Empty<int>();
        public int Version;
    }

    public sealed class NCanvasRenderer : NComponent
    {
        public UIGeometry Geo;
        public readonly List<Material> Materials = new List<Material>();
        public readonly List<Material> PopMaterials = new List<Material>();
        public int MaterialCount, PopMaterialCount;
        public Texture Texture, AlphaTexture;
        public Color Color = Color.white;
        public bool ClipEnabled;
        public Rect ClipRect;
        public Vector2 ClipSoftness;
        public bool Cull, CullTransparentMesh, HasPopInstruction;
        public int AbsoluteDepth = -1, RelativeDepth = -1;
        public int Version;

        public override void Load(JsonObject d, LoadContext ctx) => CullTransparentMesh = Serializer.D(d["m_CullTransparentMesh"]) != 0;
        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NCanvasRenderer)src;
            CullTransparentMesh = s.CullTransparentMesh;
            Color = s.Color;
        }

        /// <summary>SetMesh：Graphic 共用同一個 workerMesh，所以要把資料複製下來（Unity 也是）</summary>
        public void SetMesh(NMesh m)
        {
            if (m == null) { Geo = null; Version++; return; }
            var g = Geo ?? new UIGeometry();
            int n = m.VertexCount;
            g.VertexCount = n;
            g.Pos = Copy(m.Channels[(int)VertexAttribute.Position], n, 3, 0f);
            g.Color = Copy(m.Channels[(int)VertexAttribute.Color], n, 4, 1f);
            g.UV0 = Copy(m.Channels[(int)VertexAttribute.TexCoord0], n, 4, 0f);
            g.UV1 = m.Channels[(int)VertexAttribute.TexCoord1] != null ? Copy(m.Channels[(int)VertexAttribute.TexCoord1], n, 4, 0f) : null;
            g.UV2 = m.Channels[(int)VertexAttribute.TexCoord2] != null ? Copy(m.Channels[(int)VertexAttribute.TexCoord2], n, 4, 0f) : null;
            g.UV3 = m.Channels[(int)VertexAttribute.TexCoord3] != null ? Copy(m.Channels[(int)VertexAttribute.TexCoord3], n, 4, 0f) : null;
            g.Normal = m.Channels[(int)VertexAttribute.Normal] != null ? Copy(m.Channels[(int)VertexAttribute.Normal], n, 3, 0f) : null;
            g.Tangent = m.Channels[(int)VertexAttribute.Tangent] != null ? Copy(m.Channels[(int)VertexAttribute.Tangent], n, 4, 0f) : null;
            int total = 0;
            foreach (var s in m.Subs) total += s.Indices.Length;
            var idx = new int[total];
            int o = 0;
            foreach (var s in m.Subs) { for (int i = 0; i < s.Indices.Length; i++) idx[o++] = s.Indices[i] + s.BaseVertex; }
            g.Indices = idx;
            g.Version++;
            Geo = g;
            Version++;
        }

        static float[] Copy(NMesh.Channel c, int n, int dim, float fill)
        {
            var r = new float[n * dim];
            if (c == null) { if (fill != 0) Array.Fill(r, fill); return r; }
            int cd = c.Dim;
            for (int i = 0; i < n; i++)
                for (int k = 0; k < dim; k++)
                    r[i * dim + k] = k < cd && i * cd + k < c.Data.Length ? c.Data[i * cd + k] : (dim == 4 && k == 3 && fill != 0 ? 1f : 0f);
            return r;
        }

        public float InheritedAlpha()
        {
            float a = 1f;
            for (var t = Tr; t != null; t = t.Parent)
            {
                if (t.Go == null || !t.Go.ActiveInHierarchy) continue;
                foreach (var c in t.Go.Components)
                    if (c is NCanvasGroup g && g.Enabled && g.Alive)
                    {
                        a *= g.Alpha;
                        if (g.IgnoreParentGroups) return a;
                    }
            }
            return a;
        }
    }

    public sealed class NCanvasGroup : NBehaviour
    {
        public float Alpha = 1;
        public bool Interactable = true, BlocksRaycasts = true, IgnoreParentGroups;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            if (d["m_Alpha"] != null) Alpha = (float)Serializer.D(d["m_Alpha"]);
            if (d["m_Interactable"] != null) Interactable = Serializer.D(d["m_Interactable"]) != 0;
            if (d["m_BlocksRaycasts"] != null) BlocksRaycasts = Serializer.D(d["m_BlocksRaycasts"]) != 0;
            IgnoreParentGroups = Serializer.D(d["m_IgnoreParentGroups"]) != 0;
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NCanvasGroup)src;
            Alpha = s.Alpha; Interactable = s.Interactable; BlocksRaycasts = s.BlocksRaycasts; IgnoreParentGroups = s.IgnoreParentGroups;
        }

        public void Changed()
        {
            if (Go != null && Go.ActiveInHierarchy) Lifecycle.SendToScriptsRecursive(Go, Messages.OnCanvasGroupChanged);
        }

        public override void OnActiveChanged() { }
    }

    public static class CanvasModule
    {
        public static void Register()
        {
            Loader.Factories["Canvas"] = () => new NCanvas();
            Loader.Factories["CanvasRenderer"] = () => new NCanvasRenderer();
            Loader.Factories["CanvasGroup"] = () => new NCanvasGroup();
            Loader.Factories["Camera"] = () => new NCamera();
            Engine.PreUpdate.Add(NCanvas.UpdateAll);
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Canvas
    {
        static NCanvas C(Canvas c) => Native.Self<NCanvas>(c);
        // 巢狀 Canvas 的 renderMode／worldCamera／targetDisplay 回根 Canvas 的（Inspector 也不給巢狀的設這三個）。序列化的值是建立時的預設
        //（MT2 下拉清單的 Canvas 存的是 World Space），照自己的回，GraphicRaycaster 會改用攝影機算射線，清單的選項點不到（10/19）
        public static RenderMode get_renderMode(Canvas self) => C(self).IsRoot ? C(self).Mode : C(self).Root.Mode;
        public static void set_renderMode(Canvas self, RenderMode value) { var c = C(self); c.Mode = value; c.UpdateRect(); CanvasTree.Dirty = true; }
        public static bool get_isRootCanvas(Canvas self) => C(self).IsRoot;
        public static float get_scaleFactor(Canvas self) => C(self).IsRoot ? C(self).ScaleFactor : C(self).Root.ScaleFactor;
        public static void set_scaleFactor(Canvas self, float value)
        {
            var c = C(self);
            if (!c.IsRoot || c.ScaleFactor == value) return;
            c.ScaleFactor = value;
            c.UpdateRect();
        }
        public static float get_referencePixelsPerUnit(Canvas self) => C(self).ReferencePixelsPerUnit;
        public static void set_referencePixelsPerUnit(Canvas self, float value) => C(self).ReferencePixelsPerUnit = value;
        public static bool get_overridePixelPerfect(Canvas self) => C(self).OverridePixelPerfect;
        public static void set_overridePixelPerfect(Canvas self, bool value) => C(self).OverridePixelPerfect = value;
        public static bool get_vertexColorAlwaysGammaSpace(Canvas self) => C(self).VertexColorAlwaysGammaSpace;
        public static void set_vertexColorAlwaysGammaSpace(Canvas self, bool value) => C(self).VertexColorAlwaysGammaSpace = value;
        public static bool get_pixelPerfect(Canvas self) => C(self).IsRoot || C(self).OverridePixelPerfect ? C(self).PixelPerfect : C(self).Root.PixelPerfect;
        public static void set_pixelPerfect(Canvas self, bool value) => C(self).PixelPerfect = value;
        public static float get_planeDistance(Canvas self) => C(self).PlaneDistance;
        public static void set_planeDistance(Canvas self, float value) { C(self).PlaneDistance = value; C(self).UpdateRect(); }
        public static int get_renderOrder(Canvas self) => C(self).RenderOrder;
        public static bool get_overrideSorting(Canvas self) => C(self).OverrideSorting;
        public static void set_overrideSorting(Canvas self, bool value) { C(self).OverrideSorting = value; CanvasTree.Dirty = true; }
        public static int get_sortingOrder(Canvas self) => C(self).IsRoot || C(self).OverrideSorting ? C(self).SortingOrder : C(self).Root.SortingOrder;
        public static void set_sortingOrder(Canvas self, int value) { C(self).SortingOrder = value; CanvasTree.Dirty = true; }
        public static int get_targetDisplay(Canvas self) => C(self).IsRoot ? C(self).TargetDisplay : C(self).Root.TargetDisplay;
        public static void set_targetDisplay(Canvas self, int value) => C(self).TargetDisplay = value;
        public static int get_sortingLayerID(Canvas self) => C(self).IsRoot || C(self).OverrideSorting ? C(self).SortingLayerID : C(self).Root.SortingLayerID;
        public static void set_sortingLayerID(Canvas self, int value) { C(self).SortingLayerID = value; CanvasTree.Dirty = true; }
        public static int get_cachedSortingLayerValue(Canvas self) => SortingLayers.Value(get_sortingLayerID(self));
        public static AdditionalCanvasShaderChannels get_additionalShaderChannels(Canvas self) => C(self).Channels;
        public static void set_additionalShaderChannels(Canvas self, AdditionalCanvasShaderChannels value) => C(self).Channels = value;
        public static string get_sortingLayerName(Canvas self) => SortingLayers.Name(get_sortingLayerID(self));
        public static void set_sortingLayerName(Canvas self, string value) { C(self).SortingLayerID = SortingLayers.IdOf(value); CanvasTree.Dirty = true; }
        public static Canvas get_rootCanvas(Canvas self) => C(self).Root.C;
        public static StandaloneRenderResize get_updateRectTransformForStandalone(Canvas self) => C(self).UpdateRectForStandalone;
        public static void set_updateRectTransformForStandalone(Canvas self, StandaloneRenderResize value) => C(self).UpdateRectForStandalone = value;
        public static void SetExternalCanvasEnabled(bool enabled) { }
        public static Camera get_worldCamera(Canvas self) => C(self).IsRoot ? C(self).WorldCamera : C(self).Root.WorldCamera;
        public static void set_worldCamera(Canvas self, Camera value) { C(self).WorldCamera = value; C(self).UpdateRect(); }
        public static float get_normalizedSortingGridSize(Canvas self) => C(self).NormalizedSortingGridSize;
        public static void set_normalizedSortingGridSize(Canvas self, float value) => C(self).NormalizedSortingGridSize = value;
        public static Material GetDefaultCanvasMaterial() => Defaults.Get("Default UI Material", "UI/Default");
        public static Material GetDefaultCanvasTextMaterial() => Defaults.Get("Default UI Font Material", "UI/Default Font");
        public static Material GetETC1SupportedCanvasMaterial() => Defaults.Get("ETC1 Supported UI Material", "UI/DefaultETC1");
        public static void UpdateCanvasRectTransform(Canvas self, bool alignWithCamera) => C(self).UpdateRect();
        public static void get_pixelRect_Injected(Canvas self, out Rect ret)
        {
            var c = C(self).Root;
            var cam = c.EffectiveMode == RenderMode.ScreenSpaceCamera ? Native.Of<NCamera>(c.WorldCamera) : null;
            ret = cam != null ? cam.PixelRect : new Rect(0, 0, Engine.Config.ScreenWidth, Engine.Config.ScreenHeight);
        }
        public static void get_renderingDisplaySize_Injected(Canvas self, out Vector2 ret) => ret = new Vector2(Engine.Config.ScreenWidth, Engine.Config.ScreenHeight);
    }

    public static unsafe partial class UnityEngine_CanvasRenderer
    {
        static NCanvasRenderer R(CanvasRenderer c) => Native.Self<NCanvasRenderer>(c);
        public static bool get_hasPopInstruction(CanvasRenderer self) => R(self).HasPopInstruction;
        public static void set_hasPopInstruction(CanvasRenderer self, bool value) { R(self).HasPopInstruction = value; R(self).Version++; }
        public static int get_materialCount(CanvasRenderer self) => R(self).MaterialCount;
        public static void set_materialCount(CanvasRenderer self, int value)
        {
            var r = R(self); r.MaterialCount = value;
            while (r.Materials.Count < value) r.Materials.Add(null);
            if (r.Materials.Count > value) r.Materials.RemoveRange(value, r.Materials.Count - value);
            r.Version++;
        }
        public static int get_popMaterialCount(CanvasRenderer self) => R(self).PopMaterialCount;
        public static void set_popMaterialCount(CanvasRenderer self, int value)
        {
            var r = R(self); r.PopMaterialCount = value;
            while (r.PopMaterials.Count < value) r.PopMaterials.Add(null);
            if (r.PopMaterials.Count > value) r.PopMaterials.RemoveRange(value, r.PopMaterials.Count - value);
            r.Version++;
        }
        // 直向版面時原版 16:9 上下外面的 UI 回 −1（沒畫的元素，Unity 也是 −1），GraphicRaycaster 就打不到（SafeAreaCull.OutsideBand；沒打開時照舊）
        public static int get_absoluteDepth(CanvasRenderer self) => SafeAreaCull.OutsideBand(R(self)) ? -1 : R(self).AbsoluteDepth;
        public static bool get_hasMoved(CanvasRenderer self) => true;
        public static bool get_cullTransparentMesh(CanvasRenderer self) => R(self).CullTransparentMesh;
        public static void set_cullTransparentMesh(CanvasRenderer self, bool value) => R(self).CullTransparentMesh = value;
        public static bool get_hasRectClipping(CanvasRenderer self) => R(self).ClipEnabled;
        public static int get_relativeDepth(CanvasRenderer self) => R(self).RelativeDepth;
        public static bool get_cull(CanvasRenderer self) => R(self).Cull;
        public static void set_cull(CanvasRenderer self, bool value) => R(self).Cull = value;
        public static void DisableRectClipping(CanvasRenderer self) { R(self).ClipEnabled = false; R(self).Version++; }
        public static void SetMaterial(CanvasRenderer self, Material material, int index)
        {
            var r = R(self);
            while (r.Materials.Count <= index) r.Materials.Add(null);
            r.Materials[index] = material;
            r.MaterialCount = Math.Max(r.MaterialCount, index + 1);
            r.Version++;
        }
        public static Material GetMaterial(CanvasRenderer self, int index) { var r = R(self); return (uint)index < (uint)r.Materials.Count ? r.Materials[index] : null; }
        public static void SetPopMaterial(CanvasRenderer self, Material material, int index)
        {
            var r = R(self);
            while (r.PopMaterials.Count <= index) r.PopMaterials.Add(null);
            r.PopMaterials[index] = material;
            r.PopMaterialCount = Math.Max(r.PopMaterialCount, index + 1);
            r.Version++;
        }
        public static Material GetPopMaterial(CanvasRenderer self, int index) { var r = R(self); return (uint)index < (uint)r.PopMaterials.Count ? r.PopMaterials[index] : null; }
        public static void SetTexture(CanvasRenderer self, Texture texture) { R(self).Texture = texture; R(self).Version++; }
        public static void SetAlphaTexture(CanvasRenderer self, Texture texture) { R(self).AlphaTexture = texture; R(self).Version++; }
        public static void SetMesh(CanvasRenderer self, Mesh mesh) => R(self).SetMesh(Native.Of<NMesh>(mesh));
        public static Mesh GetMesh(CanvasRenderer self) => null;
        public static void Clear(CanvasRenderer self)
        {
            var r = R(self);
            r.Geo = null; r.Materials.Clear(); r.PopMaterials.Clear(); r.MaterialCount = 0; r.PopMaterialCount = 0; r.Texture = null;
            r.Version++;
        }
        public static float GetInheritedAlpha(CanvasRenderer self) => R(self).InheritedAlpha();
        public static void SetColor_Injected(CanvasRenderer self, ref Color color) { R(self).Color = color; R(self).Version++; }
        public static void GetColor_Injected(CanvasRenderer self, out Color ret) => ret = R(self).Color;
        public static void EnableRectClipping_Injected(CanvasRenderer self, ref Rect rect) { var r = R(self); r.ClipEnabled = true; r.ClipRect = rect; r.Version++; }
        public static void get_clippingSoftness_Injected(CanvasRenderer self, out Vector2 ret) => ret = R(self).ClipSoftness;
        public static void set_clippingSoftness_Injected(CanvasRenderer self, ref Vector2 value) => R(self).ClipSoftness = value;

        public static void SplitIndicesStreamsInternal(object verts, object indices)
        {
            var v = (List<UIVertex>)verts; var ix = (List<int>)indices;
            ix.Clear();
            for (int i = 0; i < v.Count; i++) ix.Add(i);
        }

        public static void SplitUIVertexStreamsInternal(object verts, object positions, object colors, object uv0S, object uv1S, object uv2S, object uv3S, object normals, object tangents)
        {
            var v = (List<UIVertex>)verts;
            var p = (List<Vector3>)positions; var c = (List<Color32>)colors;
            var u0 = (List<Vector4>)uv0S; var u1 = (List<Vector4>)uv1S; var u2 = (List<Vector4>)uv2S; var u3 = (List<Vector4>)uv3S;
            var n = (List<Vector3>)normals; var t = (List<Vector4>)tangents;
            p?.Clear(); c?.Clear(); u0?.Clear(); u1?.Clear(); u2?.Clear(); u3?.Clear(); n?.Clear(); t?.Clear();
            foreach (var x in v)
            {
                p?.Add(x.position); c?.Add(x.color); u0?.Add(x.uv0); u1?.Add(x.uv1); u2?.Add(x.uv2); u3?.Add(x.uv3); n?.Add(x.normal); t?.Add(x.tangent);
            }
        }

        public static void CreateUIVertexStreamInternal(object verts, object positions, object colors, object uv0S, object uv1S, object uv2S, object uv3S, object normals, object tangents, object indices)
        {
            var v = (List<UIVertex>)verts;
            var p = (List<Vector3>)positions; var c = (List<Color32>)colors;
            var u0 = (List<Vector4>)uv0S; var u1 = (List<Vector4>)uv1S; var u2 = (List<Vector4>)uv2S; var u3 = (List<Vector4>)uv3S;
            var n = (List<Vector3>)normals; var t = (List<Vector4>)tangents; var ix = (List<int>)indices;
            v.Clear();
            foreach (var i in ix)
            {
                var x = UIVertex.simpleVert;
                x.position = p[i];
                if (c != null && i < c.Count) x.color = c[i];
                if (u0 != null && i < u0.Count) x.uv0 = u0[i];
                if (u1 != null && i < u1.Count) x.uv1 = u1[i];
                if (u2 != null && i < u2.Count) x.uv2 = u2[i];
                if (u3 != null && i < u3.Count) x.uv3 = u3[i];
                if (n != null && i < n.Count) x.normal = n[i];
                if (t != null && i < t.Count) x.tangent = t[i];
                v.Add(x);
            }
        }
    }

    public static unsafe partial class UnityEngine_CanvasGroup
    {
        static NCanvasGroup G(CanvasGroup c) => Native.Self<NCanvasGroup>(c);
        public static float get_alpha(CanvasGroup self) => G(self).Alpha;
        public static void set_alpha(CanvasGroup self, float value) { var g = G(self); if (g.Alpha == value) return; g.Alpha = value; g.Changed(); }
        public static bool get_interactable(CanvasGroup self) => G(self).Interactable;
        public static void set_interactable(CanvasGroup self, bool value) { var g = G(self); if (g.Interactable == value) return; g.Interactable = value; g.Changed(); }
        public static bool get_blocksRaycasts(CanvasGroup self) => G(self).BlocksRaycasts;
        public static void set_blocksRaycasts(CanvasGroup self, bool value) { var g = G(self); if (g.BlocksRaycasts == value) return; g.BlocksRaycasts = value; g.Changed(); }
        public static bool get_ignoreParentGroups(CanvasGroup self) => G(self).IgnoreParentGroups;
        public static void set_ignoreParentGroups(CanvasGroup self, bool value) { var g = G(self); if (g.IgnoreParentGroups == value) return; g.IgnoreParentGroups = value; g.Changed(); }
    }

    public static unsafe partial class UnityEngine_RectTransformUtility
    {
        /// <summary>螢幕點打到 rect 所在平面的本地座標，是否在 rect（扣掉 offset：左、下、右、上）裡</summary>
        public static bool PointInRectangle_Injected(ref Vector2 screenPoint, RectTransform rect, Camera cam, ref Vector4 offset)
        {
            var t = Native.Of<NTransform>(rect);
            if (t == null) return false;
            if (!ScreenToLocal(screenPoint, t, Native.Of<NCamera>(cam), out var local)) return false;
            var r = t.Rect;
            return local.x >= r.xMin + offset.x && local.x <= r.xMax - offset.z && local.y >= r.yMin + offset.y && local.y <= r.yMax - offset.w;
        }

        public static bool ScreenToLocal(Vector2 sp, NTransform t, NCamera cam, out Vector2 local)
        {
            Ray ray;
            if (cam != null)
            {
                var v = cam.ScreenToViewport(new Vector3(sp.x, sp.y, 0));
                ray = cam.ViewportRay(new Vector2(v.x, v.y));
            }
            else ray = new Ray(new Vector3(sp.x, sp.y, -100f), Vector3.forward);
            var m = t.LocalToWorld;
            var normal = MathUtil.MulVector(m, Vector3.back).normalized;
            var origin = new Vector3(m.m03, m.m13, m.m23);
            float denom = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(denom) < 1e-8f) { local = default; return false; }
            float dist = Vector3.Dot(origin - ray.origin, normal) / denom;
            var hit = ray.origin + ray.direction * dist;
            // 縮放有 0 的（Diceomancer 主選單收起來的箭頭 x 縮放 0）沒有反矩陣：Unity 的 InverseTransformPoint 算出 NaN，比較都不成立，等於沒打到
            if (!MathUtil.Inverse(m, out var w2l)) { local = default; return false; }
            var lp = MathUtil.MulPoint(w2l, hit);
            local = new Vector2(lp.x, lp.y);
            return true;
        }

        public static void PixelAdjustPoint_Injected(ref Vector2 point, Transform elementTransform, Canvas canvas, out Vector2 ret) => ret = point;
        public static void PixelAdjustRect_Injected(RectTransform rectTransform, Canvas canvas, out Rect ret) => ret = Native.Self<NTransform>(rectTransform).Rect;
    }

    public static unsafe partial class UnityEngine_Display
    {
        public static void GetSystemExtImpl(IntPtr nativeDisplay, out int w, out int h) { w = Engine.Config.ScreenWidth; h = Engine.Config.ScreenHeight; }
        public static void GetRenderingExtImpl(IntPtr nativeDisplay, out int w, out int h) { w = Engine.Config.ScreenWidth; h = Engine.Config.ScreenHeight; }
        public static int RelativeMouseAtImpl(int x, int y, out int rx, out int ry) { rx = x; ry = y; return 0; }
    }
}

namespace UnityNative
{
    /// <summary>sorting layer（project.json 的 TagManager.m_SortingLayers）：uniqueID ↔ 名字 ↔ 順序</summary>
    public static class SortingLayers
    {
        static List<(string name, int id)> layers;
        static List<(string name, int id)> L
        {
            get
            {
                if (layers != null) return layers;
                layers = new List<(string, int)>();
                var tm = Assets.Index("project")["TagManager"]?["TagManager"]?["m_SortingLayers"] as JsonArray;
                if (tm != null) foreach (var l in tm) layers.Add((l?["name"]?.ToString() ?? "", unchecked((int)(uint)Serializer.D(l?["uniqueID"]))));
                if (layers.Count == 0) layers.Add(("Default", 0));
                return layers;
            }
        }
        public static int Value(int id) { var l = L; for (int i = 0; i < l.Count; i++) if (l[i].id == id) return i; return 0; }
        public static string Name(int id) { foreach (var l in L) if (l.id == id) return l.name; return "Default"; }
        public static int IdOf(string name) { foreach (var l in L) if (l.name == name) return l.id; return 0; }
        public static int Count => L.Count;
        public static (string name, int id) At(int i) => L[i];
    }
}
