// 用法：mt2-shaders [--config cs2ts.json] [--jobs N]
// 沒給 --config 就從目前資料夾往上找 cs2ts.json（同 run.py）。
using Mt2Mobile.Shaders;

var argv = args.ToList();
string? Take(string flag)
{
    int i = argv.IndexOf(flag);
    if (i < 0) return null;
    string v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}
string? config = Take("--config");
int jobs = int.TryParse(Take("--jobs"), out var j) ? j : Environment.ProcessorCount;
if (config is null)
{
    for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
    {
        if (File.Exists(Path.Combine(d.FullName, "cs2ts.json")))
        {
            config = Path.Combine(d.FullName, "cs2ts.json");
            break;
        }
    }
}
if (config is null)
{
    Console.Error.WriteLine("找不到 cs2ts.json（從目前目錄往上找），或用 --config 指定");
    return 1;
}
new ShaderRunner(config) { Log = Console.WriteLine, Parallelism = jobs }.Run();
return 0;
