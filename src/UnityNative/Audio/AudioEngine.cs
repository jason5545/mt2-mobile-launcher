// 聲音的原生半邊：AudioSource、AudioClip 的解碼、混音、AudioSettings、AudioListener。
//
// 混音在引擎裡做（Mac、Android 共用），宿主只負責把混好的 PCM（48 kHz 立體聲 float）送到喇叭（AudioEngine.Sink）。
//   - 時鐘：每格照不受 timeScale 影響的 deltaTime 混對應的樣本數（Unity 的 DSP 時鐘也不受 timeScale 影響），
//     沒有輸出裝置（無畫面測試）時一樣推進，isPlaying、time 照常。
//   - 解碼：ogg 用 NVorbis。20 秒以內的 clip 第一次播時整段解碼並快取（音效），更長的每個聲部各自串流（音樂）。
//   - 重取樣：線性內插，速度 = clip 取樣率 × pitch ÷ 輸出取樣率（pitch 負的當 0）。
//   - 音量：volume × PlayOneShot 的 volumeScale × 混音器群組音量（Mixer.cs 的 GroupGain，往上乘到主群組與 output）× AudioListener.volume。
//   - panStereo：等功率平移（未查證 Unity 的平移曲線）。
//   - VideoPlayer 的聲軌不是 AudioClip：用 IAudioFeed 聲部，每格照樣本數跟解碼器要 PCM（Media/Video.cs）。
// 規則照 Unity 文件與 TS 版（unity-cs2ts/runtime/src/csport/unityAudio.ts）：
//   - Play 從頭開始（已經在播也重來）；PlayOneShot 不影響 isPlaying、不受 clip／loop 影響，Stop 會一起停（推定）。
//   - Play(ulong delay)：delay 是 44.1 kHz 的樣本數；PlayDelayed 傳負的秒數；PlayScheduled 傳 dspTime。
//   - 元件停用、物件停用或銷毀時停止；停用狀態下 Play 不出聲（Unity 記警告「Can not play a disabled audio source」）。
//   - Pause 之後 isPlaying 是 false，UnPause 從原位置接著播。AudioListener.pause 讓沒開 ignoreListenerPause 的聲音停在原位。
// 沒做：3D 聲音（spatialBlend、距離衰減、都卜勒）、效果器（混音器只做音量）、priority 與聲部上限、GetOutputData／GetSpectrumData。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Audio;

namespace UnityNative
{
    public sealed class NAudioSource : NBehaviour
    {
        public AudioClip Clip;
        public AudioMixerGroup Output;
        public float Volume = 1, Pitch = 1, Pan, SpatialBlend, MinDistance = 1, MaxDistance = 500, Spread, Doppler = 1, ReverbZoneMix = 1;
        public bool Loop, Mute, PlayOnAwake = true, IgnoreListenerVolume, IgnoreListenerPause, BypassEffects, BypassListenerEffects, BypassReverbZones;
        public int Priority = 128;
        public AudioRolloffMode Rolloff;
        public Voice Main;
        public readonly List<Voice> OneShots = new List<Voice>();
        public bool Paused;
        bool awoken;

        public override void Load(JsonObject d, LoadContext ctx)
        {
            Clip = Serializer.Ref(d["m_audioClip"], typeof(AudioClip), ctx) as AudioClip;
            Output = Serializer.Ref(d["OutputAudioMixerGroup"], typeof(AudioMixerGroup), ctx) as AudioMixerGroup;
            if (d["m_Volume"] != null) Volume = (float)Serializer.D(d["m_Volume"]);
            if (d["m_Pitch"] != null) Pitch = (float)Serializer.D(d["m_Pitch"]);
            Loop = Serializer.D(d["Loop"]) != 0;
            Mute = Serializer.D(d["Mute"]) != 0;
            PlayOnAwake = d["m_PlayOnAwake"] == null || Serializer.D(d["m_PlayOnAwake"]) != 0;
            Priority = d["Priority"] != null ? (int)Serializer.D(d["Priority"]) : 128;
            Pan = (float)Serializer.D(d["Pan2D"]);
            IgnoreListenerVolume = Serializer.D(d["m_IgnoreListenerVolume"]) != 0;
            IgnoreListenerPause = Serializer.D(d["m_IgnoreListenerPause"]) != 0;
        }

