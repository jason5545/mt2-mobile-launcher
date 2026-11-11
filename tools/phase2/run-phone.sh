#!/system/bin/sh
set -u

PKG=org.mt2mobile.phase2
F=/data/data/$PKG/files
REMOTE=/data/local/tmp/mt2-phase2

mkdir -p "$REMOTE"
rm -f "$F/tmp/complete.txt"
am start-foreground-service -n "$PKG/.RunnerService" --es mode patch

pid=
i=0
while [ -z "$pid" ] && [ "$i" -lt 100 ]; do
  pid=$(pidof "$PKG" 2>/dev/null | awk '{print $1}')
  [ -n "$pid" ] || { sleep 0.1; i=$((i + 1)); }
done

if [ -n "$pid" ]; then
  echo "runner pid=$pid"
  ps -AZ | awk -v p="$pid" 'NR == 1 || $3 == p'
  awk '/^(Uid|Gid):/' "/proc/$pid/status"
  hwm=0
  while [ ! -f "$F/tmp/complete.txt" ]; do
    h=$(awk '/^VmHWM:/ {print $2}' "/proc/$pid/status" 2>/dev/null)
    [ -n "$h" ] && hwm=$h
    sleep 0.2
  done
  echo "VmHWM_kB=$hwm"
else
  echo "runner process did not appear"
fi

i=0
while [ ! -f "$F/tmp/complete.txt" ] && [ "$i" -lt 60 ]; do
  sleep 1
  i=$((i + 1))
done
if [ ! -f "$F/tmp/complete.txt" ]; then
  echo "runner did not write completion marker"
  exit 1
fi

echo "--- runner result"
cat "$F/tmp/complete.txt"
echo "--- runner log"
cat "$F/tmp/runner.log"
echo "--- patched files"
find "$F/output/managed" -type f | wc -l
(cd "$F/output/managed" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum)
(cd "$F/output/managed" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) > "$F/output/patch.sha256"
cp "$F/tmp/runner.log" "$REMOTE/runner.log"
cp "$F/tmp/complete.txt" "$REMOTE/complete.txt"
cp "$F/output/patch.sha256" "$REMOTE/patch.sha256"
cat "$REMOTE/patch.sha256" | tail -n 4
