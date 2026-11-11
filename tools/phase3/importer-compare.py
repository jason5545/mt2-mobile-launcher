# /// script
# requires-python = ">=3.11"
# dependencies = ["Pillow>=11"]
# ///
"""照 Phase 3 的比對標準比兩份 importer 輸出（docs/phase3.md「比對標準」；shaders/ 不在這裡比，見 shader-compare.py）。

  uv run tools/phase3/importer-compare.py <A 輸出> <B 輸出> [--only <相對路徑前綴>]

- 檔案清單完全相同；每個檔逐位元組相同。
- 例外只有兩種：
  - 計時：assets/report.json、scenes/report.json、supplement.json 去掉最上層的 seconds，
    assets/glyphs.json 去掉 report.seconds，之後相同（比對標準 Jason 10/21 定，glyphs 的 report.seconds 是第 0 步找到、協調端批准；會變的計時欄位只有這四個）。
  - assets/textures/builtin-*.png（Python 用 UnityPy 解碼、Pillow 存檔）：尺寸、模式、每個像素都相同即可。
- --only 只比某個子資料夾（逐步搬的時候用，例如 --only scenes/）。

最後一行是 `RESULT same` 或 `RESULT different`，different 時 exit 1。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image

REPORTS = {"assets/report.json", "scenes/report.json", "supplement.json"}
GLYPHS = "assets/glyphs.json"


def files(root: Path, only: str | None) -> set[str]:
    out = set()
    for p in root.rglob("*"):
        if not p.is_file():
            continue
        rel = p.relative_to(root)
        if rel.parts[0] == "shaders":
            continue
        s = rel.as_posix()
        if only is None or s.startswith(only):
            out.add(s)
    return out


def without_seconds(raw: bytes, nested: bool) -> str:
    d = json.loads(raw)
    (d["report"] if nested else d).pop("seconds", None)
    return json.dumps(d, ensure_ascii=False, separators=(",", ":"))


def main() -> int:
    argv = sys.argv[1:]
    only = None
    if "--only" in argv:
        i = argv.index("--only")
        only = argv[i + 1]
        del argv[i : i + 2]
    a, b = Path(argv[0]), Path(argv[1])
    fa, fb = files(a, only), files(b, only)
    diffs = [f"只在 A：{f}" for f in sorted(fa - fb)] + [f"只在 B：{f}" for f in sorted(fb - fa)]
    same = seconds = pixel = 0
    for f in sorted(fa & fb):
        ra, rb = (a / f).read_bytes(), (b / f).read_bytes()
        if ra == rb:
            same += 1
            continue
        if f in REPORTS or f == GLYPHS:
            if without_seconds(ra, f == GLYPHS) == without_seconds(rb, f == GLYPHS):
                seconds += 1
                continue
        if f.startswith("assets/textures/builtin-") and f.endswith(".png"):
            with Image.open(a / f) as ia, Image.open(b / f) as ib:
                if ia.size == ib.size and ia.mode == ib.mode and ia.tobytes() == ib.tobytes():
                    pixel += 1
                    continue
        diffs.append(f"不同：{f}")
    print(f"A {a}：{len(fa)} 個檔；B {b}：{len(fb)} 個檔")
    print(f"逐位元組相同 {same}，只差計時 {seconds}，PNG 位元組不同但像素相同 {pixel}")
    for d in diffs[:50]:
        print(d)
    if len(diffs) > 50:
        print(f"…另有 {len(diffs) - 50} 筆")
    print("RESULT", "same" if not diffs else "different")
    return 1 if diffs else 0


if __name__ == "__main__":
    sys.exit(main())