        public override void CopyFrom(NComponent src)
        {
            base.CopyFrom(src);
            var s = (NAudioSource)src;
            Clip = s.Clip; Output = s.Output; Volume = s.Volume; Pitch = s.Pitch; Pan = s.Pan; Loop = s.Loop; Mute = s.Mute; PlayOnAwake = s.PlayOnAwake;
            Priority = s.Priority; IgnoreListenerVolume = s.IgnoreListenerVolume; IgnoreListenerPause = s.IgnoreListenerPause; SpatialBlend = s.SpatialBlend;
        }

        public bool Active => Alive && Enabled && Go != null && Go.ActiveInHierarchy;

        public override void OnActiveChanged()
        {
            if (Active)
            {
                if (awoken) return;
                awoken = true;
                if (PlayOnAwake && Clip != null) Play(0);
            }
            else
            {
                awoken = false;
                Stop(true);
            }
        }

        public override void OnDestroyNative() => Stop(true);

        /// <summary>delay：0 立刻；正的是 dspTime（PlayScheduled）；負的是 −秒數（PlayDelayed）</summary>
        public void Play(double delay)
        {
            if (!Active) { Engine.Log(LogType.Warning, "Can not play a disabled audio source", this); return; }
            var c = Native.Of<NAudioClip>(Clip);
            if (Main != null) AudioEngine.Remove(Main);
            Main = null;
            Paused = false;
            if (c == null) return;
            // 正的是 PlayScheduled 的 dspTime，負的是 PlayDelayed 的秒數
            double start = delay > 0 ? delay : delay < 0 ? AudioEngine.DspTime - delay : 0;
            Main = AudioEngine.Start(this, c, 1, false, start);
        }

        public void PlayOneShot(AudioClip clip, float scale)
        {
            if (!Active) { Engine.Log(LogType.Warning, "Can not play a disabled audio source", this); return; }
            var c = Native.Of<NAudioClip>(clip);
            if (c == null) return;
            var v = AudioEngine.Start(this, c, scale, true, 0);
            if (v != null) OneShots.Add(v);
        }

        public void Stop(bool stopOneShots)
        {
            if (Main != null) AudioEngine.Remove(Main);
            Main = null;
            Paused = false;
            if (stopOneShots)
            {
                foreach (var v in OneShots) AudioEngine.Remove(v);
                OneShots.Clear();
            }
        }

        public bool IsPlaying => Main != null && !Main.Done && !Paused && !(AudioEngine.ListenerPaused && !IgnoreListenerPause);

        public float Time
        {
            get => Main == null ? 0 : (float)(Main.Pos / Math.Max(1, Main.Clip.Frequency));
            set { if (Main != null) Main.Seek(value * Main.Clip.Frequency); }
        }

        public int TimeSamples
        {
            get => Main == null ? 0 : (int)Main.Pos;
            set { if (Main != null) Main.Seek(value); }
        }

        public static void Register()
        {
            Loader.Factories["AudioSource"] = () => new NAudioSource();
            Engine.FrameEnd.Add(AudioEngine.Tick);
        }
    }

    /// <summary>不是 AudioClip 的聲音來源（VideoPlayer 的聲軌）：混音器每格照樣本數跟它要 48 kHz 立體聲</summary>
    public interface IAudioFeed
    {
        /// <summary>false：拿掉這個聲部</summary>
        bool Live { get; }
        /// <summary>true：這格不出聲、位置不動（暫停）</summary>
        bool Hold { get; }
        float Gain { get; }
        void Read(float[] dst, int frames);
    }

    /// <summary>一個正在播的聲音</summary>
    public sealed class Voice
    {
        public IAudioFeed Feed;
        public NAudioSource Src;
        public NAudioClip Clip;
        public float Scale = 1;
        public bool OneShot, Done;
        /// <summary>播放位置（clip 的樣本，含小數）</summary>
        public double Pos;
        /// <summary>dspTime 到這之前不出聲（PlayDelayed／PlayScheduled）；EndDsp 之後停（SetScheduledEndTime）</summary>
        public double StartDsp, EndDsp = double.PositiveInfinity;
        // 串流
        public NVorbis.VorbisReader Reader;
        public float[] Buf;
        public long BufStart, BufFrames;
        public long? ActualSamples;
        public long LastFillIdx = long.MinValue;
        public int ConsecutiveFillCount;
        public bool Loop => !OneShot && Src != null && Src.Loop;

