#!/usr/bin/env bash
# 編啟動器（驗收用的建置）、裝到手機、不開畫面地把五個分頁畫成 PNG，拉到 [輸出資料夾]（預設 ~/mt2-launcher-ui/snapshots）。
#   src/Launcher/snapshot-phone.sh [輸出資料夾]
# 手機用 ANDROID_SERIAL 指定；adb shell 要能 su（快照服務沒有 export）。建置輸出在 repo 外（LAUNCHER_OUT，預設 ~/mt2-phase4/apk）。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HOME/mt2-launcher-ui/snapshots}"
PKG=org.mt2mobile.launcher
# 快照服務只在驗收用的建置（TEST_ENTRY）；建置同 tools/phase4/build.sh（repo 內的引擎與 patcher）
B="${LAUNCHER_OUT:-$HOME/mt2-phase4/apk}"
TEST_ENTRY=1 "$HERE/../../tools/phase4/build.sh" "$B" | tail -1
adb install -r "$B/build/Release/net10.0-android/android-arm64/$PKG-Signed.apk" | tail -1
F=/data/data/$PKG/files/snapshots
adb shell su -c "rm -f $F/done.txt"
adb shell su -c "am start-foreground-service -n $PKG/$PKG.SnapshotService" >/dev/null
for _ in $(seq 1 60); do
  d=$(adb shell su -c "cat $F/done.txt" 2>/dev/null || true)
  [ -n "$d" ] && break
  sleep 1
done
echo "done: ${d:-timeout}"
mkdir -p "$OUT"
for n in 1 2 3 4 5; do
  adb shell su -c "cat $F/tab-$n.png" > "$OUT/tab-$n.png"
done
ls -la "$OUT"
