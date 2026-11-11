// TextCore 的 FontEngine（TMP 動態字型加字用）的原生半邊。
//
// 原版由 FreeType 讀字型檔、即時算 SDF。這裡不在執行期解析字型檔：匯入器（scripts/import/glyphs.py）先用 FreeType 把
// 遊戲文字會用到的字元輪廓抽成 assets/glyphs/*.json（cmap、每個字形的度量與 26.6 輪廓），執行期照 TS 版
//（unity-cs2ts/runtime/src/font/sdf.ts、atlas.ts）算距離場、用 MaxRects 排進 atlas。
//   - SDF：像素中心到輪廓的有號距離 d，值 = 127.5 + d × 255 / (2 × (padding + 1))，夾在 0–255（10/5 對 build 裡烘好的 atlas 驗證，平均差約 1 階）。
//   - 範圍照 FreeType 點陣化：左 = floor(xMin)、右 = ceil(xMax)、下 = floor(yMin)、上 = ceil(yMax)。
//   - 每個字在 atlas 佔 (寬 + 2 × padding + 1) × (高 + 2 × padding + 1)，GlyphRect 從佔用區左下角往內 padding + 1。
//   - 空區／佔用區清單由 managed 的 TMP_FontAsset 保管（含 build 裡烘好的字），這裡照傳進來的清單排。
// 動態字型的 atlas 若在 build 時清空、AssetRipper 沒匯出（嵌在字型資產裡的子素材），補一張 0×0 的 Alpha8 貼圖：
// TMP 加第一個字時看到寬高 0 會自己 Resize 到 m_AtlasWidth × m_AtlasHeight。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace UnityNative
{
    public static class FontFaces
    {
        public sealed class Store
        {
            public string Font;
            public float PointSize;
            public readonly Dictionary<uint, uint> Cmap = new Dictionary<uint, uint>();
            public readonly Dictionary<uint, (float w, float h, float bx, float by, float adv, float[][][] outline)> Glyphs = new Dictionary<uint, (float, float, float, float, float, float[][][])>();
        }

        static Dictionary<string, string> storeOfFont;   // Font 的 guid → 字形檔
        static readonly Dictionary<string, Store> stores = new Dictionary<string, Store>();
        static Dictionary<string, (string name, TextureFormat fmt)> clearedAtlases;
        public static Store Current;
        public static float Scale = 1;
        static readonly HashSet<string> warned = new HashSet<string>();

        public static void Register() => Assets.Missing.Add(MissingAtlas);

        /// <summary>TMP 字型資產：Font guid → 字形檔；清空過的動態字型 atlas（字形表空、貼圖不在素材庫）</summary>
        static void Scan()
        {
            if (storeOfFont != null) return;
            storeOfFont = new Dictionary<string, string>();
            clearedAtlases = new Dictionary<string, (string, TextureFormat)>();
            var glyphIndex = Assets.Index("glyphs")?["fontAssets"] as JsonObject ?? new JsonObject();
            var fonts = new List<(string guid, string file, string name)>();
            foreach (var kv in Assets.Index("monobehaviours"))
                if (kv.Value?["script"]?.ToString() == "TMP_FontAsset" && kv.Value["file"] != null) fonts.Add((kv.Key, kv.Value["file"].ToString(), kv.Value["name"]?.ToString()));
            // 每個字型資產最大 1 MB，只要四個欄位：平行讀、JsonElement 直接取，結果照原本順序套用
            var found = new (bool ok, string src, bool dynamicEmpty, List<string> atlases)[fonts.Count];
            System.Threading.Tasks.Parallel.For(0, fonts.Count, i =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Assets.AssetPath(fonts[i].file)));
                    var o = doc.RootElement;
                    var src = o.TryGetProperty("m_SourceFontFile", out var sf) ? Serializer.Str(sf, "guid") : null;
                    bool dynamicEmpty = o.TryGetProperty("m_AtlasPopulationMode", out var pm) && AnimationAssets.F(pm) == 1
                        && !(o.TryGetProperty("m_GlyphTable", out var gt) && gt.ValueKind == System.Text.Json.JsonValueKind.Array && gt.GetArrayLength() > 0);
                    var atlases = new List<string>();
                    if (dynamicEmpty && o.TryGetProperty("m_AtlasTextures", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var t in at.EnumerateArray()) if (Serializer.Str(t, "guid") is string g) atlases.Add(g);
                    found[i] = (true, src, dynamicEmpty, atlases);
                }
                catch { }
            });
            for (int i = 0; i < fonts.Count; i++)
            {
                var (ok, src, dynamicEmpty, atlases) = found[i];
                if (!ok) continue;
                if (src != null && glyphIndex[fonts[i].guid] is JsonNode f) storeOfFont.TryAdd(src, f.ToString());
                if (!dynamicEmpty) continue;
                foreach (var g in atlases) clearedAtlases.TryAdd(g, ((fonts[i].name ?? "") + " Atlas", TextureFormat.Alpha8));
            }
        }

        static UnityEngine.Object MissingAtlas(string guid, string fileId, Type want)
        {
            if (fileId != "2800000" || !(want.IsAssignableFrom(typeof(Texture2D)))) return null;
            Scan();
            if (!clearedAtlases.TryGetValue(guid, out var a)) return null;
            var t = (Texture2D)Native.NewShell(typeof(Texture2D));
            Native.Bind(new NTexture { Name = a.name, Width = 0, Height = 0, Pixels = Array.Empty<byte>(), Readable = true, SRGB = false, Format = a.fmt, Source = guid }, t, true);
            return t;
        }

        public static Store StoreOf(Font font)
        {
            Scan();
            var guid = Native.Of<NFont>(font)?.Source;
            if (guid == null || !storeOfFont.TryGetValue(guid, out var file)) return null;
            if (stores.TryGetValue(file, out var s)) return s;
            s = new Store();
            try
            {
                var j = Assets.ReadAssetJson("glyphs/" + file);
                s.Font = j["font"]?.ToString();
                s.PointSize = AnimationAssets.F(j["pointSize"]);
                foreach (var kv in j["cmap"] as JsonObject ?? new JsonObject())
                    if (uint.TryParse(kv.Key, out var u)) s.Cmap[u] = (uint)AnimationAssets.F(kv.Value);
                foreach (var kv in j["glyphs"] as JsonObject ?? new JsonObject())
                {
                    if (!uint.TryParse(kv.Key, out var gid) || kv.Value is not JsonArray a || a.Count < 6) continue;
                    var contours = a[5] as JsonArray ?? new JsonArray();
                    var outline = new float[contours.Count][][];
                    for (int c = 0; c < contours.Count; c++)
                    {
                        var pts = contours[c] as JsonArray ?? new JsonArray();
                        outline[c] = new float[pts.Count][];
                        for (int p = 0; p < pts.Count; p++)
                            outline[c][p] = new[] { AnimationAssets.F(pts[p]?[0]), AnimationAssets.F(pts[p]?[1]), AnimationAssets.F(pts[p]?[2]) };
                    }
                    s.Glyphs[gid] = (AnimationAssets.F(a[0]), AnimationAssets.F(a[1]), AnimationAssets.F(a[2]), AnimationAssets.F(a[3]), AnimationAssets.F(a[4]), outline);
                }
            }
            catch (Exception e) { Engine.LogException(e); }
            stores[file] = s;
            return s;
        }

        /// <summary>自我測試：把某套字型字形檔的所有字照 TMP 的加字路徑排進一張 size² 的 atlas，回傳 RGBA 像素（列 0 在最下面）</summary>
        public static byte[] SelfTest(string fontName, int size, int padding)
        {
            Scan();
            var glyphIndex = Assets.Index("glyphs")?["fontAssets"] as JsonObject ?? new JsonObject();
            foreach (var kv in storeOfFont)
            {
                var font = Assets.Load(kv.Key, "12800000", typeof(Font)) as Font;
                var s = font != null ? StoreOf(font) : null;
                if (s == null || !string.Equals(s.Font, fontName, StringComparison.OrdinalIgnoreCase)) continue;
                Current = s;
                Scale = 1;
                var tex = new NTexture { Width = size, Height = size, Pixels = new byte[size * size * 4] };
                var free = new GlyphRect[4096];
                var used = new GlyphRect[4096];
                free[0] = new GlyphRect(0, 0, size, size);
                int fc = 1, uc = 0, ok = 0;
                foreach (var gid in s.Glyphs.Keys)
                {
                    var t = (Texture2D)Native.NewShell(typeof(Texture2D));
                    if (AddToTextureRaw(gid, padding, free, ref fc, used, ref uc, tex)) ok++;
                }
                Console.WriteLine("字型自我測試：" + s.Font + " " + ok + "/" + s.Glyphs.Count + " 個字形排進 " + size + "²");
                return tex.Pixels;
            }
            Console.WriteLine("字型自我測試：找不到字形檔 " + fontName);
            return null;
        }

        static bool AddToTextureRaw(uint index, int p, GlyphRect[] free, ref int fc, GlyphRect[] used, ref int uc, NTexture t)
        {
            var d = Current.Glyphs[index];
            var bmp = Sdf(d.outline, Scale, p, out var box);
            if (box.width == 0 || box.height == 0) return true;
            if (!Pack(box.width + 2 * p + 1, box.height + 2 * p + 1, free, ref fc, used, ref uc, out var slot)) return false;
            Blit(t, bmp, slot.m_X + 1, slot.m_Y + 1, box.width + 2 * p, box.height + 2 * p);
            return true;
        }

        public static void Warn(string msg) { if (warned.Add(msg)) Engine.Log(LogType.Warning, msg); }

        // ───────── SDF（照 sdf.ts） ─────────

        const int ConicSteps = 16, CubicSteps = 24;

        /// <summary>曲線切成線段（像素單位）。輪廓是 26.6，tag 1 線上點、0 二次控制點、2 三次控制點</summary>
        static List<float> Flatten(float[][][] outline, float scale)
        {
            var o = new List<float>();
            foreach (var contour in outline)
            {
                int n = contour.Length;
                if (n == 0) continue;
                var pts = new (float x, float y, int t)[n];
                for (int i = 0; i < n; i++) pts[i] = (contour[i][0] / 64f * scale, contour[i][1] / 64f * scale, (int)contour[i][2]);
                int start = Array.FindIndex(pts, p => p.t == 1);
                (float x, float y, int t) first;
                var order = new List<(float x, float y, int t)>();
                if (start < 0)
                {
                    // 全是二次控制點：用頭兩點的中點當起點（TrueType 的規則）
                    first = ((pts[0].x + pts[1 % n].x) / 2, (pts[0].y + pts[1 % n].y) / 2, 1);
                    for (int i = 1; i < n; i++) order.Add(pts[i]);
                    order.Add(pts[0]);
                }
                else
                {
                    first = pts[start];
                    for (int i = start + 1; i < n; i++) order.Add(pts[i]);
                    for (int i = 0; i < start; i++) order.Add(pts[i]);
                }
                order.Add(first);
                float cx = first.x, cy = first.y;
                for (int k = 0; k < order.Count;)
                {
                    var p = order[k];
                    if (p.t == 1)
                    {
                        o.Add(cx); o.Add(cy); o.Add(p.x); o.Add(p.y);
                        cx = p.x; cy = p.y;
                        k++;
                    }
                    else if (p.t == 0)
                    {
                        float qx = p.x, qy = p.y;
                        k++;
                        while (true)
                        {
                            var q = order[Math.Min(k, order.Count - 1)];
                            float ex = q.t == 1 ? q.x : (qx + q.x) / 2, ey = q.t == 1 ? q.y : (qy + q.y) / 2;
                            float px = cx, py = cy;
                            for (int s = 1; s <= ConicSteps; s++)
                            {
                                float t = s / (float)ConicSteps, u = 1 - t;
                                float x = u * u * cx + 2 * u * t * qx + t * t * ex, y = u * u * cy + 2 * u * t * qy + t * t * ey;
                                o.Add(px); o.Add(py); o.Add(x); o.Add(y);
                                px = x; py = y;
                            }
                            cx = ex; cy = ey;
                            if (q.t == 1 || k >= order.Count - 1) { k++; break; }
                            qx = q.x; qy = q.y;
                            k++;
                        }
                    }
                    else
                    {
                        var c1 = p;
                        var c2 = order[Math.Min(k + 1, order.Count - 1)];
                        var e = order[Math.Min(k + 2, order.Count - 1)];
                        float px = cx, py = cy;
                        for (int s = 1; s <= CubicSteps; s++)
                        {
                            float t = s / (float)CubicSteps, u = 1 - t;
                            float x = u * u * u * cx + 3 * u * u * t * c1.x + 3 * u * t * t * c2.x + t * t * t * e.x;
                            float y = u * u * u * cy + 3 * u * u * t * c1.y + 3 * u * t * t * c2.y + t * t * t * e.y;
                            o.Add(px); o.Add(py); o.Add(x); o.Add(y);
                            px = x; py = y;
                        }
                        cx = e.x; cy = e.y;
                        k += 3;
                    }
                }
            }
            return o;
        }

        /// <summary>FreeType 點陣的範圍（像素）：輪廓控制框（含控制點）取整</summary>
        public static (int left, int bottom, int width, int height) Box(float[][][] outline, float scale)
        {
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (var c in outline)
                foreach (var p in c)
                {
                    float x = p[0] * scale, y = p[1] * scale;
                    xMin = Math.Min(xMin, x); yMin = Math.Min(yMin, y); xMax = Math.Max(xMax, x); yMax = Math.Max(yMax, y);
                }
            if (xMin == float.MaxValue) return (0, 0, 0, 0);
            int left = (int)MathF.Floor(xMin / 64), bottom = (int)MathF.Floor(yMin / 64);
            return (left, bottom, (int)MathF.Ceiling(xMax / 64) - left, (int)MathF.Ceiling(yMax / 64) - bottom);
        }

        /// <summary>有號距離場，(寬 + 2 padding) × (高 + 2 padding)，第一列在最下面</summary>
        public static byte[] Sdf(float[][][] outline, float scale, int padding, out (int left, int bottom, int width, int height) box)
        {
            box = Box(outline, scale);
            int W = box.width + 2 * padding, H = box.height + 2 * padding;
            var data = new byte[Math.Max(0, W * H)];
            if (box.width == 0 || box.height == 0) return data;
            float spread = padding + 1;
            float ox = box.left - padding, oy = box.bottom - padding;
            var segs = Flatten(outline, scale);
            var dist = new float[W * H];
            Array.Fill(dist, spread);
            for (int s = 0; s < segs.Count; s += 4)
            {
                float ax = segs[s], ay = segs[s + 1], bx = segs[s + 2], by = segs[s + 3];
                float dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                int i0 = Math.Max(0, (int)MathF.Floor(Math.Min(ax, bx) - spread - ox - 0.5f)), i1 = Math.Min(W - 1, (int)MathF.Ceiling(Math.Max(ax, bx) + spread - ox - 0.5f));
                int j0 = Math.Max(0, (int)MathF.Floor(Math.Min(ay, by) - spread - oy - 0.5f)), j1 = Math.Min(H - 1, (int)MathF.Ceiling(Math.Max(ay, by) + spread - oy - 0.5f));
                for (int j = j0; j <= j1; j++)
                {
                    float py = oy + j + 0.5f;
                    for (int i = i0; i <= i1; i++)
                    {
                        float px = ox + i + 0.5f;
                        float t = len2 > 1e-12f ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0;
                        t = t < 0 ? 0 : t > 1 ? 1 : t;
                        float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
                        float d = MathF.Sqrt(ex * ex + ey * ey);
                        int k = j * W + i;
                        if (d < dist[k]) dist[k] = d;
                    }
                }
            }
            // 內外：每一列的像素中心做非零環繞數（FreeType 預設的填色規則）
            var cross = new List<(float x, int w)>();
            for (int j = 0; j < H; j++)
            {
                float py = oy + j + 0.5f;
                cross.Clear();
                for (int s = 0; s < segs.Count; s += 4)
                {
                    float ay = segs[s + 1], by = segs[s + 3];
                    if (ay <= py && by > py) cross.Add((segs[s] + (py - ay) / (by - ay) * (segs[s + 2] - segs[s]), 1));
                    else if (ay > py && by <= py) cross.Add((segs[s] + (py - ay) / (by - ay) * (segs[s + 2] - segs[s]), -1));
                }
                cross.Sort((a, b) => a.x.CompareTo(b.x));
                int c = 0, wind = 0;
                for (int i = 0; i < W; i++)
                {
                    float px = ox + i + 0.5f;
                    while (c < cross.Count && cross[c].x < px) wind += cross[c++].w;
                    float d = wind != 0 ? dist[j * W + i] : -dist[j * W + i];
                    float v = 127.5f + d * 255 / (2 * spread);
                    data[j * W + i] = (byte)(v <= 0 ? 0 : v >= 255 ? 255 : MathF.Round(v));
                }
            }
            return data;
        }

        // ───────── MaxRects（照 atlas.ts，Best Short Side Fit） ─────────

        public static bool Pack(int width, int height, GlyphRect[] free, ref int freeCount, GlyphRect[] used, ref int usedCount, out GlyphRect slot)
        {
            slot = default;
            int bestShort = int.MaxValue, bestLong = int.MaxValue;
            bool found = false;
            for (int i = 0; i < freeCount; i++)
            {
                var f = free[i];
                if (f.m_Width < width || f.m_Height < height) continue;
                int lh = f.m_Width - width, lv = f.m_Height - height;
                int sh = Math.Min(lh, lv), lo = Math.Max(lh, lv);
                if (sh < bestShort || (sh == bestShort && lo < bestLong))
                {
                    slot = new GlyphRect(f.m_X, f.m_Y, width, height);
                    bestShort = sh; bestLong = lo; found = true;
                }
            }
            if (!found) return false;
            var r = slot;
            var next = new List<GlyphRect>();
            for (int i = 0; i < freeCount; i++)
            {
                var f = free[i];
                if (r.m_X >= f.m_X + f.m_Width || r.m_X + r.m_Width <= f.m_X || r.m_Y >= f.m_Y + f.m_Height || r.m_Y + r.m_Height <= f.m_Y) { next.Add(f); continue; }
                if (r.m_X > f.m_X) next.Add(new GlyphRect(f.m_X, f.m_Y, r.m_X - f.m_X, f.m_Height));
                if (r.m_X + r.m_Width < f.m_X + f.m_Width) next.Add(new GlyphRect(r.m_X + r.m_Width, f.m_Y, f.m_X + f.m_Width - (r.m_X + r.m_Width), f.m_Height));
                if (r.m_Y > f.m_Y) next.Add(new GlyphRect(f.m_X, f.m_Y, f.m_Width, r.m_Y - f.m_Y));
                if (r.m_Y + r.m_Height < f.m_Y + f.m_Height) next.Add(new GlyphRect(f.m_X, r.m_Y + r.m_Height, f.m_Width, f.m_Y + f.m_Height - (r.m_Y + r.m_Height)));
            }
            static bool Contained(GlyphRect a, GlyphRect b) => a.m_X >= b.m_X && a.m_Y >= b.m_Y && a.m_X + a.m_Width <= b.m_X + b.m_Width && a.m_Y + a.m_Height <= b.m_Y + b.m_Height;
            var keep = new List<GlyphRect>();
            for (int i = 0; i < next.Count; i++)
            {
                bool drop = false;
                for (int j = 0; j < next.Count && !drop; j++)
                    if (j != i && Contained(next[i], next[j]) && !(Contained(next[j], next[i]) && j > i)) drop = true;
                if (!drop) keep.Add(next[i]);
            }
            // managed 給的陣列容量至少是空區＋佔用＋1；放不下就當這張 atlas 滿了
            if (keep.Count > free.Length || usedCount + 1 > used.Length) return false;
            for (int i = 0; i < keep.Count; i++) free[i] = keep[i];
            freeCount = keep.Count;
            used[usedCount++] = r;
            return true;
        }

        public static bool Glyph(uint index, out GlyphMarshallingStruct g)
        {
            g = default;
            if (Current == null || !Current.Glyphs.TryGetValue(index, out var d)) return false;
            g.index = index;
            g.metrics = new GlyphMetrics(d.w / 64 * Scale, d.h / 64 * Scale, d.bx / 64 * Scale, d.by / 64 * Scale, d.adv / 64 * Scale);
            g.scale = 1;
            return true;
        }

        static readonly bool debug = Environment.GetEnvironmentVariable("UN_FONT_DEBUG") == "1";

        public static bool AddToTexture(uint index, int padding, GlyphRect[] free, ref int freeCount, GlyphRect[] used, ref int usedCount, GlyphRenderMode mode, Texture2D texture, out GlyphMarshallingStruct glyph)
        {
            if (!Glyph(index, out glyph)) return false;
            var t = Native.Of<NTexture>(texture);
            if (t == null) return false;
            if (((int)mode & 0x60) == 0) Warn("FontEngine：GlyphRenderMode " + mode + " 當成 SDF 畫");
            var d = Current.Glyphs[index];
            var bmp = Sdf(d.outline, Scale, padding, out var box);
            if (box.width == 0 || box.height == 0) { glyph.glyphRect = new GlyphRect(0, 0, 0, 0); return true; }
            int p = padding;
            if (!Pack(box.width + 2 * p + 1, box.height + 2 * p + 1, free, ref freeCount, used, ref usedCount, out var slot)) return false;
            var rect = new GlyphRect(slot.m_X + p + 1, slot.m_Y + p + 1, box.width, box.height);
            glyph.glyphRect = rect;
            Blit(t, bmp, rect.m_X - p, rect.m_Y - p, box.width + 2 * p, box.height + 2 * p);
            if (debug) Console.WriteLine("[FontEngine] " + Current.Font + " 加字形 " + index + " → (" + rect.m_X + "," + rect.m_Y + " " + rect.m_Width + "×" + rect.m_Height + ")，atlas " + t.Name + " " + t.Width + "×" + t.Height);
            return true;
        }

        /// <summary>寫進 atlas 的 RGBA8 像素：Alpha8 取樣是 (0,0,0,a)，TMP 的 shader 讀 a</summary>
        static void Blit(NTexture t, byte[] bmp, int x0, int y0, int W, int H)
        {
            // 從檔案來的 atlas（build 裡已經烘了字、Read/Write 開著，例如 Diceomancer 的江城圓體 600W）先解出原本的像素，不然烘好的字會被蓋成空白
            if (t.Pixels == null && t.File != null)
            {
                try { var dec = Png.Decode(t.File, out int w, out int h); if (dec != null) { t.Pixels = dec; t.Width = w; t.Height = h; } }
                catch (Exception e) { Assets.Warn("atlas 讀不了 " + t.File + "：" + e.Message); }
            }
            int tw = t.Width, th = t.Height;
            if (t.Pixels == null || t.Pixels.Length < tw * th * 4) t.Pixels = new byte[tw * th * 4];
            t.CpuWritten = true;
            var px = t.Pixels;
            for (int j = 0; j < H; j++)
            {
                int y = y0 + j;
                if (y < 0 || y >= th) continue;
                for (int i = 0; i < W; i++)
                {
                    int x = x0 + i;
                    if (x < 0 || x >= tw) continue;
                    int o = (y * tw + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 0;
                    px[o + 3] = bmp[j * W + i];
                }
            }
            NTexture.TraceSet?.Invoke(t, "FontEngine 加字", true);
            t.GpuDirty = true;
            t.UpdateCount++;
        }
    }
}