        public void Seek(double frame)
        {
            Pos = Math.Clamp(frame, 0, Math.Max(0, Clip.Samples - 1));
            Done = false;
            LastFillIdx = long.MinValue;
            ConsecutiveFillCount = 0;
        }
    }

    public static class AudioEngine
    {
        public const int Rate = 48000;
        /// <summary>宿主的輸出（交錯的立體聲 float、樣本對數）；沒有就丟掉</summary>
        public static Action<float[], int> Sink;
        public static double DspTime => mixed / (double)Rate;
        public static float ListenerVolume = 1;
        public static bool ListenerPaused;
        static long mixed;
        static double pending;
        static float[] buffer = new float[Rate];
        static readonly List<Voice> voices = new List<Voice>();
        const int StreamThresholdFrames = 20 * 48000;
        static readonly bool debug = Environment.GetEnvironmentVariable("UN_AUDIO_DEBUG") == "1";

        public static Voice Start(NAudioSource src, NAudioClip c, float scale, bool oneShot, double startDsp)
        {
            if (!Prepare(c)) return null;
            var v = new Voice { Src = src, Clip = c, Scale = scale, OneShot = oneShot, StartDsp = startDsp };
            if (c.Data == null)
            {
                try { v.Reader = new NVorbis.VorbisReader(c.File); }
                catch (Exception e) { Engine.Log(LogType.Warning, "AudioClip " + c.Name + " 開不了：" + e.Message); return null; }
            }
            voices.Add(v);
            if (debug) Console.WriteLine("[Audio] " + (oneShot ? "PlayOneShot " : "Play ") + c.Name + "（" + (c.Data != null ? "解碼" : "串流") + "，" + (c.Frequency > 0 ? (c.Samples / (float)c.Frequency).ToString("0.0") : "?") + " 秒" + (v.Loop ? "，循環" : "") + "，第 " + Engine.Time.FrameCount + " 格）");
            return v;
        }

        public static Voice AddFeed(IAudioFeed f)
        {
            var v = new Voice { Feed = f };
            voices.Add(v);
            return v;
        }

        /// <summary>AudioSource 的音量：mute、volume × scale、AudioListener.volume、混音器群組</summary>
        public static float SourceGain(NAudioSource s, float scale)
        {
            float gain = s.Mute ? 0 : s.Volume * scale * (s.IgnoreListenerVolume ? 1 : ListenerVolume);
            var g = Native.Of<NMixerGroup>(s.Output);
            if (g != null) gain *= g.Mixer.GroupGain(g.Id);
            return gain;
        }

        static float[] feedBuf = new float[0];

        static void MixFeed(Voice v, float[] dst, int frames)
        {
            var f = v.Feed;
            if (!f.Live) { Remove(v); return; }
            if (f.Hold) return;
            if (feedBuf.Length < frames * 2) feedBuf = new float[frames * 2];
            f.Read(feedBuf, frames);
            float g = f.Gain;
            if (g == 0) return;
            for (int i = 0; i < frames * 2; i++) dst[i] += feedBuf[i] * g;
        }

        public static void Remove(Voice v)
        {
            v.Done = true;
            v.Reader?.Dispose();
            v.Reader = null;
            voices.Remove(v);
        }

        /// <summary>短的 clip 整段解碼並快取；長的留給聲部串流。回傳能不能播</summary>
        public static bool Prepare(NAudioClip c)
        {
            if (c.Data != null) return true;
            if (c.File == null || !File.Exists(c.File)) return false;
            if (c.Samples > 0 && c.Samples > StreamThresholdFrames) { c.LoadState = AudioDataLoadState.Loaded; return true; }
            return Decode(c);
        }

