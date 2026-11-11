using AssetRipper.TextureDecoder.Bc;
using AssetRipper.TextureDecoder.Dxt;
using AssetRipper.TextureDecoder.Rgb;
using AssetRipper.TextureDecoder.Rgb.Formats;
using Mt2Mobile.Shared;

namespace Mt2Mobile.TextureExport;

/// <summary>
/// 用 CPU 把原始壓縮資料的第 0 層解成 RGBA8（AssetRipper 的解碼器，跟匯出 PNG 用的同一套）。列的順序照資料（第一列是圖的最下面），
/// 就是遊戲讀 PNG 再上下翻之後的順序。給執行期的退路用：GPU 不支援這個格式、或腳本要讀像素（AssetIcalls.Px）。
/// 跟 AssetRipper 匯出 PNG 的差別只有 PNG 的上下翻（遊戲讀進來又翻回去）；DXT5nm 的法線貼圖照它的 UnpackNormal 換回 RGB，
/// 所以解出來的像素跟以前從 PNG 讀到的相同（tools/texture-check 的 cpu 比對）。
/// </summary>
public static class RawTextureDecoder
{
    public static byte[] DecodeLevel0(RawTexture t, ReadOnlySpan<byte> data) => DecodeLevel0(t.Format, t.Width, t.Height, t.Dxt5nm, data);

    /// <summary>format 是 Unity 的 TextureFormat，data 從第 0 層開始（後面的 mip 不讀）</summary>
    public static byte[] DecodeLevel0(int format, int w, int h, bool dxt5nm, ReadOnlySpan<byte> data)
    {
        var o = new byte[w * h * 4];
        int read = format switch
        {
            RawTexture.DXT1 => DxtDecoder.DecompressDXT1<ColorRGBA<byte>, byte>(data, w, h, o),
            RawTexture.DXT5 => DxtDecoder.DecompressDXT5<ColorRGBA<byte>, byte>(data, w, h, o),
            RawTexture.BC7 => Bc7.Decompress<ColorRGBA<byte>, byte>(data, w, h, o),
            // 同 AssetRipper 匯出：解成 8 位元（超過 1 的截掉）
            RawTexture.BC6H => Bc6h.Decompress<ColorRGBA<byte>, byte>(data, w, h, false, o),
            _ => throw new NotSupportedException($"格式 {format}"),
        };
        if (read < 0) throw new InvalidDataException($"{RawTexture.FormatName(format)} {w}x{h} 解不了");
        if (dxt5nm) UnpackNormal(o);
        return o;
    }

    /// <summary>DXT5nm（x 在 alpha、y 在 green）換成 RGB 的法線：紅與 alpha 對調、藍用 x、y 算。算法與四捨五入同 AssetRipper TextureConverter.UnpackNormal</summary>
    static void UnpackNormal(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            byte r = px[i], g = px[i + 1], a = px[i + 3];
            const double MagnitudeSqr = 255 * 255;
            double vr = a * 2.0 - 255.0, vg = g * 2.0 - 255.0;
            double vb = double.Sqrt(MagnitudeSqr - double.Min(vr * vr + vg * vg, MagnitudeSqr));
            px[i] = a;
            px[i + 2] = NumericConversion.Convert<double, byte>((vb + 255.0) / 2.0 / 255.0);
            px[i + 3] = r;
        }
    }
}
