using Mt2Mobile.Importer;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Shaders;

/// <summary>
/// run.py 的 shaders 步驟：轉換（shaders/convert.py）放 import.shaderWork（預設 &lt;root&gt;/reference/shaders），再發布到 &lt;out&gt;/shaders。
/// 要先有 all 的輸出：assets/materials.json 決定要從原版 build 抽哪些內建 shader。
/// </summary>
public sealed class ShaderRunner
{
    // UI/Default、Sprites/Default、Sprites/Mask 由執行環境手寫，不轉
    private static readonly HashSet<string> Handwritten = ["UI/Default", "Sprites/Default", "Sprites/Mask"];

    public Action<string> Log { get; init; } = _ => { };
    public int Parallelism { get; init; } = Environment.ProcessorCount;
    private readonly ImportRunner _cfg;

    public ShaderRunner(string configPath) => _cfg = new ImportRunner(configPath);

    public PyDict Run()
    {
        string work = _cfg.Import.Get("shaderWork") is string w && w.Length > 0
            ? PyPath.Resolve(PyPath.Join(_cfg.Root, w)) : PyPath.Join(_cfg.Root, "reference/shaders");
        string src = _cfg.ShaderExport ?? _cfg.Export;
        string matsPath = PyPath.Join(_cfg.Out, "assets/materials.json");
        var mats = File.Exists(matsPath) ? PyJson.LoadDict(matsPath) : new PyDict();
        var builtinNames = mats.Values.Select(m => Assets.Dict(((PyDict)m!).Get("builtinShader")).Get("name"))
            .OfType<string>().Where(n => !Handwritten.Contains(n)).Distinct().Order(PyStr.Comparer).ToList();
        new ShaderConvert(Log, Parallelism).Convert(src, work, builtinNames.Count > 0 ? _cfg.GameData : null, builtinNames.Count > 0 ? builtinNames : null);
        var rep = ShaderPublish.Publish(work, PyPath.Join(_cfg.Out, "shaders"));
        Log($"shader 發布：{Py.Str(rep["exported"])}/{Py.Str(rep["source"])}，GLSL {Py.Str(rep["glsl"])} 個，失敗 {Assets.ListOf(rep["failed"]).Count}");
        return rep;
    }
}
