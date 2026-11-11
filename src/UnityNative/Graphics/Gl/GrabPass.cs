// GrabPass（ShaderLab 的 pass type 2）：畫用到它的物件之前，把目前的目標（這台攝影機的 CameraTarget，不在攝影機裡就是主畫面）
// 複製進一張 RT，後面的 pass 取樣的屬性綁那張。沒取名的（_GrabTexture）每個物件畫之前各抓一次；取了名的每格只在第一個用到的物件抓一次
// （Unity 手冊 GrabPass；同 TS 版 sceneRenderer.ts 的 grabScreen）。沒抓的話屬性落到預設的白貼圖，
// MT2 的 FX_RefractionUnlit_S（護甲特效的 RefractiveShield，混色 One Zero）會畫成一塊白色長方形。
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityNative.Icalls;

namespace UnityNative.Gl
{
    public sealed unsafe partial class GlGpu
    {
        readonly Dictionary<string, RenderTexture> grabRts = new Dictionary<string, RenderTexture>();
        /// <summary>這一格已經抓過的具名 GrabPass（Frame 開頭清空）</summary>
        readonly HashSet<string> grabbed = new HashSet<string>();

        /// <summary>抓畫面進這個 GrabPass 的貼圖，並在 ov 綁上（接著畫的 pass 用同一個 ov）</summary>
        void Grab(PassState pass)
        {
            var name = string.IsNullOrEmpty(pass.GrabTexture) ? "_GrabTexture" : pass.GrabTexture;
            var cur = execCam != null && camTarget.Ok ? camTarget : MainTgt();
            // 格式跟著目前的目標：攝影機畫進中繼 RT（HDR）時用它的格式，其他是 sRGB 8 位元（同主畫面）
            var inter = execCam != null ? Native.Of<NRenderTexture>(camIntermediate) : null;
            var fmt = inter != null && camTarget.Fbo == ((GpuResources.RtTarget)inter.Fbo)?.Fbo ? inter.ColorFormat : GraphicsFormat.R8G8B8A8_SRGB;
            if (!grabRts.TryGetValue(name, out var rt) || !rt)
            {
                var desc = new RenderTextureDescriptor(cur.W, cur.H, fmt, 0);
                rt = UnityEngine_RenderTexture.GetTemporary_Internal_Injected(ref desc);
                rt.name = "GrabPass " + name;
                grabRts[name] = rt;
            }
            var n = Native.Of<NRenderTexture>(rt);
            if (n.Width != cur.W || n.Height != cur.H || n.ColorFormat != fmt)
            {
                n.Width = cur.W; n.Height = cur.H; n.ColorFormat = fmt; n.SRGB = GraphicsFormatUtility.IsSRGBFormat(fmt); n.GpuDirty = true;
            }
            bool named = !string.IsNullOrEmpty(pass.GrabTexture);
            if (!named || grabbed.Add(name))
            {
                int* vp = stackalloc int[4];
                int scissor;
                GL.glGetIntegerv(GL.VIEWPORT, vp);
                GL.glGetIntegerv(GL.SCISSOR_TEST, &scissor);
                var dst = RtTgt(n);
                if (scissor != 0) GL.Disable(GL.SCISSOR_TEST);
                GL.glBindFramebuffer(GL.READ_FRAMEBUFFER, cur.Fbo);
                GL.glBindFramebuffer(GL.DRAW_FRAMEBUFFER, dst.Fbo);
                GL.glBlitFramebuffer(0, 0, cur.W, cur.H, 0, 0, dst.W, dst.H, GL.COLOR_BUFFER_BIT, GL.NEAREST);
                GL.glBindFramebuffer(GL.FRAMEBUFFER, cur.Fbo);
                GL.glViewport(vp[0], vp[1], vp[2], vp[3]);
                if (scissor != 0) GL.Enable(GL.SCISSOR_TEST);
                if (DumpNext) Dump.Add($"  GrabPass {name}：{cur.W}x{cur.H} {fmt}");
            }
            ov.Sheet.Textures[PropertyIds.Of(name)] = new TexEnv { Texture = rt, Scale = Vector2.one };
        }
    }
}
