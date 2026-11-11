#!/system/bin/sh
# Phase 1：由測試 app 的程序跑一次 converter，root 只在旁邊量測（不碰畫面）。
# 用法（手機上，建議 nohup，adb 斷了也不影響）：
#   nohup sh run-phone.sh [keep_tagging] > <log> 2>&1 &
# keep_tagging=1 時 launcher 不關 heap pointer tagging，只靠 manifest 屬性。
# 輸出：files/work/mt2-web/build/out/gen；量測寫到 stdout。
set -u
PKG=org.mt2mobile.phase1
F=/data/data/$PKG/files
KEEP=${1:-0}

temps() {
  # 溫度：CPU 最高、電池、機身（skin），以及 thermal status（同 Phase 0）
  dumpsys thermalservice | awk '
    /Thermal Status:/ { status=$3 }
    /Current temperatures from HAL/ { f=1; next }
    /Current cooling devices/ { f=0 }
    f && /mType=0,/ { v=$0; sub(/.*mValue=/,"",v); sub(/,.*/,"",v); if (v+0>cpu) cpu=v+0 }
    f && /mType=2,/ { v=$0; sub(/.*mValue=/,"",v); sub(/,.*/,"",v); bat=v }
    f && /mType=3,/ { v=$0; sub(/.*mValue=/,"",v); sub(/,.*/,"",v); if (v+0>skin) skin=v+0 }
    END { printf "cpu_max=%s battery=%s skin_max=%s thermal_status=%s\n", cpu, bat, skin, status }'
}

since=$(date '+%m-%d %H:%M:%S.000')
echo "before: $(temps)"
echo "loadavg: $(cat /proc/loadavg)"
am instrument -w -e mode convert -e keep_tagging "$KEEP" $PKG/.Runner > "$F/../am.txt" 2>&1 &
am=$!

# 等 converter 出現
pid=
i=0
while [ -z "$pid" ] && [ $i -lt 100 ]; do
  pid=$(pgrep -f "lib/arm64/libcsport.so" | head -1)
  [ -z "$pid" ] && { sleep 0.1; i=$((i+1)); }
done
if [ -n "$pid" ]; then
  echo "converter pid=$pid"
  ps -A -o LABEL,USER,PID,PPID,NAME | awk -v p="$pid" 'NR==1 || $3==p'
  ps -A -o LABEL,USER,PID,PPID,NAME | grep " $PKG\$"
  awk '/^(Uid|Gid):/' /proc/$pid/status
  hwm=0
  while kill -0 "$pid" 2>/dev/null; do
    h=$(awk '/^VmHWM:/ {print $2}' /proc/$pid/status 2>/dev/null)
    [ -n "$h" ] && hwm=$h
    sleep 0.2
  done
  echo "VmHWM_kB=$hwm"
else
  echo "converter 沒出現"
fi
wait $am
echo "after:  $(temps)"
echo "--- am instrument"
cat "$F/../am.txt"
echo "--- result.txt"
cat "$F/tmp/result.txt"
OUT=$F/work/mt2-web/build/out/gen
echo "files=$(find "$OUT" -type f 2>/dev/null | wc -l)"
echo "bytes=$(find "$OUT" -type f -exec stat -c %s {} + 2>/dev/null | awk '{b+=$1} END {print b+0}')"
echo "--- avc denied（本次、這個 app）"
logcat -d -T "$since" 2>/dev/null | grep 'avc:' | grep -F "app=$PKG" || echo "(無)"
