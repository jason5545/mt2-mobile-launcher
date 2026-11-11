// Camera 的原生半邊：屬性（從場景資料讀）、矩陣、螢幕／視埠／世界座標換算。
// 矩陣照 Unity：worldToCameraMatrix 是右手系、攝影機看 -z（= 翻轉 z × 攝影機世界矩陣的反矩陣）；projectionMatrix 是 GL 慣例（z ∈ [-1, 1]）。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNative
{
    public sealed class NCamera : NBehaviour
    {
        public float Near = 0.3f, Far = 1000f, Fov = 60f, OrthoSize = 5f, Depth;
        public bool Orthographic;
        public int CullingMask = -1, EventMask = -1, TargetDisplay;
        public CameraClearFlags ClearFlags = CameraClearFlags.Skybox;
        public Color Background = new Color(0.19f, 0.3f, 0.47f, 0);
        public Rect ViewportRect = new Rect(0, 0, 1, 1);
        public RenderTexture TargetTexture;
        public float AspectOverride = -1;
        public Matrix4x4? ProjectionOverride, WorldToCameraOverride;
        public bool AllowHDR, AllowMSAA;
        public DepthTextureMode DepthTextureMode;
        public OpaqueSortMode OpaqueSort;
        public TransparencySortMode TransparencySort;
        public Vector3 TransparencySortAxis = new Vector3(0, 0, 1);
        public readonly Dictionary<CameraEvent, List<CommandBuffer>> CommandBuffers = new Dictionary<CameraEvent, List<CommandBuffer>>();
        public static readonly List<NCamera> All = new List<NCamera>();

        public Camera C => (Camera)Managed;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Near = F(d, "near clip plane", 0.3f);
            Far = F(d, "far clip plane", 1000f);
            Fov = F(d, "field of view", 60f);
            Orthographic = F(d, "orthographic", 0) != 0;
            OrthoSize = F(d, "orthographic size", 5f);
            Depth = F(d, "m_Depth", 0);
            if (d["m_CullingMask"] is JsonObject cm) CullingMask = (int)(uint)Serializer.D(cm["m_Bits"]);
            ClearFlags = (CameraClearFlags)(int)F(d, "m_ClearFlags", 1);
            if (d["m_BackGroundColor"] != null) Background = (Color)Serializer.Convert(d["m_BackGroundColor"], typeof(Color), null);
            if (d["m_NormalizedViewPortRect"] != null) ViewportRect = (Rect)Serializer.Convert(d["m_NormalizedViewPortRect"], typeof(Rect), null);
            TargetDisplay = (int)F(d, "m_TargetDisplay", 0);
            // 序列化的欄位名是 m_HDR（Camera.allowHDR）；m_AllowMSAA 同名
            AllowHDR = F(d, "m_HDR", F(d, "m_AllowHDR", 0)) != 0;
            AllowMSAA = F(d, "m_AllowMSAA", 0) != 0;
            TargetTexture = Serializer.Ref(d["m_TargetTexture"], typeof(RenderTexture), ctx) as RenderTexture;
        }

        static float F(JsonObject d, string k, float def) => d[k] != null ? (float)Serializer.D(d[k]) : def;

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NCamera)src;
            Near = s.Near; Far = s.Far; Fov = s.Fov; OrthoSize = s.OrthoSize; Depth = s.Depth; Orthographic = s.Orthographic;
            CullingMask = s.CullingMask; ClearFlags = s.ClearFlags; Background = s.Background; ViewportRect = s.ViewportRect;
            TargetTexture = s.TargetTexture; TargetDisplay = s.TargetDisplay; AllowHDR = s.AllowHDR; AllowMSAA = s.AllowMSAA;
        }

        public override void OnActiveChanged() => Track();
        void Track()
        {
            bool on = Alive && IsActiveAndEnabled;
            if (on && !All.Contains(this)) All.Add(this);
            else if (!on) All.Remove(this);
        }
        public override void OnDestroyNative() => All.Remove(this);

        // ───────── 尺寸 ─────────

        public Vector2 TargetSize
        {
            get
            {
                var rt = Native.Of<NTexture>(TargetTexture);
                if (rt != null) return new Vector2(rt.Width, rt.Height);
                return new Vector2(Engine.Config.ScreenWidth, Engine.Config.ScreenHeight);
            }
        }

        public Rect PixelRect
        {
            get
            {
                var s = TargetSize;
                var r = ViewportRect;
                float x0 = Mathf.Clamp01(r.x), y0 = Mathf.Clamp01(r.y), x1 = Mathf.Clamp01(r.xMax), y1 = Mathf.Clamp01(r.yMax);
                return new Rect(x0 * s.x, y0 * s.y, (x1 - x0) * s.x, (y1 - y0) * s.y);
            }
        }

        public float Aspect
        {
            get
            {
                if (AspectOverride > 0) return AspectOverride;
                var p = PixelRect;
                return p.height > 0 ? p.width / p.height : 1f;
            }
        }

        // ───────── 矩陣 ─────────

        public Matrix4x4 WorldToCamera
        {
            get
            {
                if (WorldToCameraOverride.HasValue) return WorldToCameraOverride.Value;
                var t = Tr;
                var m = MathUtil.TRS(t.Position, t.Rotation, Vector3.one);
                MathUtil.Inverse(m, out var inv);
                var flip = Matrix4x4.identity;
                flip.m22 = -1;
                return MathUtil.Mul(flip, inv);
            }
        }

        public Matrix4x4 CameraToWorld { get { MathUtil.Inverse(WorldToCamera, out var r); return r; } }

        public Matrix4x4 Projection
        {
            get
            {
                if (ProjectionOverride.HasValue) return ProjectionOverride.Value;
                Matrix4x4 m;
                if (Orthographic)
                {
                    float h = OrthoSize, w = h * Aspect;
                    Icalls.UnityEngine_Matrix4x4.Ortho_Injected(-w, w, -h, h, Near, Far, out m);
                }
                else Icalls.UnityEngine_Matrix4x4.Perspective_Injected(Fov, Aspect, Near, Far, out m);
                return m;
            }
        }

        public Matrix4x4 ViewProjection => MathUtil.Mul(Projection, WorldToCamera);

        // ───────── 換算 ─────────

        public Vector3 WorldToViewport(Vector3 p)
        {
            var v = MathUtil.MulPoint(WorldToCamera, p);
            var pr = Projection;
            float x = pr.m00 * v.x + pr.m01 * v.y + pr.m02 * v.z + pr.m03;
            float y = pr.m10 * v.x + pr.m11 * v.y + pr.m12 * v.z + pr.m13;
            float w = pr.m30 * v.x + pr.m31 * v.y + pr.m32 * v.z + pr.m33;
            if (Math.Abs(w) < 1e-12f) w = 1e-12f;
            return new Vector3((x / w) * 0.5f + 0.5f, (y / w) * 0.5f + 0.5f, -v.z);
        }

        public Vector3 WorldToScreen(Vector3 p)
        {
            var vp = WorldToViewport(p);
            var r = PixelRect;
            return new Vector3(r.x + vp.x * r.width, r.y + vp.y * r.height, vp.z);
        }

        /// <summary>視埠座標（0～1）＋與攝影機的距離 → 世界座標</summary>
        public Vector3 ViewportToWorld(Vector3 vp)
        {
            float nx = vp.x * 2f - 1f, ny = vp.y * 2f - 1f, d = vp.z;
            Vector3 cam;
            if (Orthographic) cam = new Vector3(nx * OrthoSize * Aspect, ny * OrthoSize, -d);
            else
            {
                float t = MathF.Tan(Fov * Mathf.Deg2Rad * 0.5f);
                cam = new Vector3(nx * d * t * Aspect, ny * d * t, -d);
            }
            if (ProjectionOverride.HasValue)
            {
                // 自訂投影：照反矩陣算
                MathUtil.Inverse(ViewProjection, out var inv);
                var ndcZ = 0f;
                var near = MathUtil.MulPoint(inv, new Vector3(nx, ny, ndcZ));
                return near;
            }
            return MathUtil.MulPoint(CameraToWorld, cam);
        }

        public Vector3 ScreenToViewport(Vector3 s)
        {
            var r = PixelRect;
            return new Vector3(r.width > 0 ? (s.x - r.x) / r.width : 0, r.height > 0 ? (s.y - r.y) / r.height : 0, s.z);
        }

        public Vector3 ViewportToScreen(Vector3 v)
        {
            var r = PixelRect;
            return new Vector3(r.x + v.x * r.width, r.y + v.y * r.height, v.z);
        }

        public Vector3 ScreenToWorld(Vector3 s) => ViewportToWorld(ScreenToViewport(s));

        public Ray ViewportRay(Vector2 vp)
        {
            var a = ViewportToWorld(new Vector3(vp.x, vp.y, Near));
            if (Orthographic) return new Ray(a, MathUtil.MulVector(CameraToWorld, new Vector3(0, 0, -1)).normalized);
            var o = Tr.Position;
            return new Ray(a, (a - o).normalized);
        }

        public static NCamera Main()
        {
            NCamera best = null;
            foreach (var c in All)
                if (c.Alive && c.IsActiveAndEnabled && c.Go.Tag == "MainCamera" && (best == null || c.Depth < best.Depth)) best = c;
            return best;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Camera
    {
        static NCamera C(Camera c) => Native.Self<NCamera>(c);
        public static Camera Current;

        public static float get_nearClipPlane(Camera self) => C(self).Near;
        public static void set_nearClipPlane(Camera self, float value) => C(self).Near = value;
        public static float get_farClipPlane(Camera self) => C(self).Far;
        public static void set_farClipPlane(Camera self, float value) => C(self).Far = value;
        public static float get_fieldOfView(Camera self) => C(self).Fov;
        public static void set_fieldOfView(Camera self, float value) => C(self).Fov = value;
        public static RenderingPath get_renderingPath(Camera self) => RenderingPath.Forward;
        public static void set_renderingPath(Camera self, RenderingPath value) { }
        public static RenderingPath get_actualRenderingPath(Camera self) => RenderingPath.Forward;
        public static bool get_allowHDR(Camera self) => C(self).AllowHDR;
        public static void set_allowHDR(Camera self, bool value) => C(self).AllowHDR = value;
        public static bool get_allowMSAA(Camera self) => C(self).AllowMSAA;
        public static void set_allowMSAA(Camera self, bool value) => C(self).AllowMSAA = value;
        public static float get_orthographicSize(Camera self) => C(self).OrthoSize;
        public static void set_orthographicSize(Camera self, float value) => C(self).OrthoSize = value;
        public static bool get_orthographic(Camera self) => C(self).Orthographic;
        public static void set_orthographic(Camera self, bool value) => C(self).Orthographic = value;
        public static OpaqueSortMode get_opaqueSortMode(Camera self) => C(self).OpaqueSort;
        public static void set_opaqueSortMode(Camera self, OpaqueSortMode value) => C(self).OpaqueSort = value;
        public static TransparencySortMode get_transparencySortMode(Camera self) => C(self).TransparencySort;
        public static void set_transparencySortMode(Camera self, TransparencySortMode value) => C(self).TransparencySort = value;
        public static void get_transparencySortAxis_Injected(Camera self, out Vector3 ret) => ret = C(self).TransparencySortAxis;
        public static void set_transparencySortAxis_Injected(Camera self, ref Vector3 value) => C(self).TransparencySortAxis = value;
        public static float get_depth(Camera self) => C(self).Depth;
        public static void set_depth(Camera self, float value) => C(self).Depth = value;
        public static float get_aspect(Camera self) => C(self).Aspect;
        public static void set_aspect(Camera self, float value) => C(self).AspectOverride = value;
        public static void ResetAspect(Camera self) => C(self).AspectOverride = -1;
        public static int get_cullingMask(Camera self) => C(self).CullingMask;
        public static void set_cullingMask(Camera self, int value) => C(self).CullingMask = value;
        public static int get_eventMask(Camera self) => C(self).EventMask;
        public static void set_eventMask(Camera self, int value) => C(self).EventMask = value;
        public static CameraType get_cameraType(Camera self) => CameraType.Game;
        public static CameraClearFlags get_clearFlags(Camera self) => C(self).ClearFlags;
        public static void set_clearFlags(Camera self, CameraClearFlags value) => C(self).ClearFlags = value;
        public static DepthTextureMode get_depthTextureMode(Camera self) => C(self).DepthTextureMode;
        public static void set_depthTextureMode(Camera self, DepthTextureMode value) => C(self).DepthTextureMode = value;
        public static int get_pixelWidth(Camera self) => (int)C(self).PixelRect.width;
        public static int get_pixelHeight(Camera self) => (int)C(self).PixelRect.height;
        public static int get_scaledPixelWidth(Camera self) => (int)C(self).PixelRect.width;
        public static int get_scaledPixelHeight(Camera self) => (int)C(self).PixelRect.height;
        public static RenderTexture get_targetTexture(Camera self) => C(self).TargetTexture;
        public static void set_targetTexture(Camera self, RenderTexture value) => C(self).TargetTexture = value;
        public static RenderTexture get_activeTexture(Camera self) => C(self).TargetTexture;
        public static int get_targetDisplay(Camera self) => C(self).TargetDisplay;
        public static void set_targetDisplay(Camera self, int value) => C(self).TargetDisplay = value;
        public static void ResetWorldToCameraMatrix(Camera self) => C(self).WorldToCameraOverride = null;
        public static void ResetProjectionMatrix(Camera self) => C(self).ProjectionOverride = null;
        public static float HorizontalToVerticalFieldOfView(float h, float aspect) => 2f * MathF.Atan(MathF.Tan(h * Mathf.Deg2Rad * 0.5f) / aspect) * Mathf.Rad2Deg;
        public static float VerticalToHorizontalFieldOfView(float v, float aspect) => 2f * MathF.Atan(MathF.Tan(v * Mathf.Deg2Rad * 0.5f) * aspect) * Mathf.Rad2Deg;
        public static Camera get_main() => (Camera)NCamera.Main()?.Managed;
        public static Camera get_current() => Current;
        public static int GetAllCamerasCount() => NCamera.All.Count;
        public static int GetAllCamerasImpl(Camera[] cam)
        {
            int n = Math.Min(cam.Length, NCamera.All.Count);
            for (int i = 0; i < n; i++) cam[i] = (Camera)NCamera.All[i].Managed;
            return n;
        }
        public static void Render(Camera self) => FrameRenderer.RenderCamera(C(self));
        public static void RenderDontRestore(Camera self) => FrameRenderer.RenderCamera(C(self));
        public static void SetupCurrent(Camera cur) => Current = cur;
        public static void CopyFrom(Camera self, Camera other) => C(self).CopyFrom(C(other));
        public static int get_commandBufferCount(Camera self) { int n = 0; foreach (var l in C(self).CommandBuffers.Values) n += l.Count; return n; }
        public static void RemoveCommandBuffers(Camera self, CameraEvent evt) => C(self).CommandBuffers.Remove(evt);
        public static void RemoveAllCommandBuffers(Camera self) => C(self).CommandBuffers.Clear();
        public static void AddCommandBufferImpl(Camera self, CameraEvent evt, CommandBuffer buffer)
        {
            var c = C(self);
            if (!c.CommandBuffers.TryGetValue(evt, out var l)) c.CommandBuffers[evt] = l = new List<CommandBuffer>();
            l.Add(buffer);
        }
        public static void RemoveCommandBufferImpl(Camera self, CameraEvent evt, CommandBuffer buffer) { if (C(self).CommandBuffers.TryGetValue(evt, out var l)) l.Remove(buffer); }
        public static CommandBuffer[] GetCommandBuffers(Camera self, CameraEvent evt) => C(self).CommandBuffers.TryGetValue(evt, out var l) ? l.ToArray() : Array.Empty<CommandBuffer>();
        public static void get_backgroundColor_Injected(Camera self, out Color ret) => ret = C(self).Background;
        public static void set_backgroundColor_Injected(Camera self, ref Color value) => C(self).Background = value;
        public static void get_rect_Injected(Camera self, out Rect ret) => ret = C(self).ViewportRect;
        public static void set_rect_Injected(Camera self, ref Rect value) => C(self).ViewportRect = value;
        public static void get_pixelRect_Injected(Camera self, out Rect ret) => ret = C(self).PixelRect;
        public static void set_pixelRect_Injected(Camera self, ref Rect value)
        {
            var c = C(self); var s = c.TargetSize;
            c.ViewportRect = new Rect(value.x / s.x, value.y / s.y, value.width / s.x, value.height / s.y);
        }
        public static void get_cameraToWorldMatrix_Injected(Camera self, out Matrix4x4 ret) => ret = C(self).CameraToWorld;
        public static void get_worldToCameraMatrix_Injected(Camera self, out Matrix4x4 ret) => ret = C(self).WorldToCamera;
        public static void set_worldToCameraMatrix_Injected(Camera self, ref Matrix4x4 value) => C(self).WorldToCameraOverride = value;
        public static void get_projectionMatrix_Injected(Camera self, out Matrix4x4 ret) => ret = C(self).Projection;
        public static void set_projectionMatrix_Injected(Camera self, ref Matrix4x4 value) => C(self).ProjectionOverride = value;
        public static void get_nonJitteredProjectionMatrix_Injected(Camera self, out Matrix4x4 ret) => ret = C(self).Projection;
        public static void set_nonJitteredProjectionMatrix_Injected(Camera self, ref Matrix4x4 value) { }
        public static void get_previousViewProjectionMatrix_Injected(Camera self, out Matrix4x4 ret) => ret = C(self).ViewProjection;
        public static void get_cullingMatrix_Injected(Camera self, out Matrix4x4 ret) => ret = C(self).ViewProjection;
        public static void set_cullingMatrix_Injected(Camera self, ref Matrix4x4 value) { }
        public static void ResetCullingMatrix(Camera self) { }
        public static void get_velocity_Injected(Camera self, out Vector3 ret) => ret = Vector3.zero;
        public static void WorldToScreenPoint_Injected(Camera self, ref Vector3 position, Camera.MonoOrStereoscopicEye eye, out Vector3 ret) => ret = C(self).WorldToScreen(position);
        public static void WorldToViewportPoint_Injected(Camera self, ref Vector3 position, Camera.MonoOrStereoscopicEye eye, out Vector3 ret) => ret = C(self).WorldToViewport(position);
        public static void ViewportToWorldPoint_Injected(Camera self, ref Vector3 position, Camera.MonoOrStereoscopicEye eye, out Vector3 ret) => ret = C(self).ViewportToWorld(position);
        public static void ScreenToWorldPoint_Injected(Camera self, ref Vector3 position, Camera.MonoOrStereoscopicEye eye, out Vector3 ret) => ret = C(self).ScreenToWorld(position);
        public static void ScreenToViewportPoint_Injected(Camera self, ref Vector3 position, out Vector3 ret) => ret = C(self).ScreenToViewport(position);
        public static void ViewportToScreenPoint_Injected(Camera self, ref Vector3 position, out Vector3 ret) => ret = C(self).ViewportToScreen(position);
        public static void ViewportPointToRay_Injected(Camera self, ref Vector2 pos, Camera.MonoOrStereoscopicEye eye, out Ray ret) => ret = C(self).ViewportRay(pos);
        public static void ScreenPointToRay_Injected(Camera self, ref Vector2 pos, Camera.MonoOrStereoscopicEye eye, out Ray ret)
        {
            var c = C(self); var v = c.ScreenToViewport(new Vector3(pos.x, pos.y, 0));
            ret = c.ViewportRay(new Vector2(v.x, v.y));
        }
        public static void GetFrustumPlaneSizeAt_Injected(Camera self, float distance, out Vector2 ret)
        {
            var c = C(self);
            if (c.Orthographic) ret = new Vector2(c.OrthoSize * 2f * c.Aspect, c.OrthoSize * 2f);
            else { float h = 2f * distance * MathF.Tan(c.Fov * Mathf.Deg2Rad * 0.5f); ret = new Vector2(h * c.Aspect, h); }
        }
    }
}
