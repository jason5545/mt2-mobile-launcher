using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using Mt2Mobile.Importer;
using Mt2Mobile.Shaders;
using Mt2Mobile.TextureExport;

namespace Mt2Mobile.Launcher.Install;

public sealed class InstallOptions
{
    /// <summary>有來源就從複製原版開始；沒有就續跑上一次的安裝</summary>
    public IGameSource? Source { get; init; }
    /// <summary>做完不刪 export、shaderwork（驗收要拉回來比）</summary>
    public bool KeepWork { get; init; }
    /// <summary>假裝的剩餘空間（kB），驗收第 3 題用</summary>
    public long? FakeAvailKb { get; init; }
    /// <summary>來源 copy 與 target 驗證成功後清掉可重建的下載暫存，以免它佔住 export 空間。</summary>
    public Action? CleanupSourceAfterCopy { get; init; }
    /// <summary>遊戲頁的「重新轉換」：指紋都對得上也從匯出重跑（匯出、importer、shaders）</summary>
    public bool ForceConvert { get; init; }
}

/// <summary>
/// 安裝流程（docs/phase4.md「安裝流程、進度、續跑」）：複製原版 → 比對 target → patch → 匯出 → importer → shaders → 收尾。
/// 每一步都是可以從頭重跑的單位：開始前先刪自己的輸出，做完才在 install.json 記完成與指紋。
/// 指紋是這一步的程式版本加輸入（匯出以後的步驟還帶前一步的指紋），對不上就重跑。
/// 匯出每次的 GUID 都不同，所以匯出重跑時 importer、shaders 一定跟著重跑；patch 跟它們互不依賴。
/// </summary>
public sealed class Installer(Paths p, Runtime rt, InstallLog log, IStepSink state, InstallOptions opt, Func<long> availableKb)
{
    public static readonly string[] Steps = ["copy", "patch", "export", "import", "shaders"];
    public static readonly string[] Work = ["patch", "export", "import", "shaders"];

    /// <summary>跑完回傳最後的狀態（done、cancelled）；失敗丟例外</summary>
    public string Run()
    {
        if (Directory.Exists(p.Staging)) Util.DeleteDir(p.Staging); // 上一次複製到一半的
        var targets = Target.LoadAll(rt.Targets);
        log.Write($"targets：{string.Join("、", targets.Select(t => t.Id))}");
        Target target;
        if (opt.Source is { } src)
        {
            state.Step("copy", 1, Steps.Length, targets.Max(t => Base(t, "copy")), targets.Max(t => Work.Sum(s => Base(t, s))));
            CheckSpace("copy", targets.Max(t => t.NeedKb(Steps)));
            target = Copy(src, targets);
            try { opt.CleanupSourceAfterCopy?.Invoke(); }
            catch { log.Write("copy 完成，但下載暫存清理失敗"); }
        }
        else target = ResumeTarget(targets);
        return RunSteps(target);
    }