        public static bool Decode(NAudioClip c)
        {
            if (c.Data != null) return true;
            if (c.File == null || !File.Exists(c.File)) return false;
            try
            {
                using var r = new NVorbis.VorbisReader(c.File);
                c.Channels = r.Channels;
                c.Frequency = r.SampleRate;
                var all = new List<float>((int)Math.Max(0, r.TotalSamples * r.Channels));
                var tmp = new float[4096 * Math.Max(1, r.Channels)];
                int n;
                while ((n = r.ReadSamples(tmp, 0, tmp.Length)) > 0) for (int i = 0; i < n; i++) all.Add(tmp[i]);
                c.Data = all.ToArray();
                c.Samples = c.Data.Length / Math.Max(1, c.Channels);
                c.LoadState = AudioDataLoadState.Loaded;
                return true;
            }
            catch (Exception e)
            {
                Engine.Log(LogType.Warning, "AudioClip " + c.Name + " 解碼失敗：" + e.Message);
                c.LoadState = AudioDataLoadState.Failed;
                return false;
            }
        }

        /// <summary>每格結尾：照不受 timeScale 影響的時間混音、交給宿主</summary>
        public static void Tick()
        {
            pending += Engine.Time.UnscaledDeltaTime * Rate;
            int frames = (int)pending;
            if (frames <= 0) return;
            pending -= frames;
            if (buffer.Length < frames * 2) buffer = new float[frames * 2];
            Mix(buffer, frames);
            mixed += frames;
            Sink?.Invoke(buffer, frames);
        }

        static void Mix(float[] dst, int frames)
        {
            Array.Clear(dst, 0, frames * 2);
            double t0 = DspTime;
            foreach (var v in voices.ToArray())
            {
                if (v.Done) { voices.Remove(v); continue; }
                if (v.Feed != null) { MixFeed(v, dst, frames); continue; }
                var s = v.Src;
                if (s == null || !s.Alive) { Remove(v); continue; }
                if (!v.OneShot && s.Paused) continue;
                if (ListenerPaused && !s.IgnoreListenerPause) continue;
                int startAt = v.StartDsp > t0 ? (int)Math.Min(frames, (v.StartDsp - t0) * Rate) : 0;
                if (startAt >= frames) continue;
                float gain = SourceGain(s, v.Scale);
                float pan = Math.Clamp(s.Pan, -1, 1);
                float gl = gain * MathF.Cos((pan + 1) * MathF.PI / 4) * MathF.Sqrt(2), gr = gain * MathF.Sin((pan + 1) * MathF.PI / 4) * MathF.Sqrt(2);
                if (pan == 0) gl = gr = gain;
                double step = v.Clip.Frequency * Math.Max(0, s.Pitch) / (double)Rate;
                int endAt = frames;
                if (v.EndDsp < double.PositiveInfinity) endAt = (int)Math.Clamp((v.EndDsp - t0) * Rate, 0, frames);
                Render(v, dst, startAt, endAt, step, gl, gr);
                if (v.EndDsp <= t0 + frames / (double)Rate) v.Done = true;
                if (v.Done)
                {
                    if (v.OneShot) s.OneShots.Remove(v);
                    Remove(v);
                }
            }
        }

        static void Render(Voice v, float[] dst, int from, int to, double step, float gl, float gr)
        {
            var c = v.Clip;
            int ch = Math.Max(1, c.Channels);
            for (int i = from; i < to; i++)
            {
                if (v.Done) return;
                long total = v.ActualSamples ?? c.Samples;
                if (v.Pos >= total)
                {
                    if (v.Loop && total > 0)
                    {
                        v.Pos -= total;
                        if (v.Reader != null) v.BufFrames = 0;
                        v.ConsecutiveFillCount = 0;
                    }
                    else { v.Done = true; return; }
                }
                long i0 = (long)v.Pos;
                float f = (float)(v.Pos - i0);
                Frame(v, i0, ch, out float l0, out float r0);
                if (v.Done) return;
                total = v.ActualSamples ?? c.Samples;
                long i1 = i0 + 1 < total ? i0 + 1 : v.Loop ? 0 : i0;
                Frame(v, i1, ch, out float l1, out float r1);
                if (v.Done) return;
                dst[i * 2] += (l0 + (l1 - l0) * f) * gl;
                dst[i * 2 + 1] += (r0 + (r1 - r0) * f) * gr;
                v.Pos += step;
            }
        }

        static void Frame(Voice v, long idx, int ch, out float l, out float r)
        {
            float[] data;
            long off;
            if (v.Clip.Data != null) { data = v.Clip.Data; off = idx * ch; }
            else
            {
                if (idx < v.BufStart || idx >= v.BufStart + v.BufFrames) Fill(v, idx, ch);
                data = v.Buf;
                off = (idx - v.BufStart) * ch;
                if (data == null || off < 0 || off + ch > data.Length || idx >= v.BufStart + v.BufFrames) { l = r = 0; return; }
            }
            if (off + ch > data.Length) { l = r = 0; return; }
            l = data[off];
            r = ch > 1 ? data[off + 1] : l;
        }

