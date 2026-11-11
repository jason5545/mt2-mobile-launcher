"""比兩份原型（unity-native 的 host-android）的 files/log.txt：第 4 題的比對規則（第 0 步定，協調端 10/22 裁決）。

  python3 tools/phase3/device/logcmp.py <A 的 log.txt> <B 的 log.txt> [--at 900] [--mask-gamedir]

- 每行開頭的時間戳（HH:mm:ss.fff）去掉。
- 每 300 格的報告行：每格 ms、最大 ms、shader 編譯的 ms、貼圖上傳的 ms、GC 次數換成 *；
  格數、例外、draw、代用的 shader、編譯與 link 次數照原樣比。
- 貼圖上傳張數不逐字比：同一格的報告行，B 跟 A 差不超過 2 張。第 0 步存檔狀態相同的 5 次，同一格最多差 2 張
  （第 300 格 95～97，第 600、900 格 127～128）：引擎用實際經過的時間推進，同一格的畫面進度不完全相同。
- 「分段（每格平均 ms）」整行是計時，去掉；「載入 DLL」「開機」「場景 … 載入」行尾的 ms 換成 *。
- 兩個空白開頭的行都是量測細節（分段、配置、慢格、ctl 的追蹤，docs/perf-battle.md），去掉。
- GC 欄位新舊兩種寫法都收：「GC a／b 次」與「GC a／b／c 次（這窗暫停 … ms、配置 … MB）」，次數與括號都換成 *。
- 只比到第 --at 格那行報告（含）為止；之後的（按 Home 切到背景）不比。
- 兩次都要用全新的存檔（am start --es persistent <新名字>）：存檔不同，點擊後走的場景就不同。
- --mask-gamedir（Phase 4 第 2 題，Jason 10/22 定）：開機那行「畫面 …，遊戲資料 <路徑>」的路徑換成 *。原型讀外部 app 空間、
  啟動器讀內部空間，這一行只差 GameDir 的設定。沒給就照舊比（Phase 3 的行為不變）。
最後一行 RESULT same|different，different 時 exit 1。
"""
from __future__ import annotations

import difflib
import re
import sys
from pathlib import Path

TS = re.compile(r"^\d\d:\d\d:\d\d\.\d{3} ")
REPORT = re.compile(
    r"^每格 [\d.]+ ms、最大 [\d.]+ ms（(\d+) 格，例外 (\d+)，draw (\d+)，代用的 shader (\d+)）；"
    r"累計 shader 編譯 (\d+)＋link (\d+) 次 \d+ ms、貼圖上傳 (\d+) 張 \d+ ms、GC \d+／\d+(?:／\d+)? 次(?:（[^）]*）)?$")
TEX_TOLERANCE = 2


GAMEDIR = re.compile(r"^(畫面 .*，遊戲資料 ).+$")


def norm(path: Path, at: int, mask_gamedir: bool = False) -> tuple[list[str], dict[int, int]]:
    out: list[str] = []
    tex: dict[int, int] = {}
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = TS.sub("", raw)
        if mask_gamedir:
            line = GAMEDIR.sub(r"\1*", line)
        if line.startswith("  "):
            continue
        line = re.sub(r"^載入 DLL \d+ ms$", "載入 DLL * ms", line)
        line = re.sub(r"^開機 \d+ ms$", "開機 * ms", line)
        line = re.sub(r"^(場景 .+ 載入：\d+ 個 GameObject，)\d+ ms$", r"\1* ms", line)
        m = REPORT.match(line)
        if m:
            f, exc, draw, standin, comp, link, t = m.groups()
            out.append(f"每格 * ms、最大 * ms（{f} 格，例外 {exc}，draw {draw}，代用的 shader {standin}）；"
                       f"累計 shader 編譯 {comp}＋link {link} 次 * ms、貼圖上傳 * 張 * ms、GC *／* 次")
            tex[int(f)] = int(t)
            if int(f) >= at:
                return out, tex
            continue
        out.append(line)
    out.append(f"[沒有到第 {at} 格的報告]")
    return out, tex


def main() -> int:
    args = sys.argv[1:]
    at = 900
    mask = "--mask-gamedir" in args
    if mask:
        args.remove("--mask-gamedir")
    if "--at" in args:
        i = args.index("--at")
        at = int(args[i + 1])
        del args[i : i + 2]
    a, ta = norm(Path(args[0]), at, mask)
    b, tb = norm(Path(args[1]), at, mask)
    print(f"A {args[0]}：{len(a)} 行；B {args[1]}：{len(b)} 行（去掉計時、截到第 {at} 格）")
    print("貼圖上傳張數（格:A/B）：" + " ".join(f"{f}:{ta.get(f)}/{tb.get(f)}" for f in sorted(ta.keys() | tb.keys())))
    diff = list(difflib.unified_diff(a, b, "A", "B", lineterm="", n=1))
    for d in diff[:80]:
        print(d)
    if len(diff) > 80:
        print(f"…另有 {len(diff) - 80} 行")
    over = [f for f in sorted(ta.keys() & tb.keys()) if abs(ta[f] - tb[f]) > TEX_TOLERANCE]
    if over:
        print(f"貼圖上傳張數差超過 {TEX_TOLERANCE} 張的格：{over}")
    same = a == b and not over
    print("RESULT", "same" if same else "different")
    return 0 if same else 1


if __name__ == "__main__":
    sys.exit(main())
