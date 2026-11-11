// VideoPlayer：影片由宿主給的解碼器（VideoDecoders.Open；macOS 是 AVAssetReader、Android 是 MediaCodec）解成畫面與 PCM，
// 引擎照 Unity 的流程與事件時序播：
//   - Prepare（或還沒準備好就 Play）→ 開解碼器 → 第一格畫面出來時 isPrepared、prepareCompleted → 有 Play 的話接著 started、時鐘開始走
//     （waitForFirstFrame：第一格出來之前時間不動）。
//   - 時鐘：每格加不受 timeScale 影響的 deltaTime × playbackSpeed（同混音器的時鐘，聲音跟畫面自然對齊；無畫面測試一樣推進）。
//   - 播到 length：發 loopPointReached；isLooping 從頭再來，否則停下（isPlaying 變 false，畫面不再畫，同 TS 版）。
//   - 畫面：CameraFarPlane／CameraNearPlane 畫在 targetCamera 那一趟（遠平面在清背景之後、近平面在場景之後），照 aspectRatio 縮放、乘
//     targetCameraAlpha；RenderTexture 模式每格畫進 targetTexture。MaterialOverride、APIOnly 還沒做（只有聲音與流程）。GlGpu.VideoPlanes.cs。
//   - 聲音：audioOutputMode 是 AudioSource 時經過 SetTargetAudioSource 給的 AudioSource（volume、mute、混音器群組、AudioListener.volume），
//     Direct 時照 SetDirectAudioVolume／Mute 乘 AudioListener.volume；None、APIOnly 不出聲。PCM 進引擎的混音器（AudioEngine 的 IAudioFeed）。
//   - Stop：時間歸零、放掉解碼器（isPrepared 變 false）。Pause：畫面與聲音停在原位。設 time：解碼器跳過去。
// 沒有解碼器（非 macOS 的桌面）或檔案打不開時照舊：當作播完（發 loopPointReached），同 TS 版「讀不到當作播完」，遊戲不會卡在黑畫面。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using UnityEngine;
using UnityEngine.Video;

namespace UnityNative
{
    /// <summary>宿主實作的影片解碼器。Advance、Upload、Seek、Dispose 都在遊戲主執行緒呼叫（Upload 時 GL context 在這條執行緒上）</summary>
    public abstract class VideoDecoder : IDisposable
    {
        public int Width, Height;
        public double Length, FrameRate;
        /// <summary>聲軌的聲道數（0 = 沒有聲軌）。PCM 一律換成 AudioEngine.Rate 的立體聲交錯 float，照絕對樣本位置寫進 Audio</summary>
        public int AudioChannels;
        public readonly PcmRing Audio = new PcmRing();
        /// <summary>打不開、解碼失敗時的原因</summary>
        public volatile string Error;
        /// <summary>GL 的 TEXTURE_2D（RGBA8，存的是影片原本的 gamma 值，第 0 列是畫面最上面）；Upload 之後才有</summary>
        public uint Texture;
        /// <summary>已經有畫面可以顯示（第一格解出來了）</summary>
        public abstract bool HasFrame { get; }
        /// <summary>選出 t 秒該顯示的那一格（比 t 早的丟掉）。沒有 GL 也要能叫（無畫面測試）</summary>
        public abstract void Advance(double t);
        /// <summary>有 GL 的那一趟：把選好的那一格放進 Texture</summary>
        public abstract void Upload();
        /// <summary>從 t 秒重新解碼（設 time、循環）；聲音的 PcmRing 也從 t 重來</summary>
        public abstract void Seek(double t);
        public abstract void Dispose();
    }

    public static class VideoDecoders
    {
        /// <summary>宿主掛上：檔案路徑 → 解碼器（打不開回 null 或 Error 有值）。沒掛就是不解碼</summary>
        public static Func<string, VideoDecoder> Open;
    }

    /// <summary>照絕對樣本位置存的立體聲 PCM（解碼執行緒寫、混音器讀）。寫的一方最多領先讀的位置 Capacity，超過就等</summary>
    public sealed class PcmRing
    {
        public readonly int Capacity = AudioEngine.Rate * 4;
        readonly float[] buf;
        readonly object gate = new object();
        long start, end, readPos;
        bool closed;

        public PcmRing() { buf = new float[Capacity * 2]; }

        public long ReadPos { get { lock (gate) return readPos; } }
        public long End { get { lock (gate) return end; } }

