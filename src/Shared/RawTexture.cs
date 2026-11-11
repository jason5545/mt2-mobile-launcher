// 貼圖的原始壓縮資料（mt2-mobile-launcher 的 docs/perf-battle.md「B 段」）：匯出時把原版 Texture2D 的 m_ImageData（DXT1、DXT5、BC7、BC6H，含 Unity 產的整串 mip）
// 照原樣存成這個容器，執行期每層直接 glCompressedTexImage2D。引擎（UnityNative）與 mt2-mobile-launcher 的匯出（src/TextureExport）、importer、桌面的比對工具
// 共用這一份（各專案用 Link 編進去）。正本在 unity-port 的 unity-native/Shared，launcher 的 src/Shared 是 tools/sync-engine.sh 同步過去的副本。
//
// 格式（little-endian）：
//   0  "MT2T"         4 位元組
//   4  版本 u16       1
//   6  標頭長度 u16   資料從這裡開始
//   8  格式 i32       Unity 的 TextureFormat（DXT1 10、DXT5 12、BC6H 24、BC7 25）
//  12  寬 i32、16 高 i32（第 0 層）
//  20  mip 層數 i32
//  24  旗標 u32       1 = sRGB、2 = 資料用 LZ4 block 壓過、4 = DXT5nm 的法線貼圖（CPU 解時照 AssetRipper 匯出 PNG 的做法換回 RGB）
//  28  原始長度 i32   解壓後的位元組數（= 各層大小的合計）
//  32  存的長度 i32   標頭之後的位元組數
//  36  來源 u16 長度 + UTF-8：原版的「檔名|PathID」，比對工具用來找回原版的 Texture2D
// 資料的列順序跟 Unity 存的一樣（第一列是圖的最下面一列），就是 GL 要的順序，不用翻。
// 公開的只有 Mt2Mobile.TextureExport 那份；引擎與 importer 各自編一份 internal 的（RAWTEXTURE_INTERNAL），啟動器同時參考它們也不會撞名。
#nullable disable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;

namespace Mt2Mobile.Shared
{
#if RAWTEXTURE_INTERNAL
    internal
#else
    public
#endif
    sealed class RawTexture
    {
        public const int DXT1 = 10, DXT5 = 12, BC6H = 24, BC7 = 25;
        public const uint FlagSrgb = 1, FlagLz4 = 2, FlagDxt5nm = 4;
        public const string Extension = ".mt2t";
        static readonly byte[] Magic = { (byte)'M', (byte)'T', (byte)'2', (byte)'T' };
        const int FixedHeader = 38;

        public int Format, Width, Height, Mips;
        public uint Flags;
        public int RawLength, StoredLength, HeaderLength;
        public string Source = "";

        public bool Srgb => (Flags & FlagSrgb) != 0;
        public bool Lz4 => (Flags & FlagLz4) != 0;
        public bool Dxt5nm => (Flags & FlagDxt5nm) != 0;

        public static string FormatName(int format) => format switch
        {
            DXT1 => "DXT1",
            DXT5 => "DXT5",
            BC6H => "BC6H",
            BC7 => "BC7",
            _ => format.ToString(),
        };

        /// <summary>4×4 區塊的位元組數；不是這四種回 0</summary>
        public static int BlockBytes(int format) => format switch
        {
            DXT1 => 8,
            DXT5 or BC6H or BC7 => 16,
            _ => 0,
        };

        public static int LevelWidth(int width, int level) => Math.Max(1, width >> level);
        public static int LevelHeight(int height, int level) => Math.Max(1, height >> level);

        /// <summary>第 level 層的位元組數（不足 4 的邊也佔一整個區塊）</summary>
        public static int LevelSize(int format, int width, int height, int level)
            => ((LevelWidth(width, level) + 3) / 4) * ((LevelHeight(height, level) + 3) / 4) * BlockBytes(format);

        public static long ChainLength(int format, int width, int height, int mips)
        {
            long s = 0;
            for (int l = 0; l < mips; l++) s += LevelSize(format, width, height, l);
            return s;
        }

        /// <summary>第 level 層在解壓後資料裡的位移與長度（執行期上傳與比對工具都照這個切）</summary>
        public (int offset, int length) Level(int level)
        {
            int o = 0;
            for (int l = 0; l < level; l++) o += LevelSize(Format, Width, Height, l);
            return (o, LevelSize(Format, Width, Height, level));
        }

