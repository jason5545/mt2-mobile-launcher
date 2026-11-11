using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Importer;

/// <summary>
/// glyphs.py：動態字型（TMP_FontAsset 的 m_AtlasPopulationMode 1）的字形輪廓與度量值，給執行時產生 SDF glyph 用。
/// FreeType 的呼叫方式同 freetype-py 2.5.1（72 dpi、FT_LOAD_NO_BITMAP，非 hinting 加 FT_LOAD_NO_HINTING）。
/// </summary>
public sealed class Glyphs(ImportContext ctx)
{
    // TMP 會自己插入或查的字元：省略號、缺字方塊、底線、不換行空格、零寬空格、連字號、ETX
    private static readonly int[] ExtraChars = [0x2026, 0x25A1, 0x5F, 0xA0, 0x200B, 0x2D, 0x03];
    private const long RenderHinted = 4169;  // GlyphRenderMode.SDFAA_HINTED

    private static IEnumerable<string> Strings(object? v)
    {
        switch (v)
        {
            case string s: yield return s; break;
            case PyDict d:
                foreach (var x in d.Values)
                    foreach (var s in Strings(x)) yield return s;
                break;
            case List<object?> l:
                foreach (var x in l)
                    foreach (var s in Strings(x)) yield return s;
                break;
        }
    }

