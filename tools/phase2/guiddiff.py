"""照 Phase 2 的比對標準比兩份 AssetRipper 匯出，或兩份 importer 輸出（docs/phase2.md「比對標準與記憶體」）。

AssetRipper 每次匯出的 GUID 都是隨機的，`.meta` 的 timeCreated 是匯出當下的時間。標準是：GUID 一對一換名、
去掉 timeCreated 後逐位元組相同。

  export <基準 ExportedProject> <另一份 ExportedProject> [--map 對照表.json]
      第一輪照 .meta 的路徑把另一份的 GUID 對到基準的，所有文字檔裡的 32 位 id 一起換，去掉 timeCreated 那行；
      第二輪把剩下、沒有 .meta 的 id 照同一行同一位置配對，配對要一對一、前後一致。二進位檔不換，直接比。
      --map 把兩輪合起來的對照表（另一份 → 基準）寫成 JSON，給 import 用。
      還不同的 JSON（AuxiliaryFiles/path_id_map.json）忽略順序後相同的，算「只差順序或計時」。
  import <基準 importer 輸出> <另一份 importer 輸出> --map 對照表.json
      另一份的路徑與內容裡的 32 位 id 照對照表換掉（報告的鍵名「名稱（id 前 8 碼）」也換），再逐位元組比。
      還不同的 JSON 再分兩類：忽略物件與清單的順序、去掉最上層的 seconds（報告的計時）後相同的，算「只差順序或計時」
      （照 GUID 排序的清單、分包，GUID 換名後順序本來就會變）；其餘算真的不同。

最後一行是 `RESULT same`、`RESULT same-except-order-or-timing` 或 `RESULT different`，different 時 exit 1。
"""
from __future__ import annotations

import json
import os
import re
import sys
from collections import Counter, defaultdict

ID_RE = re.compile(rb"\b[0-9a-f]{32}\b")
ID_STR_RE = re.compile(r"\b[0-9a-f]{32}\b")
META_GUID_RE = re.compile(rb"^guid: ([0-9a-f]{32})$", re.M)
TIME_RE = re.compile(rb"^timeCreated: \d+\n", re.M)
# AssetRipper 匯出的 YAML 文字檔；其他（貼圖、音訊、TextAsset、反編譯的 .cs）不換 id，直接比
YAML_EXT = (".meta", ".asset", ".mat", ".prefab", ".controller", ".unity", ".mixer", ".anim",
            ".overrideController", ".physicMaterial", ".physicsMaterial2D", ".mask", ".renderTexture",
            ".spriteatlas", ".cubemap", ".playable", ".signal", ".lighting", ".giparams", ".fontsettings")
SHOW = 20


def walk(root: str) -> dict[str, str]:
    out = {}
    for d, _, files in os.walk(root):
        for f in files:
            p = os.path.join(d, f)
            out[os.path.relpath(p, root)] = p
    return out


def read(p: str) -> bytes:
    with open(p, "rb") as f:
        return f.read()


def sub_ids(b: bytes, table: dict[bytes, bytes]) -> bytes:
    return ID_RE.sub(lambda m: table.get(m.group(0), m.group(0)), b)


def report(name: str, base: dict, other: dict, changed: list[str]) -> bool:
    only_base, only_other = sorted(base.keys() - other.keys()), sorted(other.keys() - base.keys())
    print(f"{name}: base={len(base)} other={len(other)} only_base={len(only_base)} only_other={len(only_other)} different={len(changed)}")
    print("  different by extension:", dict(Counter(os.path.splitext(p)[1] for p in changed).most_common()))
    for label, xs in (("only_base", only_base), ("only_other", only_other), ("different", changed)):
        if xs:
            print(f"  {label} first {min(SHOW, len(xs))}:", xs[:SHOW])
    return not (only_base or only_other or changed)


def first_line_diff(a: bytes, b: bytes) -> str:
    for i, (x, y) in enumerate(zip(a.split(b"\n"), b.split(b"\n"))):
        if x != y:
            return f"line {i + 1}\n    base:  {x[:200].decode(errors='replace')}\n    other: {y[:200].decode(errors='replace')}"
    return f"length {len(a)} vs {len(b)}"


