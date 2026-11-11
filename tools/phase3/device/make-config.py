"""從遊戲的 cs2ts.json 產生 importer／shaders 的設定檔，路徑全部指定成絕對路徑（Mac 的基準或手機上 app 的 files/ 底下）。

  python3 tools/phase3/device/make-config.py <原 cs2ts.json> <新設定檔> --root R --export E --game G --out O --shader-work W

`//` 開頭的註解行去掉；root 與 import 段的 export、game、out、shaderWork（有的話加 shaderExport）換成給的路徑，import 段的其他鍵照舊。
給的路徑原樣寫進去（手機的路徑在 Mac 上不存在，不解析）。charsetSources 是相對 root 的路徑，不換：資料要另外推到新 root 底下（--list-sources）。
"""
import argparse
import json
from pathlib import Path

ap = argparse.ArgumentParser()
ap.add_argument("src")
ap.add_argument("dst")
ap.add_argument("--root", required=True)
ap.add_argument("--export", required=True)
ap.add_argument("--game", required=True)
ap.add_argument("--out", required=True)
ap.add_argument("--shader-work", required=True)
ap.add_argument("--shader-export", help="原設定檔有 shaderExport（Diceomancer）才換成這個")
ap.add_argument("--list-sources", action="store_true",
                help="另外印 charsetSources：每行「相對 root 的路徑<TAB>原設定檔的 root 解析後的本機路徑」，push-phone.sh 照這個推")
a = ap.parse_args()

src = Path(a.src)
lines = [ln for ln in src.read_text(encoding="utf-8").splitlines() if not ln.lstrip().startswith("//")]
cfg = json.loads("\n".join(lines))
local_root = (src.parent / (cfg.get("root") or ".")).resolve()
cfg["root"] = a.root
imp = cfg["import"]
imp["export"], imp["game"], imp["out"], imp["shaderWork"] = a.export, a.game, a.out, a.shader_work
if imp.get("shaderExport"):
    if not a.shader_export:
        ap.error("原設定檔有 shaderExport，要給 --shader-export")
    imp["shaderExport"] = a.shader_export
Path(a.dst).parent.mkdir(parents=True, exist_ok=True)
Path(a.dst).write_text(json.dumps(cfg, ensure_ascii=False, indent=1), encoding="utf-8")
print(a.dst)
if a.list_sources:
    for s in imp.get("charsetSources") or []:
        print(f"{s}\t{local_root / s}")
