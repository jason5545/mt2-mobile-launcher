#!/usr/bin/env bash
# glslangValidator 檢查一個 shader 工作資料夾（shaderWork）裡全部的 .glsl，同 convert.py 的 phase 4：
# 檔名有 _vert_ 的當 vertex，其餘當 fragment，每 100 個一批。C# 版不搬這一步，用這支在桌面檢查結果是否跟 Python 版一樣。
#   tools/phase3/shader-glslcheck.sh <shaderWork>
# 最後一行：GLSL 個數與 ERROR 行數（convert.py 的「失敗」就是有 ERROR: 開頭的行）。
set -euo pipefail
dir="$1"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
n=0
while IFS= read -r f; do
  stage=frag
  [[ "$(basename "$f")" == *_vert_* ]] && stage=vert
  ln -s "$f" "$tmp/$n.$stage"
  n=$((n + 1))
done < <(find "$dir" -name '*.glsl' | sort)
errors=$( (cd "$tmp" && ls | xargs -n 100 glslangValidator 2>&1 || true) | grep -c '^ERROR: ' || true)
echo "glslangValidator：$n 個 GLSL，ERROR 行 $errors"
