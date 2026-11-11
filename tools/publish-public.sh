#!/usr/bin/env bash
# 把正本（這個 repo，維護者私有）HEAD 的快照推到公開 repo：不帶歷史、不含 docs/（開發紀錄）。
#   tools/publish-public.sh "<commit 訊息>" [--push]
# 公開 repo 的工作資料夾：PUBLIC_DIR（預設 ~/mt2-phase4/public-repo，repo 外），第一次自動 clone。
# 每次發布是公開 repo 上的一個 commit，訊息第二段記正本的 commit。commit 前由 .githooks 跑 guard（暫存區與訊息，帶私人清單）。
# 不帶 --push 只在公開 repo 的工作資料夾 commit，看過再自己 push（或重跑帶 --push）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PUBLIC_URL=https://github.com/jason5545/mt2-mobile-launcher.git
PUB="${PUBLIC_DIR:-$HOME/mt2-phase4/public-repo}"
MSG="${1:?用法：tools/publish-public.sh \"<commit 訊息>\" [--push]}"
PUSH="${2:-}"

[ -z "$(git -C "$ROOT" status --porcelain)" ] || { echo "正本的工作樹不乾淨，先 commit" >&2; exit 2; }
COMMIT="$(git -C "$ROOT" rev-parse HEAD)"

if [ ! -d "$PUB/.git" ]; then
  git clone -q "$PUBLIC_URL" "$PUB"
fi
git -C "$PUB" config user.name "$(git -C "$ROOT" config user.name)"
git -C "$PUB" config user.email "$(git -C "$ROOT" config user.email)"
git -C "$PUB" config core.hooksPath .githooks
if git -C "$PUB" rev-parse -q --verify HEAD >/dev/null; then
  [ -z "$(git -C "$PUB" status --porcelain)" ] || { echo "$PUB 有沒 commit 的改動，先處理" >&2; exit 2; }
  git -C "$PUB" pull -q --ff-only
else
  git -C "$PUB" checkout -q -B main
fi

# 換成這次的快照：清掉公開 repo 追蹤的檔，再從正本解開（不含 docs/）
git -C "$PUB" rm -rq --ignore-unmatch -- . >/dev/null
git -C "$ROOT" archive "$COMMIT" -- . ':(exclude)docs' | tar -x -C "$PUB"
git -C "$PUB" add -A
if git -C "$PUB" diff --cached --quiet; then
  echo "跟公開 repo 現在的內容相同，不用發"
  exit 0
fi
[ ! -e "$PUB/docs" ] || { echo "快照裡出現 docs/" >&2; exit 1; }
git -C "$PUB" commit -q -m "$MSG" -m "正本 ${COMMIT:0:7}"
git -C "$PUB" log --oneline -1
git -C "$PUB" diff --stat HEAD~1 2>/dev/null | tail -1 || true

if [ "$PUSH" = --push ]; then
  git -C "$PUB" push -q -u origin main
  echo "已推到 $PUBLIC_URL"
else
  echo "還沒 push：git -C \"$PUB\" push -u origin main"
fi
