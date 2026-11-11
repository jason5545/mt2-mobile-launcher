#!/usr/bin/env bash
# 把手機上的輸出用 adb exec-out tar 串流拉到 Mac 的分大小寫磁碟映像，Mac 這邊重算清單，總雜湊要跟手機的相同。
#   [PULL="patch E B"] tools/phase3/device/pull-phone.sh <目的資料夾（分大小寫的磁碟映像裡）> <清單資料夾>
# 拉 patch/managed → <目的>/patch、export → <目的>/E、export-shaders → <目的>/E2、out → <目的>/B（PULL 指定拉哪幾份，預設 patch E B）；
# 清單（手機的 *.sha256）拉到 <清單資料夾>。
set -euo pipefail

DST="${1:?usage: pull-phone.sh <dest dir> <manifest dir>}"
MAN="${2:?}"
F=/data/data/org.mt2mobile.phase3/files
REMOTE=/data/local/tmp/mt2-phase3
mkdir -p "$DST" "$MAN"
MNT=$(df "$DST" | tail -1 | awk '{print $NF}')
diskutil info "$MNT" 2>/dev/null | grep -q "Case-sensitive" || { echo "$DST 不在分大小寫的磁碟上（${MNT}）" >&2; exit 2; }

PULL=" ${PULL:-patch E B} "
for pair in "patch/managed:patch:patch-managed" "export:E:export" "export-shaders:E2:export-shaders" "out:B:out"; do
  IFS=: read -r src name man <<< "$pair"
  [[ "$PULL" == *" $name "* ]] || continue
  rm -rf "${DST:?}/$name"; mkdir -p "$DST/$name"
  start=$(date +%s)
  adb exec-out "tar -cf - -C $F/$src ." | tar -xf - -C "$DST/$name"
  adb pull "$REMOTE/$man.sha256" "$MAN/phone-$man.sha256" >/dev/null
  (cd "$DST/$name" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256) > "$MAN/mac-$man.sha256"
  p=$(shasum -a 256 < "$MAN/phone-$man.sha256" | cut -d' ' -f1)
  m=$(shasum -a 256 < "$MAN/mac-$man.sha256" | cut -d' ' -f1)
  echo "$name: pull_s=$(( $(date +%s) - start )) files=$(wc -l < "$MAN/mac-$man.sha256" | tr -d ' ') phone=$p mac=$m $([ "$p" = "$m" ] && echo same || echo DIFFERENT)"
done