namespace UnityNative.Icalls
{
    using UnityNative;

    public static unsafe partial class UnityEngine_TextCore_LowLevel_FontEngine
    {
        const int Success = 0, InvalidFace = 35, InvalidGlyphIndex = 16;

        public static int InitializeFontEngine_Internal() => Success;
        public static int DestroyFontEngine_Internal() => Success;

        static int Load(Font font, int pointSize)
        {
            var s = FontFaces.StoreOf(font);
            if (s == null)
            {
                FontFaces.Warn("FontEngine：字型 " + (font != null ? font.name : "null") + " 沒有字形檔（匯入器沒抽到），動態加字會失敗");
                FontFaces.Current = null;
                return InvalidFace;
            }
            if (debug) Console.WriteLine("[FontEngine] 載入 " + font.name + " " + pointSize + "pt → " + s.Font + "（" + s.Glyphs.Count + " 個字形）");
            FontFaces.Current = s;
            FontFaces.Scale = pointSize > 0 && s.PointSize > 0 ? pointSize / s.PointSize : 1;
            return Success;
        }

        public static int LoadFontFace_FromFont_Internal(Font font) => Load(font, 0);
        public static int LoadFontFace_With_Size_FromFont_Internal(Font font, int pointSize) => Load(font, pointSize);
        public static int LoadFontFace_With_Size_and_FaceIndex_FromFont_Internal(Font font, int pointSize, int faceIndex) => Load(font, pointSize);
        public static int UnloadFontFace_Internal() { FontFaces.Current = null; return Success; }
        public static int UnloadAllFontFaces_Internal() { FontFaces.Current = null; return Success; }
        public static int SetFaceSize_Internal(int pointSize)
        {
            var s = FontFaces.Current;
            if (s == null) return InvalidFace;
            FontFaces.Scale = pointSize > 0 && s.PointSize > 0 ? pointSize / s.PointSize : 1;
            return Success;
        }

