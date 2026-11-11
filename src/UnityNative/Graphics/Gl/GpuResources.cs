// GPU 資源：貼圖（PNG 解碼、上傳；Unity 的 UV 原點在左下，所以 PNG 的列要上下翻；保留原始壓縮資料的 .mt2t 每層直接 glCompressedTexImage2D）、預設貼圖（white、black、gray、bump）、
// 網格的頂點與索引緩衝（照 NMesh.Version 判斷要不要重傳）、串流用的動態緩衝。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mt2Mobile.Shared;
using UnityEngine;

namespace UnityNative.Gl
{
    public sealed class GpuTexture
    {
        public uint Id;
        public int Width, Height;
        public bool Array;
        public uint UpdateCount;
        public bool Ready;
        /// <summary>上次上傳時的格式（sRGB、mipmap）</summary>
        public bool Srgb, Mips;
        /// <summary>CPU 寫過的小貼圖上次上傳的像素（Apply 之後內容沒變就不重傳）；其他是 null</summary>
        public byte[] Snapshot;
        /// <summary>上次用壓縮資料上傳、設過 TEXTURE_MAX_LEVEL（之後改傳 RGBA 時要設回預設）</summary>
        public bool LevelLimit;
    }

    public static unsafe class GpuResources
    {
        static readonly Dictionary<string, GpuTexture> defaults = new Dictionary<string, GpuTexture>();
        public static int TexturesUploaded;
        public static long UploadMs;

        public static void Init()
        {
            DetectCompressed();
            Png.Decoder = DecodeFile;
            NTexture.GpuRelease = t => { if (t.Gpu is GpuTexture g && g.Id != 0) { uint id = g.Id; GL.DeleteTextures(1, &id); g.Id = 0; } };
            NMesh.GpuRelease = m => { if (m.Gpu is GpuMesh g) g.Delete(); };
        }

        /// <summary>宿主提供的 PNG 解碼（例如 Android 的 BitmapFactory，原生解碼比 managed 的 StbImageSharp 快很多）：
        /// 回傳由上而下、沒有預乘 alpha 的 RGBA；回 null 就退回 StbImageSharp</summary>
        public static Func<string, (byte[] rgba, int w, int h)?> NativeDecoder;

        public static (byte[] rgba, int w, int h) DecodeFile(string file)
        {
            if (NativeDecoder?.Invoke(file) is { } nd)
            {
                FlipRows(nd.rgba, nd.w, nd.h);
                return nd;
            }
            return DecodeStb(file);
        }

        public static (byte[] rgba, int w, int h) DecodeStb(string file)
        {
            var bytes = File.ReadAllBytes(file);
            var img = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            FlipRows(img.Data, img.Width, img.Height);
            return (img.Data, img.Width, img.Height);
        }

        static void FlipRows(byte[] d, int w, int h)
        {
            int stride = w * 4;
            var tmp = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * stride, b = (h - 1 - y) * stride;
                Buffer.BlockCopy(d, a, tmp, 0, stride);
                Buffer.BlockCopy(d, b, d, a, stride);
                Buffer.BlockCopy(tmp, 0, d, b, stride);
            }
        }

