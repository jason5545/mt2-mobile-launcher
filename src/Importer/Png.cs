// PNG 的 mode 對照表與錯誤訊息取自 Pillow 13.0.0（https://github.com/python-pillow/Pillow）的 PngImagePlugin，MIT-CMU；授權全文見 THIRD_PARTY_NOTICES.md。
using System.Buffers.Binary;
using System.IO.Compression;
using Mt2Mobile.Importer.Serialized;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer;

/// <summary>PNG 的尺寸與 Pillow 的 mode（PIL.Image.open 只讀標頭，不解碼）</summary>
public static class PngInfo
{
    // Pillow 13 PngImagePlugin._MODES：(位元深度, 色彩類型) → mode
    private static readonly Dictionary<(int, int), string> Modes = new()
    {
        [(1, 0)] = "1", [(2, 0)] = "L", [(4, 0)] = "L", [(8, 0)] = "L", [(16, 0)] = "I;16",
        [(8, 2)] = "RGB", [(16, 2)] = "RGB",
        [(1, 3)] = "P", [(2, 3)] = "P", [(4, 3)] = "P", [(8, 3)] = "P",
        [(8, 4)] = "LA", [(16, 4)] = "RGBA",
        [(8, 6)] = "RGBA", [(16, 6)] = "RGBA",
    };

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static (int width, int height, string mode) Read(string path)
    {
        Span<byte> head = stackalloc byte[33];
        using (var fs = File.OpenRead(path))
        {
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head[..8].SequenceEqual(Signature))
                throw new InvalidDataException($"cannot identify image file '{path}'");
        }
        if (!head.Slice(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("broken PNG file");
        int w = BinaryPrimitives.ReadInt32BigEndian(head[16..]);
        int h = BinaryPrimitives.ReadInt32BigEndian(head[20..]);
        int depth = head[24], colorType = head[25];
        if (!Modes.TryGetValue((depth, colorType), out var mode)) throw new InvalidDataException("unknown PNG mode");
        return (w, h, mode);
    }

    /// <summary>8 位元 RGBA 的 PNG（由上往下的列）</summary>
    public static void WriteRgba(string path, int width, int height, ReadOnlySpan<byte> rgbaTopDown)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(ms, "IHDR"u8, ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0);
                    z.Write(rgbaTopDown.Slice(y * width * 4, width * 4));
                }
            }
            WriteChunk(ms, "IDAT"u8, raw.ToArray());
        }
        WriteChunk(ms, "IEND"u8, []);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}

/// <summary>內建 sprite 的貼圖（UnityPy 的 tex.image.save）：目前只有 RGBA32（兩款遊戲的 4 張都是），UnityPy 解完上下翻轉</summary>
public static class BuiltinTexture
{
    public static void SavePng(BuildEnv env, BuildObject texObj, PyDict tex, string path)
    {
        long format = Py.ToLong(tex["m_TextureFormat"]);
        int w = (int)Py.ToLong(tex["m_Width"]), h = (int)Py.ToLong(tex["m_Height"]);
        if (format != 4) throw new NotSupportedException($"貼圖格式 {format} 沒有實作（只有 RGBA32）");
        byte[] data = ImageData(env, texObj, tex);
        int size = w * h * 4;
        if (data.Length < size) throw new InvalidDataException("貼圖資料不足");
        var flipped = new byte[size];
        for (int y = 0; y < h; y++)
            data.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));
        Directory.CreateDirectory(PyPath.Parent(path));
        PngInfo.WriteRgba(path, w, h, flipped);
    }

    /// <summary>image data：內嵌的，或 m_StreamData 指到的 .resS</summary>
    private static byte[] ImageData(BuildEnv env, BuildObject texObj, PyDict tex)
    {
        var inline = tex.Get("image data");
        if (inline is byte[] b && b.Length > 0) return b;
        var sd = (PyDict)tex["m_StreamData"]!;
        string p = (string)sd["path"]!;
        long offset = Py.ToLong(sd["offset"]), size = Py.ToLong(sd["size"]);
        string name = p.Split('/')[^1];
        string res = PyPath.Join(env.GameData, name);
        if (!File.Exists(res)) res = PyPath.Join(PyPath.Join(env.GameData, "Resources"), name);
        using var fs = File.OpenRead(res);
        fs.Position = offset;
        var buf = new byte[size];
        fs.ReadExactly(buf);
        return buf;
    }
}
