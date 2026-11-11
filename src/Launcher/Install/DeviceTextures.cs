using Android.Opengl;
using Mt2Mobile.TextureExport;

namespace Mt2Mobile.Launcher.Install;

/// <summary>
/// 這台 GPU 支援哪些壓縮貼圖格式（docs/perf-battle.md「B 段」第 4 題）：開一個 1×1 pbuffer 的 GLES 3 context 讀 GL_EXTENSIONS。
/// 安裝（:install，匯出前決定哪些格式保留原始資料）與介面（主程序，算期望的指紋看轉換有沒有過期）都叫，不碰別的 context。
/// 不呼叫 eglTerminate：主程序的介面（HWUI）也用預設的 display。
/// 驗收用的建置（TEST_ENTRY）認 files/launcher/texture-override：內容 png 就當成都不支援（強制走 PNG 的安裝）。
/// </summary>
public static class DeviceTextures
{
    public static TexturePlan Plan(Paths p)
    {
        string? forced = null;
#if TEST_ENTRY
        var f = Path.Combine(p.Launcher, "texture-override");
        if (File.Exists(f)) forced = File.ReadAllText(f).Trim() is { Length: > 0 } v ? v : null;
#endif
        return TexturePlan.FromExtensions(Extensions(), forced);
    }

    /// <summary>GL_EXTENSIONS 拆開；拿不到 context 回空的（全部當不支援，照舊存 PNG）</summary>
    public static string[] Extensions()
    {
        // 在自己的執行緒做：EGL 的 current context 綁執行緒，不動呼叫端的
        string[] result = [];
        var t = new Thread(() => result = Query()) { IsBackground = true };
        t.Start();
        t.Join();
        return result;
    }

    static string[] Query()
    {
        var dpy = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if (dpy == null || dpy == EGL14.EglNoDisplay) return [];
        int[] ver = new int[2];
        if (!EGL14.EglInitialize(dpy, ver, 0, ver, 1)) return [];
        int[] attribs = [EGL14.EglRenderableType, EGLExt.EglOpenglEs3BitKhr, EGL14.EglSurfaceType, EGL14.EglPbufferBit, EGL14.EglNone];
        var configs = new EGLConfig[1];
        int[] count = new int[1];
        if (!EGL14.EglChooseConfig(dpy, attribs, 0, configs, 0, 1, count, 0) || count[0] < 1 || configs[0] == null) return [];
        var ctx = EGL14.EglCreateContext(dpy, configs[0], EGL14.EglNoContext, [EGL14.EglContextClientVersion, 3, EGL14.EglNone], 0);
        if (ctx == null || ctx == EGL14.EglNoContext) return [];
        var surface = EGL14.EglCreatePbufferSurface(dpy, configs[0], [EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone], 0);
        try
        {
            if (surface == null || surface == EGL14.EglNoSurface || !EGL14.EglMakeCurrent(dpy, surface, surface, ctx)) return [];
            var ext = GLES20.GlGetString(GLES20.GlExtensions) ?? "";
            return ext.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            EGL14.EglMakeCurrent(dpy, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
            if (surface != null && surface != EGL14.EglNoSurface) EGL14.EglDestroySurface(dpy, surface);
            EGL14.EglDestroyContext(dpy, ctx);
        }
    }
}
