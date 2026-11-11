#!/usr/bin/env bash
set -euo pipefail

GAME="${1:?usage: push-game-phone.sh <original game directory>}"
ADB="${ADB:-adb}"
PKG=org.mt2mobile.phase2
REMOTE=/data/local/tmp/mt2-phase2
WORK="$(mktemp -d "$HOME/mt2-phase2/push-game.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

test -d "$GAME" || { echo "missing game directory: $GAME" >&2; exit 2; }
test -d "$GAME/MonsterTrain2_Data" || { echo "missing MonsterTrain2_Data under: $GAME" >&2; exit 2; }
"$ADB" shell pm path "$PKG" >/dev/null
tar -C "$GAME" -czf "$WORK/game.tgz" .
"$ADB" shell "mkdir -p $REMOTE && rm -f $REMOTE/game.tgz"
"$ADB" push "$WORK/game.tgz" "$REMOTE/game.tgz"
"$ADB" shell "set -e
F=/data/data/$PKG/files
U=\$(stat -c %u /data/data/$PKG)
C=\$(stat -c %C /data/data/$PKG)
rm -rf \$F/game
mkdir -p \$F/game
tar -xzf $REMOTE/game.tgz -C \$F/game
chown \$U:\$U \$F
chcon \$C \$F
chown -R \$U:\$U \$F/game
chcon -R \$C \$F/game
rm $REMOTE/game.tgz
printf 'game_files='
find \$F/game -type f | wc -l
df /data"
