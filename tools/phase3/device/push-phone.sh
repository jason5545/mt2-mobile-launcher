#!/usr/bin/env bash
# 裝測試 app、推輸入到手機（ANDROID_SERIAL 指定裝置；adb shell 要是 root）：
#   [EXPORTS=<匯出設定.json>] tools/phase3/device/push-phone.sh <APK> <原版遊戲資料夾> <cs2ts.json>
# - 原版（含 Managed，patcher 直接讀 game/<名稱>_Data/Managed）用 tar 串流解到 app 的 files/game。
#   原版是 Jason 的遊戲檔：只放 app 的 files 與 repo 外，做完用 clean-phone.sh 刪掉。
# - importer 的設定檔照手機上的路徑產生（make-config.py），放 files/config/import.json；有 shaderExport 的（Diceomancer）指到 files/export-shaders。
#   charsetSources（相對 root 的資料夾）照原設定檔的 root 找到本機那份，推到 files/ 底下同一個相對路徑。
# - EXPORTS：AssetRipper 要跑哪幾份匯出（RunnerService.LoadExports 的格式，例如 dice-exports.json），放 files/config/exports.json；
#   沒給就刪掉，app 照 MT2 跑一份。
# - UnityNative.dll、redirects、patcher 打包在 APK 的 assets，app 執行時解出來。
# - run-phone.sh、manifest-phone.sh 推到 /data/local/tmp/mt2-phase3/。
# root 建的檔案與資料夾，擁有者與 SELinux 標籤改回 app（照 /data/data/<pkg>）。
set -euo pipefail

APK="${1:?usage: push-phone.sh <APK> <game dir> <cs2ts.json>}"
GAME="${2:?}"
CFG="${3:?}"
HERE="$(cd "$(dirname "$0")" && pwd)"
PKG=org.mt2mobile.phase3
F=/data/data/$PKG/files
REMOTE=/data/local/tmp/mt2-phase3
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
export COPYFILE_DISABLE=1

[ "$(ls -d "$GAME"/*_Data/Managed 2>/dev/null | wc -l | tr -d ' ')" = 1 ] || { echo "要剛好一個 *_Data/Managed：$GAME" >&2; exit 2; }
test "$(adb shell id -u | tr -d '\r')" = 0 || { echo "adb shell 要是 root" >&2; exit 2; }

adb install -r "$APK"
python3 "$HERE/make-config.py" "$CFG" "$WORK/import.json" --root "$F" --export "$F/export/ExportedProject" \
  --game "$F/game" --out "$F/out" --shader-work "$F/shaderwork" --shader-export "$F/export-shaders/ExportedProject" \
  --list-sources | tail -n +2 > "$WORK/sources.tsv"
adb shell "mkdir -p $REMOTE && rm -f $REMOTE/import.json $REMOTE/exports.json"
adb push "$WORK/import.json" "$REMOTE/import.json" >/dev/null
[ -n "${EXPORTS:-}" ] && adb push "$EXPORTS" "$REMOTE/exports.json" >/dev/null
adb push "$HERE/run-phone.sh" "$HERE/manifest-phone.sh" "$REMOTE/" >/dev/null

adb shell "set -e; rm -rf $F/game $F/config; mkdir -p $F/game $F/config $F/tmp; cp $REMOTE/import.json $F/config/import.json
  if [ -f $REMOTE/exports.json ]; then cp $REMOTE/exports.json $F/config/exports.json; fi"
while IFS=$'\t' read -r rel local; do
  echo "charsetSources: $rel ($(du -sk "$local" | cut -f1) kB)"
  adb shell "rm -rf '$F/$rel' && mkdir -p '$F/$rel'"
  tar -C "$local" -cf - --no-mac-metadata . | adb exec-in "tar -xf - -C '$F/$rel'"
done < "$WORK/sources.tsv"
start=$(date +%s)
tar -C "$GAME" -cf - --no-mac-metadata . | adb exec-in "tar -xf - -C $F/game"
echo "game_push_s=$(( $(date +%s) - start ))"
adb shell "set -e
U=\$(stat -c %u /data/data/$PKG)
C=\$(stat -c %C /data/data/$PKG)
chown -R \$U:\$U $F
chcon -R \$C $F
printf 'game_files='; find $F/game -type f | wc -l
printf 'game_kB='; du -sk $F/game | cut -f1
df -Pk /data | tail -1"
