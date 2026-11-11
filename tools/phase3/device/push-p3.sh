#!/usr/bin/env bash
# 第 4 題：把一份 importer 輸出（Mac 的 A，或從手機拉回來的 B）的 assets、scenes、shaders 推給 p3 原型（APP，預設 com.jason5545.mt2native.p3），
# 推完在手機上算這三個資料夾的總雜湊，跟 Mac 這份的總雜湊比（算法同 manifest-phone.sh）。
#   [APP=<p3 原型的套件名>] tools/phase3/device/push-p3.sh <importer 輸出資料夾>
# 擁有者照 /data/data/<pkg> 的 uid 改：unity-native/android.sh 讀 /sdcard/Android/data/<APP_ID> 的 uid，
# 剛裝、沒開過的 app 那個資料夾是推檔時 root 建的，會讀到 0（第 0 步踩到，unity-port 不改）。
# 原版 *_Data 的那份（files/data）不動：p3 原型第一次用 android.sh --data 推過。
# KEEP_AWAKE=1：推檔期間每 20 秒送一個 Shift 鍵，螢幕不會逾時鎖住（原型要在解鎖的前景開機；不改任何設定）。
set -euo pipefail

SRC="${1:?usage: push-p3.sh <importer output dir>}"
APP=${APP:-com.jason5545.mt2native.p3}
R=/sdcard/Android/data/$APP/files
LOWER=/data/media/0/Android/data/$APP/files
export COPYFILE_DISABLE=1
test "$(adb shell id -u | tr -d '\r')" = 0 || { echo "adb shell 要是 root" >&2; exit 2; }

adb shell am force-stop $APP
adb shell "rm -rf $R/game && mkdir -p $R/game"
if [ "${KEEP_AWAKE:-}" = 1 ]; then
  ( while :; do adb shell input keyevent KEYCODE_SHIFT_LEFT; sleep 20; done ) &
  AWAKE=$!
  trap 'kill $AWAKE 2>/dev/null' EXIT
fi
start=$(date +%s)
tar -C "$SRC" -cf - --no-mac-metadata assets scenes shaders | adb exec-in "tar -xf - -C $R/game"
adb shell "touch $R/game/.complete"
echo "push_s=$(( $(date +%s) - start ))"
adb shell "U=\$(stat -c %u /data/data/$APP); chown -R \$U:1078 $R/game && chmod -R ug=rwX,o= $R/game"
adb shell "find $LOWER/game ! -user \$(stat -c %u /data/data/$APP) | wc -l" | sed 's/^/not_app_owned=/'

phone=$(adb shell "cd $LOWER/game && find ./assets ./scenes ./shaders -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum" | cut -d' ' -f1)
mac=$(cd "$SRC" && find ./assets ./scenes ./shaders -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256 | shasum -a 256 | cut -d' ' -f1)
n=$(cd "$SRC" && find ./assets ./scenes ./shaders -type f | wc -l | tr -d ' ')
echo "assets+scenes+shaders files=$n phone=$phone mac=$mac $([ "$phone" = "$mac" ] && echo same || echo DIFFERENT)"
[ "$phone" = "$mac" ]
