#!/usr/bin/env bash
# 引擎的正本在 unity-port（unity-native/engine、patcher、Shared），這個 repo 的 src/UnityNative、src/Patcher、src/Shared 是副本，兩邊逐檔相同。
# unity-port 是維護者私有的開發 repo，沒有它的環境用不到這支腳本；這三個資料夾照常在這裡改、開 PR，維護者合併時再寫回正本（見 AGENTS.md）。
#   tools/sync-engine.sh           從 unity-port 複製過來，列出新增、更新、刪除了哪些檔
#   tools/sync-engine.sh --check   只比對：不一樣就列出來、回傳 1，相同回傳 0
# unity-port 預設在這個 repo 旁邊（../unity-port），別的位置用 UNITY_PORT=<路徑> 指定。
# bin、obj、Generated（舊版 patcher 的替身）、.DS_Store 不比也不複製。src/Shared 只同步正本有的檔，這個 repo 自己放的不動、不刪。
# 兩邊不一樣的設定不放在同步的檔案裡：ManagedDir 的預設值在 src/Directory.Build.props，其他由 tools/phase4/build.sh 用 -p: 傳。目前沒有例外。
# 同步完照 AGENTS.md 逐檔看過 git diff 再 commit。
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
UP="${UNITY_PORT:-$REPO/../unity-port}"
[ -d "$UP/unity-native/engine" ] || { echo "找不到 unity-port 的引擎：$UP/unity-native/engine（用 UNITY_PORT= 指定）" >&2; exit 2; }
UP="$(cd "$UP" && pwd)"
CHECK=0
case "${1:-}" in
  --check) CHECK=1 ;;
  "") ;;
  *) echo "用法：tools/sync-engine.sh [--check]" >&2; exit 2 ;;
esac

# 正本:副本:模式（mirror = 副本多的檔刪掉；files = 只同步正本有的檔）
PAIRS=(
  "unity-native/engine:src/UnityNative:mirror"
  "unity-native/patcher:src/Patcher:mirror"
  "unity-native/Shared:src/Shared:files"
)

list() {
  [ -d "$1" ] || return 0
  (cd "$1" && find . \( -name bin -o -name obj -o -name Generated \) -prune -o -type f ! -name .DS_Store -print) | sed 's|^\./||' | LC_ALL=C sort
}

N=0
for p in "${PAIRS[@]}"; do
  IFS=: read -r from to mode <<< "$p"
  src="$UP/$from"; dst="$REPO/$to"
  while IFS= read -r f; do
    if [ ! -f "$dst/$f" ]; then
      N=$((N + 1))
      if [ $CHECK = 1 ]; then echo "只有 unity-port  $to/$f"; else mkdir -p "$(dirname "$dst/$f")"; cp "$src/$f" "$dst/$f"; echo "新增  $to/$f"; fi
    elif ! cmp -s "$src/$f" "$dst/$f"; then
      N=$((N + 1))
      if [ $CHECK = 1 ]; then echo "不一樣  $to/$f"; else cp "$src/$f" "$dst/$f"; echo "更新  $to/$f"; fi
    fi
  done < <(list "$src")
  [ "$mode" = mirror ] || continue
  while IFS= read -r f; do
    [ -f "$src/$f" ] && continue
    N=$((N + 1))
    if [ $CHECK = 1 ]; then echo "只有這裡  $to/$f"; else rm "$dst/$f"; echo "刪除  $to/$f"; fi
  done < <(list "$dst")
  [ $CHECK = 1 ] || find "$dst" -mindepth 1 -type d -empty -delete
done

REV="$(git -C "$UP" rev-parse --short HEAD 2>/dev/null || echo '?')"
[ -n "$(git -C "$UP" status --porcelain -- unity-native/engine unity-native/patcher unity-native/Shared 2>/dev/null)" ] && REV="${REV}（有未 commit 的改動）"
if [ $CHECK = 1 ]; then
  if [ $N -gt 0 ]; then echo "$N 個檔跟 unity-port $REV 不一樣，用 tools/sync-engine.sh 同步"; exit 1; fi
  echo "相同：src/UnityNative、src/Patcher、src/Shared 跟 unity-port $REV 逐檔相同"
else
  echo "同步完：$N 個檔，來源 unity-port $REV"
fi
