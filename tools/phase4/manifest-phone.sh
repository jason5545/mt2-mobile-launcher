#!/system/bin/sh
# 手機上用 root 跑：對啟動器的安裝輸出算 sha256 清單（patch、export、out；沒有的跳過），放 /data/local/tmp/mt2-phase4/<名稱>.sha256，
# 印出每份的檔數與總雜湊。算法跟 Mac 相同：find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum，總雜湊是清單本身的 sha256。
#   sh manifest-phone.sh [target，預設 mt2-2.2.1-win]
T=${1:-mt2-2.2.1-win}
I=/data/data/org.mt2mobile.launcher/files/installs/$T
REMOTE=/data/local/tmp/mt2-phase4
for d in patch/managed export out; do
  n=$(echo "$d" | tr '/' '-')
  if [ ! -d "$I/$d" ]; then echo "$n: missing"; continue; fi
  (cd "$I/$d" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) > "$REMOTE/$n.sha256"
  echo "$n files=$(wc -l < "$REMOTE/$n.sha256") kB=$(du -sk "$I/$d" | cut -f1) total=$(sha256sum < "$REMOTE/$n.sha256" | cut -d' ' -f1)"
done