    Target Copy(IGameSource src, List<Target> targets)
    {
        var sw = Stopwatch.StartNew();
        var staging = Path.Combine(p.Staging, Guid.NewGuid().ToString("N"));
        var dataName = src.DataDirName;
        var data = Path.Combine(staging, dataName);
        log.Write($"copy：{src.Describe} → {data}");
        var hashes = new SortedDictionary<string, string>(Util.Utf8Order);
        long bytes = 0;
        var buf = new byte[1 << 20];
        foreach (var rel in src.Files())
        {
            var dst = Path.Combine(data, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using var input = src.Open(rel);
            using var output = File.Create(dst);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0) { output.Write(buf, 0, n); sha.AppendData(buf, 0, n); bytes += n; }
            hashes[rel] = Util.Hex(sha.GetHashAndReset());
        }
        // AssetRipper 靠 <名稱>.exe 認出 Windows 的結構（WindowsGameStructure），只看檔案在不在、不讀內容：放 0 位元組的佔位檔，不複製真的 exe
        File.WriteAllBytes(Path.Combine(staging, dataName[..^"_Data".Length] + ".exe"), []);
        var managed = new SortedDictionary<string, string>(Util.Utf8Order);
        foreach (var (k, v) in hashes) if (k.StartsWith("Managed/", StringComparison.Ordinal)) managed[k["Managed/".Length..]] = v;
        log.Write($"copy：{hashes.Count} 個檔、{bytes} bytes、{sw.Elapsed.TotalSeconds:F2} 秒；Managed {managed.Count} 個，總雜湊 {Util.ListTotal(managed)}");
        // 全部檔案的總雜湊：Release 版讀不到 app 私有資料夾，驗收拿它跟 Mac 上用原版或 manifest 算的值比（docs/phase6.md）
        log.Write($"copy：全部 {hashes.Count} 個檔的總雜湊 {Util.ListTotal(hashes)}");

        var target = targets.FirstOrDefault(t => t.DataDir == dataName && t.FirstDifference(managed) is null);
        if (target is null)
        {
            Util.DeleteDir(staging);
            var near = targets.FirstOrDefault(t => t.DataDir == dataName);
            var why = near is null ? Strings.Get(Resource.String.install_error_no_target_for_format, dataName) : Strings.Get(Resource.String.install_error_compare_format, near.Id, near.FirstDifference(managed)!);
            throw new InstallException(Strings.Get(Resource.String.install_error_unsupported_format, why), "unsupported");
        }
        log.Write($"target：{target.Id}");
        Record(target, "copy", sw.Elapsed.TotalSeconds);
        var (hwm, rss) = Util.Memory();
        Util.WriteJson(Path.Combine(staging, "original.json"), new JsonObject
        {
            ["target"] = target.Id,
            ["source"] = src.Describe,
            ["dataDir"] = dataName,
            ["files"] = hashes.Count,
            ["bytes"] = bytes,
            ["total"] = Util.ListTotal(hashes),
            ["managedTotal"] = Util.ListTotal(managed),
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2),
            ["vmhwm_kB"] = hwm,
            ["vmrss_kB"] = rss,
            ["copiedAt"] = DateTime.UtcNow.ToString("O"),
        });
        // 換上新的原版；之前的安裝作廢
        var dest = p.Original(target.Id);
        Directory.CreateDirectory(p.Originals);
        if (Directory.Exists(dest))
        {
            var old = dest + ".old-" + Guid.NewGuid().ToString("N");
            Directory.Move(dest, old);
            Directory.Move(staging, dest);
            Util.DeleteDir(old);
        }
        else Directory.Move(staging, dest);
        Util.DeleteDir(p.Install(target.Id).Root);
        return target;
    }

    /// <summary>沒給來源：續跑最近一次的安裝（installs/*/install.json 最新的那個）</summary>
    Target ResumeTarget(List<Target> targets)
    {
        var last = Directory.Exists(p.Installs)
            ? Directory.GetDirectories(p.Installs).Select(d => new InstallDir(d)).Where(d => File.Exists(d.Json))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(d.Json)).FirstOrDefault()
            : null;
        if (last is null) throw new InstallException(Strings.Get(Resource.String.install_error_nothing_to_resume), "idle");
        var id = Path.GetFileName(last.Root);
        if (!File.Exists(Path.Combine(p.Original(id), "original.json"))) throw new InstallException(Strings.Get(Resource.String.install_error_original_missing_format, id), "idle");
        return targets.FirstOrDefault(t => t.Id == id) ?? throw new InstallException(Strings.Get(Resource.String.install_error_target_unsupported_format, id), "unsupported");
    }

    // 預估（docs/phase4.md「安裝流程、進度、續跑」的進度）：基準是這支手機上次跑這一步的實際秒數（launcher/step-times.json，
    // 重新安裝、換 APK 都留著），沒有就用 target 記的 Phase 3 實測；再乘上這一輪已經跑完的步驟「實際 ÷ 基準」（夾在 0.5～3）。
    // 10/22 晚上第一次用 SAF 匯入：每一步都比 Phase 3 慢 1～3 成，固定用 Phase 3 的值時整體剩餘低估約 18%；照這個算法模擬，
    // 匯出做完之後誤差 +3%（匯出之前還沒有這支手機的資料，照樣低估一到兩成）。
    readonly Dictionary<string, double> doneActual = [], doneBase = [];
    JsonObject? stepTimes;

    JsonObject StepTimes => stepTimes ??= Util.ReadJson(Path.Combine(p.Launcher, "step-times.json")) ?? [];

    double Base(Target t, string step) => StepTimes[t.Id]?[step]?.GetValue<double>() is double v && v > 0 ? v : t.StepSeconds(step);

    double Estimate(Target t, string step)
    {
        double a = doneActual.Values.Sum(), b = doneBase.Values.Sum();
        return Base(t, step) * (b > 0 ? Math.Clamp(a / b, 0.5, 3) : 1);
    }

    void Record(Target t, string step, double seconds)
    {
        doneBase[step] = Base(t, step);
        doneActual[step] = seconds;
        var all = StepTimes;
        var mine = all[t.Id] as JsonObject ?? new JsonObject();
        mine[step] = Math.Round(seconds, 2);
        all[t.Id] = mine;
        Util.WriteJson(Path.Combine(p.Launcher, "step-times.json"), all);
    }

    string RunSteps(Target t)
    {
        var inst = p.Install(t.Id);
        var original = Util.ReadJson(Path.Combine(p.Original(t.Id), "original.json"))!;
        var j = Util.ReadJson(inst.Json) ?? new JsonObject { ["target"] = t.Id, ["steps"] = new JsonObject() };
        var steps = (JsonObject)j["steps"]!;

        // 貼圖哪些格式保留原始壓縮資料：看這台 GPU（docs/perf-battle.md「B 段」），決定進匯出的指紋與 install.json
        var textures = DeviceTextures.Plan(p);
        log.Write($"貼圖：GPU 擴充 s3tc={textures.S3tc} s3tc_srgb={textures.S3tcSrgb} bptc={textures.Bptc}{(textures.Forced is { } f ? $"（診斷開關 {f}）" : "")}，保留原始資料的格式 {textures.Key}");

        // 先排好要跑哪些步驟：指紋對不上、沒做完、或輸出不在的才跑；匯出要重跑，importer、shaders 就跟著重跑
        var fp = Fingerprints(rt, t, original, textures);
        bool Fresh(string s) => steps[s]?["fp"]?.GetValue<string>() == fp[s];
        var need = new Dictionary<string, bool>
        {
            ["patch"] = !Fresh("patch") || !Directory.Exists(inst.Patch),
            ["export"] = !Fresh("export") || opt.ForceConvert,
        };
        need["import"] = need["export"] || !Fresh("import") || !Directory.Exists(Path.Combine(inst.Out, "assets"));
        need["shaders"] = need["import"] || !Fresh("shaders") || !Directory.Exists(Path.Combine(inst.Out, "shaders"));
        if ((need["import"] || need["shaders"]) && !Directory.Exists(inst.Export))
            need["export"] = need["import"] = need["shaders"] = true; // 匯出裝完就刪了：重匯出，GUID 也跟著變
        var plan = Work.Where(s => need[s]).ToList();
        log.Write($"{t.Id}：要跑 {(plan.Count > 0 ? string.Join("、", plan) : "（都做好了）")}；略過 {string.Join("、", Work.Except(plan))}");

        foreach (var step in plan)
        {
            if (File.Exists(p.Cancel))
            {
                File.Delete(p.Cancel);
                log.Write($"取消：停在 {step} 之前");
                return "cancelled";
            }
            steps.Remove(step);
            j["complete"] = false;
            Util.WriteJson(inst.Json, j);
            state.Step(step, Array.IndexOf(Steps, step) + 1, Steps.Length, Estimate(t, step), plan.SkipWhile(s => s != step).Skip(1).Sum(s => Estimate(t, s)));
            ClearOutputs(step, inst);
            CheckSpace(step, t.NeedKb(plan.SkipWhile(s => s != step)));
            if (step == "export") j["textures"] = textures.Describe();
            log.Write($"step={step} start {Mem()}");
            var sw = Stopwatch.StartNew();
            var extra = RunStep(step, t, inst, textures);
            sw.Stop();
            Collect();
            var (hwm, rss) = Util.Memory();
            var rec = new JsonObject
            {
                ["fp"] = fp[step],
                ["done"] = DateTime.UtcNow.ToString("O"),
                ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2),
                ["vmhwm_kB"] = hwm,
                ["vmrss_kB"] = rss,
                ["avail_kB"] = availableKb(),
            };
            foreach (var (k, v) in extra) rec[k] = v?.DeepClone();
            steps[step] = rec;
            Util.WriteJson(inst.Json, j);
            Record(t, step, sw.Elapsed.TotalSeconds);
            log.Write($"step={step} end elapsed_s={sw.Elapsed.TotalSeconds:F2} {Mem()}");
        }

        if (!opt.KeepWork)
        {
            Util.DeleteDir(inst.Export);
            Util.DeleteDir(inst.ShaderWork);
        }
        j["complete"] = true;
        j["completedAt"] = DateTime.UtcNow.ToString("O");
        Util.WriteJson(inst.Json, j);
        log.Write($"{t.Id}：安裝完成{(opt.KeepWork ? "（保留 export、shaderwork）" : "")}");
        RemoveOtherTargets(t);
        return "done";
    }

    /// <summary>
    /// 換了版本（例如 2.2.1 裝好之後用 Steam 下載 2.3.0）：新的裝完才清掉別的 target 的安裝、原版與下載暫存。
    /// 遊戲只開唯一一份裝好的（GameActivity.Locate，兩份就不開）；新的沒裝完之前舊的照樣能玩。存檔在 files/saves，不受影響。
    /// </summary>
    void RemoveOtherTargets(Target keep)
    {
        foreach (var root in new[] { p.Installs, p.Originals, p.SteamDownloads })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root))
            {
                var id = Path.GetFileName(d);
                if (id == keep.Id) continue;
                try
                {
                    Util.DeleteDir(d);
                    log.Write($"換成 {keep.Id}：刪掉 {Path.GetFileName(root)}/{id}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Write($"換成 {keep.Id}：{Path.GetFileName(root)}/{id} 刪不掉（{e.Message}）");
                }
            }
        }
    }

    void ClearOutputs(string step, InstallDir inst)
    {
        switch (step)
        {
            case "patch": Util.DeleteDir(inst.Patch); break;
            case "export": Util.DeleteDir(inst.Export); break;
            case "import": Util.DeleteDir(inst.Out); break;
            case "shaders": Util.DeleteDir(inst.ShaderWork); Util.DeleteDir(Path.Combine(inst.Out, "shaders")); break;
        }
    }

    JsonObject RunStep(string step, Target t, InstallDir inst, TexturePlan textures) => step switch
    {
        "patch" => RunPatcher(t, inst),
        "export" => PrelinkThen(["AsmResolver.DotNet", "ICSharpCode.Decompiler"], () => RunAssetRipper(t, inst, p.Original(t.Id), log, textures)),
        "import" => RunImporter(t, inst),
        "shaders" => RunShaders(t, inst),
        _ => throw new ArgumentException(step),
    };

    /// <summary>匯出前把 AssetRipper 帶的組件裡對 libc 的 P/Invoke 先解析好（反編譯用的 ICSharpCode.Decompiler 等）</summary>
    JsonObject PrelinkThen(string[] assemblies, Func<JsonObject> step)
    {
        foreach (var name in assemblies) Prelink(Assembly.Load(name));
        return step();
    }

    // ---- patch ----

    static bool resolverAdded;

    JsonObject RunPatcher(Target t, InstallDir inst)
    {
        if (!resolverAdded)
        {
            resolverAdded = true;
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var dep = Path.Combine(rt.Patcher, name.Name + ".dll");
                return File.Exists(dep) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dep) : null;
            };
        }
        Directory.CreateDirectory(inst.Patch);
        var managed = Path.Combine(p.Original(t.Id), t.DataDir, "Managed");
        string[] args = [managed, inst.Patch, "--hand", rt.UnityNative, .. rt.RedirectFiles()];
        log.Write("patcher " + string.Join(" ", args));
        var entry = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(rt.Patcher, "patcher.dll")).EntryPoint
            ?? throw new InvalidOperationException("patcher 沒有進入點");
        var previous = Console.Out;
        Console.SetOut(new LogWriter(log)); // patcher 的進度印在 Console
        try { entry.Invoke(null, [args]); }
        catch (TargetInvocationException e) when (e.InnerException is { } inner) { throw inner; }
        finally { Console.SetOut(previous); }

        // 手機 patch 的結果要跟開發機逐位元組相同，開發機編好的引擎才對得上（版本綁定的前提）
        var tree = Util.HashTree(inst.Patch);
        var total = Util.ListTotal(tree);
        var want = Util.ReadJson(rt.PatchExpect)?[t.Id];
        log.Write($"patch：{tree.Count} 個檔，總雜湊 {total}；期望 {want?["files"]} 個，{want?["total"]}");
        if (want is null || (string)want["total"]! != total)
            throw new InstallException(Strings.Get(Resource.String.install_error_patch_mismatch_format, tree.Count, total[..8], want?["files"]?.ToString() ?? "?", ((string?)want?["total"])?[..8] ?? "?"));
        return new JsonObject { ["files"] = tree.Count, ["total"] = total };
    }

    // ---- 匯出 ----

    /// <summary>不 inline：AssetRipper 的型別要等 APP_CONTEXT_BASE_DIRECTORY 設好（InstallService）才能碰。
    /// 貼圖換成 RawTextureExportHandler：裝置支援的壓縮格式保留原始資料（LZ4），其他照舊 PNG；.meta、GUID、檔名不變</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static JsonObject RunAssetRipper(Target t, InstallDir inst, string game, InstallLog log, TexturePlan textures)
    {
        Logger.Clear();
        Logger.Add(new AssetRipperLog(log));
        Logger.LogSystemInformation("AssetRipper (mt2-mobile-launcher)");
        FullConfiguration settings = new();
        settings.ExportSettings.ScriptExportMode = Enum.Parse<ScriptExportMode>(t.AssetRipper("ScriptExportMode"));
        settings.ExportSettings.ShaderExportMode = Enum.Parse<ShaderExportMode>(t.AssetRipper("ShaderExportMode"));
        log.Write($"AssetRipper：{game} → {inst.Export}（{settings.ExportSettings.ScriptExportMode}、{settings.ExportSettings.ShaderExportMode}，貼圖 {textures.Key}）");
        var handler = new RawTextureExportHandler(settings, textures);
        handler.LoadProcessAndExport([game], inst.Export, LocalFileSystem.Instance);
        Logger.Clear();
        var stats = handler.Stats.Describe();
        log.Write($"貼圖：{stats.ToJsonString()}");
        return new JsonObject { ["files"] = Directory.EnumerateFiles(inst.Export, "*", SearchOption.AllDirectories).Count(), ["textures"] = stats };
    }

    // ---- importer、shaders ----

    string WriteConfig(Target t, InstallDir inst)
    {
        var import = t.ImportConfig;
        import["export"] = Path.Combine(inst.Export, "ExportedProject");
        import["game"] = p.Original(t.Id);
        import["out"] = inst.Out;
        import["shaderWork"] = inst.ShaderWork;
        Util.WriteJson(inst.Config, new JsonObject { ["root"] = inst.Root, ["import"] = import });
        return inst.Config;
    }

    JsonObject RunImporter(Target t, InstallDir inst)
    {
        new ImportRunner(WriteConfig(t, inst)) { Log = log.Write }.Run("all");
        return new JsonObject();
    }

    JsonObject RunShaders(Target t, InstallDir inst)
    {
        new ShaderRunner(WriteConfig(t, inst)) { Log = log.Write }.Run();
        return new JsonObject();
    }

    /// <summary>
    /// 會在 Android 上解析、又不在 .NET for Android 預先建好的表裡的原生函式庫（APK 裡全部 P/Invoke 的目標見 docs/phase4.md）。
    /// 只剩匯出用到的 libc：shaders 的 dxbcspv、spirv-cross-c-shared 改用函式指標（src/Shaders/Native.cs），不走 P/Invoke；
    /// importer 的 freetype Prelink 綁不到（見下面），拿掉。Windows 專用的（kernel32、user32、ole32、dsound）與 IL2CPP 才用的 capstone 不碰
    /// </summary>
    static readonly HashSet<string> PrelinkLibraries = ["libc"];

    /// <summary>
    /// 匯出之前，先在這個執行緒把組件裡對上面那幾個函式庫的 P/Invoke 解析好（Marshal.Prelink）。
    /// .NET for Android 10（dotnet/android release/10.0.1xx，01024bb）的 CoreCLR 宿主沒有呼叫 MonodroidState::mark_startup_done()，
    /// StartupAwareLock 一直不上鎖；非內建函式庫的 P/Invoke 快取（PinvokeOverride::other_pinvoke_map 與每個函式庫的進入點表，tsl::robin_map）
    /// 多個執行緒同時第一次綁定時會同時插入、rehash，弄壞 heap。上游 main 的 f8d980f（#12219，2026-07-24）補上了呼叫，release/10.0.1xx 還沒有。
    /// Prelink 只對 ReadyToRun 沒有預編 stub 的 P/Invoke 有效：要 marshalling 的（AsmResolver 的 realpath 有 string 參數）在這裡就綁好；
    /// blittable 的 crossgen2 預編了 stub（ICSharpCode.Decompiler 的 2 個、importer 的 FreeType），Prelink 不會綁，要等第一次呼叫
    ///（10/26 開宿主的 assembly log 在手機上實測）。FreeType 只在 importer 的字型那一步、從單一執行緒呼叫。原始碼位置與實測見 docs/phase4.md。
    /// LibraryImport 產生的 extern 方法是包在型別裡的非公開靜態方法，照 PinvokeImpl 找
    /// </summary>
    void Prelink(Assembly asm)
    {
        int n = 0, failed = 0;
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        foreach (var type in types)
        {
            if (type is null) continue;
            foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
                if (m.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>()?.Value is not { } lib || !PrelinkLibraries.Contains(lib)) continue;
                try { System.Runtime.InteropServices.Marshal.Prelink(m); n++; }
                catch (Exception e) { failed++; log.Write($"prelink 失敗：{type.FullName}.{m.Name}（{lib}）：{e.GetType().Name} {e.Message}"); }
            }
        }
        log.Write($"prelink：{asm.GetName().Name} 的 P/Invoke {n} 個{(failed > 0 ? $"，失敗 {failed} 個" : "")}");
    }

    // ---- 指紋 ----

    /// <summary>
    /// 每一步的指紋：程式版本（組件的 MVID，決定性建置下原始碼沒變就不變；用到的原生函式庫取 APK 裡那份的 SHA256）加輸入；
    /// 匯出以後的步驟帶前一步的指紋。
    /// 匯出的程式版本從貼圖 exporter 的組件（Mt2Mobile.TextureExport）往下找，它參考的 AssetRipper 都在裡面；它不在啟動器組件裡，
    /// 只改介面不會觸發重新匯出。貼圖保留哪些格式（看 GPU）也進指紋。
    /// 介面（ConversionCheck）也用這個算期望值，跟 install.json 比看轉換過期了沒有。
    /// </summary>
    public static Dictionary<string, string> Fingerprints(Runtime rt, Target t, JsonObject original, TexturePlan textures)
    {
        var redirects = string.Join(",", rt.RedirectFiles().Select(f => Util.Hex(SHA256.HashData(File.ReadAllBytes(f)))));
        var patch = Util.Sha256($"patch|{Util.ListTotal(Util.HashTree(rt.Patcher))}|{Util.Hex(SHA256.HashData(File.ReadAllBytes(rt.UnityNative)))}|{redirects}|{t.ManagedTotal}");
        var export = Util.Sha256($"export|{Mvids(typeof(RawTextureExportHandler).Assembly)}|{t.Json["assetRipper"]!.ToJsonString()}|{original["total"]}|textures:{textures.Key}");
        // importer 用 FreeType（字形），shaders 用 dxbc-spirv 與 SPIRV-Cross：只換原生函式庫、C# 沒變時也要重跑
        var import = Util.Sha256($"import|{Mvids(typeof(ImportRunner).Assembly)}|{rt.NativeHash("libfreetype.so")}|{t.Json["import"]!.ToJsonString()}|{export}");
        var shaders = Util.Sha256($"shaders|{Mvids(typeof(ShaderRunner).Assembly)}|{rt.NativeHash("libdxbcspv.so")}|{rt.NativeHash("libspirv-cross-c-shared.so")}|{import}");
        return new() { ["patch"] = patch, ["export"] = export, ["import"] = import, ["shaders"] = shaders };
    }

    static readonly string[] OwnPrefixes = ["AssetRipper", "Mt2Mobile", "AssetsTools", "YamlDotNet", "Mono.Cecil", "K4os"];

    /// <summary>root 與它參考的（遞迴）這些套件的 MVID</summary>
    static string Mvids(Assembly root)
    {
        var seen = new SortedDictionary<string, Guid>(StringComparer.Ordinal);
        void Visit(Assembly a)
        {
            if (!seen.TryAdd(a.GetName().Name!, a.ManifestModule.ModuleVersionId)) return;
            foreach (var r in a.GetReferencedAssemblies().Where(r => OwnPrefixes.Any(x => r.Name!.StartsWith(x, StringComparison.Ordinal))))
            {
                try { Visit(Assembly.Load(r)); } catch (FileNotFoundException) { }
            }
        }
        Visit(root);
        return Util.Sha256(string.Join(";", seen.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    // ---- 其他 ----

    void CheckSpace(string step, long needKb)
    {
        var avail = availableKb();
        log.Write($"空間：{step} 起還要 {needKb} kB，可用 {avail} kB");
        if (avail < needKb)
            throw new InstallException(Strings.Get(Resource.String.install_error_no_space_format, Strings.Gb(needKb), Strings.Gb(avail), Strings.Gb(needKb - avail)), "no-space");
    }

    /// <summary>每步做完把上一步的物件收掉（Phase 3：importer 開始時程序還留著匯出的 3.19 GB）</summary>
    static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
    }

    string Mem()
    {
        var (hwm, rss) = Util.Memory();
        return $"VmHWM_kB={hwm} VmRSS_kB={rss} avail_kB={availableKb()}";
    }

    /// <summary>AssetRipper 的 log：不用 ConsoleLogger（它設終端機顏色，Android 上不支援），Debug、Verbose 不寫</summary>
    sealed class AssetRipperLog(InstallLog log) : ILogger
    {
        public void Log(LogType type, LogCategory category, string message)
        {
            if (type is LogType.Debug or LogType.Verbose) return;
            log.Write(category == LogCategory.None ? $"{type}: {message}" : $"{category} {type}: {message}");
        }

        public void BlankLine(int numLines) { }
    }

    /// <summary>Console 的輸出一行一行寫進 log</summary>
    sealed class LogWriter(InstallLog log) : TextWriter
    {
        readonly StringBuilder line = new();
        public override Encoding Encoding => Util.Utf8;

        public override void Write(char value)
        {
            if (value == '\n') { log.Write(line.ToString().TrimEnd('\r')); line.Clear(); }
            else line.Append(value);
        }
    }
}
