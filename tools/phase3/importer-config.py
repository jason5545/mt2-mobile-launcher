"""把遊戲的 cs2ts.json 複製到 repo 外，給 Python 與 C# 的 importer／shaders 跑基準用（原檔不改）。

  python3 tools/phase3/importer-config.py <原 cs2ts.json> <新設定檔> <import.out> [<import.shaderWork>]

`//` 開頭的註解行去掉；root 換成原設定檔的 root 解析後的絕對路徑；import.out（與 shaderWork）換成給的路徑。
import 段的其他鍵照舊。
"""
import json
import sys
from pathlib import Path

src, dst, out = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
lines = [ln for ln in src.read_text(encoding="utf-8").splitlines() if not ln.lstrip().startswith("//")]
cfg = json.loads("\n".join(lines))
cfg["root"] = str((src.parent / (cfg.get("root") or ".")).resolve())
cfg["import"]["out"] = str(out.resolve())
if len(sys.argv) > 4:
    cfg["import"]["shaderWork"] = str(Path(sys.argv[4]).resolve())
dst.parent.mkdir(parents=True, exist_ok=True)
dst.write_text(json.dumps(cfg, ensure_ascii=False, indent=1), encoding="utf-8")
print(dst)
