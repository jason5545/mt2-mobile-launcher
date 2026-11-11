"""照 Phase 3 的比對標準比兩份發布的 shaders/（docs/phase3.md「比對標準」）。

  python3 tools/phase3/shader-compare.py <A 的 shaders/> <B 的 shaders/>

檔案清單完全相同，每個檔（各 shader 的 index.json 與 .glsl、最上層的 index.json）逐位元組相同。工作資料夾（shaderWork）不比。
最後一行是 `RESULT same` 或 `RESULT different`，different 時 exit 1。
"""
import sys
from pathlib import Path


def files(root: Path) -> set[str]:
    return {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file()}


def main() -> int:
    a, b = Path(sys.argv[1]), Path(sys.argv[2])
    fa, fb = files(a), files(b)
    diffs = [f"只在 A：{f}" for f in sorted(fa - fb)] + [f"只在 B：{f}" for f in sorted(fb - fa)]
    diffs += [f"不同：{f}" for f in sorted(fa & fb) if (a / f).read_bytes() != (b / f).read_bytes()]
    glsl = sum(1 for f in fa if f.endswith(".glsl"))
    print(f"A {a}：{len(fa)} 個檔（GLSL {glsl}）；B {b}：{len(fb)} 個檔")
    for d in diffs[:50]:
        print(d)
    if len(diffs) > 50:
        print(f"…另有 {len(diffs) - 50} 筆")
    print("RESULT", "same" if not diffs else "different")
    return 1 if diffs else 0


if __name__ == "__main__":
    sys.exit(main())
