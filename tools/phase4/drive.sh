#!/usr/bin/env bash
# Mac 端：在手機上跑一次安裝，同時存整段 logcat（main、events：抓得到 lmkd 與 am_kill），結束後把結果拉回來。
#   [KILL_STEP=<步驟> KILL_SEC=<秒>] tools/phase4/drive.sh <名稱> <結果資料夾> [am start-foreground-service 的其他參數…]
#   例：tools/phase4/drive.sh full ~/mt2-phase4/run --es source /data/data/org.mt2mobile.launcher/files/inbox/game-win-2.2.1 --es keep-work 1
# run-phone.sh 用 nohup 在手機上跑（USB 斷線也不會中斷），這邊每 10 秒看一次 <名稱>.finished；adb 連不上就停。
# 前後記四個原型的 lastUpdateTime（不能碰的 app，要相同）。
set -uo pipefail
NAME="${1:?usage: drive.sh <name> <result dir> [am args…]}"
RES="${2:?}"; shift 2
HERE="$(cd "$(dirname "$0")" && pwd)"
REMOTE=/data/local/tmp/mt2-phase4
mkdir -p "$RES"
protos() { for p in com.jason5545.mt2native com.jason5545.diceomancer com.jason5545.mt2native.p3 com.jason5545.diceomancer.p3; do echo "$p $(adb shell dumpsys package $p | grep -m1 lastUpdateTime | tr -s ' ')"; done; }
protos > "$RES/$NAME.protos-before"
adb shell "mkdir -p $REMOTE"
adb push "$HERE/run-phone.sh" "$HERE/manifest-phone.sh" "$REMOTE/" >/dev/null
adb logcat -b main,events -v threadtime -T 1 > "$RES/$NAME.logcat" 2>&1 &
LOGCAT=$!
start=$(date +%s)
adb shell "rm -f $REMOTE/$NAME.finished; KILL_STEP='${KILL_STEP:-}' KILL_SEC='${KILL_SEC:-}' nohup sh $REMOTE/run-phone.sh $NAME $* > $REMOTE/console-$NAME.txt 2>&1 &"
sleep 5
while :; do
  r=$(adb shell "test -f $REMOTE/$NAME.finished && echo finished || echo running" 2>&1 | tr -d '\r')
  [ "$r" = finished ] && break
  if [ "$r" != running ]; then echo "adb 連不上：$r"; kill "$LOGCAT" 2>/dev/null; exit 5; fi
  sleep 10
done
echo "mac_wall_s=$(( $(date +%s) - start ))" > "$RES/$NAME.wall"
sleep 2
kill "$LOGCAT" 2>/dev/null
for s in preflight steps samples state.json install.json install.log crash killed start-service; do
  adb shell "test -f $REMOTE/$NAME.$s" && adb pull "$REMOTE/$NAME.$s" "$RES/$NAME.$s" >/dev/null
done
protos > "$RES/$NAME.protos-after"
adb pull "$REMOTE/console-$NAME.txt" "$RES/console-$NAME.txt" >/dev/null && cat "$RES/console-$NAME.txt"
diff "$RES/$NAME.protos-before" "$RES/$NAME.protos-after" >/dev/null && echo "protos unchanged" || echo "PROTOS CHANGED"
