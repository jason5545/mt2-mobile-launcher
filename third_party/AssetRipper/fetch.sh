#!/usr/bin/env bash
# AssetRipper 的原始碼：建置時抓上游指定的 commit，套用同資料夾的 assetripper-local.patch。src/Importer、src/TextureExport、src/Launcher 從原始碼參考它。
#
# 用法：
#   third_party/AssetRipper/fetch.sh                    抓到 third_party/AssetRipper/build/src；已經抓過、patch 也套好了就不動
#   third_party/AssetRipper/fetch.sh --archive <檔名>   另外把套好 patch 的原始碼打包成 tar.gz（發布 APK 時附上，GPL-3.0 第 6 條）
#
# 上游：1.3.10 之後的 master 261d740（2026/2/2，1.3.10 之後 4 個 commit）。
# patch：反編譯模式下巢狀類別的 GUID、略過的組件找不到 GUID 時退回 UnityEngine、Unity 套件組件的 GUID 對照、DecompiledGameOnly 模式，
#   以及命令列工具 AssetRipper.CLI（桌面比對用，APK 不帶）。逐項說明見 THIRD_PARTY_NOTICES.md 的 AssetRipper 那一節。
# 授權：AssetRipper 與這份 patch 都是 GPL-3.0，不是這個 repo 其他部分的 MIT。抓下來的原始碼放 build/，不進版控。
# csproj 的 AssetRipperSource 預設指到 build/src/Source；放在別的地方就用 -p:AssetRipperSource=<路徑>/Source。
# 需要：git。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/build/src"
PATCH="$HERE/assetripper-local.patch"
REPO=https://github.com/AssetRipper/AssetRipper.git
COMMIT=261d740120251d48c3aa2b3e95500753603d06fa

ARCHIVE=
case "${1:-}" in
  --archive) ARCHIVE="${2:?用法：fetch.sh --archive <檔名>.tar.gz}" ;;
  "") ;;
  *) echo "用法：third_party/AssetRipper/fetch.sh [--archive <檔名>.tar.gz]" >&2; exit 2 ;;
esac

if [[ ! -d "$SRC/.git" ]]; then
  mkdir -p "$SRC"
  git -C "$SRC" init -q
  git -C "$SRC" remote add origin "$REPO"
fi
git -C "$SRC" cat-file -e "$COMMIT^{commit}" 2>/dev/null || git -C "$SRC" fetch -q --depth 1 origin "$COMMIT"

if [[ "$(git -C "$SRC" rev-parse -q --verify HEAD || true)" == "$COMMIT" ]] && git -C "$SRC" apply --check -R "$PATCH" 2>/dev/null; then
  echo "AssetRipper：已經是 ${COMMIT:0:7} 加 patch（${SRC}）"
else
  # 第一次抓，或 patch 換過：回到上游的 commit 再套。bin、obj 是 .gitignore 裡的，留著不重編
  git -C "$SRC" checkout -q -f --detach "$COMMIT"
  git -C "$SRC" clean -q -fd
  git -C "$SRC" apply "$PATCH"
  echo "AssetRipper：${COMMIT:0:7} 加 patch（${SRC}）"
fi

if [[ -n "$ARCHIVE" ]]; then
  tar -czf "$ARCHIVE" -C "$SRC" --exclude ./.git --exclude bin --exclude obj --exclude 0Bins .
  echo "原始碼：$ARCHIVE"
fi
