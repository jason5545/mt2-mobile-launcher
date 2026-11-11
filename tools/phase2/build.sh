#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
INPUT_DIR="${1:-$HOME/mt2-phase2/input}"
ASSETRIPPER_DIR="${2:-}"
OUT="${PHASE2_BUILD_OUT:-$HOME/mt2-phase2/apk}"
ART="$OUT/artifacts"
PATCHER_DIR="$ART/bin/patcher/release"

test -f "$INPUT_DIR/UnityNative.dll" || { echo "missing $INPUT_DIR/UnityNative.dll" >&2; exit 2; }
mkdir -p "$OUT"
dotnet build "$REPO/src/Patcher/patcher.csproj" -c Release --nologo --artifacts-path "$ART"

# Release emits the CoreCLR assembly store needed to find System.Private.CoreLib.dll.
# Keep trimming disabled in phase2.csproj because the runner loads both test libraries by reflection.
args=(publish "$HERE/phase2.csproj" -c Release -f net10.0-android -r android-arm64
  -p:Phase2InputDir="$INPUT_DIR"
  -p:Phase2PatcherDir="$PATCHER_DIR"
  -p:BaseOutputPath="$OUT/build/"
  )
if [[ -n "$ASSETRIPPER_DIR" ]]; then
  args+=("-p:Phase2AssetRipperDir=$ASSETRIPPER_DIR")
fi
args+=(-o "$OUT/publish")

dotnet "${args[@]}"
find "$OUT" -maxdepth 2 -type f -name '*.apk' -print -exec stat -c 'bytes=%s' {} \;