        static void Fill(Voice v, long idx, int ch)
        {
            var rd = v.Reader;
            if (rd == null) { v.BufFrames = 0; return; }
            if (v.LastFillIdx == idx) v.ConsecutiveFillCount++;
            else { v.LastFillIdx = idx; v.ConsecutiveFillCount = 1; }
            if (v.ConsecutiveFillCount > 4)
            {
                v.Done = true;
                v.BufStart = idx;
                v.BufFrames = 0;
                return;
            }
            v.Buf ??= new float[8192 * ch];
            long next = v.BufStart + v.BufFrames;
            if ((idx != next || v.BufFrames == 0) && rd.SamplePosition != idx)
            {
                try { rd.SamplePosition = idx; } catch { }
            }
            int n = rd.ReadSamples(v.Buf, 0, v.Buf.Length);
            v.BufStart = idx;
            v.BufFrames = n / ch;
            if (n == 0) v.ActualSamples ??= rd.SamplePosition;
        }
    }
}

namespace UnityNative.Icalls
{
    using UnityNative;

    public static unsafe partial class UnityEngine_AudioSource
    {
        static NAudioSource S(AudioSource s) => Native.Self<NAudioSource>(s);

        public static void PlayHelper(AudioSource source, ulong delay) => S(source).Play(delay == 0 ? 0 : -(delay / 44100.0));
        public static void Play(AudioSource self, double delay) => S(self).Play(delay);
        public static void PlayOneShotHelper(AudioSource source, AudioClip clip, float volumeScale) => S(source).PlayOneShot(clip, volumeScale);
        public static void Stop(AudioSource self, bool stopOneShots) => S(self).Stop(stopOneShots);
        public static void Pause(AudioSource self) { var s = S(self); if (s.Main != null) s.Paused = true; }
        public static void UnPause(AudioSource self) => S(self).Paused = false;
        public static bool get_isPlaying(AudioSource self) => S(self).IsPlaying;
        public static bool get_isVirtual(AudioSource self) => false;
        public static void SetScheduledStartTime(AudioSource self, double time) { var m = S(self).Main; if (m != null) m.StartDsp = time; }
        public static void SetScheduledEndTime(AudioSource self, double time) { var m = S(self).Main; if (m != null) m.EndDsp = time; }

