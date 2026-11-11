using Mt2Mobile.Importer.PyCompat;
using Mt2Mobile.Importer.Unity;

namespace Mt2Mobile.Importer;

/// <summary>
/// run.py：設定檔（cs2ts.json 的 import 段）與步驟。步驟名稱跟 Python 版相同：
/// all、assets、addressables、scenes、supplement、glyphs、streaming、packs（shaders 在 Shaders 函式庫）。
/// </summary>
public sealed class ImportRunner
{
    public static readonly string[] Steps = ["all", "assets", "addressables", "scenes", "supplement", "glyphs", "streaming", "packs"];

    public string ConfigPath { get; }
    public PyDict Config { get; }
    public string Root { get; }
    public PyDict Import { get; }
    public string Export { get; }
    public string Game { get; }
    public string Out { get; }
    public string? ShaderExport { get; }
    public string GameData { get; }
    public Action<string> Log { get; init; } = _ => { };
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    public ImportRunner(string configPath)
    {
        ConfigPath = PyPath.Resolve(configPath);
        Config = LoadConfig(ConfigPath);
        Root = (string)Config["_root"]!;
        Import = Assets.Dict(Config.Get("import"));
        string? P(string key) => Import.Get(key) is string v && v.Length > 0 ? PyPath.Resolve(PyPath.Join(Root, v)) : null;
        var export = P("export");
        var game = P("game");
        var outDir = P("out");
        if (export is null || game is null || outDir is null)
            throw new InvalidOperationException($"{ConfigPath}：import 段要有 export、game、out");
        Export = export;
        Game = game;
        Out = outDir;
        ShaderExport = P("shaderExport");
        GameData = GameDataDir(Game);
    }

    /// <summary>設定檔可以有 // 開頭的註解行</summary>
    public static PyDict LoadConfig(string path)
    {
        var lines = PyStr.SplitLines(PyStr.ReadText(path), false).Where(l => !PyStr.LStrip(l).StartsWith("//", StringComparison.Ordinal));
        var cfg = (PyDict)PyJson.Loads(string.Join("\n", lines))!;
        string root = cfg.Get("root") is string r && r.Length > 0 ? r : ".";
        cfg["_root"] = PyPath.Resolve(PyPath.Join(PyPath.Parent(path), root));
        return cfg;
    }

    /// <summary>原版 build 的 Data 資料夾：macOS 是 &lt;名稱&gt;.app/Contents/Resources/Data，Windows／Linux 是 &lt;名稱&gt;_Data</summary>
    public static string GameDataDir(string game)
    {
        if (File.Exists(PyPath.Join(game, "globalgamemanagers"))) return game;
        if (PyPath.Suffix(game) == ".app") return PyPath.Join(game, "Contents/Resources/Data");
        var hits = PyPath.Glob(game, "*_Data").Where(p => File.Exists(PyPath.Join(p, "globalgamemanagers"))).Order(PyPath.Comparer).ToList();
        if (hits.Count != 1) throw new InvalidOperationException($"在 {game} 找不到唯一的 <名稱>_Data（找到 {hits.Count} 個）");
        return hits[0];
    }

    public void Run(string step = "all")
    {
        if (!Steps.Contains(step)) throw new ArgumentException($"沒有這個步驟：{step}（{string.Join("、", Steps)}）");
        double t0 = ImportContext.Now();
        string assets = PyPath.Join(Export, "Assets");
        var yaml = new UnityYaml(Assets.ListOf(Import.Get("int32ArrayKeys")).Cast<string>());
        Log($"匯出：{Export}\nbuild：{GameData}\n輸出：{Out}");
        var guids = step != "packs" ? UnityYaml.BuildGuidIndex(assets) : [];
        if (step != "packs") Log($"GUID 索引：{guids.Count} 個（{ImportContext.Now() - t0:F1} 秒）");
        ImportContext Ctx(string outDir) => new() { Assets = assets, GameData = GameData, Out = outDir, Yaml = yaml, Log = Log, Parallelism = Parallelism };

        if (step is "all" or "assets")
        {
            var rep = new Assets(Ctx(PyPath.Join(Out, "assets"))).ExportAll(ShaderExport, guids, GameData);
            foreach (var (name, c) in (PyDict)rep["categories"]!)
            {
                var cd = (PyDict)c!;
                Log($"  {name}: {Py.Str(cd.Get("exported"))}/{Py.Str(cd.Get("source"))}，失敗 {Assets.ListOf(cd.Get("failed")).Count}");
            }
            var sr = (PyDict)rep["spriteRebase"]!;
            Log($"  sprite 範圍改回 build 的值：{Py.Str(sr.Get("rebased"))}（對到 {Py.Str(sr.Get("matched"))}）");
            Log($"素材完成（{Py.Str(((PyDict)rep["seconds"]!)["total"])} 秒），報告 {PyPath.Join(Out, "assets/report.json")}");
        }
        if (step == "addressables")
        {
            var (table, r) = new Assets(Ctx(PyPath.Join(Out, "assets"))).ExportAddressables(GameData, guids);
            ImportContext.WriteJson(PyPath.Join(Out, "assets/addressables.json"), table);
            Log($"Addressables：GUID {Py.Str(r["exported"])}/{Py.Str(r["source"])}、位址 {Py.Str(r.Get("addresses", 0L))}，對不到 {Assets.ListOf(r["failed"]).Count}");
        }
        if (step is "all" or "scenes")
        {
            var rep = new Scenes(Ctx(Out)).Export(GameData, guids);
            var f = (PyDict)rep["files"]!;
            Log($"  場景 {Py.Str(f["scenes"])}、prefab {Py.Str(f["prefabs"])}，失敗 {Assets.ListOf(f["failed"]).Count}");
            Log($"場景完成（{Py.Str(rep["seconds"])} 秒），報告 {PyPath.Join(Out, "scenes/report.json")}");
        }
        if (step is "all" or "supplement")
        {
            var rep = new Supplement(Ctx(Out)).Export();
            Log($"補充完成（{Py.Str(rep["seconds"])} 秒），報告 {PyPath.Join(Out, "supplement.json")}");
        }
        if (step is "all" or "glyphs")
        {
            var sources = Assets.ListOf(Import.Get("charsetSources")).Select(s => PyPath.Resolve(PyPath.Join(Root, (string)s!))).ToList();
            var rep = new Glyphs(Ctx(Out)).Export(sources, Import.Get("extraChars") as string ?? "");
            Log($"字形完成（{Py.Str(rep["seconds"])} 秒），報告 {PyPath.Join(Out, "assets/glyphs.json")}");
        }
        if (step is "all" or "streaming")
        {
            var rep = Streaming.Export(GameData, Out);
            Log($"StreamingAssets：{Py.Str(rep["files"])} 個檔（{Py.Str(rep["seconds"])} 秒）→ {PyPath.Join(Out, "streaming")}");
        }
        if (step is "all" or "packs")
        {
            string oldMeshes = PyPath.Join(Out, "assets/meshes.json");
            if (File.Exists(oldMeshes)) Assets.WriteMeshes(PyPath.Join(Out, "assets"), PyJson.LoadDict(oldMeshes));
            var rep = Packs.Export(Out);
            Log($"打包完成（{Py.Str(rep["seconds"])} 秒）→ {PyPath.Join(Out, "packs")}");
        }
    }
}
