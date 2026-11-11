#!/usr/bin/env bash
# 發布打包：拿 tools/phase4/build.sh（RELEASE=1）的輸出資料夾，檢查過再產出要上傳 release 的檔案。
#   GAME_DIR=<原版遊戲資料夾>[:<另一版的資料夾>…] tools/release/package.sh <build.sh 的輸出資料夾> <repo 外的輸出資料夾>
#   GAME_DIR 跟 build.sh 用同一個值：每一版的原版都要排除。
# 檢查（任何一項沒過就停、回傳非 0）：
#   1. 打包的原始碼就是編出 APK 的那份：工作樹乾淨、HEAD 跟建置紀錄（build.sh 在 APK 編好後才寫的 source-commit.txt）相同、建置時也是乾淨的；
#      APK、AssetRipper 原始碼、三個原生函式庫的 .so 與原始碼都跟紀錄的摘要相同（tools/release/srcdigest.py）。
#   2. 簽章：apksigner 驗得過、只有 1 個簽署者、憑證 SHA-256 在 src/Launcher/OfficialSigning.cs 的官方清單裡。
#   3. apk-check：APK 跟原版遊戲資料夾（GAME_DIR 的每一個）、每個 target 的原版 Managed、這次建置 patch 過的 DLL（<build>/managed）都沒有相同的檔。
#   4. license-check：APK 裡每個 .NET 組件與 .so 都列在 THIRD_PARTY_NOTICES.md。
# 產出：APK 與 SHA-256、這個 repo 在該 commit 的原始碼、AssetRipper 套用 patch 後的原始碼（GPL-3.0）、原生函式庫的上游原始碼、
#   SOURCES.md（各部分的原始碼在哪，含 APK 裡 GPL／LGPL 的 NuGet 套件）、LICENSE、THIRD_PARTY_NOTICES.md、GPL-3.0 原文。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
REPO_URL=https://github.com/jason5545/mt2-mobile-launcher

