using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Mt2Mobile.Phase2;

[Service(Name = "org.mt2mobile.phase2.RunnerService", Exported = true)]
public sealed class RunnerService : Service
{
    private const string ChannelId = "phase2-runner";
    private const int NotificationId = 2;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("MT2 Phase 2 test")
            .SetContentText("Background task is running")
            .SetSmallIcon(Android.Resource.Drawable.IcMenuInfoDetails)
            .SetOngoing(true)
            .Build();
        StartForeground(NotificationId, notification);

        var mode = intent?.GetStringExtra("mode") ?? "patch";
        _ = Task.Run(() => Run(mode, intent, startId));
        return StartCommandResult.NotSticky;
    }

    private void Run(string mode, Intent? intent, int startId)
    {
        var files = FilesDir!.AbsolutePath;
        var temp = Path.Combine(files, "tmp");
        Directory.CreateDirectory(temp);
        var logPath = Path.Combine(temp, "runner.log");
        var resultPath = Path.Combine(temp, "complete.txt");
        var stopwatch = Stopwatch.StartNew();
        var exitCode = 0;
        var previousOut = Console.Out;
        var previousError = Console.Error;
        StreamWriter? writer = null;

        try
        {
            writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
            Console.SetOut(writer);
            Console.SetError(writer);
            Console.WriteLine($"mode={mode}");
            Console.WriteLine($"pid={Android.OS.Process.MyPid()}");
            Console.WriteLine($"uid={Android.OS.Process.MyUid()}");
            Console.WriteLine($"selinux={ReadText("/proc/self/attr/current")}");

            if (mode == "patch")
                RunPatcher(files);
            else if (mode == "export")
                RunAssetRipper(files, intent);
            else
                throw new ArgumentException($"Unknown mode: {mode}");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            try
            {
                Console.Error.WriteLine(ex);
                if (ex is TargetInvocationException { InnerException: not null } tie)
                    Console.Error.WriteLine(tie.InnerException);
            }
            catch (ObjectDisposedException) { }
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            writer?.Dispose();
            stopwatch.Stop();
            File.WriteAllText(resultPath,
                $"exit={exitCode} elapsed_s={stopwatch.Elapsed.TotalSeconds:F2}{System.Environment.NewLine}",
                Encoding.UTF8);
            StopForeground(StopForegroundFlags.Remove);
            StopSelf(startId);
        }
    }

    private void RunPatcher(string files)
    {
        CopyAsset("phase2/input/UnityNative.dll", Path.Combine(files, "input", "UnityNative.dll"));
        CopyAsset("phase2/redirects/Facepunch.txt", Path.Combine(files, "redirects", "Facepunch.txt"));
        CopyAsset("phase2/redirects/Game.txt", Path.Combine(files, "redirects", "Game.txt"));
        var patcherDirectory = Path.Combine(files, "patcher");
        CopyAssetTree("phase2/patcher", patcherDirectory);
        RegisterAssemblyDirectory(patcherDirectory);

        var managed = Path.Combine(files, "managed");
        var output = Path.Combine(files, "output", "managed");
        Directory.CreateDirectory(output);
        var args = new[]
        {
            managed,
            output,
            "--hand", Path.Combine(files, "input", "UnityNative.dll"),
            Path.Combine(files, "redirects", "Facepunch.txt"),
            Path.Combine(files, "redirects", "Game.txt")
        };

        var patcherPath = Path.Combine(patcherDirectory, "patcher.dll");
        var entryPoint = AssemblyLoadContext.Default.LoadFromAssemblyPath(patcherPath).EntryPoint
            ?? throw new InvalidOperationException("patcher assembly has no entry point");
        Console.WriteLine($"patcher_entry={entryPoint}");
        var stopwatch = Stopwatch.StartNew();
        entryPoint.Invoke(null, new object?[] { args });
        stopwatch.Stop();
        Console.WriteLine($"patcher_elapsed_s={stopwatch.Elapsed.TotalSeconds:F2}");
    }

    private void RunAssetRipper(string files, Intent? intent)
    {
        var assetRipper = Path.Combine(files, "assetripper");
        CopyAssetTree("phase2/assetripper", assetRipper);
        var assetRipperBase = Path.GetFullPath(assetRipper) + Path.DirectorySeparatorChar;
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", assetRipperBase);
        System.Environment.CurrentDirectory = assetRipperBase;
        Console.WriteLine($"assetripper_base={assetRipperBase}");
        Console.WriteLine($"app_context_base={AppContext.BaseDirectory}");
        Console.WriteLine($"temp_path={Path.GetTempPath()}");
        Console.WriteLine($"assetripper_local_temp={Path.Combine(assetRipperBase, "temp")}");
        var input = intent?.GetStringExtra("input")
            ?? throw new ArgumentException("export mode requires --es input <game path>");
        var output = intent?.GetStringExtra("output")
            ?? throw new ArgumentException("export mode requires --es output <export path>");
        var noDecompile = intent?.GetBooleanExtra("no_decompile", false) ?? false;

        RegisterAssemblyDirectory(assetRipper);
        var importAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(assetRipper, "AssetRipper.Import.dll"));
        var exportAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(assetRipper, "AssetRipper.Export.dll"));
        var fileSystemAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(assetRipper, "AssetRipper.IO.Files.dll"));
        var unityProjectsAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(assetRipper, "AssetRipper.Export.UnityProjects.dll"));

        var loggerType = importAssembly.GetType("AssetRipper.Import.Logging.Logger", throwOnError: true)!;
        loggerType.GetMethod("Clear")!.Invoke(null, null);
        var loggerInterface = importAssembly.GetType("AssetRipper.Import.Logging.ILogger", throwOnError: true)!;
        var logger = DispatchProxy.Create(loggerInterface, typeof(AssetRipperLoggerProxy));
        loggerType.GetMethod("Add")!.Invoke(null, new[] { logger });
        loggerType.GetMethod("LogSystemInformation")!.Invoke(null, new object?[] { "AssetRipper CLI" });

        var configurationType = exportAssembly.GetType("AssetRipper.Export.Configuration.FullConfiguration", throwOnError: true)!;
        var configuration = Activator.CreateInstance(configurationType)
            ?? throw new InvalidOperationException("Could not create AssetRipper configuration");
        var exportSettings = configurationType.GetProperty("ExportSettings")!.GetValue(configuration)!;
        var scriptModeType = exportAssembly.GetType("AssetRipper.Export.Configuration.ScriptExportMode", throwOnError: true)!;
        var shaderModeType = exportAssembly.GetType("AssetRipper.Export.Configuration.ShaderExportMode", throwOnError: true)!;
        exportSettings.GetType().GetProperty("ScriptExportMode")!.SetValue(
            exportSettings,
            Enum.Parse(scriptModeType, noDecompile ? "DllExportWithoutRenaming" : "DecompiledGameOnly"));
        exportSettings.GetType().GetProperty("ShaderExportMode")!.SetValue(
            exportSettings,
            Enum.Parse(shaderModeType, "Yaml"));
        Console.WriteLine($"Script Export Mode: {exportSettings.GetType().GetProperty("ScriptExportMode")!.GetValue(exportSettings)}");
        Console.WriteLine($"Shader Export Mode: {exportSettings.GetType().GetProperty("ShaderExportMode")!.GetValue(exportSettings)}");

        var exportHandlerType = unityProjectsAssembly.GetType("AssetRipper.Export.UnityProjects.ExportHandler", throwOnError: true)!;
        var handler = Activator.CreateInstance(exportHandlerType, configuration)
            ?? throw new InvalidOperationException("Could not create AssetRipper export handler");
        var fileSystemType = fileSystemAssembly.GetType("AssetRipper.IO.Files.FileSystem", throwOnError: true)!;
        var localFileSystemType = fileSystemAssembly.GetType("AssetRipper.IO.Files.LocalFileSystem", throwOnError: true)!;
        var localFileSystem = localFileSystemType.GetProperty("Instance")!.GetValue(null);
        var exportMethod = exportHandlerType.GetMethod("LoadProcessAndExport", new[]
        {
            typeof(IReadOnlyList<string>), typeof(string), fileSystemType
        }) ?? throw new InvalidOperationException("AssetRipper export method not found");
        var arguments = new object?[] { new[] { input }, output, localFileSystem };
        Console.WriteLine($"assetripper_handler={exportMethod}");
        var stopwatch = Stopwatch.StartNew();
        exportMethod.Invoke(handler, arguments);
        stopwatch.Stop();
        Console.WriteLine($"assetripper_elapsed_s={stopwatch.Elapsed.TotalSeconds:F2}");
        Console.WriteLine("Export completed successfully!");
    }

    private static void RegisterAssemblyDirectory(string directory)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(dependency)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency)
                : null;
        };
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
        var entries = Assets!.List(assetDirectory) ?? Array.Empty<string>();
        Directory.CreateDirectory(destination);
        foreach (var entry in entries)
        {
            var assetPath = $"{assetDirectory}/{entry}";
            var target = Path.Combine(destination, entry);
            var children = Assets.List(assetPath) ?? Array.Empty<string>();
            if (children.Length > 0)
                CopyAssetTree(assetPath, target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = Assets.Open(assetPath);
                using var output = File.Create(target);
                source.CopyTo(output);
            }
        }
    }

    private static void CreateChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;
        var manager = (NotificationManager?)Android.App.Application.Context
            .GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId, "Phase 2 background runner", NotificationImportance.Low));
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) { return $"unavailable ({ex.Message})"; }
    }
}

public class AssetRipperLoggerProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "BlankLine" && args?.Length == 1)
        {
            for (var i = 0; i < (int)args[0]!; i++)
                Console.WriteLine();
            return null;
        }

        if (targetMethod?.Name == "Log" && args?.Length == 3)
        {
            var type = args[0]?.ToString() ?? "Info";
            if (type is "Debug" or "Verbose")
                return null;
            var category = args[1]?.ToString();
            var message = args[2]?.ToString() ?? string.Empty;
            Console.WriteLine(category is null or "None" ? $"{type}: {message}" : $"{category} {type}: {message}");
        }
        return null;
    }
}
