#!/system/bin/sh
# 手機上用 root 跑（drive.sh 推到 /data/local/tmp/mt2-phase4/）：
#   [KILL_STEP=<步驟> KILL_SEC=<秒>] sh run-phone.sh <名稱> [am start-foreground-service 的其他參數…]
# 啟動 InstallService，每秒取樣；state.json 換步驟時先記下前一步最後的 VmHWM，再寫 /proc/<pid>/clear_refs 歸零（app 自己寫會被拒絕，
# 同 Phase 3），所以每步的 VmHWM 是這一步的峰值。每 5 秒另外記 VmRSS、df、thermal。
# KILL_STEP：進入那一步 KILL_SEC 秒後用 kill -9 結束 :install（第 4 題）。
# 結果放 /data/local/tmp/mt2-phase4/<名稱>.*（preflight、steps、samples、state.json、install.json、install.log、crash），最後建 <名稱>.finished。
set -u
PKG=org.mt2mobile.launcher
F=/data/data/$PKG/files
REMOTE=/data/local/tmp/mt2-phase4
NAME=$1; shift
OUT=$REMOTE/$NAME
STATE=$F/launcher/state.json
mkdir -p "$REMOTE"
rm -f "$OUT".*
thermal() { dumpsys thermalservice 2>/dev/null | grep -m1 'Thermal Status' | tr -d '\t'; }
dfk() { df -Pk /data | awk 'NR == 2 {print $4}'; }
stepof() { grep -o '"step": "[a-z]*"' "$STATE" 2>/dev/null | cut -d'"' -f4; }
statusof() { grep -o '"state": "[a-z-]*"' "$STATE" 2>/dev/null | cut -d'"' -f4; }
START=$(date '+%m-%d %H:%M:%S')
{
  echo "name=$NAME start=$START args=$*"
  echo "before_df_avail_kB=$(dfk)"
  echo "before_$(thermal)"
  dumpsys battery 2>/dev/null | grep -i temperature
} > "$OUT.preflight"
rm -f "$STATE"
am start-foreground-service -n "$PKG/.InstallService" "$@" > "$OUT.start-service" 2>&1
pid=; i=0
while [ -z "$pid" ] && [ "$i" -lt 100 ]; do pid=$(pidof "$PKG:install" 2>/dev/null | awk '{print $1}'); [ -n "$pid" ] || { sleep 0.1; i=$((i + 1)); }; done
[ -n "$pid" ] || { echo ":install 沒有起來" | tee "$OUT.crash"; touch "$OUT.finished"; exit 1; }
echo "install_pid=$pid" >> "$OUT.preflight"
step=; lasthwm=0; maxrss=0; mindf=$(dfk); t=0; stept=0
while [ -r "/proc/$pid/status" ]; do
  hwm=$(awk '/^VmHWM:/ {print $2}' "/proc/$pid/status" 2>/dev/null)
  rss=$(awk '/^VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null)
  [ -n "$hwm" ] && lasthwm=$hwm
  [ -n "$rss" ] && [ "$rss" -gt "$maxrss" ] && maxrss=$rss
  cur=$(stepof)
  if [ -n "$cur" ] && [ "$cur" != "$step" ]; then
    [ -n "$step" ] && echo "step=$step VmHWM_kB=$lasthwm max_sampled_VmRSS_kB=$maxrss" >> "$OUT.steps"
    echo 5 > "/proc/$pid/clear_refs"
    echo "$(date '+%H:%M:%S') t=${t}s step $step -> $cur, clear_refs (VmHWM was $lasthwm)" >> "$OUT.samples"
    step=$cur; maxrss=0; lasthwm=0; stept=0
  fi
  if [ -n "${KILL_STEP:-}" ] && [ "$step" = "$KILL_STEP" ] && [ "$stept" -ge "${KILL_SEC:-10}" ]; then
    echo "$(date '+%H:%M:%S') t=${t}s kill -9 ${pid}（$step 第 ${stept} 秒）" | tee -a "$OUT.samples" > "$OUT.killed"
    kill -9 "$pid"; sleep 1; break
  fi
  if [ $((t % 5)) -eq 0 ]; then
    d=$(dfk); [ "$d" -lt "$mindf" ] && mindf=$d
    echo "$(date '+%H:%M:%S') t=${t}s step=$step VmRSS_kB=$rss VmHWM_kB=$hwm df_avail_kB=$d $(thermal)" >> "$OUT.samples"
  fi
  case "$(statusof)" in running|"") ;; *) sleep 2; break ;; esac  # 服務做完會結束程序；萬一沒結束，狀態寫好了也算完
  sleep 1; t=$((t + 1)); stept=$((stept + 1))
done
[ -n "$step" ] && [ ! -f "$OUT.killed" ] && echo "step=$step VmHWM_kB=$lasthwm max_sampled_VmRSS_kB=$maxrss" >> "$OUT.steps"
{
  echo "after_df_avail_kB=$(dfk)"
  echo "min_df_avail_kB=$mindf"
  echo "after_$(thermal)"
  dumpsys battery 2>/dev/null | grep -i temperature
  echo "wall_s=$t"
} >> "$OUT.preflight"
cp "$STATE" "$OUT.state.json" 2>/dev/null
for j in $F/installs/*/install.json; do [ -f "$j" ] && cp "$j" "$OUT.install.json"; done
cp "$(ls -t $F/logs/install-*.log 2>/dev/null | head -1)" "$OUT.install.log" 2>/dev/null
S=$(statusof)
if [ "$S" != done ]; then
  logcat -d -b main,events,crash -v threadtime -T "$START" 2>/dev/null \
    | grep -iE 'lmkd|lowmemorykiller|am_kill|am_proc_died|mt2mobile|coreclr|DOTNET|FATAL' | tail -n 200 >> "$OUT.crash"
fi
echo "--- preflight"; cat "$OUT.preflight"
echo "--- steps"; cat "$OUT.steps" 2>/dev/null
echo "--- state"; cat "$OUT.state.json" 2>/dev/null
touch "$OUT.finished"
