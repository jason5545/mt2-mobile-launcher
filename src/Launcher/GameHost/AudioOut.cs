// 從 unity-native/host-android 搬來（Phase 4 拆段 B，逐檔看過：Android API 手寫的宿主程式），只改命名空間與 GameActivity.Tag。
#nullable disable
// Android 的聲音輸出：引擎每格混好的 48 kHz 立體聲 float（AudioEngine.Sink）用非阻塞的方式寫進 AudioTrack（PCM float 串流）。
// 寫不進去的部分（緩衝已滿）直接丟掉，遊戲執行緒不會被聲音卡住。
using System;
using Android.Media;

namespace Mt2Mobile.Launcher.GameHost
{
    public sealed class AudioOut
    {
        readonly AudioTrack track;

        public static AudioOut TryOpen()
        {
            try { return new AudioOut(); }
            catch (Exception e) { Android.Util.Log.Error(GameActivity.Tag, "[聲音] 開不了 AudioTrack：" + e.Message); return null; }
        }

        AudioOut()
        {
            int rate = UnityNative.AudioEngine.Rate;
            int min = AudioTrack.GetMinBufferSize(rate, ChannelOut.Stereo, Encoding.PcmFloat);
            // 緩衝約 0.15 秒：遊戲偶爾慢一格不會斷音，延遲也不會太長
            int bytes = Math.Max(min, rate * 8 * 15 / 100);
            track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Game).SetContentType(AudioContentType.Music).Build())
                .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.PcmFloat).SetSampleRate(rate).SetChannelMask(ChannelOut.Stereo).Build())
                .SetBufferSizeInBytes(bytes)
                .SetTransferMode(AudioTrackMode.Stream)
                .Build();
            track.Play();
            Android.Util.Log.Info(GameActivity.Tag, $"[聲音] AudioTrack {rate} Hz 立體聲 float，緩衝 {bytes} bytes");
        }

        public void Push(float[] buf, int frames) => track.Write(buf, 0, frames * 2, WriteMode.NonBlocking);

        public void Pause() { try { track.Pause(); } catch { } }
        public void Resume() { try { track.Play(); } catch { } }
    }
}