        /// <summary>從 pos 重來（也解除 Close）</summary>
        public void Reset(long pos)
        {
            lock (gate) { start = end = readPos = pos; closed = false; Monitor.PulseAll(gate); }
        }

        public void Close()
        {
            lock (gate) { closed = true; Monitor.PulseAll(gate); }
        }

        /// <summary>現在能不能不等就寫進 frames 個樣本（不能等的解碼迴圈用）</summary>
        public bool CanWrite(int frames) { lock (gate) return end + frames - readPos <= Capacity; }

        /// <summary>寫進 [at, at+frames)：跟已有的重疊的部分略過，中間空出來的補 0；太前面就等（block = false 時直接回 false）</summary>
        public bool Write(long at, float[] src, int frames, bool block = true)
        {
            lock (gate)
            {
                int off = 0;
                if (at < end) { int skip = (int)Math.Min(frames, end - at); off = skip; at += skip; frames -= skip; }
                if (frames <= 0) return true;
                if (at > end + Capacity || at < readPos) { start = end = at; }
                while (!closed && Math.Max(at, end) + frames - readPos > Capacity)
                {
                    if (!block) return false;
                    Monitor.Wait(gate, 50);
                }
                if (closed) return false;
                for (; end < at; end++) { long i = end % Capacity; buf[i * 2] = buf[i * 2 + 1] = 0; }
                for (int k = 0; k < frames; k++)
                {
                    long i = (end + k) % Capacity;
                    buf[i * 2] = src[(off + k) * 2];
                    buf[i * 2 + 1] = src[(off + k) * 2 + 1];
                }
                end += frames;
                if (end - start > Capacity) start = end - Capacity;
                return true;
            }
        }

        /// <summary>讀 [at, at+frames) 到 dst（交錯立體聲），還沒解出來的是 0</summary>
        public void Read(long at, float[] dst, int frames)
        {
            lock (gate)
            {
                for (int k = 0; k < frames; k++)
                {
                    long p = at + k;
                    if (p >= start && p < end) { long i = p % Capacity; dst[k * 2] = buf[i * 2]; dst[k * 2 + 1] = buf[i * 2 + 1]; }
                    else dst[k * 2] = dst[k * 2 + 1] = 0;
                }
                readPos = at + frames;
                if (start < readPos - AudioEngine.Rate) start = Math.Min(end, readPos - AudioEngine.Rate);
                Monitor.PulseAll(gate);
            }
        }
    }

