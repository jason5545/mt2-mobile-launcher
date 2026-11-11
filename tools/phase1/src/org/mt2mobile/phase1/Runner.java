package org.mt2mobile.phase1;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Context;
import android.os.Bundle;
import android.system.ErrnoException;
import android.system.Os;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;

/**
 * 在 app 自己的程序裡跑 converter，不開任何畫面。
 *
 *   am instrument -w -e mode probe   org.mt2mobile.phase1/.Runner
 *   am instrument -w -e mode convert org.mt2mobile.phase1/.Runner
 *
 * 原生函式庫（Mono runtime、hostfxr、System.*.Native、OpenSSL）都在 APK 的 lib/，
 * 由系統解到 nativeLibraryDir；從 app 自己的資料夾載入 .so 雖然還允許，但 SELinux 會特別記錄
 * （auditallow），之後的 Android 可能擋掉，所以不放那裡。
 *
 * files 資料夾的配置（由 root 事先放好，擁有者改成 app）：
 *   conv/     converter 發布裡的 DLL 與 json（不含 .so）；每次執行前在這裡建 symlink
 *             指向 nativeLibraryDir 的 .so，hostfxr 照舊在同一個資料夾找到全部檔案
 *   work/mt2-web/...  stage-inputs.sh 整理的輸入
 * 結果寫到 files/tmp/result.txt，converter 的 stdout、stderr 也在 files/tmp/。
 */
public class Runner extends Instrumentation {
    private Bundle args;

    @Override
    public void onCreate(Bundle arguments) {
        args = arguments;
        start();
    }

    @Override
    public void onStart() {
        Bundle out = new Bundle();
        StringBuilder log = new StringBuilder();
        int code = Activity.RESULT_OK;
        try {
            Context ctx = getTargetContext();
            File files = ctx.getFilesDir();
            File tmp = new File(files, "tmp");
            tmp.mkdirs();
            String nativeDir = ctx.getApplicationInfo().nativeLibraryDir;
            log.append("nativeLibraryDir=").append(nativeDir).append('\n');
            log.append("filesDir=").append(files).append('\n');
            log.append("self_context=").append(read("/proc/self/attr/current")).append('\n');
            log.append("self_uid=").append(android.os.Process.myUid()).append('\n');

            String mode = args.getString("mode", "probe");
            if (mode.equals("probe")) {
                // exec 系統的 id（應該可以）與 files 裡的檔案（targetSdk 29+ 應該被擋）
                log.append(exec(List.of("/system/bin/id"), files, null, tmp, "probe-id"));
                File copy = new File(files, "exec-test.sh");
                Files.write(copy.toPath(), "#!/system/bin/sh\necho hi\n".getBytes(StandardCharsets.UTF_8));
                copy.setExecutable(true);
                log.append(exec(List.of(copy.getPath()), files, null, tmp, "probe-files"));
                log.append(exec(List.of(nativeDir + "/libcsport.so"), files, null, tmp, "probe-native"));
                Thread.sleep(Long.parseLong(args.getString("sleep", "0")));
            } else if (mode.equals("convert")) {
                File conv = new File(files, "conv");
                log.append("linked=").append(linkNativeLibs(new File(nativeDir), conv)).append('\n');
                File web = new File(files, "work/mt2-web");
                deleteTree(new File(web, "build/out/gen").toPath());
                List<String> cmd = new ArrayList<>(List.of(
                        nativeDir + "/libcsport.so",
                        conv.getPath(),
                        new File(conv, "CsPort.dll").getPath(),
                        "--out", "build/out", "web", "*"));
                Map<String, String> env = Map.of(
                        "HOME", files.getPath(),
                        "TMPDIR", tmp.getPath(),
                        "DOTNET_CLI_TELEMETRY_OPTOUT", "1",
                        "LD_LIBRARY_PATH", nativeDir,
                        "MT2_KEEP_TAGGING", args.getString("keep_tagging", "0"));
                log.append(exec(cmd, web, env, tmp, "convert"));
            } else {
                throw new IllegalArgumentException("未知的 mode：" + mode);
            }
            Files.write(new File(tmp, "result.txt").toPath(), log.toString().getBytes(StandardCharsets.UTF_8));
        } catch (Throwable t) {
            code = Activity.RESULT_CANCELED;
            log.append("error=").append(t).append('\n');
        }
        out.putString(Instrumentation.REPORT_KEY_STREAMRESULT, log.toString());
        finish(code, out);
    }

    /** 執行一個指令，stdout、stderr 寫到 tmp/<名稱>.out、.err，回傳 exit code 與時間。 */
    private static String exec(List<String> cmd, File dir, Map<String, String> env, File tmp, String name)
            throws InterruptedException {
        ProcessBuilder pb = new ProcessBuilder(cmd).directory(dir)
                .redirectOutput(new File(tmp, name + ".out"))
                .redirectError(new File(tmp, name + ".err"));
        if (env != null) pb.environment().putAll(env);
        long t0 = System.nanoTime();
        try {
            Process p = pb.start();
            int rc = p.waitFor();
            double s = (System.nanoTime() - t0) / 1e9;
            return String.format("%s: exit=%d elapsed_s=%.2f%n", name, rc, s);
        } catch (IOException e) {
            return name + ": start failed: " + e.getMessage() + "\n";
        }
    }

    /**
     * 在 conv/ 建 symlink 指向 nativeLibraryDir 的每個 lib*.so（launcher 本身除外）。
     * nativeLibraryDir 每次更新 app 都會換路徑，所以每次執行都重建。回傳建了幾個。
     */
    private static int linkNativeLibs(File nativeDir, File conv) throws ErrnoException {
        int n = 0;
        for (String name : nativeDir.list()) {
            if (!name.startsWith("lib") || !name.endsWith(".so") || name.equals("libcsport.so")) continue;
            File link = new File(conv, name);
            link.delete();
            Os.symlink(new File(nativeDir, name).getPath(), link.getPath());
            n++;
        }
        return n;
    }

    private static String read(String path) {
        try {
            return new String(Files.readAllBytes(new File(path).toPath()), StandardCharsets.UTF_8).trim();
        } catch (IOException e) {
            return "(" + e.getMessage() + ")";
        }
    }

    private static void deleteTree(Path root) throws IOException {
        if (!Files.exists(root)) return;
        try (Stream<Path> s = Files.walk(root)) {
            for (Path p : (Iterable<Path>) s.sorted(Comparator.reverseOrder())::iterator) Files.delete(p);
        }
    }
}
