// 貼圖原始壓縮資料的比對工具（docs/perf-battle.md「B 段」的驗收）。原版只讀，輸出放 repo 外。
//   export <原版遊戲資料夾> <輸出資料夾> [all|none]
//       用啟動器同一份 exporter（RawTextureExportHandler）匯出；all = 四種格式都留原始資料，none = 全部 PNG（等於原本的匯出）
//   verify <原版遊戲資料夾> <out/assets> [<舊的 out/assets>]
//       raw：textures.json 裡每張 .mt2t 照執行期的讀法（RawTexture.Read、Level）切出每一層，跟原版 GetImageData() 的同一段逐位元組比；
//            標頭的格式、寬高、層數、sRGB 跟原版比
//       png：留 PNG 的跟舊安裝同一個 src 的檔逐位元組比
//       cpu：每張 .mt2t 用 CPU 解（RawTextureDecoder，執行期 GPU 不支援時的退路）跟舊安裝的 PNG 解出來再上下翻（遊戲讀 PNG 的做法）比
//   texhash <原版遊戲資料夾> <texhash.txt>
//       遊戲的診斷開關（TEST_ENTRY 建置 am start --es texhash 1）記下每次 glCompressedTexImage2D 送出的位元組雜湊，跟原版 GetImageData() 的同一段比
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AssetRipper.Export.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Mt2Mobile.Shared;
using Mt2Mobile.TextureExport;

if (args.Length < 3) { Console.Error.WriteLine("用法見 Program.cs 開頭"); return 2; }
string cmd = args[0], game = args[1];

FullConfiguration settings = new();
// 同 targets/mt2-2.2.1-win.json 的 assetRipper
settings.ExportSettings.ScriptExportMode = ScriptExportMode.DecompiledGameOnly;
settings.ExportSettings.ShaderExportMode = ShaderExportMode.Yaml;
var sw = Stopwatch.StartNew();

if (cmd == "export")
{
    var plan = args.Length > 3 && args[3] == "none" ? TexturePlan.None() : TexturePlan.All();
    var h = new RawTextureExportHandler(settings, plan);
    h.LoadProcessAndExport([game], args[2], LocalFileSystem.Instance);
    Console.WriteLine($"export {plan.Key}：{sw.Elapsed.TotalSeconds:F1} 秒；{h.Stats.Describe().ToJsonString()}");
    return 0;
}

var gd = new RawTextureExportHandler(settings, TexturePlan.None()).LoadAndProcess([game], LocalFileSystem.Instance);
var bySource = new Dictionary<string, ITexture2D>(StringComparer.Ordinal);
foreach (var a in gd.GameBundle.FetchAssets())
    if (a is ITexture2D t) bySource[$"{t.Collection.Name}|{t.PathID}"] = t;
Console.WriteLine($"原版 Texture2D {bySource.Count} 張（載入 {sw.Elapsed.TotalSeconds:F1} 秒）");

