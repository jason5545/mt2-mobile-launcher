"""產生 target 的 managed 段（docs/phase4.md「版本綁定」）：原版 <名稱>_Data/Managed 底下每個檔的 SHA256。

  python3 -I tools/target/make-target.py <原版遊戲資料夾> <target 檔>

target 檔已經存在時只換 managed 與 dataDir，其他欄位（手寫的）照舊；不存在就只寫 schema、id（檔名）、dataDir、managed。
路徑照 UTF-8 位元組排序（同 `LC_ALL=C sort`），印出檔數與總雜湊：總雜湊是 `shasum -a 256` 格式清單（`<雜湊>  ./<路徑>`）本身的 SHA256，
跟 `cd Managed && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256 | shasum -a 256` 相同。
target 檔只有檔名與雜湊，沒有遊戲內容，可以進版控。
"""
import hashlib
import json
import sys
from pathlib import Path

if len(sys.argv) != 3:
    sys.exit(__doc__)
game, out = Path(sys.argv[1]), Path(sys.argv[2])
hits = [p for p in sorted(game.glob("*_Data")) if (p / "Managed").is_dir()]
if len(hits) != 1:
    sys.exit(f"要剛好一個 *_Data/Managed：{game}（找到 {len(hits)} 個）")
data = hits[0]
managed = data / "Managed"
files = sorted((p for p in managed.rglob("*") if p.is_file()), key=lambda p: p.relative_to(managed).as_posix().encode())
hashes = {p.relative_to(managed).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
total = hashlib.sha256("".join(f"{h}  ./{k}\n" for k, h in hashes.items()).encode()).hexdigest()

target = json.loads(out.read_text(encoding="utf-8")) if out.exists() else {"schema": 1, "id": out.stem}
target["dataDir"] = data.name
target["managed"] = hashes
out.write_text(json.dumps(target, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
print(f"{out}: dataDir={data.name} managed files={len(hashes)} total={total}")
