using System.IO.Compression;
using System.Security.Cryptography;
using Android.Content;
using Android.Content.Res;

namespace Mt2Mobile.Launcher.Install;

/// <summary>
/// APK 的 assets 解到 files/runtime/&lt;APK 的 lastUpdateTime&gt;/：targets、patcher、只有手寫部分的 UnityNative.dll、redirects、
/// patch 的期望值、轉接組件（Glue.*.dll，遊戲用）。安裝服務與遊戲都會叫，APK 沒換就不重解；換了就解新的、刪掉舊的。
/// </summary>
public sealed class Runtime
{
    public string Dir { get; }
    public string Targets => Path.Combine(Dir, "targets");
    public string Patcher => Path.Combine(Dir, "runtime", "patcher");
    public string UnityNative => Path.Combine(Dir, "runtime", "UnityNative.dll");
    public string Redirects => Path.Combine(Dir, "runtime", "redirects");
    public string PatchExpect => Path.Combine(Dir, "runtime", "patch-expect.json");
    public string Glue => Path.Combine(Dir, "runtime", "glue");
    /// <summary>已安裝的 APK（ApplicationInfo.SourceDir）</summary>
    public string Apk { get; }

    Runtime(string dir, string apk) { Dir = dir; Apk = apk; }

    public static Runtime Extract(Context ctx, Paths p, Action<string> log)
    {
        var stamp = ctx.PackageManager!.GetPackageInfo(ctx.PackageName!, 0)!.LastUpdateTime.ToString();
        var dir = Path.Combine(p.Runtime, stamp);
        var done = Path.Combine(dir, ".complete");
        if (!File.Exists(done))
        {
            Util.DeleteDir(dir);
            CopyTree(ctx.Assets!, "targets", Path.Combine(dir, "targets"));
            CopyTree(ctx.Assets!, "runtime", Path.Combine(dir, "runtime"));
            File.WriteAllText(done, "");
            log($"runtime：從 APK 解到 {dir}");
        }
        foreach (var old in Directory.GetDirectories(p.Runtime).Where(d => d != dir)) Util.DeleteDir(old);
        return new Runtime(dir, ctx.ApplicationInfo!.SourceDir!);
    }

    /// <summary>
    /// APK 裡 lib/&lt;abi&gt;/&lt;name&gt; 的 SHA256（指紋用）。算的是已安裝的 APK 裡那份：系統不管是直接從 APK 載入還是解到 nativeLibraryDir，內容都是它。
    /// APK 只有 arm64-v8a；萬一有多個 ABI，取 Build.SupportedAbis 裡第一個有這個檔的
    /// </summary>
    public string NativeHash(string name)
    {
        using var zip = ZipFile.OpenRead(Apk);
        var entry = Android.OS.Build.SupportedAbis!.Select(abi => zip.GetEntry($"lib/{abi}/{name}")).FirstOrDefault(e => e is not null)
            ?? throw new FileNotFoundException($"APK 裡沒有 lib/<abi>/{name}");
        using var s = entry.Open();
        return Util.Hex(SHA256.HashData(s));
    }

    /// <summary>redirects 照檔名順序（引擎的在前、遊戲的在後，同 unity-native/build.sh）</summary>
    public string[] RedirectFiles() => Directory.GetFiles(Redirects, "*.txt").Order(StringComparer.Ordinal).ToArray();

    static void CopyTree(AssetManager assets, string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in assets.List(from) ?? [])
        {
            var src = $"{from}/{entry}";
            var dst = Path.Combine(to, entry);
            if ((assets.List(src) ?? []).Length > 0) { CopyTree(assets, src, dst); continue; }
            using var input = assets.Open(src);
            using var output = File.Create(dst);
            input.CopyTo(output);
        }
    }
}
