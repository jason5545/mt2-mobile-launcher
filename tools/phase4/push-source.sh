#!/usr/bin/env bash
# 裝啟動器、把原版推進它的私有空間 files/inbox/<資料夾名>（驗收用的路徑匯入；adb shell 要是 root，ANDROID_SERIAL 指定裝置）。
#   tools/phase4/push-source.sh <APK> <原版遊戲資料夾>
# 原版是 Jason 的遊戲檔：只放 app 的私有空間與 repo 外，做完刪掉（clean-phone.sh）。
# root 建的檔把擁有者與 SELinux 標籤改回 app（照 /data/data/<pkg>）。
set -euo pipefail
APK="${1:?usage: push-source.sh <APK> <game dir>}"
GAME="${2:?}"
PKG=org.mt2mobile.launcher
F=/data/data/$PKG/files
export COPYFILE_DISABLE=1
test "$(adb shell id -u | tr -d '\r')" = 0 || { echo "adb shell 要是 root" >&2; exit 2; }
adb install -r "$APK" | tail -1
N=$(basename "$GAME")
adb shell "rm -rf $F/inbox && mkdir -p $F/inbox/$N"
start=$(date +%s)
tar -C "$GAME" -cf - --no-mac-metadata . | adb exec-in "tar -xf - -C $F/inbox/$N"
echo "push_s=$(( $(date +%s) - start ))"
adb shell "U=\$(stat -c %u /data/data/$PKG); C=\$(stat -c %C /data/data/$PKG); chown -R \$U:\$U $F; chcon -R \$C $F
printf 'inbox_files='; find $F/inbox -type f | wc -l; printf 'appledouble='; find $F/inbox -name '._*' | wc -l"
echo "source=$F/inbox/$N"
