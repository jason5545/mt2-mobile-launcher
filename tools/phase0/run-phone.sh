#!/system/bin/sh
# 在手機上跑一次 converter 並量測（adb shell 執行，不碰畫面）。
# 用法：sh run-phone.sh <工作根目錄>
#   <根>/conv/CsPort           linux-bionic-arm64 的 self-contained 發布
#   <根>/libnotag.so           notag.c 編出來的 LD_PRELOAD
#   <根>/ssl/libssl.so、libcrypto.so  NDK 編的 OpenSSL 3（系統的 BoringSSL 缺 .NET 要的符號）
#   <根>/work/mt2-web/...      cs2ts.json 與輸入（保持 unity-port 的相對結構）
#   <根>/work/mt2-web/build/out/runtime.ts  相容層
# 輸出：<根>/work/mt2-web/build/out/gen，量測寫到 stdout。
set -u
D=$1
OUT=build/out

temps() {
  # 溫度：CPU 最高、電池、機身（skin），以及 thermal status
  dumpsys thermalservice | awk '
    /Thermal Status:/ { status=$3 }
    /Current temperatures from HAL/ { f=1; next }
    /Current cooling devices/ { f=0 }
    f && /mType=0,/ { v=$0; sub(/.*mValue=/,"",v); sub(/,.*/,"",v); if (v+0>cpu) cpu=v+0 }
    f && /mType=2,/ { v=$0; sub(/.*mValue=/,"",v); sub(/,.*/,"",v); bat=v }
    f && /mType=3,/ { v=$0; sub(/.*mValue=/,"",v); sub(/,.*/,"",v); if (v+0>skin) skin=v+0 }
    END { printf "cpu_max=%s battery=%s skin_max=%s thermal_status=%s\n", cpu, bat, skin, status }'
}

cd "$D/work/mt2-web" || exit 1
rm -rf "$OUT/gen"
echo "before: $(temps)"
start=$(date +%s.%N)
env HOME="$D" TMPDIR="$D/tmp" DOTNET_CLI_TELEMETRY_OPTOUT=1 LD_PRELOAD="$D/libnotag.so" LD_LIBRARY_PATH="$D/ssl" \
  "$D/conv/CsPort" --out "$OUT" web '*' > "$D/tmp/stdout.txt" 2> "$D/tmp/stderr.txt" &
pid=$!
hwm=0; peak_cpu=0
while kill -0 $pid 2>/dev/null; do
  h=$(awk '/^VmHWM:/ {print $2}' /proc/$pid/status 2>/dev/null)
  [ -n "$h" ] && hwm=$h
  sleep 0.2
done
wait $pid; rc=$?
end=$(date +%s.%N)
echo "after:  $(temps)"
echo "exit=$rc"
awk -v s="$start" -v e="$end" 'BEGIN { printf "elapsed_s=%.2f\n", e-s }'
echo "VmHWM_kB=$hwm"
echo "files=$(find "$OUT/gen" -type f | wc -l)"
echo "bytes=$(find "$OUT/gen" -type f -exec stat -c %s {} + | awk '{b+=$1} END {print b}')"
