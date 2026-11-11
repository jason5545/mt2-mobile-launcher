#!/usr/bin/env bash
# 編啟動器的 APK（src/Launcher）與它需要的 patcher、UnityNative.dll、轉接組件。
#   GAME_DIR=<原版遊戲資料夾>[:<另一版的資料夾>…] [TEST_ENTRY=1] tools/phase4/build.sh [輸出資料夾，預設 ~/mt2-phase4/apk]
# 原版只從 GAME_DIR 讀；所有編譯與 patch 輸出都放在輸出資料夾，不進 repo。
# targets/*.json 每個 target 都要在 GAME_DIR 裡找到 Managed 對得上的那一版（用 : 分隔），patch-expect.json 才有每個 target 的期望值。
# 引擎、轉接組件拿最新那版（version 最大）patch 出來的參考組件編：2.2.1 與 2.3.0 只差 Assembly-CSharp，編出來的 IL 相同（docs/phase6.md「2.3.0」）。
# 1. 編 src/Patcher，對原版做一次初始 patch（編引擎用的參考組件）。
# 2. 編 src/UnityNative，再帶這份 hand DLL 從每一版原版重 patch，算手機要比對的 patch-expect.json。
# 3. 編 src/Glue.* 與 APK。TEST_ENTRY=1 帶驗收入口，發布用的不要帶。
# RELEASE=1：用官方 release keystore 簽（預設 ~/.config/mt2-mobile-launcher/release.jks，RELEASE_KEYSTORE= 可換；
#   密碼從 MT2_RELEASE_KS_PASS 讀；沒設時 macOS 讀 Keychain 的 mt2-mobile-launcher-release-keystore，其他系統讀
#   ~/.config/mt2-mobile-launcher/release.pass（權限 600，第一行是密碼，RELEASE_KS_PASS_FILE= 可換）。不帶就是這台機器的 debug keystore。
# 需要：native/*/build.sh android 的 .so。AssetRipper 的原始碼由 third_party/AssetRipper/fetch.sh 抓上游加 patch，這裡會先跑一次（抓過就不動）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$HOME/mt2-phase4/apk}"
: "${GAME_DIR:?先指定 GAME_DIR=<原版遊戲資料夾>（含 MonsterTrain2_Data/Managed）}"
SIGN=()
if [ "${RELEASE:-}" = 1 ]; then
  [ "${TEST_ENTRY:-}" = 1 ] && { echo "RELEASE=1 不能跟 TEST_ENTRY=1 一起用：驗收入口不能進官方簽章的 APK" >&2; exit 2; }
  KS="${RELEASE_KEYSTORE:-$HOME/.config/mt2-mobile-launcher/release.jks}"
  [ -f "$KS" ] || { echo "找不到 release keystore：${KS}" >&2; exit 2; }
  if [ -z "${MT2_RELEASE_KS_PASS:-}" ]; then
    if command -v security >/dev/null; then
      MT2_RELEASE_KS_PASS="$(security find-generic-password -s mt2-mobile-launcher-release-keystore -w)" || { echo "Keychain 讀不到 release keystore 的密碼，改用 MT2_RELEASE_KS_PASS=" >&2; exit 2; }
    else
      PASS_FILE="${RELEASE_KS_PASS_FILE:-$HOME/.config/mt2-mobile-launcher/release.pass}"
      [ -f "$PASS_FILE" ] || { echo "找不到 release keystore 的密碼檔：${PASS_FILE}（或改用 MT2_RELEASE_KS_PASS=）" >&2; exit 2; }
      MT2_RELEASE_KS_PASS="$(head -n 1 "$PASS_FILE")"
    fi
  fi
  export MT2_RELEASE_KS_PASS
  SIGN=(-p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$KS" -p:AndroidSigningKeyAlias=mt2-release
    -p:AndroidSigningStorePass=env:MT2_RELEASE_KS_PASS -p:AndroidSigningKeyPass=env:MT2_RELEASE_KS_PASS)
fi
ART="$OUT/artifacts"
PATCHER="$ART/bin/patcher/release"
HAND="$ART/bin/UnityNative/release"
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet-android/sdk}
export PATH=$DOTNET_ROOT:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p "$OUT"
# 建置紀錄（source-commit.txt）只在 APK 編好之後才寫；先刪掉舊的，建置失敗就不會留下可以拿去打包的紀錄
rm -f "$OUT/source-commit.txt"
COMMIT="$(git -C "$REPO" rev-parse HEAD)"
[ -z "$(git -C "$REPO" status --porcelain)" ] && STATE=clean || STATE=dirty
"$REPO/third_party/AssetRipper/fetch.sh"

dotnet build "$REPO/src/Patcher/patcher.csproj" -c Release --nologo -v q -clp:NoSummary --artifacts-path "$ART"

# 每個 target 配一份 Managed 對得上的原版，不然期望值沒有意義。印出「target<TAB>資料夾」，照 version 由舊到新，最後一行是最新的
PAIRS="$OUT/targets.tsv"
python3 -I - "$REPO/targets" "$GAME_DIR" > "$PAIRS" <<'PY'
import hashlib, json, sys
from pathlib import Path
targets = sorted((json.load(open(p, encoding="utf-8")) for p in Path(sys.argv[1]).glob("*.json")),
                 key=lambda t: tuple(int(x) for x in t["version"].split(".")))
dirs = [Path(d) for d in sys.argv[2].split(":") if d]
def managed(d, data):
    m = d / data / "Managed"
    return {p.relative_to(m).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in m.rglob("*") if p.is_file()} if m.is_dir() else None