def cmd_export(base_root: str, other_root: str, map_out: str | None) -> str:
    base, other = walk(base_root), walk(other_root)
    gb = {rel[:-5]: g.group(1) for rel, p in base.items() if rel.endswith(".meta") and (g := META_GUID_RE.search(read(p)))}
    go = {rel[:-5]: g.group(1) for rel, p in other.items() if rel.endswith(".meta") and (g := META_GUID_RE.search(read(p)))}
    table = {g: gb[path] for path, g in go.items() if path in gb}
    print(f"pass 1: .meta guids base={len(gb)} other={len(go)} mapped={len(table)}")

    common = sorted(base.keys() & other.keys())
    raw = [rel for rel in common if read(base[rel]) != read(other[rel])]
    print(f"raw different={len(raw)} binary(non-YAML) different={sum(not r.endswith(YAML_EXT) for r in raw)}")

    def norm_base(rel: str) -> bytes:
        b = read(base[rel])
        return TIME_RE.sub(b"", b) if rel.endswith(YAML_EXT) else b

    def norm_other(rel: str, t: dict) -> bytes:
        b = read(other[rel])
        return sub_ids(TIME_RE.sub(b"", b), t) if rel.endswith(YAML_EXT) else b

    left = [rel for rel in raw if norm_base(rel) != norm_other(rel, table)]
    print(f"pass 1 left={len(left)}", dict(Counter(os.path.splitext(p)[1] for p in left).most_common()))

    pairs: dict[bytes, set[bytes]] = defaultdict(set)
    structural = []
    for rel in left:
        if not rel.endswith(YAML_EXT):
            continue
        la, lb = norm_base(rel).split(b"\n"), norm_other(rel, table).split(b"\n")
        if len(la) != len(lb):
            structural.append(rel)
            continue
        for x, y in zip(la, lb):
            if x == y:
                continue
            ia, ib = ID_RE.findall(x), ID_RE.findall(y)
            if len(ia) != len(ib) or ID_RE.sub(b"G", x) != ID_RE.sub(b"G", y):
                structural.append(rel)
                break
            for u, v in zip(ia, ib):
                if u != v:
                    pairs[v].add(u)
    reverse: dict[bytes, set[bytes]] = defaultdict(set)
    for v, us in pairs.items():
        for u in us:
            reverse[u].add(v)
    bad = sum(len(us) != 1 for us in pairs.values()) + sum(len(vs) != 1 for vs in reverse.values())
    clash = sum(1 for v in pairs if v in table)
    print(f"pass 2: positional ids={len(pairs)} inconsistent={bad} already-mapped={clash} structural={len(structural)}")
    table2 = dict(table)
    if not bad and not clash:
        table2.update({v: next(iter(us)) for v, us in pairs.items()})
    changed, order_only = [], []
    for rel in left:
        a, b = norm_base(rel), norm_other(rel, table2)
        if a == b:
            continue
        # AuxiliaryFiles/path_id_map.json 的檔案順序照讀到的順序，不同檔案系統列目錄的順序不同
        if rel.endswith(".json") and canon(json.loads(a)) == canon(json.loads(sub_ids(b, table2))):
            order_only.append(rel)
            continue
        changed.append(rel)
    for rel in changed[:3]:
        print(f"  first difference {rel}: {first_line_diff(norm_base(rel), norm_other(rel, table2))}")
    print(f"order-or-timing only={len(order_only)}:", order_only[:SHOW])
    ok = report("after pass 2", base, other, changed) and not bad and not clash
    if map_out:
        with open(map_out, "w") as f:
            json.dump({k.decode(): v.decode() for k, v in sorted(table2.items())}, f, indent=0)
        print(f"map: {len(table2)} ids -> {map_out}")
    if not ok:
        return "different"
    return "same-except-order-or-timing" if order_only else "same"


def canon(x):
    if isinstance(x, dict):
        return {k: canon(v) for k, v in sorted(x.items())}
    if isinstance(x, list):
        return sorted((canon(v) for v in x), key=lambda v: json.dumps(v, sort_keys=True))
    return x


def cmd_import(base_root: str, other_root: str, map_in: str) -> str:
    with open(map_in) as f:
        tstr = json.load(f)
    table = {k.encode(): v.encode() for k, v in tstr.items()}
    # 報告的鍵名用 id 前 8 碼（「名稱（xxxxxxxx）」），前 8 碼撞號的不換
    p8count = Counter(k[:8] for k in tstr)
    prefix = {k[:8].encode(): v[:8].encode() for k, v in tstr.items() if p8count[k[:8]] == 1}
    prefix_re = re.compile("（([0-9a-f]{8})）".encode())
    print(f"map: {len(table)} ids, {len(prefix)} unique 8-char prefixes")

    def sub_all(b: bytes) -> bytes:
        b = sub_ids(b, table)
        return prefix_re.sub(lambda m: "（".encode() + prefix.get(m.group(1), m.group(1)) + "）".encode(), b)

    base = walk(base_root)
    other = {}
    for rel, p in walk(other_root).items():
        other[ID_STR_RE.sub(lambda m: tstr.get(m.group(0), m.group(0)), rel)] = p
    changed, order_only = [], []
    for rel in sorted(base.keys() & other.keys()):
        a, b = read(base[rel]), read(other[rel])
        if a == b:
            continue
        b = sub_all(b)
        if a == b:
            continue
        if rel.endswith(".json"):
            ja, jb = json.loads(a), json.loads(b)
            if isinstance(ja, dict) and isinstance(jb, dict):
                ja.pop("seconds", None)
                jb.pop("seconds", None)
            if canon(ja) == canon(jb):
                order_only.append(rel)
                continue
        changed.append(rel)
    for rel in changed[:3]:
        print(f"  first difference {rel}: {first_line_diff(read(base[rel]), sub_all(read(other[rel])))}")
    print(f"order-or-timing only={len(order_only)}:", order_only[:SHOW])
    if not report("import", base, other, changed):
        return "different"
    return "same-except-order-or-timing" if order_only else "same"


def main() -> None:
    args = sys.argv[1:]
    opt = None
    if "--map" in args:
        i = args.index("--map")
        opt = args[i + 1]
        del args[i:i + 2]
    if len(args) != 3 or args[0] not in ("export", "import") or (args[0] == "import" and not opt):
        raise SystemExit(__doc__)
    result = cmd_export(args[1], args[2], opt) if args[0] == "export" else cmd_import(args[1], args[2], opt)
    print(f"RESULT {result}")
    sys.exit(1 if result == "different" else 0)


main()
