// 結構配置與常數照 FreeType 2.13.2（https://freetype.org/）的 include/freetype/freetype.h，FreeType License；授權全文見 THIRD_PARTY_NOTICES.md。
using System.Runtime.InteropServices;

namespace Mt2Mobile.Importer;

/// <summary>
/// FreeType 2.13.2 的 P/Invoke（native/freetype/build.sh 編出的 libfreetype）。結構照 freetype.h，
/// 只支援 LP64（macOS arm64、Android arm64：long 與指標都是 64 位元）。用法同 freetype-py 2.5.1。
/// </summary>
internal static unsafe partial class FreeType
{
    private const string Lib = "freetype";

    public const int LoadNoHinting = 0x2;
    public const int LoadNoBitmap = 0x8;

    [LibraryImport(Lib, EntryPoint = "FT_Init_FreeType")]
    public static partial int InitFreeType(out IntPtr library);

    [LibraryImport(Lib, EntryPoint = "FT_Done_FreeType")]
    public static partial int DoneFreeType(IntPtr library);

    [LibraryImport(Lib, EntryPoint = "FT_New_Memory_Face")]
    public static partial int NewMemoryFace(IntPtr library, byte* data, nint size, nint faceIndex, out FaceRec* face);

    [LibraryImport(Lib, EntryPoint = "FT_Done_Face")]
    public static partial int DoneFace(FaceRec* face);

    [LibraryImport(Lib, EntryPoint = "FT_Set_Char_Size")]
    public static partial int SetCharSize(FaceRec* face, nint charWidth, nint charHeight, uint horzResolution, uint vertResolution);

    [LibraryImport(Lib, EntryPoint = "FT_Get_Char_Index")]
    public static partial uint GetCharIndex(FaceRec* face, nuint charcode);

    [LibraryImport(Lib, EntryPoint = "FT_Load_Glyph")]
    public static partial int LoadGlyph(FaceRec* face, uint glyphIndex, int loadFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct Generic
    {
        public IntPtr Data;
        public IntPtr Finalizer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BBox
    {
        public nint XMin, YMin, XMax, YMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FaceRec
    {
        public nint NumFaces, FaceIndex, FaceFlags, StyleFlags, NumGlyphs;
        public IntPtr FamilyName, StyleName;
        public int NumFixedSizes;
        public IntPtr AvailableSizes;
        public int NumCharmaps;
        public IntPtr Charmaps;
        public Generic Generic;
        public BBox BBox;
        public ushort UnitsPerEM;
        public short Ascender, Descender, Height, MaxAdvanceWidth, MaxAdvanceHeight, UnderlinePosition, UnderlineThickness;
        public GlyphSlotRec* Glyph;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphMetrics
    {
        public nint Width, Height, HoriBearingX, HoriBearingY, HoriAdvance, VertBearingX, VertBearingY, VertAdvance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector
    {
        public nint X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Bitmap
    {
        public uint Rows, Width;
        public int Pitch;
        public IntPtr Buffer;
        public ushort NumGrays;
        public byte PixelMode, PaletteMode;
        public IntPtr Palette;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Outline
    {
        public short NContours, NPoints;
        public Vector* Points;
        public byte* Tags;
        public short* Contours;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphSlotRec
    {
        public IntPtr Library, Face, Next;
        public uint GlyphIndex;
        public Generic Generic;
        public GlyphMetrics Metrics;
        public nint LinearHoriAdvance, LinearVertAdvance;
        public Vector Advance;
        public int Format;
        public Bitmap Bitmap;
        public int BitmapLeft, BitmapTop;
        public Outline Outline;
    }

    /// <summary>freetype-py 的 FT_Exception 訊息（ft_errors 的對照）只用到錯誤碼本身</summary>
    public sealed class FtException(int code) : Exception($"FT_Exception:  (error 0x{code:x2})")
    {
        public int Code { get; } = code;
    }

    public static void Check(int err)
    {
        if (err != 0) throw new FtException(err);
    }
}