cache = {}
missing = []
for t in targets:
    hit = [d for d in dirs if cache.setdefault((d, t["dataDir"]), managed(d, t["dataDir"])) == t["managed"]]
    if not hit:
        missing.append(t["id"]); continue
    print(f"{t['id']}\t{hit[0]}")
    print(f"原版 Managed 對得上 {t['id']}（{len(t['managed'])} 個檔）：{hit[0]}", file=sys.stderr)
if missing:
    sys.exit(f"GAME_DIR 裡找不到對得上的原版：{'、'.join(missing)}（每個 target 都要一份，用 : 分隔）")
PY
LATEST_DIR="$(tail -1 "$PAIRS" | cut -f2)"

PO="$OUT/managed"
REDIRECTS=("$REPO/src/Glue.Facepunch/redirects.txt" "$REPO/src/Glue.MT2/redirects.txt")
rm -rf "$PO"; mkdir -p "$PO"
dotnet "$PATCHER/patcher.dll" "$LATEST_DIR/MonsterTrain2_Data/Managed" "$PO" "${REDIRECTS[@]}" | tail -1

DEFINES=$(python3 -I - "$PO" <<'PY'
from pathlib import Path
import sys
root=Path(sys.argv[1])
data=b''.join(p.read_bytes() for p in root.glob('UnityEngine*.dll'))
print('%3B'.join('NO_'+name for name in ('InstantiateParameters','ParticleSystemBakeMeshOptions') if name.encode() not in data))
PY
)
dotnet build "$REPO/src/UnityNative/UnityNative.csproj" -c Release --nologo -v q -clp:NoSummary --artifacts-path "$ART" -p:ManagedDir="$PO" -p:UnityDefines="$DEFINES"

# 每一版各 patch 一次（帶 hand DLL）算期望值；最新那版最後做，留在 $PO 給轉接組件與 APK 參考
EXPECT="{"
while IFS=$'\t' read -r tid tdir; do
  rm -rf "$PO"; mkdir -p "$PO"
  dotnet "$PATCHER/patcher.dll" "$tdir/MonsterTrain2_Data/Managed" "$PO" --hand "$HAND/UnityNative.dll" "${REDIRECTS[@]}" | tail -1
  n=$(cd "$PO" && find . -type f | wc -l | tr -d ' ')
  total=$(cd "$PO" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256 | shasum -a 256 | cut -d' ' -f1)
  [ "$EXPECT" = "{" ] || EXPECT="$EXPECT, "
  EXPECT="$EXPECT\"$tid\": {\"files\": $n, \"total\": \"$total\"}"
  echo "patch 期望值：$tid files=$n total=$total"
done < "$PAIRS"
printf '%s}\n' "$EXPECT" > "$OUT/patch-expect.json"

dotnet build "$REPO/src/Glue.Facepunch/Glue.Facepunch.csproj" -c Release --nologo -v q -clp:NoSummary --artifacts-path "$ART" -p:ManagedDir="$PO"
dotnet build "$REPO/src/Glue.MT2/Glue.MT2.csproj" -c Release --nologo -v q -clp:NoSummary --artifacts-path "$ART" -p:ManagedDir="$PO" -p:HandDir="$HAND"

P=(
  -p:PatchExpect="$OUT/patch-expect.json"
  -p:PatcherDir="$PATCHER"
  -p:UnityNativeDll="$HAND/UnityNative.dll"
  -p:ManagedDir="$PO"
  -p:GlueFacepunchDll="$ART/bin/Glue.Facepunch/release/Glue.Facepunch.dll"
  -p:GlueMT2Dll="$ART/bin/Glue.MT2/release/Glue.MT2.dll"
)
[ "${TEST_ENTRY:-}" = 1 ] && P+=(-p:TestEntry=true)
P+=(${SIGN[@]+"${SIGN[@]}"})
dotnet build "$REPO/src/Launcher/Launcher.csproj" -c Release --nologo -p:BaseOutputPath="$OUT/build/" -p:IntermediateOutputPath="$OUT/obj/" "${P[@]}" -v q -clp:NoSummary
APK="$OUT/build/Release/net10.0-android/android-arm64/org.mt2mobile.launcher-Signed.apk"
# 給 tools/release/package.sh 核對：commit、APK 的 SHA-256、AssetRipper 原始碼摘要、三個原生函式庫各自的 .so 與原始碼摘要（native/*/build.sh 寫的 .source）
{
  echo "commit $COMMIT"
  echo "state $STATE"
  echo "apk $(shasum -a 256 "$APK" | cut -d' ' -f1)"
  echo "assetripper $(python3 -I "$REPO/tools/release/srcdigest.py" "$REPO/third_party/AssetRipper/build/src" --exclude bin --exclude obj --exclude 0Bins)"
  for so in freetype/build/out/android-arm64/libfreetype.so dxbc-spirv/build/out/android-arm64/libdxbcspv.so spirv-cross/build/out/android-arm64/libspirv-cross-c-shared.so; do
    f="$REPO/native/$so"
    [ -f "$f.source" ] || { echo "$f.source 不存在：用現在的 native/*/build.sh android 重編" >&2; exit 1; }
    read -r sha src dir < "$f.source"
    [ "$sha" = "$(shasum -a 256 "$f" | cut -d' ' -f1)" ] || { echo "$f 跟 $f.source 記的不同：用 native/*/build.sh android 重編" >&2; exit 1; }
    echo "native ${so%%/*} $(basename "$f") $sha $src $dir"
  done
} > "$OUT/source-commit.tmp"
mv "$OUT/source-commit.tmp" "$OUT/source-commit.txt"
echo "APK $APK bytes=$(stat -f %z "$APK")"
