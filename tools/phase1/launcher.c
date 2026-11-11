// Phase 1 的 .NET 啟動器：代替 apphost。
// 一般 app 只能 exec nativeLibraryDir 裡的檔案，apphost 卻只會在自己旁邊找 CsPort.dll 與 hostfxr，
// 所以這支放 nativeLibraryDir（APK 裡叫 libcsport.so），DLL 放 app 的 files 資料夾（<runtime 資料夾>），
// 由這裡 dlopen <runtime 資料夾>/libhostfxr.so，把路徑明確交給 hostfxr。
//
// <runtime 資料夾> 裡的 .so 是指向 nativeLibraryDir 的 symlink（見 Runner.java）。hostpolicy 會把
// libcoreclr.so 解成真實路徑，再到那個資料夾找 System.Private.CoreLib.dll；nativeLibraryDir 只能放 lib*.so，
// 所以執行前把 TRUSTED_PLATFORM_ASSEMBLIES 裡不存在的項目改回 <runtime 資料夾>。
//
// 用法：libcsport.so <runtime 資料夾> <app.dll> [app 參數...]
// 另外在載入 runtime 前關掉 heap pointer tagging（Phase 0 的 libnotag.so 做的事）。
// 設環境變數 MT2_KEEP_TAGGING=1 可以跳過，用來確認 manifest 屬性本身有沒有效（實測沒有，見 docs/phase1.md）。
#include <dlfcn.h>
#include <limits.h>
#include <malloc.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

typedef void *hostfxr_handle;
struct hostfxr_initialize_parameters {
    size_t size;
    const char *host_path;
    const char *dotnet_root;
};
typedef int (*init_fn)(int argc, const char **argv, const struct hostfxr_initialize_parameters *params,
                       hostfxr_handle *handle);
typedef int (*get_prop_fn)(hostfxr_handle handle, const char *name, const char **value);
typedef int (*set_prop_fn)(hostfxr_handle handle, const char *name, const char *value);
typedef int (*run_fn)(hostfxr_handle handle);
typedef int (*close_fn)(hostfxr_handle handle);

static void *sym(void *h, const char *name) {
    void *p = dlsym(h, name);
    if (!p) {
        fprintf(stderr, "找不到 %s：%s\n", name, dlerror());
        exit(3);
    }
    return p;
}

// TPA 是冒號分隔的路徑清單；不存在的項目改成 <root>/<檔名>（那裡有的話）
static char *fix_tpa(const char *tpa, const char *root) {
    size_t cap = strlen(tpa) + 1;
    char *src = strdup(tpa), *out = NULL;
    // 每一項最多變長 strlen(root) + 1
    size_t items = 1;
    for (const char *c = tpa; *c; c++) items += *c == ':';
    cap += items * (strlen(root) + 1);
    out = calloc(cap, 1);
    char *save = NULL;
    for (char *p = strtok_r(src, ":", &save); p; p = strtok_r(NULL, ":", &save)) {
        char alt[PATH_MAX];
        const char *use = p;
        if (access(p, F_OK) != 0) {
            const char *base = strrchr(p, '/');
            snprintf(alt, sizeof alt, "%s/%s", root, base ? base + 1 : p);
            if (access(alt, F_OK) == 0) use = alt;
        }
        if (*out) strcat(out, ":");
        strcat(out, use);
    }
    free(src);
    return out;
}

int main(int argc, char **argv) {
    if (argc < 3) {
        fprintf(stderr, "usage: %s <runtime dir> <app.dll> [args...]\n", argv[0]);
        return 2;
    }
    const char *keep = getenv("MT2_KEEP_TAGGING");
    if (!keep || strcmp(keep, "1") != 0)
        mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE);

    const char *root = argv[1];
    const char *app = argv[2];
    char fxr[PATH_MAX], host[PATH_MAX];
    snprintf(fxr, sizeof fxr, "%s/libhostfxr.so", root);

    // host_path 只用來推出 app 名稱與資料夾，指向「如果有 apphost 會在的位置」
    size_t n = strlen(app);
    if (n < 4 || strcmp(app + n - 4, ".dll") != 0) {
        fprintf(stderr, "app 要是 .dll：%s\n", app);
        return 2;
    }
    snprintf(host, sizeof host, "%.*s", (int)(n - 4), app);

    void *h = dlopen(fxr, RTLD_NOW | RTLD_LOCAL);
    if (!h) {
        fprintf(stderr, "dlopen 失敗：%s\n", dlerror());
        return 3;
    }
    init_fn init = (init_fn)sym(h, "hostfxr_initialize_for_dotnet_command_line");
    get_prop_fn get_prop = (get_prop_fn)sym(h, "hostfxr_get_runtime_property_value");
    set_prop_fn set_prop = (set_prop_fn)sym(h, "hostfxr_set_runtime_property_value");
    run_fn run = (run_fn)sym(h, "hostfxr_run_app");
    close_fn close_ = (close_fn)sym(h, "hostfxr_close");

    // 這個入口的 argv 不含程式本身：第一個是 app.dll，後面接 app 參數
    int nargc = argc - 2;
    const char **nargv = calloc((size_t)nargc + 1, sizeof *nargv);
    nargv[0] = app;
    for (int i = 3; i < argc; i++) nargv[i - 2] = argv[i];

    struct hostfxr_initialize_parameters params = {sizeof params, host, root};
    hostfxr_handle handle = NULL;
    int rc = init(nargc, nargv, &params, &handle);
    if (rc != 0 || !handle) {
        fprintf(stderr, "hostfxr 初始化失敗：0x%x\n", (unsigned)rc);
        return 4;
    }
    const char *tpa = NULL;
    if (get_prop(handle, "TRUSTED_PLATFORM_ASSEMBLIES", &tpa) == 0 && tpa) {
        char *fixed = fix_tpa(tpa, root);
        set_prop(handle, "TRUSTED_PLATFORM_ASSEMBLIES", fixed);
        free(fixed);
    }
    rc = run(handle);
    close_(handle);
    return rc;
}