        static readonly bool debug = Environment.GetEnvironmentVariable("UN_FONT_DEBUG") == "1";

        public static uint GetGlyphIndex(uint unicode)
        {
            uint g = FontFaces.Current != null && FontFaces.Current.Cmap.TryGetValue(unicode, out var x) ? x : 0;
            if (debug) Console.WriteLine("[FontEngine] " + FontFaces.Current?.Font + " 查 U+" + unicode.ToString("X4") + "（" + (char.IsControl((char)unicode) ? "?" : ((char)unicode).ToString()) + "）→ " + g);
            return g;
        }
        public static bool TryGetGlyphIndex(uint unicode, out uint glyphIndex) => (glyphIndex = GetGlyphIndex(unicode)) != 0;
        public static uint GetVariantGlyphIndex(uint unicode, uint variantSelectorUnicode) => GetGlyphIndex(unicode);
        public static int LoadGlyph_Internal(uint unicode, GlyphLoadFlags flags) => GetGlyphIndex(unicode) != 0 ? Success : InvalidGlyphIndex;

        public static bool TryGetGlyphWithUnicodeValue_Internal(uint unicode, GlyphLoadFlags flags, ref GlyphMarshallingStruct glyphStruct)
            => FontFaces.Glyph(GetGlyphIndex(unicode), out glyphStruct);
        public static bool TryGetGlyphWithIndexValue_Internal(uint glyphIndex, GlyphLoadFlags flags, ref GlyphMarshallingStruct glyphStruct)
            => FontFaces.Glyph(glyphIndex, out glyphStruct);

