using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using Mt2Mobile.Importer;
using Mt2Mobile.Shaders;

namespace Mt2Mobile.Phase3;

/// <summary>
/// 手機上跑安裝管線的四步：patch（unity-native 的 patcher）、export（AssetRipper）、import（C# importer 的 all）、shaders。
/// am start-foreground-service --es mode patch|export|import|shaders|pipeline 啟動，不叫 Activity。pipeline 在同一個程序裡依序跑四步。
/// 匯出可以不只一份（Diceomancer 要兩份，見 LoadExports）：每份是一步，步驟名是輸出資料夾名；mode export 跑全部，mode 給資料夾名只跑那一份。
/// 輸入與輸出都在 app 的 files/ 底下（見 Paths）；log 寫 files/tmp/runner.log，結束寫 files/tmp/complete.txt（都沒有 BOM）。
/// 每步開始前把步驟名寫進 files/tmp/step.txt、等 2 秒：手機上 root 的取樣迴圈（run-phone.sh）看到換步驟就寫 /proc/&lt;pid&gt;/clear_refs
/// 把 VmHWM 歸零（app 自己寫會被拒絕），所以每步結束時讀到的 VmHWM 是這一步的峰值。
/// </summary>
[Service(Name = "org.mt2mobile.phase3.RunnerService", Exported = true)]
public sealed class RunnerService : Service
{
    private const string ChannelId = "phase3-runner";
    private const int NotificationId = 3;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly object LogGate = new();
    private static StreamWriter? _log;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Phase 3 runner", NotificationImportance.Low));
        StartForeground(NotificationId, new Notification.Builder(this, ChannelId)
            .SetContentTitle("MT2 Phase 3 test")
            .SetContentText("Background task is running")
            .SetSmallIcon(Android.Resource.Drawable.IcMenuInfoDetails)
            .SetOngoing(true)
            .Build());
        var mode = intent?.GetStringExtra("mode") ?? "pipeline";
        _ = Task.Run(() => Run(mode, startId));
        return StartCommandResult.NotSticky;
    }

    private void Run(string mode, int startId)
    {
        var p = new Paths(FilesDir!.AbsolutePath);
        Directory.CreateDirectory(p.Tmp);
        var total = Stopwatch.StartNew();
        int exitCode = 0;
        try
        {
            _log = new StreamWriter(new FileStream(p.Log, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8) { AutoFlush = true };
            // AssetRipper 的 ExecutingDirectory 用 AppContext.BaseDirectory（Android 上是空字串），一定要在碰到它的型別之前設
            Directory.CreateDirectory(p.AssetRipperBase);
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", p.AssetRipperBase + "/");
            Log($"mode={mode} pid={Android.OS.Process.MyPid()} uid={Android.OS.Process.MyUid()} cpus={System.Environment.ProcessorCount}");
            Log($"selinux={ReadText("/proc/self/attr/current")}");
            Log($"app_context_base={AppContext.BaseDirectory} temp={Path.GetTempPath()}");
            var exports = LoadExports(p);
            var steps = mode switch
            {
                "pipeline" => new[] { "patch" }.Concat(exports.Select(e => e.Dir)).Concat(new[] { "import", "shaders" }).ToArray(),
                "export" => exports.Select(e => e.Dir).ToArray(),
                _ => new[] { mode },
            };
            foreach (var step in steps)
            {
                var export = exports.FirstOrDefault(e => e.Dir == step);
                Action action = step switch
                {
                    "patch" => () => RunPatcher(p),
                    _ when export is not null => () => RunAssetRipper(p, export),
                    "import" => () => RunImporter(p),
                    "shaders" => () => RunShaders(p),
                    _ => throw new ArgumentException($"Unknown mode: {step}"),
                };
                RunStep(p, step, action);
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Log("[exception] " + (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex));
        }
        finally
        {
            total.Stop();
            Log($"done exit={exitCode} elapsed_s={total.Elapsed.TotalSeconds:F2}");
            lock (LogGate) { _log?.Dispose(); _log = null; }
            File.WriteAllText(p.Complete, $"exit={exitCode} elapsed_s={total.Elapsed.TotalSeconds:F2}\n", Utf8);
            StopForeground(StopForegroundFlags.Remove);
            StopSelf(startId);
        }
    }

    private void RunStep(Paths p, string step, Action action)
    {
        File.WriteAllText(p.Step, step + "\n", Utf8);
        Thread.Sleep(2000);
        Log($"step={step} start {Now()} {Mem()} df_avail_kB={DfAvailKb(p.Files)}");
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        Log($"step={step} end {Now()} elapsed_s={sw.Elapsed.TotalSeconds:F2} {Mem()} df_avail_kB={DfAvailKb(p.Files)}");
    }

    private void RunPatcher(Paths p)
    {
        CopyAsset("phase3/input/UnityNative.dll", Path.Combine(p.Input, "UnityNative.dll"));
        // 1-engine.txt（引擎的轉接組件）、2-game.txt（遊戲自己的），照檔名順序傳給 patcher，同 unity-native/build.sh
        var redirects = (Assets!.List("phase3/redirects") ?? Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray();
        foreach (var r in redirects) CopyAsset($"phase3/redirects/{r}", Path.Combine(p.Input, r));
        CopyAssetTree("phase3/patcher", p.Patcher);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(p.Patcher, name.Name + ".dll");
            return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
        };
        if (Directory.Exists(p.PatchOut)) Directory.Delete(p.PatchOut, true);
        Directory.CreateDirectory(p.PatchOut);
        var args = new[] { p.Managed, p.PatchOut, "--hand", Path.Combine(p.Input, "UnityNative.dll") }
            .Concat(redirects.Select(r => Path.Combine(p.Input, r))).ToArray();
        Log("patcher " + string.Join(" ", args));
        var entryPoint = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(p.Patcher, "patcher.dll")).EntryPoint
            ?? throw new InvalidOperationException("patcher assembly has no entry point");
        // patcher 的進度印在 Console：導進 log
        var previous = Console.Out;
        Console.SetOut(new LogWriter());
        try { entryPoint.Invoke(null, new object?[] { args }); }
        finally { Console.SetOut(previous); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunAssetRipper(Paths p, ExportSpec spec)
    {
        Logger.Clear();
        Logger.Add(new AssetRipperLog());
        Logger.LogSystemInformation("AssetRipper (Phase 3 runner)");
        FullConfiguration settings = new();
        settings.ExportSettings.ScriptExportMode = spec.ScriptExportMode;
        settings.ExportSettings.ShaderExportMode = spec.ShaderExportMode;
        if (spec.EnableStaticMeshSeparation is { } separation) settings.ProcessingSettings.EnableStaticMeshSeparation = separation;
        var output = Path.Combine(p.Files, spec.Dir);
        Log($"Script Export Mode: {settings.ExportSettings.ScriptExportMode}");
        Log($"Shader Export Mode: {settings.ExportSettings.ShaderExportMode}");
        Log($"EnableStaticMeshSeparation: {settings.ProcessingSettings.EnableStaticMeshSeparation}");
        Log($"Output: {output}");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        new ExportHandler(settings).LoadProcessAndExport(new[] { p.Game }, output, LocalFileSystem.Instance);
        Log("Export completed successfully!");
    }

    /// <summary>
    /// 要跑的 AssetRipper 匯出：files/config/exports.json（push-phone.sh 的 EXPORTS）。陣列，每項
    /// {"dir": files/ 底下的輸出資料夾（也是步驟名）, "ScriptExportMode", "ShaderExportMode", "EnableStaticMeshSeparation"（選填）}。
    /// 沒有這個檔就是 MT2 的一份：同 AssetRipper.CLI 的 --decompile-game-only --shader-yaml，輸出 files/export。
    /// </summary>
    private static ExportSpec[] LoadExports(Paths p)
    {
        if (!File.Exists(p.Exports))
            return new[] { new ExportSpec("export", ScriptExportMode.DecompiledGameOnly, ShaderExportMode.Yaml, null) };
        using var doc = JsonDocument.Parse(File.ReadAllText(p.Exports));
        return doc.RootElement.EnumerateArray().Select(e => new ExportSpec(
            e.GetProperty("dir").GetString()!,
            Enum.Parse<ScriptExportMode>(e.GetProperty("ScriptExportMode").GetString()!),
            Enum.Parse<ShaderExportMode>(e.GetProperty("ShaderExportMode").GetString()!),
            e.TryGetProperty("EnableStaticMeshSeparation", out var s) ? s.GetBoolean() : null)).ToArray();
    }

    private sealed record ExportSpec(string Dir, ScriptExportMode ScriptExportMode, ShaderExportMode ShaderExportMode, bool? EnableStaticMeshSeparation);

    private static void RunImporter(Paths p) => new ImportRunner(p.Config) { Log = Log }.Run("all");

    private static void RunShaders(Paths p) => new ShaderRunner(p.Config) { Log = Log }.Run();

    internal static void Log(string s)
    {
        lock (LogGate) { _log?.WriteLine(s); }
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    private static string Mem()
    {
        try
        {
            return string.Join(" ", File.ReadLines("/proc/self/status")
                .Where(l => l.StartsWith("VmHWM:") || l.StartsWith("VmRSS:"))
                .Select(l => l.Split(':')[0] + "_kB=" + l.Split(':')[1].Trim().Split(' ')[0]));
        }
        catch (Exception e) { return "mem=unavailable(" + e.Message + ")"; }
    }

    private static long DfAvailKb(string path) => new StatFs(path).AvailableBytes / 1024;

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) { return $"unavailable ({ex.Message})"; }
    }

    private void CopyAsset(string assetPath, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = Assets!.Open(assetPath);
        using var target = File.Create(destination);
        source.CopyTo(target);
    }

    private void CopyAssetTree(string assetDirectory, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Assets!.List(assetDirectory) ?? Array.Empty<string>())
        {
            var assetPath = $"{assetDirectory}/{entry}";
            var target = Path.Combine(destination, entry);
            if ((Assets.List(assetPath) ?? Array.Empty<string>()).Length > 0)
                CopyAssetTree(assetPath, target);
            else
                CopyAsset(assetPath, target);
        }
    }

    /// <summary>app 的 files/ 底下的位置（push-phone.sh、run-phone.sh、make-config.py 用同一套）</summary>
    private sealed record Paths(string Files)
    {
        public string Tmp => Path.Combine(Files, "tmp");
        public string Log => Path.Combine(Tmp, "runner.log");
        public string Complete => Path.Combine(Tmp, "complete.txt");
        public string Step => Path.Combine(Tmp, "step.txt");
        public string Game => Path.Combine(Files, "game");
        /// <summary>原版的 &lt;名稱&gt;_Data/Managed（MT2 是 MonsterTrain2_Data，Diceomancer 是 Diceomancer_Data），要剛好一個</summary>
        public string Managed => Directory.GetDirectories(Game, "*_Data").Select(d => Path.Combine(d, "Managed")).Where(Directory.Exists).Single();
        public string Input => Path.Combine(Files, "input");
        public string Patcher => Path.Combine(Files, "patcher");
        public string PatchOut => Path.Combine(Files, "patch", "managed");
        public string AssetRipperBase => Path.Combine(Files, "assetripper");
        public string Config => Path.Combine(Files, "config", "import.json");
        public string Exports => Path.Combine(Files, "config", "exports.json");
    }

    /// <summary>AssetRipper 的 log：不用 ConsoleLogger（它設終端機顏色，Android 上不支援），Debug、Verbose 不寫</summary>
    private sealed class AssetRipperLog : ILogger
    {
        public void Log(LogType type, LogCategory category, string message)
        {
            if (type is LogType.Debug or LogType.Verbose) return;
            RunnerService.Log(category == LogCategory.None ? $"{type}: {message}" : $"{category} {type}: {message}");
        }

        public void BlankLine(int numLines) { }
    }

    /// <summary>把 Console 的輸出一行一行寫進 runner.log</summary>
    private sealed class LogWriter : TextWriter
    {
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Utf8;

        public override void Write(char value)
        {
            if (value == '\n') { RunnerService.Log(_line.ToString().TrimEnd('\r')); _line.Clear(); }
            else _line.Append(value);
        }
    }
}
