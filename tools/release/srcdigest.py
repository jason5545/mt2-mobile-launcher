"""原始碼資料夾的摘要：建置時記下、打包時重算，兩次相同才表示附上的原始碼就是編進 APK 的那份。

  python3 -I tools/release/srcdigest.py <資料夾> [--exclude <名稱> …]

每個檔（依相對路徑排序）算「相對路徑 \\0 內容的 SHA-256」，再整串取 SHA-256。名稱等於 .git 或 --exclude 的檔案、資料夾不算
（AssetRipper 照 fetch.sh --archive 打包時排除的 bin、obj、0Bins）。.DS_Store 一律不算。符號連結算它指向的路徑，不跟進去。
"""
from __future__ import annotations

import argparse
import hashlib
import os
import sys
from pathlib import Path


def digest(root: Path, exclude: set[str]) -> str:
    skip = exclude | {".git", ".DS_Store"}
    entries: list[tuple[str, str]] = []
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dirnames[:] = sorted(d for d in dirnames if d not in skip)
        for name in filenames:
            if name in skip:
                continue
            p = Path(dirpath) / name
            rel = p.relative_to(root).as_posix()
            if p.is_symlink():
                h = hashlib.sha256(b"link:" + os.readlink(p).encode()).hexdigest()
            else:
                h = hashlib.sha256(p.read_bytes()).hexdigest()
            entries.append((rel, h))
    if not entries:
        raise SystemExit(f"{root} 裡沒有檔案")
    total = hashlib.sha256()
    for rel, h in sorted(entries):
        total.update(rel.encode() + b"\0" + h.encode() + b"\n")
    return total.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("root", type=Path)
    ap.add_argument("--exclude", action="append", default=[])
    a = ap.parse_args()
    if not a.root.is_dir():
        print(f"找不到資料夾：{a.root}", file=sys.stderr)
        return 2
    print(digest(a.root, set(a.exclude)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