        public static bool TryAddGlyphToTexture_Internal(uint glyphIndex, int padding, GlyphPackingMode packingMode, GlyphRect[] freeGlyphRects, ref int freeGlyphRectCount,
            GlyphRect[] usedGlyphRects, ref int usedGlyphRectCount, GlyphRenderMode renderMode, Texture2D texture, out GlyphMarshallingStruct glyph)
            => FontFaces.AddToTexture(glyphIndex, padding, freeGlyphRects, ref freeGlyphRectCount, usedGlyphRects, ref usedGlyphRectCount, renderMode, texture, out glyph);

        public static bool TryAddGlyphsToTexture_Internal(uint[] glyphIndex, int padding, GlyphPackingMode packingMode, GlyphRect[] freeGlyphRects, ref int freeGlyphRectCount,
            GlyphRect[] usedGlyphRects, ref int usedGlyphRectCount, GlyphRenderMode renderMode, Texture2D texture, GlyphMarshallingStruct[] glyphs, ref int glyphCount)
        {
            int n = 0;
            bool all = true;
            foreach (var gi in glyphIndex ?? Array.Empty<uint>())
            {
                if (n >= glyphs.Length) break;
                if (FontFaces.AddToTexture(gi, padding, freeGlyphRects, ref freeGlyphRectCount, usedGlyphRects, ref usedGlyphRectCount, renderMode, texture, out var g)) glyphs[n++] = g;
                else all = false;
            }
            glyphCount = n;
            return all;
        }

