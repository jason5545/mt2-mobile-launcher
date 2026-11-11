#!/usr/bin/env bash
# 驗收做完：移除啟動器（連同私有空間裡的原版、安裝輸出），刪 /data/local/tmp/mt2-phase4。四個原型不動。
set -euo pipefail
adb uninstall org.mt2mobile.launcher || true
adb shell "rm -rf /data/local/tmp/mt2-phase4"
adb shell "ls -d /data/data/org.mt2mobile.launcher 2>&1; df -k /data | tail -1"
