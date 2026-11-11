#!/usr/bin/env bash
# 把 converter 的輸入照 unity-port 的相對結構複製到 <目的地>，給手機或 app 用。
# 用法：stage-inputs.sh <目的地>
# 產生：
#   <目的地>/mt2-web/cs2ts.json
#   <目的地>/mt2-web/reference/...         cs2ts.json 的 csharp、sourceAssemblies（只有 .cs）、managed（只有 .dll）
#   <目的地>/unity-cs2ts/runtime/src/csport/*.ts
#   <目的地>/mt2-web/build/out/runtime.ts   相容層（runtime.ts 用相對路徑 export 上面那包）
# 在 <目的地>/mt2-web 執行：CsPort --out build/out web '*'
# 內容是 Jason 自己的遊戲檔，<目的地> 要在這個 repo 外面。
set -euo pipefail

SRC="${UNITY_PORT:-$HOME/GitHub/unity-port}"
DEST="${1:?用法：stage-inputs.sh <目的地>}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
DEST="$(realpath -m "$DEST")"
case "$DEST/" in "$REPO"/*) echo "目的地不能在 repo 裡：$DEST" >&2; exit 1 ;; esac
mkdir -p "$DEST"

cd "$SRC/mt2-web"
# cs2ts.json 是 JSONC：去掉整行註解再解析
mapfile -t DIRS < <(python3 -I -c '
import json, re, sys
c = json.loads(re.sub(r"^\s*//.*$", "", open("cs2ts.json").read(), flags=re.M))
print("cs\t" + c["csharp"])
print("dll\t" + c["managed"])
for v in c.get("sourceAssemblies", {}).values():
    print("cs\t" + v)
')

rm -rf "$DEST/mt2-web" "$DEST/unity-cs2ts"
mkdir -p "$DEST/mt2-web/build/out" "$DEST/unity-cs2ts/runtime/src"
cp cs2ts.json "$DEST/mt2-web/"
for line in "${DIRS[@]}"; do
  ext="${line%%$'\t'*}"; dir="${line#*$'\t'}"
  [ -d "$dir" ] || { echo "略過（不存在，converter 也會略過）：$dir"; continue; }
  (find "$dir" -type f -name "*.$ext" -print0 | rsync -a --from0 --files-from=- ./ "$DEST/mt2-web/")
done
rsync -a "$SRC/unity-cs2ts/runtime/src/csport" "$DEST/unity-cs2ts/runtime/src/"
cp src/csport/runtime.ts "$DEST/mt2-web/build/out/"

echo "檔案數：$(find "$DEST" -type f | wc -l)，大小：$(du -sh --apparent-size "$DEST" | cut -f1)"
