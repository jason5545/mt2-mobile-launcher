// 從 unity-native/host-android 搬來（Phase 4 拆段 B，逐檔看過：Android API 手寫的宿主程式），只改命名空間與 GameActivity.Tag。
#nullable disable
// Android 的影片解碼器（VideoPlayer 用，引擎的 VideoDecoders.Open）：MediaExtractor + MediaCodec（系統的硬體解碼器）。
//   - 畫面：解到 SurfaceTexture（GL_TEXTURE_EXTERNAL_OES），Upload 時 updateTexImage，再用一個小 shader 照 getTransformMatrix 轉進一般的
//     RGBA8 貼圖（第 0 列是畫面最上面，同桌面版），引擎畫影片的那段就不用管 OES。
//   - 聲音：另一組 MediaExtractor + MediaCodec 解成 PCM（16 位元或 float），換成 48 kHz 立體聲 float，照時間戳寫進 PcmRing。
//   - 全部在遊戲執行緒上做（就是 GL 執行緒）：每格 Advance 時不等待地餵輸入、收輸出（MediaCodec 的同步模式，timeout 0），
//     輸出的畫面留著，等時間到了才 releaseOutputBuffer(render = true)；比 t 早又沒輪到的直接丟（render = false）。
//   - 準備好：第一格畫面送出去，而且聲音已經解出 0.25 秒（或沒有聲軌、聲軌讀完），開始播的時候聲音不會缺頭。
//   - 跳時間：兩個 codec flush，extractor 跳到 t 之前的關鍵格。
using System;
using System.Collections.Generic;
using Android.Graphics;
using Android.Media;
using Android.Views;
using UnityNative;
using UnityNative.Gl;

namespace Mt2Mobile.Launcher.GameHost
{
    public sealed unsafe class AndroidVideoDecoder : VideoDecoder
    {
        const uint TEXTURE_EXTERNAL_OES = 0x8D65;
        const string ConvVert = "#version 300 es\nlayout(location = 0) in vec2 POSITION;\nout vec2 vUv;\n" +
            "void main(){ vUv = vec2(POSITION.x * 0.5 + 0.5, 0.5 - POSITION.y * 0.5); gl_Position = vec4(POSITION, 0.0, 1.0); }";
        const string ConvFrag = "#version 300 es\n#extension GL_OES_EGL_image_external_essl3 : require\nprecision highp float;\n" +
            "uniform samplerExternalOES sTex;\nuniform mat4 uTex;\nin vec2 vUv;\nlayout(location = 0) out vec4 fragColor;\n" +
            "void main(){ fragColor = vec4(texture(sTex, (uTex * vec4(vUv, 0.0, 1.0)).xy).rgb, 1.0); }";
        static uint convProg, quadVbo, quadVao;

        readonly MediaExtractor vext, aext;
        readonly MediaCodec vcodec;
        MediaCodec acodec;
        readonly SurfaceTexture st;
        readonly Surface surface;
        readonly uint oesTex;
        uint fbo;
        int texW, texH;
        volatile bool frameAvailable;
        bool vInputDone, aInputDone, aOutputDone, rendered;
        long audioStart;
        int aRate = 48000, aChannels = 2;
        bool aFloat;
        readonly List<(int index, double pts)> pending = new List<(int, double)>();
        readonly MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        readonly float[] stMatrix = new float[16];
        byte[] bytes = new byte[0];
        float[] pcm = new float[0];

        public static void Install() => VideoDecoders.Open = p => new AndroidVideoDecoder(p);

        sealed class FrameListener : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
        {
            readonly AndroidVideoDecoder d;
            public FrameListener(AndroidVideoDecoder d) { this.d = d; }
            public void OnFrameAvailable(SurfaceTexture surfaceTexture) => d.frameAvailable = true;
        }

