#!/usr/bin/env bash
# 第 4 題：開 p3 原型跑到第 N 格，截圖、按 Home，拉回 files/log.txt（不重建、不重裝、不推資料；照 unity-native/android.sh 的執行段）。
#   [APP=<p3 原型的套件名>] [CLICKS=<自動點擊>] tools/phase3/device/run-p3.sh <輸出前綴（截圖 <前綴>.png、log <前綴>.log.txt）> <存檔資料夾名> [格數，預設 900]
# APP 預設 com.jason5545.mt2native.p3；CLICKS 是遊戲 android.env 的 AUTO_CLICKS，預設 MT2 的 400:0.5,0.5，給空字串就不點（Diceomancer 沒設）。
# 存檔資料夾名每次給新的（--es persistent）：全新存檔，點擊後走的場景才固定（第 0 步）。
# 原型要在前景開機：手機鎖著（keyguard）就不跑；跑到一半鎖了就停下來（exit 4），不等逾時。
set -uo pipefail

OUTP="${1:?usage: run-p3.sh <out prefix> <save name> [frames]}"
SAVE="${2:?}"
AT="${3:-900}"
APP=${APP:-com.jason5545.mt2native.p3}
CLICKS=${CLICKS-400:0.5,0.5}
R=/sdcard/Android/data/$APP/files
locked() { adb shell dumpsys window | grep -q "isKeyguardShowing=true"; }

adb shell input keyevent KEYCODE_WAKEUP
sleep 1
if locked; then echo "手機鎖著，沒有跑"; exit 3; fi
adb shell am force-stop $APP
adb shell rm -f $R/log.txt
start=$(date +%s)
adb shell am start -W -n "$APP/unitynative.MainActivity" ${CLICKS:+--es clicks "$CLICKS"} --es persistent "$SAVE" >/dev/null
for i in $(seq 1 30); do adb shell pidof $APP >/dev/null && break; sleep 1; done
n=
for i in $(seq 1 300); do
  n=$(adb shell "grep -o '（[0-9]* 格' $R/log.txt 2>/dev/null | tail -1" | tr -dc '0-9')
  [ -n "$n" ] && [ "$n" -ge "$AT" ] && break
  if ! adb shell pidof $APP >/dev/null; then echo "app 沒在跑了（當掉？）"; break; fi
  if locked; then echo "跑到一半鎖了（第 ${n:-?} 格），停下來"; adb shell am force-stop $APP; exit 4; fi
  sleep 2
done
adb exec-out screencap -p > "$OUTP.png"
echo "截圖 $OUTP.png（第 ${n:-?} 格），$(( $(date +%s) - start )) 秒"
adb shell input keyevent KEYCODE_HOME
sleep 2
adb shell cat $R/log.txt > "$OUTP.log.txt"
adb shell am force-stop $APP
[ -n "$n" ] && [ "$n" -ge "$AT" ]
