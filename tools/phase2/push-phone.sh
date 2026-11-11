#!/usr/bin/env bash
set -euo pipefail

MANAGED="${1:?usage: push-phone.sh <original Managed directory>}"
ADB="${ADB:-adb}"
PKG=org.mt2mobile.phase2
REMOTE=/data/local/tmp/mt2-phase2
WORK="$(mktemp -d "$HOME/mt2-phase2/push.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

test -d "$MANAGED" || { echo "missing Managed directory: $MANAGED" >&2; exit 2; }
"$ADB" shell pm path "$PKG" >/dev/null
tar -C "$MANAGED" -czf "$WORK/managed.tgz" .
"$ADB" shell "mkdir -p $REMOTE"
"$ADB" shell "rm -f $REMOTE/managed.tgz"
"$ADB" push "$WORK/managed.tgz" "$REMOTE/managed.tgz"
"$ADB" shell "set -e
F=/data/data/$PKG/files
U=\$(stat -c %u /data/data/$PKG)
C=\$(stat -c %C /data/data/$PKG)
rm -rf \$F/managed \$F/output
mkdir -p \$F/managed \$F/output \$F/tmp
tar -xzf $REMOTE/managed.tgz -C \$F/managed
chown -R \$U:\$U \$F
chcon -R \$C \$F
rm $REMOTE/managed.tgz
printf 'managed_files='
find \$F/managed -type f | wc -l
df /data"
