#!/system/bin/sh
set -u

PKG=org.mt2mobile.phase2
F=/data/data/$PKG/files
REMOTE=/data/local/tmp/mt2-phase2
MODE=${1:-decompile}

case "$MODE" in
  decompile) NO_DECOMPILE=false ;;
  nodecompile) NO_DECOMPILE=true ;;
  *) echo "usage: run-export-phone.sh [decompile|nodecompile]"; exit 2 ;;
esac

INPUT=$F/game
OUTPUT=$F/export-$MODE
COMPLETE=$F/tmp/complete.txt

if [ ! -d "$INPUT/MonsterTrain2_Data" ]; then
  echo "missing original game under $INPUT"
  exit 2
fi

mkdir -p "$REMOTE" "$F/tmp"
# 這支用 root 跑：剛裝好的 app 沒有 files/tmp 時，root 建的資料夾 app 寫不進去，runner 寫不了 log 與完成標記，會一直等到逾時
U=$(stat -c %u /data/data/$PKG)
C=$(stat -c %C /data/data/$PKG)
chown "$U:$U" "$F" "$F/tmp"
chcon "$C" "$F" "$F/tmp"
rm -rf "$OUTPUT"
rm -f "$COMPLETE" "$REMOTE/export-$MODE.log" "$REMOTE/export-$MODE.complete" \
  "$REMOTE/export-$MODE.samples" "$REMOTE/export-$MODE.loaded-so.all" \
  "$REMOTE/export-$MODE.loaded-so" "$REMOTE/export-$MODE.start" \
  "$REMOTE/export-$MODE.preflight" "$REMOTE/export-$MODE.start-service" \
  "$REMOTE/export-$MODE.identity" "$REMOTE/export-$MODE.crash"

START=$(date '+%m-%d %H:%M:%S')
echo "$START" > "$REMOTE/export-$MODE.start"
{
  echo "mode=$MODE"
  echo "before_df:"
  df -Pk /data
  echo "before_thermal:"
  dumpsys thermalservice 2>&1 | grep -iE 'thermal status|current thermal|mStatus|status' | head -10
  echo "before_battery:"
  dumpsys battery 2>&1 | grep -i temperature
} > "$REMOTE/export-$MODE.preflight"

am start-foreground-service -n "$PKG/.RunnerService" \
  --es mode export \
  --es input "$INPUT" \
  --es output "$OUTPUT" \
  --ez no_decompile "$NO_DECOMPILE" > "$REMOTE/export-$MODE.start-service" 2>&1

pid=
i=0
while [ -z "$pid" ] && [ "$i" -lt 100 ]; do
  pid=$(pidof "$PKG" 2>/dev/null | awk '{print $1}')
  [ -n "$pid" ] || { sleep 0.1; i=$((i + 1)); }
done

if [ -z "$pid" ]; then
  echo "runner process did not appear" | tee "$REMOTE/export-$MODE.crash"
  logcat -d -b all -v time -T "$START" 2>/dev/null \
    | grep -iE 'lmkd|lowmemorykiller|out of memory|org\.mt2mobile\.phase2|coreclr' \
    | tail -n 160 >> "$REMOTE/export-$MODE.crash"
  cat "$REMOTE/export-$MODE.crash"
  exit 1
fi

{
  echo "runner_pid=$pid"
  ps -AZ | awk -v p="$pid" 'NR == 1 || $3 == p'
  awk '/^(Uid|Gid):/' "/proc/$pid/status"
} > "$REMOTE/export-$MODE.identity"
cat "$REMOTE/export-$MODE.identity"

elapsed=0
while [ ! -f "$COMPLETE" ] && [ "$elapsed" -lt 3600 ]; do
  if [ ! -r "/proc/$pid/status" ]; then
    echo "runner process exited before completion marker" >> "$REMOTE/export-$MODE.crash"
    break
  fi

  {
    echo "sample_time=$(date '+%m-%d %H:%M:%S') elapsed_s=$elapsed"
    rss=$(awk '/^VmRSS:/ {print $2}' "/proc/$pid/status")
    hwm=$(awk '/^VmHWM:/ {print $2}' "/proc/$pid/status")
    echo "pid=$pid VmRSS_kB=$rss VmHWM_kB=$hwm"
    df -Pk /data
    dumpsys thermalservice 2>/dev/null | grep -iE 'thermal status|current thermal|mStatus|status' | head -10
    dumpsys battery 2>/dev/null | grep -i temperature
    awk '$NF ~ /\.so/ {print $NF}' "/proc/$pid/maps" 2>/dev/null >> "$REMOTE/export-$MODE.loaded-so.all"
  } >> "$REMOTE/export-$MODE.samples"
  sleep 5
  elapsed=$((elapsed + 5))
done

if [ -f "$COMPLETE" ]; then
  cp "$COMPLETE" "$REMOTE/export-$MODE.complete"
  cp "$F/tmp/runner.log" "$REMOTE/export-$MODE.log"
  sort -u "$REMOTE/export-$MODE.loaded-so.all" 2>/dev/null > "$REMOTE/export-$MODE.loaded-so" || true
  {
    echo "after_df:"
    df -Pk /data
    echo "after_thermal:"
    dumpsys thermalservice 2>&1 | grep -iE 'thermal status|current thermal|mStatus|status' | head -10
    echo "after_battery:"
    dumpsys battery 2>&1 | grep -i temperature
    echo "output_files=$(find "$OUTPUT" -type f | wc -l)"
    du -sk "$OUTPUT"
  } >> "$REMOTE/export-$MODE.samples"
else
  logcat -d -b all -v time -T "$START" 2>/dev/null \
    | grep -iE 'lmkd|lowmemorykiller|out of memory|org\.mt2mobile\.phase2|coreclr' \
    | tail -n 160 >> "$REMOTE/export-$MODE.crash"
fi

echo "--- preflight"
cat "$REMOTE/export-$MODE.preflight"
echo "--- result"
cat "$REMOTE/export-$MODE.complete" 2>/dev/null || true
echo "--- runner log"
cat "$REMOTE/export-$MODE.log" 2>/dev/null || true
echo "--- samples"
cat "$REMOTE/export-$MODE.samples" 2>/dev/null || true
echo "--- loaded .so"
cat "$REMOTE/export-$MODE.loaded-so" 2>/dev/null || true
echo "--- lmkd / OOM / process log"
cat "$REMOTE/export-$MODE.crash" 2>/dev/null || true

# complete.txt 開頭有 UTF-8 BOM（RunnerService 用 Encoding.UTF8 寫），不能用 ^ 錨定
grep -q 'exit=0 ' "$REMOTE/export-$MODE.complete" 2>/dev/null
