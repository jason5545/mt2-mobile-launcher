#!/usr/bin/env bash
# 編 Phase 3 的測試 app（org.mt2mobile.phase3）。
#   UNITY_NATIVE_DLL=<hand DLL> tools/phase3/device/build.sh [輸出資料夾，預設 ~/mt2-phase3/device/apk]
# 先要有：native/*/build.sh android 的 .so、src/UnityNative 編出的 hand DLL。
# patcher 從 src/Patcher 用 Release 編，managed 輸出放進 APK 的 assets。
# MT2 的 redirects 用 src/Glue.*；其他遊戲可另外給 ENGINE_REDIRECTS、GAME_REDIRECTS。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
OUT="${1:-$HOME/mt2-phase3/device/apk}"
ART="$OUT/artifacts"
: "${UNITY_NATIVE_DLL:?先指定 UNITY_NATIVE_DLL=<src/UnityNative 編出的 hand DLL>}"
ENGINE_REDIRECTS="${ENGINE_REDIRECTS:-$REPO/src/Glue.Facepunch/redirects.txt}"
GAME_REDIRECTS="${GAME_REDIRECTS:-$REPO/src/Glue.MT2/redirects.txt}"
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet-android/sdk}
export PATH=$DOTNET_ROOT:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet build "$REPO/src/Patcher/patcher.csproj" -c Release --nologo -v q -clp:NoSummary --artifacts-path "$ART"
# obj 也放輸出資料夾：換輸入時 AndroidAsset 的連結名稱不變，共用 obj 的增量建置不會重新複製
P=(
  -p:Phase3PatcherDir="$ART/bin/patcher/release"
  -p:UnityNativeDll="$UNITY_NATIVE_DLL"
  -p:EngineRedirects="$ENGINE_REDIRECTS"
  -p:GameRedirects="$GAME_REDIRECTS"
)
dotnet build "$HERE/phase3.csproj" -c Release --nologo -p:BaseOutputPath="$OUT/build/" -p:IntermediateOutputPath="$OUT/obj/" "${P[@]}" -v q -clp:NoSummary
APK="$OUT/build/Release/net10.0-android/android-arm64/org.mt2mobile.phase3-Signed.apk"
echo "APK $APK bytes=$(stat -f %z "$APK")"
unzip -l "$APK" | awk '/\.so$|phase3\/(input|redirects)\// {print $1, $4}'