if [[ $# -ne 2 ]]; then
  echo "用法：GAME_DIR=<遊戲資料夾> tools/release/package.sh <build.sh 的輸出資料夾> <repo 外的輸出資料夾>" >&2
  exit 2
fi

: "${GAME_DIR:?請設定 GAME_DIR，供 APK 遊戲內容排除檢查使用}"

realpath_py() { python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$1"; }
BUILD="$(realpath_py "$1")"
OUTPUT="$(realpath_py "$2")"
GAME_ROOTS=()
IFS=: read -r -a GAME_DIRS <<<"${GAME_DIR}"
for d in "${GAME_DIRS[@]}"; do
  [[ -n "${d}" ]] || continue
  GAME_ROOTS+=("$(realpath_py "${d}")")
done
APK="${BUILD}/build/Release/net10.0-android/android-arm64/org.mt2mobile.launcher-Signed.apk"

[[ -f "${APK}" ]] || { echo "找不到 APK：${APK}" >&2; exit 2; }
[[ ${#GAME_ROOTS[@]} -gt 0 ]] || { echo "GAME_DIR 是空的" >&2; exit 2; }
for g in "${GAME_ROOTS[@]}"; do
  [[ -d "${g}/MonsterTrain2_Data" ]] || { echo "GAME_DIR 底下找不到 MonsterTrain2_Data：${g}" >&2; exit 2; }
done
[[ -d "${BUILD}/managed" ]] || { echo "找不到這次建置 patch 過的 DLL：${BUILD}/managed" >&2; exit 2; }
case "${OUTPUT}/" in
  "${ROOT}/"*) echo "輸出資料夾必須在 repo 外：${OUTPUT}" >&2; exit 2 ;;
esac
[[ ! -e "${OUTPUT}" ]] || { echo "輸出路徑已存在，為避免覆蓋而停止：${OUTPUT}" >&2; exit 2; }

echo "檢查原始碼與建置是同一份……"
[[ -z "$(git -C "${ROOT}" status --porcelain)" ]] || { echo "工作樹有未 commit 的改動，打包的原始碼會跟 APK 對不上" >&2; exit 1; }
COMMIT="$(git -C "${ROOT}" rev-parse HEAD)"
RECORD="${BUILD}/source-commit.txt"
[[ -f "${RECORD}" ]] || { echo "找不到 ${RECORD}：建置沒有成功做完，或是舊版 build.sh 建的，重新建置" >&2; exit 1; }
field() { awk -v k="$1" '$1 == k { print $2 }' "${RECORD}"; }
[[ "$(field commit)" == "${COMMIT}" ]] || { echo "APK 是在 $(field commit) 建的，現在的 HEAD 是 ${COMMIT}" >&2; exit 1; }
[[ "$(field state)" == clean ]] || { echo "建置時工作樹有未 commit 的改動" >&2; exit 1; }
[[ "$(field apk)" == "$(shasum -a 256 "${APK}" | cut -d' ' -f1)" ]] || { echo "APK 跟建置紀錄的 SHA-256 不同：建置之後被換過" >&2; exit 1; }
AR_SRC="${ROOT}/third_party/AssetRipper/build/src"
ar_digest() { python3 -I "${ROOT}/tools/release/srcdigest.py" "${AR_SRC}" --exclude bin --exclude obj --exclude 0Bins; }
[[ "$(field assetripper)" == "$(ar_digest)" ]] || { echo "AssetRipper 的原始碼（${AR_SRC}）跟建置時不同" >&2; exit 1; }
NATIVE_LIBS=()
while read -r _ lib so sha src dir; do
  inapk="$(unzip -p "${APK}" "lib/arm64-v8a/${so}" | shasum -a 256 | cut -d' ' -f1)"
  [[ "${inapk}" == "${sha}" ]] || { echo "APK 裡的 ${so} 跟建置紀錄不同" >&2; exit 1; }
  [[ "$(python3 -I "${ROOT}/tools/release/srcdigest.py" "${ROOT}/native/${lib}/build/src")" == "${src}" ]] || { echo "native/${lib}/build/src 跟編 ${so} 時不同：用 native/${lib}/build.sh android 重編再建置" >&2; exit 1; }
  [[ "$(python3 -I "${ROOT}/tools/release/srcdigest.py" "${ROOT}/native/${lib}" --exclude build)" == "${dir}" ]] || { echo "native/${lib} 的腳本或 patch 跟編 ${so} 時不同：用 native/${lib}/build.sh android 重編再建置" >&2; exit 1; }
  NATIVE_LIBS+=("${lib}")
done < <(awk '$1 == "native"' "${RECORD}")
[[ ${#NATIVE_LIBS[@]} == 3 ]] || { echo "建置紀錄的原生函式庫是 ${#NATIVE_LIBS[@]} 個，要 3 個" >&2; exit 1; }
echo "commit ${COMMIT}；AssetRipper 與原生函式庫 ${NATIVE_LIBS[*]} 的原始碼跟建置時相同"

echo "檢查簽章……"
APKSIGNER="${APKSIGNER:-}"
if [[ -z "${APKSIGNER}" ]]; then
  SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
  APKSIGNER="$(ls -d "${SDK}"/build-tools/*/apksigner 2>/dev/null | sort -V | tail -1)"
fi
[[ -x "${APKSIGNER}" ]] || { echo "找不到 apksigner（APKSIGNER= 或 ANDROID_HOME= 指定）" >&2; exit 2; }
CERTS="$("${APKSIGNER}" verify --print-certs "${APK}" 2>/dev/null)" || { echo "apksigner 驗證失敗" >&2; exit 1; }
SIGNERS="$(grep -c 'certificate SHA-256 digest:' <<<"${CERTS}" || true)"
[[ "${SIGNERS}" == 1 ]] || { echo "簽署者 ${SIGNERS} 個，要剛好 1 個" >&2; exit 1; }
DIGEST="$(sed -n 's/.*certificate SHA-256 digest: \([0-9a-f]*\).*/\1/p' <<<"${CERTS}")"
OFFICIAL="$(sed -n 's/.*static readonly string\[\] Official = \[\(.*\)\];.*/\1/p' "${ROOT}/src/Launcher/OfficialSigning.cs" | tr -d ' "' | tr ',' '\n')"
grep -qx "${DIGEST}" <<<"${OFFICIAL}" || { echo "簽章 ${DIGEST} 不在官方清單：用 RELEASE=1 tools/phase4/build.sh 重新建置" >&2; exit 1; }
echo "官方簽章 ${DIGEST}"

echo "檢查 APK 遊戲內容……"
CHECK=()
for t in "${ROOT}"/targets/*.json; do CHECK+=(--target "${t}"); done
for g in "${GAME_ROOTS[@]}"; do CHECK+=(--forbid-dir "${g}"); done
python3 "${ROOT}/tools/phase4/apk-check.py" "${APK}" "${CHECK[@]}" --forbid-dir "${BUILD}/managed"

echo "檢查 APK 授權清單……"
python3 "${ROOT}/tools/release/license-check.py" "${APK}" \
  --notices "${ROOT}/THIRD_PARTY_NOTICES.md"

mkdir -p "$(dirname "${OUTPUT}")"
STAGING="$(mktemp -d "${OUTPUT}.tmp.XXXXXX")"
cleanup() {
  if [[ -n "${STAGING:-}" && -d "${STAGING}" ]]; then
    rm -rf -- "${STAGING}"
  fi
}
trap cleanup EXIT

APK_NAME="$(basename "${APK}")"
SHORT="${COMMIT:0:7}"
cp "${APK}" "${STAGING}/${APK_NAME}"
(cd "${STAGING}" && shasum -a 256 "${APK_NAME}" > "${APK_NAME}.sha256")

# 公開 repo 是不含 docs/（開發紀錄）的快照（tools/publish-public.sh），原始碼包照同樣的範圍
git -C "${ROOT}" archive --format=tar.gz --prefix="mt2-mobile-launcher-${SHORT}/" -o "${STAGING}/mt2-mobile-launcher-source-${SHORT}.tar.gz" "${COMMIT}" -- . ':(exclude)docs'
tar -tzf "${STAGING}/mt2-mobile-launcher-source-${SHORT}.tar.gz" | grep -q "^mt2-mobile-launcher-${SHORT}/docs/" && { echo "原始碼包裡還有 docs/" >&2; exit 1; }
(cd "${STAGING}" && "${ROOT}/third_party/AssetRipper/fetch.sh" --archive "AssetRipper-source.tar.gz")
[[ "$(field assetripper)" == "$(ar_digest)" ]] || { echo "打包 AssetRipper 時原始碼變了" >&2; exit 1; }
# 原生函式庫的上游原始碼（套用 patch 後、編 .so 時的那份），不含 .git
tar -czf "${STAGING}/native-sources.tar.gz" -C "${ROOT}/native" --exclude .git --exclude .DS_Store \
  "${NATIVE_LIBS[@]/%//build/src}"
cp "${ROOT}/third_party/AssetRipper/build/src/LICENSE.md" "${STAGING}/GPL-3.0-LICENSE.md"
cp "${ROOT}/LICENSE" "${STAGING}/LICENSE"
cp "${ROOT}/THIRD_PARTY_NOTICES.md" "${STAGING}/THIRD_PARTY_NOTICES.md"

# SOURCES.md：APK 裡 GPL／LGPL 的 NuGet 套件（THIRD_PARTY_NOTICES.md「APK 中的 NuGet 套件盤點」表的授權欄），原始碼位置照 nuspec 的 repository
python3 -I - "${ROOT}/THIRD_PARTY_NOTICES.md" "${STAGING}/SOURCES.md" "${REPO_URL}" "${COMMIT}" "${SHORT}" <<'PY'
import re, sys, xml.etree.ElementTree as ET
from pathlib import Path
notices, out, repo, commit, short = sys.argv[1:]
rows = []
for line in Path(notices).read_text(encoding="utf-8").splitlines():
    m = re.match(r"\| `([^`]+)` \| `([^`]+)` \| ([^|]+) \|", line)
    if m and "GPL" in m.group(3):
        rows.append((m.group(1), m.group(2), m.group(3).strip()))
rows = sorted(set(rows))
if not rows:
    sys.exit("THIRD_PARTY_NOTICES.md 裡找不到 GPL／LGPL 的 NuGet 套件列，表格格式可能改了")
lines = [
    "# 原始碼",
    "",
    f"- 啟動器（MIT，見 LICENSE）：`mt2-mobile-launcher-source-{short}.tar.gz`，編出這個 APK 的原始碼（commit `{commit}`，跟 {repo} 這個版本的 tag 內容相同，不含開發紀錄 docs/）。建置方法見裡面的 README「How to Build」；原版遊戲檔要自己的，不在這裡。",
    "- AssetRipper（GPL-3.0，含本機修改）：`AssetRipper-source.tar.gz`，由 `third_party/AssetRipper/fetch.sh --archive` 產生。",
    "- 原生函式庫（FreeType、dxbc-spirv、SPIRV-Cross）：編 APK 裡那幾個 .so 用的上游原始碼（已套用本機 patch）在 `native-sources.tar.gz`；建置腳本、patch 與 C 介面在啟動器原始碼的 `native/` 底下。授權見 THIRD_PARTY_NOTICES.md。",
    "",
    "APK 裡 GPL／LGPL 授權的 NuGet 套件，原始碼位置取自套件 nuspec 的 repository：",
    "",
    "| 套件 | 版本 | 授權 | 原始碼 |",
    "|---|---|---|---|",
]
home = Path.home() / ".nuget" / "packages"
for pkg, ver, lic in rows:
    spec = next((home / pkg.lower() / ver.lower()).glob("*.nuspec"), None)
    if spec is None:
        sys.exit(f"找不到 {pkg} {ver} 的 nuspec（~/.nuget/packages）")
    root = ET.parse(spec).getroot()
    r = next((e for e in root.iter() if e.tag.endswith("repository")), None)
    if r is None or not r.get("url") or not r.get("commit"):
        sys.exit(f"{pkg} {ver} 的 nuspec 沒有 repository url／commit，要手動補原始碼位置")
    lines.append(f"| {pkg} | {ver} | {lic} | {r.get('url')} 的 commit `{r.get('commit')}` |")
Path(out).write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"SOURCES.md：GPL／LGPL 的 NuGet 套件 {len(rows)} 個")
PY

mv "${STAGING}" "${OUTPUT}"
STAGING=""
trap - EXIT

echo "打包完成：${OUTPUT}"
ls -1 "${OUTPUT}"