if (cmd == "verify")
{
    string assets = args[2];
    string? oldAssets = args.Length > 3 ? args[3] : null;
    var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(assets, "textures.json"))).RootElement;
    Dictionary<string, string> oldBySrc = new(StringComparer.Ordinal);
    // AssetRipper 撞名時加的 _N 每次匯出的順序可能不同（docs/phase4.md 拆段 B：FX_Flash* 編號互換）：同一個去掉 _N 的名字底下的都當候選
    Dictionary<string, List<string>> oldByStem = new(StringComparer.Ordinal);
    static string Stem(string src) => System.Text.RegularExpressions.Regex.Replace(src, @"(_\d+)?\.png$", "");
    if (oldAssets != null)
        foreach (var e in JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oldAssets, "textures.json"))).RootElement.EnumerateObject())
        {
            var src = e.Value.GetProperty("src").GetString()!;
            var f = Path.Combine(oldAssets, e.Value.GetProperty("file").GetString()!);
            oldBySrc[src] = f;
            if (!oldByStem.TryGetValue(Stem(src), out var l)) oldByStem[Stem(src)] = l = [];
            l.Add(f);
        }

    int raw = 0, rawBad = 0, levels = 0, headerBad = 0, png = 0, pngSame = 0, pngDiff = 0, pngNoRef = 0;
    long rawBytes = 0;
    var fmtCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var cpuWork = new List<(string name, string file, string? oldPng)>();
    int pngRenumbered = 0;
    foreach (var e in table.EnumerateObject())
    {
        var r = e.Value;
        string file = Path.Combine(assets, r.GetProperty("file").GetString()!), src = r.GetProperty("src").GetString()!;
        if (!file.EndsWith(RawTexture.Extension, StringComparison.Ordinal))
        {
            png++;
            if (!oldBySrc.TryGetValue(src, out var o)) { pngNoRef++; continue; }
            var mine = File.ReadAllBytes(file);
            if (mine.AsSpan().SequenceEqual(File.ReadAllBytes(o))) pngSame++;
            else if (oldByStem[Stem(src)].Any(c => mine.AsSpan().SequenceEqual(File.ReadAllBytes(c)))) { pngSame++; pngRenumbered++; }
            else { pngDiff++; Console.WriteLine($"  png 不同：{src}"); }
            continue;
        }
        raw++;
        var h = RawTexture.Read(file, out var data);
        rawBytes += data.Length;
        fmtCount[RawTexture.FormatName(h.Format)] = fmtCount.GetValueOrDefault(RawTexture.FormatName(h.Format)) + 1;
        if (!bySource.TryGetValue(h.Source, out var tex)) { rawBad++; Console.WriteLine($"  找不到原版：{src} {h.Source}"); continue; }
        var want = tex.GetImageData();
        bool srgb = tex.ColorSpace_C28E == ColorSpace.Linear;
        if ((int)tex.Format_C28E != h.Format || tex.Width_C28 != h.Width || tex.Height_C28 != h.Height || Math.Max(1, tex.MipCount_C28) != h.Mips || srgb != h.Srgb
            || r.GetProperty("width").GetInt32() != h.Width || r.GetProperty("height").GetInt32() != h.Height
            || r.GetProperty("format").GetString() != RawTexture.FormatName(h.Format) || r.GetProperty("mips").GetInt32() != h.Mips)
        {
            headerBad++;
            Console.WriteLine($"  標頭不同：{src}");
        }
        bool ok = want.Length == h.RawLength;
        int end = 0;
        for (int l = 0; l < h.Mips && ok; l++)
        {
            var (off, len) = h.Level(l);
            ok = data.AsSpan(off, len).SequenceEqual(want.AsSpan(off, len));
            end = off + len;
            levels++;
        }
        if (!ok || end != want.Length) { rawBad++; Console.WriteLine($"  資料不同：{src}"); }
        cpuWork.Add((src, file, oldBySrc.GetValueOrDefault(src)));
    }
    Console.WriteLine($"RESULT raw：{raw} 張（{string.Join("、", fmtCount.Select(kv => $"{kv.Key} {kv.Value}"))}），{levels} 層、{rawBytes:N0} 位元組跟 GetImageData() 比，不一致 {rawBad} 張，標頭不一致 {headerBad} 張");
    Console.WriteLine($"RESULT png：{png} 張，跟舊安裝相同 {pngSame}（其中撞名編號不同 {pngRenumbered}）、不同 {pngDiff}、舊安裝沒有 {pngNoRef}");
    if (oldAssets != null)
    {
        int same = 0, renumbered = 0, diff = 0, noRef = 0, maxDiff = 0;
        var s2 = Stopwatch.StartNew();
        // 跟一張 PNG 比：-1 = 大小不同，其他 = 最大的色階差（0 是逐位元組相同）
        static int Compare(byte[] mine, RawTexture h, string png)
        {
            var img = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(png), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (img.Width != h.Width || img.Height != h.Height) return -1;
            int md = 0, stride = h.Width * 4;
            for (int y = 0; y < h.Height; y++)
            {
                // PNG 由上而下，原始資料由下而上
                var a = mine.AsSpan(y * stride, stride);
                var b = img.Data.AsSpan((h.Height - 1 - y) * stride, stride);
                if (a.SequenceEqual(b)) continue;
                for (int i = 0; i < stride; i++) md = Math.Max(md, Math.Abs(a[i] - b[i]));
            }
            return md;
        }
        Parallel.ForEach(cpuWork, new ParallelOptions { MaxDegreeOfParallelism = 8 }, w =>
        {
            if (w.oldPng == null) { Interlocked.Increment(ref noRef); return; }
            var h = RawTexture.Read(w.file, out var data);
            var mine = RawTextureDecoder.DecodeLevel0(h, data);
            int md = Compare(mine, h, w.oldPng);
            if (md == 0) { Interlocked.Increment(ref same); return; }
            if (oldByStem[Stem(w.name)].Any(c => c != w.oldPng && Compare(mine, h, c) == 0)) { Interlocked.Increment(ref renumbered); return; }
            Interlocked.Increment(ref diff);
            lock (cpuWork) { maxDiff = Math.Max(maxDiff, md < 0 ? 999 : md); Console.WriteLine($"  cpu 不同：{w.name}（最大差 {md}）"); }
        });
        Console.WriteLine($"RESULT cpu：{cpuWork.Count} 張用 CPU 解，跟舊安裝同一個 src 的 PNG 相同 {same}、撞名編號不同但跟同名的另一張相同 {renumbered}、不同 {diff}（最大差 {maxDiff}）、舊安裝沒有 {noRef}（{s2.Elapsed.TotalSeconds:F1} 秒）");
    }
    return rawBad + headerBad + pngDiff > 0 ? 1 : 0;
}

if (cmd == "texhash")
{
    // 每行：<來源>\t<層>\t<長度>\t<sha256>（遊戲的 texhash 診斷開關寫的，files/logs/texhash.txt）
    int n = 0, bad = 0;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(args[2]))
    {
        var p = line.Split('\t');
        if (p.Length != 4) continue;
        n++;
        seen.Add(p[0]);
        if (!bySource.TryGetValue(p[0], out var tex)) { bad++; Console.WriteLine($"  找不到原版：{line}"); continue; }
        var want = tex.GetImageData();
        int format = (int)tex.Format_C28E, level = int.Parse(p[1]), mips = Math.Max(1, tex.MipCount_C28);
        int off = 0;
        for (int l = 0; l < level; l++) off += RawTexture.LevelSize(format, tex.Width_C28, tex.Height_C28, l);
        int len = RawTexture.LevelSize(format, tex.Width_C28, tex.Height_C28, level);
        string sha = level < mips && int.Parse(p[2]) == len ? Convert.ToHexString(SHA256.HashData(want.AsSpan(off, len))).ToLowerInvariant() : "";
        if (sha != p[3]) { bad++; Console.WriteLine($"  不同：{line}"); }
    }
    Console.WriteLine($"RESULT texhash：{seen.Count} 張、{n} 層的上傳跟 GetImageData() 比，不一致 {bad}");
    return bad > 0 ? 1 : 0;
}

Console.Error.WriteLine($"不認得的指令 {cmd}");
return 2;