        public static void ResetAtlasTexture(Texture2D texture)
        {
            var t = Native.Of<NTexture>(texture);
            if (t == null) return;
            t.Pixels = new byte[t.Width * t.Height * 4];
            t.CpuWritten = true;
            NTexture.TraceSet?.Invoke(t, "FontEngine 清 atlas", true);
            t.GpuDirty = true;
            t.UpdateCount++;
        }

        public static GlyphPairAdjustmentRecord[] GetPairAdjustmentRecords(uint glyphIndex) => Array.Empty<GlyphPairAdjustmentRecord>();
        public static GlyphPairAdjustmentRecord[] GetAllPairAdjustmentRecords() => Array.Empty<GlyphPairAdjustmentRecord>();
        public static int PopulatePairAdjustmentRecordMarshallingArray_from_KernTable(uint[] glyphIndexes, out int recordCount) { recordCount = 0; return Success; }
        public static int PopulatePairAdjustmentRecordMarshallingArray_from_GlyphIndex(uint glyphIndex, out int recordCount) { recordCount = 0; return Success; }
        public static int PopulatePairAdjustmentRecordMarshallingArray_for_NewlyAddedGlyphIndexes(uint[] newGlyphIndexes, uint[] allGlyphIndexes, out int recordCount) { recordCount = 0; return Success; }
        public static int PopulatePairAdjustmentRecordMarshallingArray(uint[] glyphIndexes, out int recordCount) { recordCount = 0; return Success; }
        public static int PopulatePairAdjustmentRecordMarshallingArray_for_LookupIndex(uint[] glyphIndexes, int lookupIndex, out int recordCount) { recordCount = 0; return Success; }
        public static int GetPairAdjustmentRecordsFromMarshallingArray(GlyphPairAdjustmentRecord[] glyphPairAdjustmentRecords) => 0;
    }
}