        public static bool IsContainer(ReadOnlySpan<byte> head) => head.Length >= 4 && head.Slice(0, 4).SequenceEqual(Magic);

        /// <summary>只讀標頭（importer 用）；不是這個格式回 null</summary>
        public static RawTexture ReadHeader(string path)
        {
            using var fs = File.OpenRead(path);
            var head = new byte[FixedHeader];
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !IsContainer(head)) return null;
            int len = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
            var all = new byte[len];
            fs.Position = 0;
            fs.ReadExactly(all);
            return Parse(all);
        }

        static RawTexture Parse(ReadOnlySpan<byte> b)
        {
            if (!IsContainer(b) || b.Length < FixedHeader) throw new InvalidDataException("不是 MT2T 貼圖");
            int version = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(4));
            if (version != 1) throw new InvalidDataException($"MT2T 版本 {version} 不支援");
            var t = new RawTexture
            {
                HeaderLength = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6)),
                Format = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(8)),
                Width = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(12)),
                Height = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(16)),
                Mips = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(20)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(24)),
                RawLength = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(28)),
                StoredLength = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(32)),
            };
            int sl = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(36));
            if (FixedHeader + sl != t.HeaderLength || b.Length < t.HeaderLength) throw new InvalidDataException("MT2T 標頭長度不對");
            t.Source = Encoding.UTF8.GetString(b.Slice(FixedHeader, sl));
            if (BlockBytes(t.Format) == 0 || t.Width <= 0 || t.Height <= 0 || t.Mips <= 0 || ChainLength(t.Format, t.Width, t.Height, t.Mips) != t.RawLength)
                throw new InvalidDataException($"MT2T 標頭不一致（格式 {t.Format}、{t.Width}x{t.Height}、{t.Mips} 層、{t.RawLength} 位元組）");
            return t;
        }

        /// <summary>讀整個檔、解壓：回傳標頭與各層連在一起的原始資料（執行期上傳、比對工具都走這裡）</summary>
        public static RawTexture Read(string path, out byte[] data) => Read(File.ReadAllBytes(path), out data);

        public static RawTexture Read(byte[] file, out byte[] data)
        {
            var t = Parse(file);
            if (file.Length - t.HeaderLength != t.StoredLength) throw new InvalidDataException("MT2T 資料長度不對");
            if (t.Lz4)
            {
                data = new byte[t.RawLength];
                int n = LZ4Codec.Decode(file, t.HeaderLength, t.StoredLength, data, 0, data.Length);
                if (n != t.RawLength) throw new InvalidDataException($"MT2T 解壓得到 {n} 位元組，應該是 {t.RawLength}");
            }
            else
            {
                if (t.StoredLength != t.RawLength) throw new InvalidDataException("MT2T 資料長度不對");
                data = file.AsSpan(t.HeaderLength, t.StoredLength).ToArray();
            }
            return t;
        }

        /// <summary>寫容器：data 是原版的整串資料（各層連在一起），lz4Level 小於 0 就不壓</summary>
        public static byte[] Write(int format, int width, int height, int mips, uint flags, string source, ReadOnlySpan<byte> data, int lz4Level)
        {
            if (ChainLength(format, width, height, mips) != data.Length) throw new ArgumentException("資料長度跟格式、大小、層數對不上");
            var src = Encoding.UTF8.GetBytes(source ?? "");
            int header = FixedHeader + src.Length;
            byte[] stored;
            int storedLength;
            if (lz4Level >= 0)
            {
                stored = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
                storedLength = LZ4Codec.Encode(data, stored, (LZ4Level)lz4Level);
                flags |= FlagLz4;
            }
            else
            {
                stored = data.ToArray();
                storedLength = stored.Length;
                flags &= ~FlagLz4;
            }
            var o = new byte[header + storedLength];
            var s = o.AsSpan();
            Magic.CopyTo(s);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(6), (ushort)header);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(8), format);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(12), width);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(16), height);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(20), mips);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24), flags);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(28), data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(32), storedLength);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(36), (ushort)src.Length);
            src.CopyTo(s.Slice(FixedHeader));
            stored.AsSpan(0, storedLength).CopyTo(s.Slice(header));
            return o;
        }
    }
}