        public static float GetPitch(AudioSource self) => S(self).Pitch;
        public static void SetPitch(AudioSource self, float value) => S(self).Pitch = value;
        public static float get_volume(AudioSource self) => S(self).Volume;
        public static void set_volume(AudioSource self, float value) => S(self).Volume = Math.Clamp(value, 0, 1);
        public static float get_time(AudioSource self) => S(self).Time;
        public static void set_time(AudioSource self, float value) => S(self).Time = value;
        public static int get_timeSamples(AudioSource self) => S(self).TimeSamples;
        public static void set_timeSamples(AudioSource self, int value) => S(self).TimeSamples = value;
        public static AudioClip get_clip(AudioSource self) => S(self).Clip;
        public static void set_clip(AudioSource self, AudioClip value) => S(self).Clip = value;
        public static AudioMixerGroup get_outputAudioMixerGroup(AudioSource self) => S(self).Output;
        public static void set_outputAudioMixerGroup(AudioSource self, AudioMixerGroup value) => S(self).Output = value;
        public static bool get_loop(AudioSource self) => S(self).Loop;
        public static void set_loop(AudioSource self, bool value) => S(self).Loop = value;
        public static bool get_mute(AudioSource self) => S(self).Mute;
        public static void set_mute(AudioSource self, bool value) => S(self).Mute = value;
        public static bool get_playOnAwake(AudioSource self) => S(self).PlayOnAwake;
        public static void set_playOnAwake(AudioSource self, bool value) => S(self).PlayOnAwake = value;
        public static bool get_ignoreListenerVolume(AudioSource self) => S(self).IgnoreListenerVolume;
        public static void set_ignoreListenerVolume(AudioSource self, bool value) => S(self).IgnoreListenerVolume = value;
        public static bool get_ignoreListenerPause(AudioSource self) => S(self).IgnoreListenerPause;
        public static void set_ignoreListenerPause(AudioSource self, bool value) => S(self).IgnoreListenerPause = value;
        public static float get_panStereo(AudioSource self) => S(self).Pan;
        public static void set_panStereo(AudioSource self, float value) => S(self).Pan = Math.Clamp(value, -1, 1);
        public static float get_spatialBlend(AudioSource self) => S(self).SpatialBlend;
        public static void set_spatialBlend(AudioSource self, float value) => S(self).SpatialBlend = Math.Clamp(value, 0, 1);
        public static int get_priority(AudioSource self) => S(self).Priority;
        public static void set_priority(AudioSource self, int value) => S(self).Priority = Math.Clamp(value, 0, 256);
        public static float get_minDistance(AudioSource self) => S(self).MinDistance;
        public static void set_minDistance(AudioSource self, float value) => S(self).MinDistance = value;
        public static float get_maxDistance(AudioSource self) => S(self).MaxDistance;
        public static void set_maxDistance(AudioSource self, float value) => S(self).MaxDistance = value;
        public static AudioRolloffMode get_rolloffMode(AudioSource self) => S(self).Rolloff;
        public static void set_rolloffMode(AudioSource self, AudioRolloffMode value) => S(self).Rolloff = value;
        public static float get_spread(AudioSource self) => S(self).Spread;
        public static void set_spread(AudioSource self, float value) => S(self).Spread = value;
        public static float get_dopplerLevel(AudioSource self) => S(self).Doppler;
        public static void set_dopplerLevel(AudioSource self, float value) => S(self).Doppler = value;
        public static float get_reverbZoneMix(AudioSource self) => S(self).ReverbZoneMix;
        public static void set_reverbZoneMix(AudioSource self, float value) => S(self).ReverbZoneMix = value;
        public static bool get_bypassEffects(AudioSource self) => S(self).BypassEffects;
        public static void set_bypassEffects(AudioSource self, bool value) => S(self).BypassEffects = value;
        public static bool get_bypassListenerEffects(AudioSource self) => S(self).BypassListenerEffects;
        public static void set_bypassListenerEffects(AudioSource self, bool value) => S(self).BypassListenerEffects = value;
        public static bool get_bypassReverbZones(AudioSource self) => S(self).BypassReverbZones;
        public static void set_bypassReverbZones(AudioSource self, bool value) => S(self).BypassReverbZones = value;
        public static void GetOutputDataHelper(AudioSource source, float[] samples, int channel) { if (samples != null) Array.Clear(samples, 0, samples.Length); }
        public static void GetSpectrumDataHelper(AudioSource source, float[] samples, int channel, FFTWindow window) { if (samples != null) Array.Clear(samples, 0, samples.Length); }
    }

    public static unsafe partial class UnityEngine_AudioSettings
    {
        public static double get_dspTime() => AudioEngine.DspTime;
        public static int GetSampleRate() => AudioEngine.Rate;
        public static AudioSpeakerMode GetSpeakerMode() => AudioSpeakerMode.Stereo;
        public static AudioSpeakerMode get_driverCapabilities() => AudioSpeakerMode.Stereo;
        public static void GetDSPBufferSize(out int bufferLength, out int numBuffers) { bufferLength = 1024; numBuffers = 4; }
        public static bool get_unityAudioDisabled() => false;
        public static void set_unityAudioDisabled(bool value) { }
        public static string GetSpatializerPluginName() => "";
        public static string GetAmbisonicDecoderPluginName() => "";
    }

    public static unsafe partial class UnityEngine_AudioListener
    {
        public static float get_volume() => AudioEngine.ListenerVolume;
        public static void set_volume(float value) => AudioEngine.ListenerVolume = Math.Max(0, value);
        public static bool get_pause() => AudioEngine.ListenerPaused;
        public static void set_pause(bool value) => AudioEngine.ListenerPaused = value;
        public static void GetOutputDataHelper(float[] samples, int channel) { if (samples != null) Array.Clear(samples, 0, samples.Length); }
        public static void GetSpectrumDataHelper(float[] samples, int channel, FFTWindow window) { if (samples != null) Array.Clear(samples, 0, samples.Length); }
    }
}