        AndroidVideoDecoder(string path)
        {
            vext = new MediaExtractor();
            vext.SetDataSource(path);
            int vi = -1, ai = -1;
            MediaFormat vf = null, af = null;
            for (int i = 0; i < vext.TrackCount; i++)
            {
                var f = vext.GetTrackFormat(i);
                var mime = f.GetString(MediaFormat.KeyMime) ?? "";
                if (vi < 0 && mime.StartsWith("video/")) { vi = i; vf = f; }
                else if (ai < 0 && mime.StartsWith("audio/")) { ai = i; af = f; }
            }
            if (vi < 0) { Error = "沒有影像軌：" + path; return; }
            vext.SelectTrack(vi);
            Width = vf.GetInteger(MediaFormat.KeyWidth);
            Height = vf.GetInteger(MediaFormat.KeyHeight);
            Length = vf.ContainsKey(MediaFormat.KeyDuration) ? vf.GetLong(MediaFormat.KeyDuration) / 1e6 : 0;
            FrameRate = vf.ContainsKey(MediaFormat.KeyFrameRate) ? vf.GetInteger(MediaFormat.KeyFrameRate) : 30;

            oesTex = GL.Gen(GL.glGenTextures);
            GL.BindTexture(TEXTURE_EXTERNAL_OES, oesTex);
            GL.glTexParameteri(TEXTURE_EXTERNAL_OES, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
            GL.glTexParameteri(TEXTURE_EXTERNAL_OES, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
            GL.glTexParameteri(TEXTURE_EXTERNAL_OES, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
            GL.glTexParameteri(TEXTURE_EXTERNAL_OES, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
            GL.BindTexture(TEXTURE_EXTERNAL_OES, 0);
            st = new SurfaceTexture((int)oesTex);
            st.SetOnFrameAvailableListener(new FrameListener(this));
            surface = new Surface(st);
            vcodec = MediaCodec.CreateDecoderByType(vf.GetString(MediaFormat.KeyMime));
            vcodec.Configure(vf, surface, null, MediaCodecConfigFlags.None);
            vcodec.Start();

            if (ai >= 0)
            {
                aext = new MediaExtractor();
                aext.SetDataSource(path);
                aext.SelectTrack(ai);
                if (af.ContainsKey(MediaFormat.KeySampleRate)) aRate = af.GetInteger(MediaFormat.KeySampleRate);
                if (af.ContainsKey(MediaFormat.KeyChannelCount)) aChannels = af.GetInteger(MediaFormat.KeyChannelCount);
                acodec = MediaCodec.CreateDecoderByType(af.GetString(MediaFormat.KeyMime));
                acodec.Configure(af, null, null, MediaCodecConfigFlags.None);
                acodec.Start();
                AudioChannels = 2;
            }
        }

        public override bool HasFrame =>
            rendered && (acodec == null || aOutputDone || Audio.End - audioStart >= AudioEngine.Rate / 4);

        public override void Seek(double t)
        {
            if (Error != null) return;
            long us = (long)(t * 1e6);
            pending.Clear(); // flush 之後索引都失效，不用 release
            vcodec.Flush();
            vext.SeekTo(us, MediaExtractorSeekTo.PreviousSync);
            vInputDone = false;
            rendered = false;
            audioStart = (long)Math.Round(t * AudioEngine.Rate);
            Audio.Reset(audioStart);
            if (acodec != null)
            {
                acodec.Flush();
                aext.SeekTo(us, MediaExtractorSeekTo.PreviousSync);
                aInputDone = aOutputDone = false;
            }
        }

        static void Feed(MediaCodec codec, MediaExtractor ext, ref bool done)
        {
            while (!done)
            {
                int i = codec.DequeueInputBuffer(0);
                if (i < 0) return;
                var buf = codec.GetInputBuffer(i);
                int n = ext.ReadSampleData(buf, 0);
                if (n < 0) { codec.QueueInputBuffer(i, 0, 0, 0, MediaCodecBufferFlags.EndOfStream); done = true; return; }
                codec.QueueInputBuffer(i, 0, n, ext.SampleTime, MediaCodecBufferFlags.None);
                ext.Advance();
            }
        }

        public override void Advance(double t)
        {
            if (Error != null) return;
            try
            {
                Feed(vcodec, vext, ref vInputDone);
                while (true)
                {
                    int o = vcodec.DequeueOutputBuffer(info, 0);
                    if (o == (int)MediaCodecInfoState.TryAgainLater) break;
                    if (o < 0) continue; // 格式改變之類
                    if (info.Size == 0 && (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) { vcodec.ReleaseOutputBuffer(o, false); break; }
                    pending.Add((o, info.PresentationTimeUs / 1e6));
                }
                // 時間到了的畫面：最新的那格送去 SurfaceTexture，前面的丟掉；還沒有畫面時第一格不管時間先送
                int pick = -1;
                for (int k = 0; k < pending.Count; k++) if (pending[k].pts <= t + 1e-4) pick = k;
                if (pick < 0 && !rendered && pending.Count > 0) pick = 0;
                for (int k = 0; k <= pick; k++) vcodec.ReleaseOutputBuffer(pending[k].index, k == pick);
                if (pick >= 0) { pending.RemoveRange(0, pick + 1); rendered = true; }
                if (acodec != null) PumpAudio();
            }
            catch (Exception e) { Error = "MediaCodec：" + e.Message; }
        }

        void PumpAudio()
        {
            Feed(acodec, aext, ref aInputDone);
            while (!aOutputDone && Audio.CanWrite(AudioEngine.Rate / 4))
            {
                int o = acodec.DequeueOutputBuffer(info, 0);
                if (o == (int)MediaCodecInfoState.TryAgainLater) break;
                if (o == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    var f = acodec.OutputFormat;
                    if (f.ContainsKey(MediaFormat.KeySampleRate)) aRate = f.GetInteger(MediaFormat.KeySampleRate);
                    if (f.ContainsKey(MediaFormat.KeyChannelCount)) aChannels = f.GetInteger(MediaFormat.KeyChannelCount);
                    aFloat = f.ContainsKey(MediaFormat.KeyPcmEncoding) && f.GetInteger(MediaFormat.KeyPcmEncoding) == (int)Encoding.PcmFloat;
                    continue;
                }
                if (o < 0) continue;
                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) aOutputDone = true;
                if (info.Size > 0)
                {
                    var buf = acodec.GetOutputBuffer(o);
                    if (bytes.Length < info.Size) bytes = new byte[info.Size];
                    buf.Position(info.Offset);
                    buf.Get(bytes, 0, info.Size);
                    WritePcm(info.Size, info.PresentationTimeUs);
                }
                acodec.ReleaseOutputBuffer(o, false);
            }
        }

        /// <summary>codec 輸出的 PCM（aRate、aChannels，16 位元或 float）→ 48 kHz 立體聲 float</summary>
        void WritePcm(int size, long ptsUs)
        {
            int ch = Math.Max(1, aChannels), bps = aFloat ? 4 : 2;
            int inFrames = size / (ch * bps);
            int outFrames = (int)((long)inFrames * AudioEngine.Rate / Math.Max(1, aRate));
            if (pcm.Length < outFrames * 2) pcm = new float[outFrames * 2];
            fixed (byte* b = bytes)
            {
                for (int k = 0; k < outFrames; k++)
                {
                    int src = (int)((long)k * aRate / AudioEngine.Rate);
                    float l, r;
                    if (aFloat) { var f = (float*)b; l = f[src * ch]; r = ch > 1 ? f[src * ch + 1] : l; }
                    else { var s = (short*)b; l = s[src * ch] / 32768f; r = ch > 1 ? s[src * ch + 1] / 32768f : l; }
                    pcm[k * 2] = l; pcm[k * 2 + 1] = r;
                }
            }
            Audio.Write((long)Math.Round(ptsUs * (AudioEngine.Rate / 1e6)), pcm, outFrames, block: false);
        }

        public override void Upload()
        {
            if (Error != null || !frameAvailable) return;
            frameAvailable = false;
            st.UpdateTexImage();
            st.GetTransformMatrix(stMatrix);
            if (convProg == 0) InitGl();
            if (Texture == 0 || texW != Width || texH != Height)
            {
                if (Texture == 0) Texture = GL.Gen(GL.glGenTextures);
                GL.BindTexture(GL.TEXTURE_2D, Texture);
                GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, Width, Height, 0, GL.RGBA, GL.UNSIGNED_BYTE, null);
                GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
                GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
                GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
                GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
                if (fbo == 0) fbo = GL.Gen(GL.glGenFramebuffers);
                GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
                GL.glFramebufferTexture2D(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.TEXTURE_2D, Texture, 0);
                texW = Width; texH = Height;
            }
            GL.glBindFramebuffer(GL.FRAMEBUFFER, fbo);
            GL.glViewport(0, 0, texW, texH);
            GL.Disable(GL.BLEND); GL.Disable(GL.DEPTH_TEST); GL.Disable(GL.STENCIL_TEST); GL.Disable(GL.CULL_FACE); GL.Disable(GL.SCISSOR_TEST);
            GL.ColorMask(1, 1, 1, 1);
            GL.UseProgram(convProg);
            GL.ActiveTexture(GL.TEXTURE0);
            GL.BindTexture(TEXTURE_EXTERNAL_OES, oesTex);
            GL.glUniform1i(GL.Uniform(convProg, "sTex"), 0);
            fixed (float* m = stMatrix) GL.glUniformMatrix4fv(GL.Uniform(convProg, "uTex"), 1, 0, m);
            GL.glBindVertexArray(quadVao);
            GL.glDrawArrays(GL.TRIANGLE_STRIP, 0, 4);
            GL.glBindVertexArray(0);
            GL.BindTexture(TEXTURE_EXTERNAL_OES, 0);
        }

        static void InitGl()
        {
            var v = GL.Compile(GL.VERTEX_SHADER, ConvVert, out var vl);
            var f = GL.Compile(GL.FRAGMENT_SHADER, ConvFrag, out var fl);
            convProg = GL.Link(v, f, out var ll);
            if (convProg == 0) throw new Exception("影片轉換 shader 失敗：" + vl + fl + ll);
            quadVbo = GL.Gen(GL.glGenBuffers);
            quadVao = GL.Gen(GL.glGenVertexArrays);
            GL.glBindVertexArray(quadVao);
            GL.glBindBuffer(GL.ARRAY_BUFFER, quadVbo);
            float* q = stackalloc float[8] { -1, -1, 1, -1, -1, 1, 1, 1 };
            GL.glBufferData(GL.ARRAY_BUFFER, 32, q, GL.STATIC_DRAW);
            GL.glEnableVertexAttribArray(0);
            GL.glVertexAttribPointer(0, 2, GL.FLOAT, 0, 8, null);
            GL.glBindVertexArray(0);
        }

        public override void Dispose()
        {
            Audio.Close();
            try { vcodec?.Stop(); } catch { }
            try { vcodec?.Release(); } catch { }
            try { acodec?.Stop(); } catch { }
            try { acodec?.Release(); } catch { }
            vext?.Release(); aext?.Release();
            surface?.Release(); st?.Release();
            uint t = oesTex; if (t != 0) GL.DeleteTextures(1, &t);
            if (Texture != 0) { t = Texture; GL.DeleteTextures(1, &t); Texture = 0; }
            if (fbo != 0) { uint f = fbo; GL.glDeleteFramebuffers(1, &f); fbo = 0; }
        }
    }
}