    /// <summary>字串的每個 code point（Python 的 set(str)）</summary>
    private static IEnumerable<int> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else yield return s[i];
        }
    }

    public HashSet<int> GameCharset(List<string> sources, string extra)
    {
        var chars = new HashSet<int>(Enumerable.Range(32, 127 - 32));
        chars.UnionWith(ExtraChars);
        chars.UnionWith(CodePoints(extra));
        foreach (var src in sources)
        {
            var files = Directory.Exists(src) ? PyPath.RGlob(src, "*.json").Order(PyPath.Comparer).ToList() : [src];
            foreach (var f in files)
                foreach (var v in Strings(PyJson.LoadFile(f)))
                    chars.UnionWith(CodePoints(v));
        }
        string scenes = PyPath.Join(ctx.Out, "scenes");
        var sceneFiles = PyPath.Glob(scenes, "*.json").Order(PyPath.Comparer)
            .Concat(PyPath.Glob(PyPath.Join(scenes, "prefabs"), "*.json").Order(PyPath.Comparer))
            .Where(f => PyPath.Name(f) is not ("index.json" or "report.json"));
        foreach (var f in sceneFiles)
        {
            // 場景資料夾裡也有別的 JSON（hierarchy.json）：沒有 components 的跳過
            var data = PyJson.LoadDict(f);
            foreach (var c in Assets.Dict(data.Get("components")).Values)
                if (((PyDict)c!).Get("m_text") is string t) chars.UnionWith(CodePoints(t));
        }
        return chars;
    }

    private static List<object?> Outline(FreeType.Outline ol)
    {
        unsafe
        {
            var contours = new List<object?>();
            int start = 0;
            for (int c = 0; c < ol.NContours; c++)
            {
                int end = ol.Contours[c];
                var pts = new List<object?>();
                for (int i = start; i <= end; i++)
                    pts.Add(new List<object?> { (long)ol.Points[i].X, (long)ol.Points[i].Y, (long)(ol.Tags[i] & 3) });
                contours.Add(pts);
                start = end + 1;
            }
            return contours;
        }
    }

    public unsafe PyDict Export(List<string> sources, string extra)
    {
        double t0 = ImportContext.Now();
        string assets = PyPath.Join(ctx.Out, "assets");
        var mb = PyJson.LoadDict(PyPath.Join(assets, "monobehaviours.json"));
        var fonts = PyJson.LoadDict(PyPath.Join(assets, "fonts.json"));
        var chars = GameCharset(sources, extra).Order().ToList();
        string outDir = PyPath.Join(assets, "glyphs");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var index = new PyDict();
        var stores = new PyDict();
        var fontsReport = new PyDict();
        var noSource = new List<object?>();
        var failed = new List<object?>();
        var report = new PyDict { ["charset"] = (long)chars.Count, ["fonts"] = fontsReport, ["noSource"] = noSource, ["failed"] = failed };

        FreeType.Check(FreeType.InitFreeType(out var library));
        try
        {
            foreach (var (guid, infoObj) in Assets.Sorted(mb))
            {
                var info = (PyDict)infoObj!;
                if (!Py.Eq(info["kind"], "TMP_FontAsset")) continue;
                var asset = PyJson.LoadDict(PyPath.Join(assets, (string)info["file"]!));
                if (!Py.Eq(asset.Get("m_AtlasPopulationMode"), 1L)) continue;
                var src = Assets.Dict(asset.Get("m_SourceFontFile")).Get("guid");
                if (src is null || !fonts.ContainsKey(src))
                {
                    noSource.Add(info["name"]);
                    continue;
                }
                var size = ((PyDict)asset["m_FaceInfo"]!)["m_PointSize"]!;
                bool hinted = Py.Eq(asset["m_AtlasRenderMode"], RenderHinted);
                var font = (PyDict)fonts[src]!;
                string ttf = PyPath.Join(assets, (string)font["file"]!);
                var bytes = File.ReadAllBytes(ttf);
                string digest = Convert.ToHexStringLower(SHA1.HashData(bytes))[..16];
                string name = $"{digest}_{Py.Str(size)}_{(hinted ? 'h' : 'u')}.json";
                index[guid] = name;
                if (stores.ContainsKey(name)) continue;

                var cmap = new PyDict();
                var glyphs = new PyDict();
                long unitsPerEm;
                fixed (byte* p = bytes)
                {
                    FreeType.Check(FreeType.NewMemoryFace(library, p, bytes.Length, 0, out var face));
                    try
                    {
                        FreeType.Check(FreeType.SetCharSize(face, (nint)Py.ToLong(Math.Truncate(Py.ToDouble(size) * 64)), 0, 72, 72));
                        int flags = FreeType.LoadNoBitmap | (hinted ? 0 : FreeType.LoadNoHinting);
                        foreach (int ch in chars)
                        {
                            uint gid = FreeType.GetCharIndex(face, (nuint)ch);
                            if (gid == 0) continue;
                            cmap[ch.ToString(Py.Inv)] = (long)gid;
                            string gkey = gid.ToString(Py.Inv);
                            if (glyphs.ContainsKey(gkey)) continue;
                            int err = FreeType.LoadGlyph(face, gid, flags);
                            if (err != 0)
                            {
                                failed.Add(new PyDict { ["font"] = font["name"], ["char"] = char.ConvertFromUtf32(ch), ["error"] = new FreeType.FtException(err).Message });
                                continue;
                            }
                            var slot = face->Glyph;
                            var m = slot->Metrics;
                            glyphs[gkey] = new List<object?>
                            {
                                (long)m.Width, (long)m.Height, (long)m.HoriBearingX, (long)m.HoriBearingY, (long)m.HoriAdvance, Outline(slot->Outline),
                            };
                        }
                        unitsPerEm = face->UnitsPerEM;
                    }
                    finally
                    {
                        FreeType.DoneFace(face);
                    }
                }
                stores[name] = new PyDict
                {
                    ["font"] = font["name"], ["pointSize"] = size, ["hinted"] = hinted, ["unitsPerEm"] = unitsPerEm, ["cmap"] = cmap, ["glyphs"] = glyphs,
                };
                fontsReport[name] = new PyDict { ["font"] = font["name"], ["chars"] = (long)cmap.Count, ["glyphs"] = (long)glyphs.Count };
            }
        }
        finally
        {
            FreeType.DoneFreeType(library);
        }
        foreach (var (name, store) in stores)
            PyStr.WriteText(PyPath.Join(outDir, (string)name!), PyJson.Compact(store));
        report["seconds"] = ImportContext.Seconds(t0);
        PyStr.WriteText(PyPath.Join(assets, "glyphs.json"), PyJson.Compact(new PyDict { ["fontAssets"] = index, ["report"] = report }));
        return report;
    }
}
