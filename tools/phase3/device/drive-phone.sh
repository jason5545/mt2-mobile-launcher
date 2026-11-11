#!/usr/bin/env bash
# Mac 端：在手機上跑一次測試 app，跑的時候另外存整段 logcat（main、events：抓得到 lmkd 與 am_kill），結束後把結果拉回來。
#   tools/phase3/device/drive-phone.sh <patch|export|import|shaders|pipeline> <結果資料夾>
# run-phone.sh 用 nohup 在手機上跑（USB 斷線也不會中斷），這邊每 10 秒看一次它的 <mode>.finished。
# 不用 pgrep -f 判斷：adb shell 的那個 sh 自己的命令列就含同一個字串，永遠比對得到。adb 連不上（例如 USB 斷線）就停下來。
# 結束時記 thermal status 與兩個原型的 lastUpdateTime（不能碰的 app，前後要相同）。
set -uo pipefail

MODE="${1:?usage: drive-phone.sh <mode> <result dir>}"
RES="${2:?}"
REMOTE=/data/local/tmp/mt2-phase3
mkdir -p "$RES"

protos() { for p in com.jason5545.mt2native com.jason5545.diceomancer; do echo "$p $(adb shell dumpsys package $p | grep -m1 lastUpdateTime | tr -s ' ')"; done; }
protos > "$RES/$MODE.protos-before"
adb logcat -b main,events -v threadtime -T 1 > "$RES/$MODE.logcat" 2>&1 &
LOGCAT=$!
start=$(date +%s)
adb shell "rm -f $REMOTE/$MODE.finished; nohup sh $REMOTE/run-phone.sh $MODE > $REMOTE/console-$MODE.txt 2>&1 &"
sleep 5
while :; do
  r=$(adb shell "test -f $REMOTE/$MODE.finished && echo finished || echo running" 2>&1 | tr -d '\r')
  [ "$r" = finished ] && break
  if [ "$r" != running ]; then echo "adb 連不上：$r"; kill "$LOGCAT" 2>/dev/null; exit 5; fi
  sleep 10
done
echo "wall_s=$(( $(date +%s) - start ))" > "$RES/$MODE.wall"
sleep 3
kill "$LOGCAT" 2>/dev/null
for s in preflight steps samples runner.log complete crash start-service; do
  adb shell "test -f $REMOTE/$MODE.$s" && adb pull "$REMOTE/$MODE.$s" "$RES/$MODE.$s" >/dev/null
done
protos > "$RES/$MODE.protos-after"
adb shell dumpsys thermalservice | grep -m1 "Thermal Status" > "$RES/$MODE.thermal-after"
adb pull "$REMOTE/console-$MODE.txt" "$RES/console-$MODE.txt" >/dev/null && cat "$RES/console-$MODE.txt"
diff "$RES/$MODE.protos-before" "$RES/$MODE.protos-after" && echo "protos unchanged"
grep -q 'exit=0 ' "$RES/$MODE.complete" 2>/dev/null
