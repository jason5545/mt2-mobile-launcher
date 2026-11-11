// 用法：mt2-importer [--config cs2ts.json] [all|assets|addressables|scenes|supplement|glyphs|streaming|packs]
// 沒給 --config 就從目前資料夾往上找 cs2ts.json（同 run.py）。
using Mt2Mobile.Importer;

var argv = args.ToList();
string? config = null;
int i = argv.IndexOf("--config");
if (i >= 0)
{
    config = argv[i + 1];
    argv.RemoveRange(i, 2);
}
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
var runner = new ImportRunner(config) { Log = Console.WriteLine };
runner.Run(argv.Count > 0 ? argv[0] : "all");
return 0;
