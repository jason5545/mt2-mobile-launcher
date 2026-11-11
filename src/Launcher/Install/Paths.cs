namespace Mt2Mobile.Launcher.Install;

/// <summary>
/// app 內部空間 files/ 底下的位置（docs/phase4.md「原版、匯出、輸出放哪裡」）。
/// 原版放 originals/&lt;target&gt;/（&lt;名稱&gt;_Data 加 0 位元組的 &lt;名稱&gt;.exe 佔位檔：AssetRipper 靠 exe 認出 Windows 的結構，不讀它的內容），
/// 安裝的東西放 installs/&lt;target&gt;/，介面與服務共用的狀態放 launcher/。
/// </summary>
public sealed record Paths(string Files)
{
    public string Staging => Path.Combine(Files, "staging");
    public string Originals => Path.Combine(Files, "originals");
    public string Original(string target) => Path.Combine(Originals, target);
    public string SteamDownloads => Path.Combine(Files, "steam-downloads");
    public string SteamDownload(string target) => Path.Combine(SteamDownloads, target);
    public string Installs => Path.Combine(Files, "installs");
    public InstallDir Install(string target) => new(Path.Combine(Installs, target));
    public string Runtime => Path.Combine(Files, "runtime");
    public string Launcher => Path.Combine(Files, "launcher");
    /// <summary>安裝進度：服務每秒寫，介面讀（跨程序不用 binder）</summary>
    public string State => Path.Combine(Launcher, "state.json");
    /// <summary>安裝期間服務整段持有的檔案鎖；介面拿得到鎖就代表沒有在跑的安裝</summary>
    public string Lock => Path.Combine(Launcher, "install.lock");
    /// <summary>介面要求在步驟之間停下來</summary>
    public string Cancel => Path.Combine(Launcher, "cancel");
    public string Logs => Path.Combine(Files, "logs");
    /// <summary>AssetRipper 的 ExecutingDirectory（APP_CONTEXT_BASE_DIRECTORY）與它的暫存</summary>
    public string AssetRipperBase => Path.Combine(Files, "assetripper");
}

/// <summary>installs/&lt;target&gt;/</summary>
public sealed record InstallDir(string Root)
{
    /// <summary>每一步的狀態與指紋</summary>
    public string Json => Path.Combine(Root, "install.json");
    /// <summary>importer、shaders 的設定（從 target 產生）</summary>
    public string Config => Path.Combine(Root, "config", "import.json");
    /// <summary>patcher 的輸出，只放 patcher 寫的檔（總雜湊直接跟開發機比）</summary>
    public string Patch => Path.Combine(Root, "patch", "managed");
    public string Export => Path.Combine(Root, "export");
    public string ShaderWork => Path.Combine(Root, "shaderwork");
    /// <summary>importer 與 shaders 的輸出＝引擎的 GameDir</summary>
    public string Out => Path.Combine(Root, "out");
}
