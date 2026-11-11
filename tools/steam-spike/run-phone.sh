#!/usr/bin/env bash
# 編 steam-spike、裝到手機、用前景服務跑一次（不開畫面），印出 files/spike.log。
#   tools/steam-spike/run-phone.sh
# 手機用 ANDROID_SERIAL 指定（沒給就用 adb 預設的那一台）；adb shell 要能 su 讀 app 的 files/。
# 建置輸出放 repo 外的 ~/mt2-steam-spike。log 裡 CDN 的主機名稱換成 <cdn>。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${STEAM_SPIKE_OUT:-$HOME/mt2-steam-spike}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build "$HERE/steam-spike.csproj" -c Release -p:BaseOutputPath="$OUT/build/" -p:IntermediateOutputPath="$OUT/obj/" \
  ${ANDROID_HOME:+-p:AndroidSdkDirectory="$ANDROID_HOME"} -v q -clp:NoSummary
APK="$OUT/build/Release/net10.0-android/android-arm64/org.mt2mobile.steamspike-Signed.apk"
adb install -r "$APK" | tail -1
F=/data/data/org.mt2mobile.steamspike/files
adb shell su -c "rm -f $F/spike-done.txt"
adb shell am force-stop org.mt2mobile.steamspike
adb shell am start-foreground-service -n org.mt2mobile.steamspike/.SpikeService >/dev/null
for _ in $(seq 1 60); do
  done_txt=$(adb shell su -c "cat $F/spike-done.txt" 2>/dev/null || true)
  [ -n "$done_txt" ] && break
  sleep 2
done
adb shell su -c "cat $F/spike.log" | sed -E 's/((H|h)ost[ =:]+)[^ ,]+/\1<cdn>/g'
echo "DONE ${done_txt:-timeout}"
