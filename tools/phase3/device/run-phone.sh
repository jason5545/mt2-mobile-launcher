#!/system/bin/sh
# 手機上用 root 跑（push-phone.sh 推到 /data/local/tmp/mt2-phase3/）：
#   sh run-phone.sh <patch|export|import|shaders|pipeline>
# 啟動測試 app 的 foreground service，每秒取樣一次；app 換步驟（files/tmp/step.txt）時先記下前一步最後的 VmHWM，
# 再寫 /proc/<pid>/clear_refs 歸零（app 自己寫會被拒絕），之後的 VmHWM 就是這一步的峰值。每 5 秒另外記 VmRSS、df、thermal。
# 結果放 /data/local/tmp/mt2-phase3/<mode>.*：runner.log、complete、samples、steps（每步的 VmHWM 與取樣到的最大 VmRSS）、crash；
# 全部寫完最後建 <mode>.finished（drive-phone.sh 等這個檔）。
set -u

PKG=org.mt2mobile.phase3
F=/data/data/$PKG/files
REMOTE=/data/local/tmp/mt2-phase3
MODE=${1:-pipeline}
COMPLETE=$F/tmp/complete.txt
STEPF=$F/tmp/step.txt
OUT=$REMOTE/$MODE

mkdir -p "$REMOTE"
# root 建的資料夾要把擁有者與 SELinux 標籤改回 app，不然 app 寫不進去（Phase 2 的 run-export-phone.sh 踩過）
U=$(stat -c %u /data/data/$PKG)
C=$(stat -c %C /data/data/$PKG)
mkdir -p "$F/tmp"
chown "$U:$U" "$F" "$F/tmp"
chcon "$C" "$F" "$F/tmp"
rm -f "$COMPLETE" "$STEPF" "$OUT".preflight "$OUT".start-service "$OUT".steps "$OUT".samples "$OUT".runner.log "$OUT".complete "$OUT".crash "$OUT".finished

thermal() { dumpsys thermalservice 2>/dev/null | grep -m1 'Thermal Status' | tr -d '\t'; }
dfk() { df -Pk /data | awk 'NR == 2 {print $4}'; }

START=$(date '+%m-%d %H:%M:%S')
{
  echo "mode=$MODE start=$START"
  echo "before_df_avail_kB=$(dfk)"
  echo "before_$(thermal)"
  dumpsys battery 2>/dev/null | grep -i temperature
} > "$OUT.preflight"

am start-foreground-service -n "$PKG/.RunnerService" --es mode "$MODE" > "$OUT.start-service" 2>&1

pid=
i=0
while [ -z "$pid" ] && [ "$i" -lt 100 ]; do
  pid=$(pidof "$PKG" 2>/dev/null | awk '{print $1}')
  [ -n "$pid" ] || { sleep 0.1; i=$((i + 1)); }
done
if [ -z "$pid" ]; then
  echo "runner process did not appear" | tee "$OUT.crash"
  exit 1
fi
echo "runner_pid=$pid" >> "$OUT.preflight"

step=
lasthwm=0
maxrss=0
mindf=$(dfk)
t=0
while [ ! -f "$COMPLETE" ]; do
  if [ ! -r "/proc/$pid/status" ]; then
    echo "runner process exited before completion marker (t=${t}s step=$step)" >> "$OUT.crash"
    break
  fi
  hwm=$(awk '/^VmHWM:/ {print $2}' "/proc/$pid/status" 2>/dev/null)
  rss=$(awk '/^VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null)
  [ -n "$hwm" ] && lasthwm=$hwm
  [ -n "$rss" ] && [ "$rss" -gt "$maxrss" ] && maxrss=$rss
  cur=$(cat "$STEPF" 2>/dev/null)
  if [ -n "$cur" ] && [ "$cur" != "$step" ]; then
    [ -n "$step" ] && echo "step=$step VmHWM_kB=$lasthwm max_sampled_VmRSS_kB=$maxrss" >> "$OUT.steps"
    echo 5 > "/proc/$pid/clear_refs"
    echo "$(date '+%H:%M:%S') t=${t}s step $step -> $cur, clear_refs (VmHWM was $lasthwm)" >> "$OUT.samples"
    step=$cur
    maxrss=0
    lasthwm=0
  fi
  if [ $((t % 5)) -eq 0 ]; then
    d=$(dfk)
    [ "$d" -lt "$mindf" ] && mindf=$d
    echo "$(date '+%H:%M:%S') t=${t}s step=$step VmRSS_kB=$rss VmHWM_kB=$hwm df_avail_kB=$d $(thermal)" >> "$OUT.samples"
  fi
  sleep 1
  t=$((t + 1))
done
[ -n "$step" ] && echo "step=$step VmHWM_kB=$lasthwm max_sampled_VmRSS_kB=$maxrss" >> "$OUT.steps"

{
  echo "after_df_avail_kB=$(dfk)"
  echo "min_df_avail_kB=$mindf"
  echo "after_$(thermal)"
  dumpsys battery 2>/dev/null | grep -i temperature
} >> "$OUT.preflight"
cp "$F/tmp/runner.log" "$OUT.runner.log" 2>/dev/null
cp "$COMPLETE" "$OUT.complete" 2>/dev/null
if [ ! -f "$OUT.complete" ]; then
  logcat -d -b main,events -v threadtime -T "$START" 2>/dev/null \
    | grep -iE 'lmkd|lowmemorykiller|am_kill|am_proc_died|org\.mt2mobile\.phase3|coreclr|DOTNET' \
    | tail -n 200 >> "$OUT.crash"
fi

echo "--- preflight"; cat "$OUT.preflight"
echo "--- steps"; cat "$OUT.steps" 2>/dev/null
echo "--- result"; cat "$OUT.complete" 2>/dev/null
echo "--- crash"; cat "$OUT.crash" 2>/dev/null
touch "$OUT.finished"
# 完成標記沒有 BOM；照樣不錨定行首
grep -q 'exit=0 ' "$OUT.complete" 2>/dev/null
