// 從 unity-native/host-android 搬來（Phase 4 拆段 B，逐檔看過：Android API 手寫的宿主程式），只改命名空間與 GameActivity.Tag。
#nullable disable
// PNG 解碼走 Android 的 BitmapFactory（原生的 Skia），比 managed 的 StbImageSharp 快 3～5 倍。
// 不預乘 alpha、不縮放，要 ARGB_8888（記憶體裡就是 R、G、B、A 的順序）；解不了回 null，引擎退回 StbImageSharp。
using System;
using System.Runtime.InteropServices;
using Android.Graphics;

namespace Mt2Mobile.Launcher.GameHost
{
    static class PngAndroid
    {
        public static void Install() => UnityNative.Gl.GpuResources.NativeDecoder = Decode;

        static (byte[] rgba, int w, int h)? Decode(string file)
        {
            var opts = new BitmapFactory.Options { InPremultiplied = false, InScaled = false, InPreferredConfig = Bitmap.Config.Argb8888 };
            using var bmp = BitmapFactory.DecodeFile(file, opts);
            if (bmp == null || bmp.GetConfig() != Bitmap.Config.Argb8888) return null;
            int w = bmp.Width, h = bmp.Height;
            var px = new byte[w * h * 4];
            using var buf = Java.Nio.ByteBuffer.AllocateDirect(px.Length);
            bmp.CopyPixelsToBuffer(buf);
            Marshal.Copy(buf.GetDirectBufferAddress(), px, 0, px.Length);
            return (px, w, h);
        }
    }
}