        public static GpuTexture Default(string name)
        {
            name ??= "white";
            if (defaults.TryGetValue(name, out var t)) return t;
            byte r = 255, g = 255, b = 255, a = 255;
            switch (name)
            {
                // Unity 的內建 black 是透明黑 (0,0,0,0)：Texture2D.blackTexture 文件這樣寫，空的 _LayerNTex 也靠它不蓋住底下（ShaderLab 屬性頁寫 alpha 1，跟實際不符）
                case "black": r = g = b = a = 0; break;
                case "gray": case "grey": r = g = b = 128; break;
                case "bump": r = 128; g = 128; b = 255; break;
                case "red": g = b = 0; break;
                case "clear": r = g = b = a = 0; break;
            }
            var px = new byte[] { r, g, b, a, r, g, b, a, r, g, b, a, r, g, b, a };
            t = new GpuTexture { Id = GL.Gen(GL.glGenTextures), Width = 2, Height = 2, Ready = true };
            GL.BindTexture(GL.TEXTURE_2D, t.Id);
            fixed (byte* p = px) GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, 2, 2, 0, GL.RGBA, GL.UNSIGNED_BYTE, p);
            Params(GL.TEXTURE_2D, FilterMode.Bilinear, TextureWrapMode.Repeat, TextureWrapMode.Repeat, false);
            defaults[name] = t;
            return t;
        }

        public static GpuTexture Of(Texture tex, string fallback = "white")
        {
            var n = Native.Of<NTexture>(tex);
            if (n == null) return Default(fallback);
            if (n.Gpu is GpuTexture g && g.Ready && !n.GpuDirty && g.UpdateCount == n.UpdateCount)
            {
                if (n.ParamsDirty) ApplyParams(n, g);
                return g;
            }
            if (traceUntil == 0) return Upload(n) ?? Default(fallback);
            int before = TexturesUploaded;
            var r = Upload(n);
            Trace(n, TexturesUploaded != before);
            return r ?? Default(fallback);
        }

        /// <summary>只重設 texture 參數（setter 改了 wrap／filter，像素沒變）。mipmap 照上次上傳的（壓縮資料只有一層時不能用 mipmap 的取樣）</summary>
        static void ApplyParams(NTexture n, GpuTexture g)
        {
            uint target = g.Array ? GL.TEXTURE_2D_ARRAY : GL.TEXTURE_2D;
            GL.BindTexture(target, g.Id);
            Params(target, n.Filter, n.WrapU, n.WrapV, !g.Array && g.Mips);
            n.ParamsDirty = false;
        }

        /// <summary>CPU 寫過、不超過這個大小的貼圖留上次上傳的內容來比（PPv2 的 Internal Curves Texture 是 128x2，每格 Apply 一樣的曲線）</summary>
        const int SnapshotMax = 256 * 1024;

        // ───────── 量測：ctl「tex」之後 N 格，每次上傳記下哪張、為什麼、誰叫的；setter 記誰設、值有沒有變 ─────────

        sealed class TexTrace
        {
            public int Uploads;
            public readonly Dictionary<string, int> Reasons = new Dictionary<string, int>();
            public string Stack;
            public readonly Dictionary<string, (int calls, int changed, string stack)> Sets = new Dictionary<string, (int, int, string)>();
        }
        static readonly Dictionary<NTexture, TexTrace> traced = new Dictionary<NTexture, TexTrace>();
        static int traceUntil;

        static string StackOf(int skip)
        {
            var lines = new System.Diagnostics.StackTrace(skip, false).ToString().Split('\n');
            return string.Join(" ← ", lines.Take(14).Select(l => l.Trim().Replace("at ", "")));
        }

        static TexTrace TraceOf(NTexture n) { if (!traced.TryGetValue(n, out var t)) traced[n] = t = new TexTrace(); return t; }

        /// <summary>Upload 之後記：這次真的上傳了（uploaded），或內容跟上次一樣略過</summary>
        static void Trace(NTexture n, bool uploaded)
        {
            if (n is NRenderTexture) return;
            var t = TraceOf(n);
            string why = uploaded ? "上傳" : "內容相同略過";
            if (uploaded) t.Uploads++;
            t.Reasons[why] = t.Reasons.TryGetValue(why, out var c) ? c + 1 : 1;
            t.Stack ??= StackOf(2);
        }

        public static void StartTrace(int frames)
        {
            traced.Clear();
            traceUntil = Engine.Time.FrameCount + frames;
            NTexture.TraceSet = (n, what, changed) =>
            {
                if (n is NRenderTexture) return;
                var t = TraceOf(n);
                t.Sets.TryGetValue(what, out var v);
                t.Sets[what] = (v.calls + 1, v.changed + (changed ? 1 : 0), v.stack ?? StackOf(3));
            };
        }

        /// <summary>每格叫：追蹤期滿回報告（上傳次數前 10 張，含 setter），沒滿回 null</summary>
        public static string PollTrace()
        {
            if (traceUntil == 0 || Engine.Time.FrameCount < traceUntil) return null;
            traceUntil = 0;
            NTexture.TraceSet = null;
            var sb = new System.Text.StringBuilder();
            sb.Append($"  [tex] 追蹤期間上傳的貼圖 {traced.Count(kv => kv.Value.Uploads > 0)} 張、共 {traced.Values.Sum(t => t.Uploads)} 次；setter 動過的 {traced.Count(kv => kv.Value.Sets.Count > 0)} 張");
            foreach (var (n, t) in traced.OrderByDescending(kv => kv.Value.Uploads).ThenByDescending(kv => kv.Value.Reasons.Values.Sum()).ThenByDescending(kv => kv.Value.Sets.Values.Sum(v => v.calls)).Take(10))
            {
                sb.Append($"\n  [tex] {n.Name ?? "(無名)"} {n.Width}x{n.Height} 檔={(n.File != null ? Path.GetFileName(n.File) : "-")} CpuWritten={n.CpuWritten} 上傳 {t.Uploads} 次（要傳的 {t.Reasons.Values.Sum()} 次：{string.Join("、", t.Reasons.Select(kv => kv.Key + " " + kv.Value))}）");
                foreach (var (what, v) in t.Sets) sb.Append($"\n  [tex]   setter {what}：{v.calls} 次，值有變 {v.changed} 次；堆疊 {v.stack}");
                if (t.Stack != null) sb.Append($"\n  [tex]   上傳的堆疊 {t.Stack}");
            }
            traced.Clear();
            return sb.ToString();
        }

        public sealed class RtTarget { public uint Fbo, Depth, Format; public int W, H; }

        /// <summary>RenderTexture 的顏色格式 → GL 的 internal format、format、type（浮點格式給 HDR：PPv2 的中繼與暫存 RT、LUT；其他一律 8 位元）</summary>
        public static (uint internalFormat, uint format, uint type) RtFormat(NRenderTexture n) => n.ColorFormat switch
        {
            UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat => (GL.RGBA16F, GL.RGBA, GL.HALF_FLOAT),
            UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat => (GL.RGBA32F, GL.RGBA, GL.FLOAT),
            UnityEngine.Experimental.Rendering.GraphicsFormat.B10G11R11_UFloatPack32 => (GL.R11F_G11F_B10F, GL.RGB, GL.HALF_FLOAT),
            _ => (n.SRGB ? GL.SRGB8_ALPHA8 : GL.RGBA8, GL.RGBA, GL.UNSIGNED_BYTE),
        };

        /// <summary>RenderTexture：貼圖＋framebuffer（深度與 stencil 一起），建好時清成透明</summary>
        public static GpuTexture UploadRT(NRenderTexture n)
        {
            var g = n.Gpu as GpuTexture ?? new GpuTexture();
            n.Gpu = g;
            int w = Math.Max(1, n.Width), h = Math.Max(1, n.Height);
            var (ifmt, fmt, type) = RtFormat(n);
            if (g.Id != 0 && g.Width == w && g.Height == h && n.Fbo is RtTarget rt0 && rt0.Format == ifmt) { n.GpuDirty = false; return g; }
            if (g.Id == 0) g.Id = GL.Gen(GL.glGenTextures);
            GL.BindTexture(GL.TEXTURE_2D, g.Id);
            GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)ifmt, w, h, 0, fmt, type, null);
            Params(GL.TEXTURE_2D, n.Filter, n.WrapU, n.WrapV, false);
            var t = n.Fbo as RtTarget ?? new RtTarget();
            if (t.Fbo == 0) t.Fbo = GL.Gen(GL.glGenFramebuffers);
            if (t.Depth == 0) t.Depth = GL.Gen(GL.glGenRenderbuffers);
            GL.glBindRenderbuffer(GL.RENDERBUFFER, t.Depth);
            GL.glRenderbufferStorage(GL.RENDERBUFFER, GL.DEPTH24_STENCIL8, w, h);
            int prev;
            GL.glGetIntegerv(GL.FRAMEBUFFER_BINDING, &prev);
            GL.glBindFramebuffer(GL.FRAMEBUFFER, t.Fbo);
            GL.glFramebufferTexture2D(GL.FRAMEBUFFER, GL.COLOR_ATTACHMENT0, GL.TEXTURE_2D, g.Id, 0);
            GL.glFramebufferRenderbuffer(GL.FRAMEBUFFER, GL.DEPTH_STENCIL_ATTACHMENT, GL.RENDERBUFFER, t.Depth);
            GL.glViewport(0, 0, w, h);
            GL.ColorMask(1, 1, 1, 1);
            GL.glClearColor(0, 0, 0, 0);
            GL.glClear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT | GL.STENCIL_BUFFER_BIT);
            GL.glBindFramebuffer(GL.FRAMEBUFFER, (uint)prev);
            t.W = w; t.H = h; t.Format = ifmt;
            n.Fbo = t;
            g.Width = w; g.Height = h; g.Ready = true;
            n.GpuDirty = false;
            return g;
        }

        static GpuTexture Upload(NTexture n)
        {
            if (n is NRenderTexture rt) return UploadRT(rt);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = n.Gpu as GpuTexture ?? new GpuTexture();
            n.Gpu = g;
            byte[] px = n.Pixels;
            int w = n.Width, h = n.Height;
            if (px == null && n.File != null)
            {
                if (n.Dimension == UnityEngine.Rendering.TextureDimension.Tex2DArray) return UploadArray(n, g);
                if (n.Compressed != 0) return UploadCompressed(n, g, sw);
                try { (px, w, h) = DecodeFile(n.File); }
                catch (Exception e) { Assets.Warn("貼圖讀不了 " + n.File + "：" + e.Message); return null; }
                n.Width = w; n.Height = h;
            }
            if (px == null) return null;
            // 內容、大小、格式都跟上次上傳的一樣：不重傳，只補取樣參數（不算一次上傳）
            if (g.Ready && g.Id != 0 && g.Snapshot != null && g.Width == w && g.Height == h && g.Srgb == n.SRGB && g.Mips == n.Mips && px.AsSpan().SequenceEqual(g.Snapshot))
            {
                if (n.ParamsDirty) ApplyParams(n, g);
                g.UpdateCount = n.UpdateCount;
                n.GpuDirty = false;
                return g;
            }
            if (g.Id == 0) g.Id = GL.Gen(GL.glGenTextures);
            GL.BindTexture(GL.TEXTURE_2D, g.Id);
            if (g.LevelLimit) { GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAX_LEVEL, 1000); g.LevelLimit = false; }
            GL.glPixelStorei(GL.UNPACK_ALIGNMENT, 1);
            fixed (byte* p = px) GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)(n.SRGB ? GL.SRGB8_ALPHA8 : GL.RGBA8), w, h, 0, GL.RGBA, GL.UNSIGNED_BYTE, p);
            if (n.Mips) GL.glGenerateMipmap(GL.TEXTURE_2D);
            Params(GL.TEXTURE_2D, n.Filter, n.WrapU, n.WrapV, n.Mips);
            g.Width = w; g.Height = h; g.Ready = true; g.UpdateCount = n.UpdateCount;
            g.Srgb = n.SRGB; g.Mips = n.Mips;
            if (n.CpuWritten && px.Length <= SnapshotMax)
            {
                if (g.Snapshot == null || g.Snapshot.Length != px.Length) g.Snapshot = new byte[px.Length];
                Buffer.BlockCopy(px, 0, g.Snapshot, 0, px.Length);
            }
            else g.Snapshot = null;
            n.GpuDirty = false;
            n.ParamsDirty = false;
            // 從檔案讀的不留像素（3 GB 的素材留在記憶體會爆）；腳本或引擎改過的（CpuWritten）才留，只讀的要用時再從檔案解（AssetIcalls.Px）
            if (!n.CpuWritten && n.File != null) n.Pixels = null;
            TexturesUploaded++;
            UploadMs += sw.ElapsedMilliseconds;
            return g;
        }

        // ───────── 保留原始壓縮資料的貼圖（docs/perf-battle.md「B 段」）─────────
        // 安裝時查過 GPU 支援才會存成 .mt2t；開機再查一次（GL_EXTENSIONS），不支援（換了驅動、診斷開關）就用 CPU 解成 RGBA，
        // 跟以前讀 PNG 一樣傳第 0 層再 glGenerateMipmap，解出來的像素跟以前的 PNG 相同（tools/texture-check 的 cpu 比對）。

        /// <summary>GPU 支援 S3TC（DXT1、DXT5，含 sRGB）、BPTC（BC6H、BC7）</summary>
        public static bool S3tc, Bptc;
        /// <summary>宿主給的 CPU 解碼（AssetRipper 的解碼器）：格式、寬、高、是不是 DXT5nm、從第 0 層開始的資料 → RGBA8，列順序同資料（由下而上）</summary>
        public static Func<int, int, int, bool, byte[], byte[]> CpuDecoder;
        /// <summary>診斷開關（驗收的截圖分組）：all 全部用 CPU 解；mips 只有帶 mip 的與 BC6H 用 CPU 解（就是以前的做法）</summary>
        public static string ForceCpu;
        public delegate void UploadTapFn(string source, int level, ReadOnlySpan<byte> data);
        /// <summary>診斷：每次 glCompressedTexImage2D 送出的資料（ctl texhash 記雜湊，跟原版比）</summary>
        public static UploadTapFn UploadTap;
        /// <summary>直接傳壓縮資料的張數、用 CPU 解的張數與毫秒</summary>
        public static int CompressedUploads, CpuDecoded;
        public static long CpuDecodeMs;
        static readonly HashSet<int> cpuNoted = new HashSet<int>();

        public static void DetectCompressed()
        {
            var ext = " " + GL.Str(GL.EXTENSIONS) + " ";
            S3tc = ext.Contains(" GL_EXT_texture_compression_s3tc ") && ext.Contains(" GL_EXT_texture_compression_s3tc_srgb ");
            Bptc = ext.Contains(" GL_EXT_texture_compression_bptc ") || ext.Contains(" GL_ARB_texture_compression_bptc ");
        }

        static bool GpuSupports(RawTexture t) => t.Format switch
        {
            RawTexture.DXT1 or RawTexture.DXT5 => S3tc,
            RawTexture.BC6H or RawTexture.BC7 => Bptc,
            _ => false,
        };

        static uint InternalFormat(int format, bool srgb) => format switch
        {
            RawTexture.DXT1 => srgb ? GL.COMPRESSED_SRGB_ALPHA_S3TC_DXT1 : GL.COMPRESSED_RGBA_S3TC_DXT1,
            RawTexture.DXT5 => srgb ? GL.COMPRESSED_SRGB_ALPHA_S3TC_DXT5 : GL.COMPRESSED_RGBA_S3TC_DXT5,
            RawTexture.BC7 => srgb ? GL.COMPRESSED_SRGB_ALPHA_BPTC_UNORM : GL.COMPRESSED_RGBA_BPTC_UNORM,
            // Unity 的 BC6H 是無號的（BC6H_UF16），沒有 sRGB
            RawTexture.BC6H => GL.COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT,
            _ => throw new NotSupportedException("壓縮格式 " + format),
        };

        static void Note(string msg) => Engine.Config?.LogSink?.Invoke(msg);

        /// <summary>第 0 層用 CPU 解成 RGBA8（列順序同資料，由下而上；同 DecodeFile 讀 PNG 之後的順序）</summary>
        static byte[] CpuDecode(NTexture n, RawTexture t, byte[] data, string why)
        {
            if (CpuDecoder == null) { Assets.Warn("沒有 CPU 解碼器，壓縮貼圖讀不了：" + n.File); return null; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var px = CpuDecoder(t.Format, t.Width, t.Height, t.Dxt5nm, data);
            CpuDecoded++;
            CpuDecodeMs += sw.ElapsedMilliseconds;
            if (cpuNoted.Add(t.Format)) Note($"壓縮貼圖用 CPU 解：{RawTexture.FormatName(t.Format)}（{why}；第一張 {n.Name} {t.Width}x{t.Height}，之後同格式的不再寫）");
            return px;
        }

        /// <summary>腳本要讀像素（AssetIcalls.Px）：壓縮貼圖用 CPU 解第 0 層</summary>
        public static byte[] DecodeCompressedPixels(NTexture n)
        {
            try
            {
                var t = RawTexture.Read(n.File, out var data);
                Note($"  Px() 讀壓縮貼圖 {n.Name}（{RawTexture.FormatName(t.Format)} {t.Width}x{t.Height}），用 CPU 解");
                return CpuDecode(n, t, data, "腳本讀像素");
            }
            catch (Exception e) { Assets.Warn("貼圖讀不了 " + n.File + "：" + e.Message); return null; }
        }

        static GpuTexture UploadCompressed(NTexture n, GpuTexture g, System.Diagnostics.Stopwatch sw)
        {
            RawTexture t;
            byte[] data;
            try { t = RawTexture.Read(n.File, out data); }
            catch (Exception e) { Assets.Warn("貼圖讀不了 " + n.File + "：" + e.Message); return null; }
            n.Width = t.Width; n.Height = t.Height;
            bool forced = ForceCpu == "all" || (ForceCpu == "mips" && (t.Mips > 1 || t.Format == RawTexture.BC6H));
            bool mips;
            if (g.Id == 0) g.Id = GL.Gen(GL.glGenTextures);
            if (GpuSupports(t) && !forced)
            {
                uint ifmt = InternalFormat(t.Format, n.SRGB);
                GL.BindTexture(GL.TEXTURE_2D, g.Id);
                fixed (byte* p = data)
                    for (int l = 0; l < t.Mips; l++)
                    {
                        var (off, len) = t.Level(l);
                        GL.glCompressedTexImage2D(GL.TEXTURE_2D, l, ifmt, RawTexture.LevelWidth(t.Width, l), RawTexture.LevelHeight(t.Height, l), 0, len, p + off);
                        UploadTap?.Invoke(t.Source, l, data.AsSpan(off, len));
                    }
                // 原版存幾層就用幾層（不用 glGenerateMipmap：壓縮格式不能產）
                GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAX_LEVEL, t.Mips - 1);
                g.LevelLimit = true;
                mips = t.Mips > 1;
                CompressedUploads++;
            }
            else
            {
                var px = CpuDecode(n, t, data, forced ? "診斷開關 " + ForceCpu : "GPU 不支援");
                if (px == null) return null;
                GL.BindTexture(GL.TEXTURE_2D, g.Id);
                if (g.LevelLimit) { GL.glTexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAX_LEVEL, 1000); g.LevelLimit = false; }
                GL.glPixelStorei(GL.UNPACK_ALIGNMENT, 1);
                fixed (byte* p = px) GL.glTexImage2D(GL.TEXTURE_2D, 0, (int)(n.SRGB ? GL.SRGB8_ALPHA8 : GL.RGBA8), t.Width, t.Height, 0, GL.RGBA, GL.UNSIGNED_BYTE, p);
                if (n.Mips) GL.glGenerateMipmap(GL.TEXTURE_2D);
                mips = n.Mips;
            }
            Params(GL.TEXTURE_2D, n.Filter, n.WrapU, n.WrapV, mips);
            g.Width = t.Width; g.Height = t.Height; g.Ready = true; g.UpdateCount = n.UpdateCount;
            g.Srgb = n.SRGB; g.Mips = mips; g.Snapshot = null;
            n.GpuDirty = false;
            n.ParamsDirty = false;
            TexturesUploaded++;
            UploadMs += sw.ElapsedMilliseconds;
            return g;
        }

        /// <summary>Texture2DArray：匯入器把各層上下疊成一張 PNG</summary>
        static GpuTexture UploadArray(NTexture n, GpuTexture g)
        {
            byte[] px; int w, h;
            try { (px, w, h) = DecodeFile(n.File); }
            catch (Exception e) { Assets.Warn("貼圖讀不了 " + n.File + "：" + e.Message); return null; }
            int layers = Math.Max(1, n.Depth), lh = h / layers;
            // DecodeFile 已經上下翻過：第 0 層在最下面那一段（原圖最上面的一段 = 第 0 層，翻過之後在底部）
            var arr = new byte[px.Length];
            for (int l = 0; l < layers; l++)
                Buffer.BlockCopy(px, (layers - 1 - l) * lh * w * 4, arr, l * lh * w * 4, lh * w * 4);
            if (g.Id == 0) g.Id = GL.Gen(GL.glGenTextures);
            GL.BindTexture(GL.TEXTURE_2D_ARRAY, g.Id);
            fixed (byte* p = arr) GL.glTexImage3D(GL.TEXTURE_2D_ARRAY, 0, (int)(n.SRGB ? GL.SRGB8_ALPHA8 : GL.RGBA8), w, lh, layers, 0, GL.RGBA, GL.UNSIGNED_BYTE, p);
            Params(GL.TEXTURE_2D_ARRAY, n.Filter, n.WrapU, n.WrapV, false);
            g.Width = w; g.Height = lh; g.Array = true; g.Ready = true;
            n.GpuDirty = false;
            n.ParamsDirty = false;
            return g;
        }

        public static void Params(uint target, FilterMode f, TextureWrapMode u, TextureWrapMode v, bool mips)
        {
            int mag = f == FilterMode.Point ? (int)GL.NEAREST : (int)GL.LINEAR;
            int min = f == FilterMode.Point ? (int)(mips ? GL.NEAREST_MIPMAP_NEAREST : GL.NEAREST) : (int)(mips ? (f == FilterMode.Trilinear ? GL.LINEAR_MIPMAP_LINEAR : GL.LINEAR_MIPMAP_NEAREST) : GL.LINEAR);
            GL.glTexParameteri(target, GL.TEXTURE_MAG_FILTER, mag);
            GL.glTexParameteri(target, GL.TEXTURE_MIN_FILTER, min);
            GL.glTexParameteri(target, GL.TEXTURE_WRAP_S, Wrap(u));
            GL.glTexParameteri(target, GL.TEXTURE_WRAP_T, Wrap(v));
        }

        static int Wrap(TextureWrapMode w) => w switch
        {
            TextureWrapMode.Repeat => (int)GL.REPEAT,
            TextureWrapMode.Mirror => (int)GL.MIRRORED_REPEAT,
            TextureWrapMode.MirrorOnce => (int)GL.MIRRORED_REPEAT,
            _ => (int)GL.CLAMP_TO_EDGE,
        };
    }

    /// <summary>素材網格的 GPU 緩衝：每個通道一個 VBO，索引一個 IBO（子網格是 IBO 裡的區段）</summary>
    public sealed unsafe class GpuMesh
    {
        public readonly uint[] Vbo = new uint[14];
        public readonly int[] Dim = new int[14];
        public uint Ibo;
        public readonly List<(int start, int count, uint topology)> Subs = new List<(int, int, uint)>();
        public int Version = -1;

        public void Delete()
        {
            for (int i = 0; i < Vbo.Length; i++) if (Vbo[i] != 0) { uint id = Vbo[i]; GL.glDeleteBuffers(1, &id); Vbo[i] = 0; }
            if (Ibo != 0) { uint id = Ibo; GL.glDeleteBuffers(1, &id); Ibo = 0; }
        }

        /// <summary>粒子的頂點直接從 Builder 上傳（以前先複製成 NMesh 再走 Of）：通道與長度跟 Builder.Into 產的 NMesh 一樣
        ///（位置 3、法線 3、顏色 4、TEXCOORD0… 各 4，前 Vtx 個頂點），索引 int 跟 uint 的位元組相同，直接傳</summary>
        public void UploadBuilt(ParticleGeometry.Builder b)
        {
            Version = -2;
            for (int i = 0; i < Dim.Length; i++) Dim[i] = 0;
            Channel((int)UnityEngine.Rendering.VertexAttribute.Position, b.Pos, b.Vtx, 3);
            Channel((int)UnityEngine.Rendering.VertexAttribute.Normal, b.Nrm, b.Vtx, 3);
            Channel((int)UnityEngine.Rendering.VertexAttribute.Color, b.Col, b.Vtx, 4);
            for (int k = 0; k < b.TcCount && k < 8; k++) Channel((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + k, b.Tc[k], b.Vtx, 4);
            Subs.Clear();
            Subs.Add((0, b.Idx, GL.TRIANGLES));
            if (Ibo == 0) Ibo = GL.Gen(GL.glGenBuffers);
            GL.glBindBuffer(GL.ELEMENT_ARRAY_BUFFER, Ibo);
            fixed (int* p = b.Ind) GL.glBufferData(GL.ELEMENT_ARRAY_BUFFER, Math.Max(4, b.Idx * 4), b.Idx > 0 ? p : null, GL.STATIC_DRAW);
        }

        void Channel(int i, float[] data, int vertices, int dim)
        {
            if (vertices == 0) return;
            if (Vbo[i] == 0) Vbo[i] = GL.Gen(GL.glGenBuffers);
            GL.glBindBuffer(GL.ARRAY_BUFFER, Vbo[i]);
            fixed (float* p = data) GL.glBufferData(GL.ARRAY_BUFFER, vertices * dim * 4, p, GL.STATIC_DRAW);
            Dim[i] = dim;
        }

        public static GpuMesh Of(NMesh m)
        {
            var g = m.Gpu as GpuMesh;
            if (g == null) { g = new GpuMesh(); m.Gpu = g; }
            if (g.Version == m.Version) return g;
            g.Version = m.Version;
            for (int i = 0; i < m.Channels.Length; i++)
            {
                var c = m.Channels[i];
                if (c == null || c.Data.Length == 0) { g.Dim[i] = 0; continue; }
                if (g.Vbo[i] == 0) g.Vbo[i] = GL.Gen(GL.glGenBuffers);
                GL.glBindBuffer(GL.ARRAY_BUFFER, g.Vbo[i]);
                fixed (float* p = c.Data) GL.glBufferData(GL.ARRAY_BUFFER, c.Data.Length * 4, p, GL.STATIC_DRAW);
                g.Dim[i] = c.Dim;
            }
            int total = 0;
            foreach (var s in m.Subs) total += s.Indices.Length;
            // 索引加上 BaseVertex 轉成 uint：暫存重用（Spine 這類每格改的網格每格都會重傳）
            if (indexScratch.Length < total) indexScratch = new uint[Math.Max(total, indexScratch.Length * 2)];
            var all = indexScratch;
            g.Subs.Clear();
            int o = 0;
            foreach (var s in m.Subs)
            {
                g.Subs.Add((o, s.Indices.Length, s.Topology == MeshTopology.Lines ? GL.LINES : s.Topology == MeshTopology.LineStrip ? GL.LINE_STRIP : s.Topology == MeshTopology.Points ? GL.POINTS : GL.TRIANGLES));
                for (int i = 0; i < s.Indices.Length; i++) all[o++] = (uint)(s.Indices[i] + s.BaseVertex);
            }
            if (g.Ibo == 0) g.Ibo = GL.Gen(GL.glGenBuffers);
            GL.glBindBuffer(GL.ELEMENT_ARRAY_BUFFER, g.Ibo);
            fixed (uint* p = all) GL.glBufferData(GL.ELEMENT_ARRAY_BUFFER, Math.Max(4, total * 4), total > 0 ? p : null, GL.STATIC_DRAW);
            return g;
        }

        static uint[] indexScratch = System.Array.Empty<uint>();
    }
}
