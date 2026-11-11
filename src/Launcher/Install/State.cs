using System.Text.Json.Nodes;

namespace Mt2Mobile.Launcher.Install;

/// <summary>安裝器進入步驟時通知（state.json；服務另外更新通知）</summary>
public interface IStepSink
{
    /// <summary>est：這一步預估的秒數；estAfter：之後還要跑的步驟預估合計（介面顯示整個安裝還剩多久）</summary>
    void Step(string step, int index, int count, double est, double estAfter);
}

/// <summary>
/// launcher/state.json：安裝進度，服務每秒寫一次（先寫暫存檔再 rename），介面在前景時每秒讀。
/// state：running、done、failed、cancelled、unsupported、no-space、idle、timeout。
/// </summary>
public sealed class StateWriter : IStepSink, IDisposable
{
    readonly string path;
    readonly object gate = new();
    readonly Timer timer;
    readonly JsonObject s = new();
    DateTime stepStarted;

    public StateWriter(string path)
    {
        this.path = path;
        s["pid"] = Android.OS.Process.MyPid();
        s["state"] = "running";
        s["started"] = DateTime.UtcNow.ToString("O");
        timer = new Timer(_ => Flush(), null, 0, 1000);
    }

    /// <summary>
    /// 進入一個步驟。AssetRipper 與 importer 沒有百分比，進度用預估的秒數算（Installer.Estimate：這支手機上次的實測，
    /// 沒有就用 target 記的 Phase 3 實測，再乘上這一輪已經跑完的步驟「實際 ÷ 預估」）
    /// </summary>
    public void Step(string step, int index, int count, double est, double estAfter)
    {
        lock (gate)
        {
            stepStarted = DateTime.UtcNow;
            s["step"] = step;
            s["index"] = index;
            s["count"] = count;
            s["stepStarted"] = stepStarted.ToString("O");
            s["estSeconds"] = Math.Round(est, 1);
            s["estAfter"] = Math.Round(estAfter, 1);
        }
        Flush();
    }

    /// <summary>Steam 下載的位元組數（Steam 頁的進度面板拿來算速度與剩餘時間）</summary>
    public void Download(long done, long total)
    {
        lock (gate) { s["downloadedBytes"] = done; s["totalBytes"] = total; }
        Flush();
    }

    public void Set(string key, JsonNode? value)
    {
        lock (gate) s[key] = value;
        Flush();
    }

    public void Finish(string state, string message)
    {
        lock (gate) { s["state"] = state; s["message"] = message; }
        Flush();
    }

    void Flush()
    {
        lock (gate)
        {
            s["updated"] = DateTime.UtcNow.ToString("O");
            if (s["step"] is not null) s["stepElapsed"] = Math.Round((DateTime.UtcNow - stepStarted).TotalSeconds, 1);
            try { Util.WriteJson(path, s.DeepClone()); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        Flush();
    }
}
