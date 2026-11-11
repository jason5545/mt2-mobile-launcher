#!/usr/bin/env bash
# 第 2 題：從啟動器開遊戲（GameActivity，:game）跑到第 N 格，截圖、按 Home，拉回 files/logs/game.log（照 tools/phase3/device/run-p3.sh）。
#   [CLICKS=<自動點擊>] tools/phase4/run-game.sh <輸出前綴（截圖 <前綴>.png、log <前綴>.log.txt）> <存檔資料夾名> [格數，預設 900]
# 要驗收用的建置（TEST_ENTRY）才收 clicks 與 persistent。CLICKS 預設 MT2 的 400:0.5,0.5（第 400 格點標題畫面中央），給空字串就不點。
# EXTRA：多帶給 GameActivity 的 am start 參數（TEST_ENTRY 的診斷開關，例如 "--es tex-cpu all"、"--es texhash 1"，docs/perf-battle.md「B 段」）。
# 存檔資料夾名每次給新的：全新存檔，點擊後走的場景才固定（Phase 3 第 0 步）。
# 遊戲要在前景開機：手機鎖著（keyguard）就不跑；跑到一半鎖了就停下來（exit 4）。adb shell 要是 root（讀 app 私有空間的 log）。
set -uo pipefail
OUTP="${1:?usage: run-game.sh <out prefix> <save name> [frames]}"
SAVE="${2:?}"
AT="${3:-900}"
PKG=org.mt2mobile.launcher
CLICKS=${CLICKS-400:0.5,0.5}
LOG=/data/data/$PKG/files/logs/game.log
locked() { adb shell dumpsys window | grep -q "isKeyguardShowing=true"; }
gamepid() { adb shell pidof $PKG:game | tr -d '\r'; }

adb shell input keyevent KEYCODE_WAKEUP
sleep 1
if locked; then echo "手機鎖著，沒有跑"; exit 3; fi
p=$(gamepid); [ -n "$p" ] && adb shell kill "$p"
adb shell rm -f $LOG
start=$(date +%s)
# --activity-clear-task：上一次按 Home 之後程序被殺、task 還在時，不加這個會還原舊的 task、沿用上一次的 Intent（存檔名與 EXTRA 都不會換）
adb shell am start -W --activity-clear-task -n "$PKG/.GameActivity" ${CLICKS:+--es clicks "$CLICKS"} --es persistent "$SAVE" ${EXTRA:-} >/dev/null
for i in $(seq 1 30); do [ -n "$(gamepid)" ] && break; sleep 1; done
n=
for i in $(seq 1 300); do
  n=$(adb shell "grep -o '（[0-9]* 格' $LOG 2>/dev/null | tail -1" | tr -dc '0-9')
  [ -n "$n" ] && [ "$n" -ge "$AT" ] && break
  if [ -z "$(gamepid)" ]; then echo "遊戲沒在跑了（當掉？）"; break; fi
  if locked; then echo "跑到一半鎖了（第 ${n:-?} 格），停下來"; p=$(gamepid); [ -n "$p" ] && adb shell kill "$p"; exit 4; fi
  sleep 2
done
adb exec-out screencap -p > "$OUTP.png"
echo "截圖 $OUTP.png（第 ${n:-?} 格），$(( $(date +%s) - start )) 秒"
adb shell input keyevent KEYCODE_HOME
sleep 2
adb shell cat $LOG > "$OUTP.log.txt"
p=$(gamepid); [ -n "$p" ] && adb shell kill "$p"
[ -n "$n" ] && [ "$n" -ge "$AT" ]
