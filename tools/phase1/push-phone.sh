#!/usr/bin/env bash
# 把 converter 的 DLL 與輸入放進測試 app 的 files 資料夾（開發機執行，手機端用 adb 的 root）。
# .so 不推：它們在 APK 的 lib/ 裡（build.sh），Runner 執行前在 conv/ 建 symlink。
# 用法：push-phone.sh <stage-inputs.sh 整理好的資料夾>
# 搬檔、改擁有者、改 SELinux 標籤用 root；converter 本身之後由 app 程序執行（run-phone.sh）。
set -euo pipefail

STAGE="${1:?用法：push-phone.sh <輸入資料夾>}"
ADB="${ADB:-/opt/android-sdk/platform-tools/adb}"
BUILD0="${BUILD0:-/tmp/mt2-phase0-build}"      # tools/phase0/build.sh 的輸出
PKG=org.mt2mobile.phase1
TMP=/data/local/tmp/mt2-p1x
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# 不需要的檔：靜態函式庫、.so（在 APK 裡）、apphost（app 裡用 launcher 代替）；輸入裡上一次的輸出
tar -C "$BUILD0/pub-bionic" --exclude '*.a' --exclude '*.so' --exclude ./CsPort -czf "$WORK/conv.tgz" .
tar -C "$STAGE" --exclude ./mt2-web/build/out/gen -czf "$WORK/work.tgz" .
ls -la "$WORK"

"$ADB" shell "mkdir -p $TMP"
"$ADB" push "$WORK/conv.tgz" "$WORK/work.tgz" "$TMP/"
"$ADB" shell "set -e
F=/data/data/$PKG/files
U=\$(stat -c %u /data/data/$PKG)
C=\$(stat -c %C \$F)   # 照抄 files 的標籤（含 MLS 類別），restorecon 會漏掉類別
rm -rf \$F/conv \$F/ssl \$F/work \$F/exec-test.sh
mkdir -p \$F/conv \$F/work \$F/tmp
tar -xzf $TMP/conv.tgz -C \$F/conv
tar -xzf $TMP/work.tgz -C \$F/work
chown -R \$U:\$U \$F
chcon -R \$C \$F
rm $TMP/conv.tgz $TMP/work.tgz
echo \"conv \$(find \$F/conv -type f | wc -l) 個檔（.so \$(find \$F/conv -name '*.so' | wc -l)）、work \$(find \$F/work -type f | wc -l)\"
ls -laZ \$F | head -8"
