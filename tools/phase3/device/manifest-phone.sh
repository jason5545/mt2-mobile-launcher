#!/system/bin/sh
# 手機上用 root 跑：對測試 app 的輸出算 sha256 清單（patch、export、export-shaders、out；沒有的跳過），放 /data/local/tmp/mt2-phase3/<名稱>.sha256，
# 印出每份的檔數與總雜湊。算法跟 Mac 相同：find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum，總雜湊是清單本身的 sha256。
#   sh manifest-phone.sh
F=/data/data/org.mt2mobile.phase3/files
REMOTE=/data/local/tmp/mt2-phase3
for d in patch/managed export export-shaders out; do
  n=$(echo "$d" | tr '/' '-')
  if [ ! -d "$F/$d" ]; then echo "$n: missing"; continue; fi
  (cd "$F/$d" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) > "$REMOTE/$n.sha256"
  echo "$n files=$(wc -l < "$REMOTE/$n.sha256") kB=$(du -sk "$F/$d" | cut -f1) total=$(sha256sum < "$REMOTE/$n.sha256" | cut -d' ' -f1)"
done