    public sealed class NVideoPlayer : NBehaviour
    {
        public VideoSource Source;
        public string Url = "";
        public VideoClip Clip;
        public VideoRenderMode RenderMode;
        public RenderTexture TargetTexture;
        public Camera TargetCamera;
        public VideoAspectRatio AspectRatio = VideoAspectRatio.FitHorizontally;
        public VideoAudioOutputMode AudioOutput = VideoAudioOutputMode.AudioSource;
        public AudioSource TargetAudio;
        public float DirectVolume = 1;
        public bool DirectMute, AudioTrackEnabled = true;
        public bool PlayOnAwake = true, Looping, Prepared, Playing, Paused, WaitForFirstFrame = true, SkipOnDrop = true, SendFrameReady;
        public float PlaybackSpeed = 1, TargetCameraAlpha = 1;
        public double Time;
        public VideoDecoder Decoder;
        int state; // 0 閒置、1 準備中、2 播放中
        Feed feed;
        public static readonly List<NVideoPlayer> All = new List<NVideoPlayer>();
        static readonly bool debug = Environment.GetEnvironmentVariable("UN_VIDEO_DEBUG") == "1";

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Source = (VideoSource)(int)Serializer.D(d["m_DataSource"]);
            Url = d["m_Url"]?.ToString() ?? "";
            Clip = Serializer.Ref(d["m_VideoClip"], typeof(VideoClip), ctx) as VideoClip;
            RenderMode = (VideoRenderMode)(int)Serializer.D(d["m_RenderMode"]);
            TargetTexture = Serializer.Ref(d["m_TargetTexture"], typeof(RenderTexture), ctx) as RenderTexture;
            TargetCamera = Serializer.Ref(d["m_TargetCamera"], typeof(Camera), ctx) as Camera;
            PlayOnAwake = d["m_PlayOnAwake"] == null || Serializer.D(d["m_PlayOnAwake"]) != 0;
            Looping = Serializer.D(d["m_Looping"]) != 0;
            if (d["m_PlaybackSpeed"] != null) PlaybackSpeed = (float)Serializer.D(d["m_PlaybackSpeed"]);
            if (d["m_AspectRatio"] != null) AspectRatio = (VideoAspectRatio)(int)Serializer.D(d["m_AspectRatio"]);
            if (d["m_AudioOutputMode"] != null) AudioOutput = (VideoAudioOutputMode)(int)Serializer.D(d["m_AudioOutputMode"]);
            if (d["m_TargetCameraAlpha"] != null) TargetCameraAlpha = (float)Serializer.D(d["m_TargetCameraAlpha"]);
            if (d["m_WaitForFirstFrame"] != null) WaitForFirstFrame = Serializer.D(d["m_WaitForFirstFrame"]) != 0;
            if (d["m_SkipOnDrop"] != null) SkipOnDrop = Serializer.D(d["m_SkipOnDrop"]) != 0;
            if (d["m_TargetAudioSources"] is JsonArray srcs && srcs.Count > 0) TargetAudio = Serializer.Ref(srcs[0], typeof(AudioSource), ctx) as AudioSource;
            if (d["m_DirectAudioVolumes"] is JsonArray vols && vols.Count > 0) DirectVolume = (float)Serializer.D(vols[0]);
            if (d["m_DirectAudioMutes"] is JsonArray mutes && mutes.Count > 0) DirectMute = Serializer.D(mutes[0]) != 0;
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NVideoPlayer)src;
            Source = s.Source; Url = s.Url; Clip = s.Clip; RenderMode = s.RenderMode; TargetTexture = s.TargetTexture; TargetCamera = s.TargetCamera;
            PlayOnAwake = s.PlayOnAwake; Looping = s.Looping; PlaybackSpeed = s.PlaybackSpeed; AspectRatio = s.AspectRatio; AudioOutput = s.AudioOutput;
            TargetAudio = s.TargetAudio; DirectVolume = s.DirectVolume; DirectMute = s.DirectMute; TargetCameraAlpha = s.TargetCameraAlpha;
            WaitForFirstFrame = s.WaitForFirstFrame; SkipOnDrop = s.SkipOnDrop;
        }

        public override void OnActiveChanged()
        {
            bool on = Alive && IsActiveAndEnabled;
            if (on && !All.Contains(this)) { All.Add(this); if (PlayOnAwake && HasSource) Play(); }
            else if (!on && All.Contains(this)) { All.Remove(this); Stop(); }
        }

        public override void OnDestroyNative() { All.Remove(this); Stop(); }

        VideoPlayer V => (VideoPlayer)Managed;

        bool HasSource => Source == VideoSource.Url ? !string.IsNullOrEmpty(Url) : (object)Clip != null;

        /// <summary>url 的檔案路徑（file:// 拿掉）</summary>
        string FilePath()
        {
            if (Source != VideoSource.Url) return null; // VideoClip：匯入資料沒有影片檔，還沒做
            var u = Url ?? "";
            if (u.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) u = Uri.UnescapeDataString(new Uri(u).LocalPath);
            return u;
        }

        public double Length => Decoder?.Length ?? 0;
        public double FrameRate => Decoder?.FrameRate ?? 0;

        /// <summary>這一格要不要畫（繪製端讀）</summary>
        public bool Showing => Alive && (Playing || Paused) && state == 2 && Decoder != null && Decoder.HasFrame && Decoder.Texture != 0;

        public void Prepare()
        {
            if (Prepared || state == 1) return;
            if (!HasSource) return;
            state = 1;
        }

        public void Play()
        {
            Paused = false;
            if (feed != null) feed.Hold = false;
            if (Playing) return;
            if (!HasSource) { Engine.Log(LogType.Warning, "VideoPlayer：沒有 url 也沒有 clip，不能播", this); return; }
            Playing = true;
            if (!Prepared) state = 1; else StartPlayback();
        }

        public void Pause()
        {
            if (!Prepared && state != 1) Prepare();
            Paused = true;
            if (feed != null) feed.Hold = true;
        }

        public void Stop()
        {
            Playing = false; Paused = false; Time = 0; state = 0; Prepared = false;
            StopAudio();
            Decoder?.Dispose();
            Decoder = null;
        }

        public void SetTime(double t)
        {
            Time = Math.Max(0, t);
            if (Decoder != null) { Decoder.Seek(Time); Decoder.Advance(Time); }
            if (feed != null) feed.Pos = (long)Math.Round(Time * AudioEngine.Rate);
        }

        void StartPlayback()
        {
            state = 2;
            StartAudio();
            if (debug) Engine.Config.LogSink($"[Video] 開始播 {Url}（{Decoder?.Width}x{Decoder?.Height}，{Length:0.0} 秒，第 {Engine.Time.FrameCount} 格）");
            Fire("started");
        }

        void StartAudio()
        {
            StopAudio();
            if (Decoder == null || Decoder.AudioChannels == 0 || !AudioTrackEnabled) return;
            if (AudioOutput != VideoAudioOutputMode.AudioSource && AudioOutput != VideoAudioOutputMode.Direct) return;
            feed = new Feed { Player = this, Pos = (long)Math.Round(Time * AudioEngine.Rate), Hold = Paused };
            AudioEngine.AddFeed(feed);
        }

        void StopAudio()
        {
            if (feed != null) feed.Dead = true;
            feed = null;
        }

        /// <summary>影片的聲音交給混音器：照播放位置讀解碼器的 PCM</summary>
        sealed class Feed : IAudioFeed
        {
            public NVideoPlayer Player;
            public long Pos;
            public bool Hold, Dead;
            public bool Live => !Dead && Player.Alive && Player.Decoder != null;
            bool IAudioFeed.Hold => Hold || !Player.Playing;
            public float Gain
            {
                get
                {
                    var p = Player;
                    if (p.AudioOutput == VideoAudioOutputMode.Direct) return p.DirectMute ? 0 : p.DirectVolume * AudioEngine.ListenerVolume;
                    var s = Native.Of<NAudioSource>(p.TargetAudio);
                    if (s == null || !s.Alive) return 0;
                    if (AudioEngine.ListenerPaused && !s.IgnoreListenerPause) return 0;
                    return AudioEngine.SourceGain(s, 1);
                }
            }

            public void Read(float[] dst, int frames)
            {
                Player.Decoder.Audio.Read(Pos, dst, frames);
                Pos += frames;
            }
        }

        void Fire(string what)
        {
            try
            {
                switch (what)
                {
                    case "prepared": VideoPlayer.InvokePrepareCompletedCallback_Internal(V); break;
                    case "started": VideoPlayer.InvokeStartedCallback_Internal(V); break;
                    case "loop": VideoPlayer.InvokeLoopPointReachedCallback_Internal(V); break;
                }
            }
            catch (Exception e) { Engine.LogException(e, this); }
        }

        /// <summary>沒辦法解碼：照舊當作播完（TS 版讀不到也是這樣），Unity 這時會發 errorReceived</summary>
        void Unplayable(string why)
        {
            Engine.Log(LogType.Warning, "VideoPlayer：" + Url + " 不能播（" + why + "），當作播完", this);
            Decoder?.Dispose();
            Decoder = null;
            Prepared = true;
            Fire("prepared");
            bool wasPlaying = Playing;
            Playing = false;
            state = 0;
            if (wasPlaying) { Fire("started"); Fire("loop"); }
        }

        public void Tick()
        {
            if (!Alive) return;
            if (state == 1)
            {
                if (Decoder == null)
                {
                    var path = FilePath();
                    if (VideoDecoders.Open == null) { Unplayable("這個平台沒有影片解碼器"); return; }
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { Unplayable("找不到檔案 " + path); return; }
                    try { Decoder = VideoDecoders.Open(path); }
                    catch (Exception e) { Unplayable(e.Message); return; }
                    if (Decoder == null) { Unplayable("解碼器打不開"); return; }
                    Decoder.Seek(Time);
                    if (debug) Engine.Config.LogSink($"[Video] 準備 {path}（第 {Engine.Time.FrameCount} 格）");
                }
                if (Decoder.Error != null) { Unplayable(Decoder.Error); return; }
                Decoder.Advance(Time);
                if (!Decoder.HasFrame) return;
                Prepared = true;
                if (debug) Engine.Config.LogSink($"[Video] 準備好了（第 {Engine.Time.FrameCount} 格）");
                Fire("prepared");
                if (Playing) StartPlayback();
                else state = 0;
                return;
            }
            if (state != 2 || !Playing || Paused || Decoder == null) return;
            if (Decoder.Error != null) { Engine.Log(LogType.Warning, "VideoPlayer：" + Decoder.Error, this); }
            Time += Engine.Time.UnscaledDeltaTime * PlaybackSpeed;
            double len = Length;
            if (len > 0 && Time >= len)
            {
                if (debug) Engine.Config.LogSink($"[Video] 播到結尾 {Time:0.00} 秒（第 {Engine.Time.FrameCount} 格）");
                Fire("loop");
                if (!Alive || Decoder == null) return; // 事件裡 Stop 或銷毀了
                if (Looping)
                {
                    SetTime(Time - len >= len ? 0 : Time - len);
                }
                else
                {
                    Playing = false;
                    state = 0;
                    StopAudio();
                    return;
                }
            }
            Decoder.Advance(Time);
        }

        public static void Register()
        {
            Loader.Factories["VideoPlayer"] = () => new NVideoPlayer();
            Engine.PreUpdate.Add(() => { foreach (var v in All.ToArray()) v.Tick(); });
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Video_VideoPlayer
    {
        static NVideoPlayer V(VideoPlayer v) => Native.Self<NVideoPlayer>(v);
        public static VideoSource get_source(VideoPlayer self) => V(self).Source;
        public static void set_source(VideoPlayer self, VideoSource value) => V(self).Source = value;
        public static string get_url(VideoPlayer self) => V(self).Url;
        public static void set_url(VideoPlayer self, string value)
        {
            // url 跟 clip 後設的優先（Unity 文件）：設 url 就把 source 換成 Url，設 clip 換成 VideoClip
            var v = V(self);
            value ??= "";
            if (value != v.Url && v.Decoder != null) { bool playing = v.Playing; v.Stop(); v.Url = value; v.Source = VideoSource.Url; if (playing) v.Play(); return; }
            v.Url = value;
            v.Source = VideoSource.Url;
        }
        public static VideoClip get_clip(VideoPlayer self) => V(self).Clip;
        public static void set_clip(VideoPlayer self, VideoClip value) { V(self).Clip = value; V(self).Source = VideoSource.VideoClip; }
        public static VideoRenderMode get_renderMode(VideoPlayer self) => V(self).RenderMode;
        public static void set_renderMode(VideoPlayer self, VideoRenderMode value) => V(self).RenderMode = value;
        public static Camera get_targetCamera(VideoPlayer self) => V(self).TargetCamera;
        public static void set_targetCamera(VideoPlayer self, Camera value) => V(self).TargetCamera = value;
        public static RenderTexture get_targetTexture(VideoPlayer self) => V(self).TargetTexture;
        public static void set_targetTexture(VideoPlayer self, RenderTexture value) => V(self).TargetTexture = value;
        public static float get_targetCameraAlpha(VideoPlayer self) => V(self).TargetCameraAlpha;
        public static void set_targetCameraAlpha(VideoPlayer self, float value) => V(self).TargetCameraAlpha = Math.Clamp(value, 0, 1);
        public static VideoAspectRatio get_aspectRatio(VideoPlayer self) => V(self).AspectRatio;
        public static void set_aspectRatio(VideoPlayer self, VideoAspectRatio value) => V(self).AspectRatio = value;
        public static Texture get_texture(VideoPlayer self) => V(self).TargetTexture;
        public static void Prepare(VideoPlayer self) => V(self).Prepare();
        public static bool get_isPrepared(VideoPlayer self) => V(self).Prepared;
        public static bool get_playOnAwake(VideoPlayer self) => V(self).PlayOnAwake;
        public static void set_playOnAwake(VideoPlayer self, bool value) => V(self).PlayOnAwake = value;
        public static void Play(VideoPlayer self) => V(self).Play();
        public static void Pause(VideoPlayer self) => V(self).Pause();
        public static void Stop(VideoPlayer self) => V(self).Stop();
        public static bool get_isPlaying(VideoPlayer self) => V(self).Playing && !V(self).Paused;
        public static bool get_isPaused(VideoPlayer self) => V(self).Paused;
        public static bool get_canSetTime(VideoPlayer self) => true;
        public static double get_time(VideoPlayer self) => V(self).Time;
        public static void set_time(VideoPlayer self, double value) => V(self).SetTime(value);
        public static double get_clockTime(VideoPlayer self) => V(self).Time;
        public static long get_frame(VideoPlayer self) => (long)Math.Floor(V(self).Time * V(self).FrameRate);
        public static void set_frame(VideoPlayer self, long value) { var v = V(self); if (v.FrameRate > 0) v.SetTime(value / v.FrameRate); }
        public static bool get_canStep(VideoPlayer self) => true;
        public static void StepForward(VideoPlayer self) { var v = V(self); if (v.FrameRate > 0) v.SetTime(v.Time + 1 / v.FrameRate); }
        public static float get_playbackSpeed(VideoPlayer self) => V(self).PlaybackSpeed;
        public static void set_playbackSpeed(VideoPlayer self, float value) => V(self).PlaybackSpeed = Math.Max(0, value);
        public static bool get_canSetPlaybackSpeed(VideoPlayer self) => true;
        public static bool get_isLooping(VideoPlayer self) => V(self).Looping;
        public static void set_isLooping(VideoPlayer self, bool value) => V(self).Looping = value;
        public static bool get_skipOnDrop(VideoPlayer self) => V(self).SkipOnDrop;
        public static void set_skipOnDrop(VideoPlayer self, bool value) => V(self).SkipOnDrop = value;
        public static bool get_canSetSkipOnDrop(VideoPlayer self) => true;
        public static bool get_waitForFirstFrame(VideoPlayer self) => V(self).WaitForFirstFrame;
        public static void set_waitForFirstFrame(VideoPlayer self, bool value) => V(self).WaitForFirstFrame = value;
        public static bool get_sendFrameReadyEvents(VideoPlayer self) => V(self).SendFrameReady;
        public static void set_sendFrameReadyEvents(VideoPlayer self, bool value) => V(self).SendFrameReady = value;
        public static ulong get_frameCount(VideoPlayer self) => (ulong)Math.Round(V(self).Length * V(self).FrameRate);
        public static float get_frameRate(VideoPlayer self) => (float)V(self).FrameRate;
        public static double get_length(VideoPlayer self) => V(self).Length;
        public static uint get_width(VideoPlayer self) => (uint)(V(self).Decoder?.Width ?? 0);
        public static uint get_height(VideoPlayer self) => (uint)(V(self).Decoder?.Height ?? 0);
        public static uint get_pixelAspectRatioNumerator(VideoPlayer self) => 1;
        public static uint get_pixelAspectRatioDenominator(VideoPlayer self) => 1;
        // 聲音：只有一條聲軌（track 0）
        public static ushort get_audioTrackCount(VideoPlayer self) => (ushort)(V(self).Decoder?.AudioChannels > 0 ? 1 : 0);
        public static ushort GetAudioChannelCount(VideoPlayer self, ushort trackIndex) => (ushort)(trackIndex == 0 ? V(self).Decoder?.AudioChannels ?? 0 : 0);
        public static uint GetAudioSampleRate(VideoPlayer self, ushort trackIndex) => trackIndex == 0 && V(self).Decoder?.AudioChannels > 0 ? (uint)AudioEngine.Rate : 0;
        public static string GetAudioLanguageCode(VideoPlayer self, ushort trackIndex) => "";
        public static ushort get_controlledAudioTrackMaxCount() => 64;
        public static ushort GetControlledAudioTrackCount(VideoPlayer self) => 1;
        public static void SetControlledAudioTrackCount(VideoPlayer self, ushort value) { }
        public static void EnableAudioTrack(VideoPlayer self, ushort trackIndex, bool enabled) { if (trackIndex == 0) V(self).AudioTrackEnabled = enabled; }
        public static bool IsAudioTrackEnabled(VideoPlayer self, ushort trackIndex) => trackIndex == 0 && V(self).AudioTrackEnabled;
        public static VideoAudioOutputMode get_audioOutputMode(VideoPlayer self) => V(self).AudioOutput;
        public static void set_audioOutputMode(VideoPlayer self, VideoAudioOutputMode value) => V(self).AudioOutput = value;
        public static bool get_canSetDirectAudioVolume(VideoPlayer self) => true;
        public static float GetDirectAudioVolume(VideoPlayer self, ushort trackIndex) => trackIndex == 0 ? V(self).DirectVolume : 1;
        public static void SetDirectAudioVolume(VideoPlayer self, ushort trackIndex, float volume) { if (trackIndex == 0) V(self).DirectVolume = Math.Clamp(volume, 0, 1); }
        public static bool GetDirectAudioMute(VideoPlayer self, ushort trackIndex) => trackIndex == 0 && V(self).DirectMute;
        public static void SetDirectAudioMute(VideoPlayer self, ushort trackIndex, bool mute) { if (trackIndex == 0) V(self).DirectMute = mute; }
        public static AudioSource GetTargetAudioSource(VideoPlayer self, ushort trackIndex) => trackIndex == 0 ? V(self).TargetAudio : null;
        public static void SetTargetAudioSource(VideoPlayer self, ushort trackIndex, AudioSource source) { if (trackIndex == 0) V(self).TargetAudio = source; }
    }
}
