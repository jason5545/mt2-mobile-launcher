# /// script
# requires-python = ">=3.11"
# dependencies = ["Pillow>=11"]
# ///
"""tools/phase2/guiddiff.py 的 Phase 3 版：比兩份 AssetRipper 匯出（export），或兩份 importer 輸出（import）。

  uv run tools/phase3/device/guiddiff3.py export <基準匯出> <另一份匯出> [--map 對照表.json]
  uv run tools/phase3/device/guiddiff3.py import <基準 importer 輸出> <另一份 importer 輸出> --map 對照表.json

跟 Phase 2 版一樣的部分（docs/phase2.md「比對標準與記憶體」）：GUID 一對一換名、去掉 .meta 的 timeCreated 後逐位元組相同；
第一輪照 .meta 的路徑配 GUID，第二輪把沒有 .meta 的 id 照同一行同一位置配對；import 模式照對照表換路徑與內容裡的 id，
只差順序或計時的 JSON 另外列。

Phase 3 加的（docs/prompts/phase3-device.md「匯出比對標準的放寬」，Jason 10/22 定）：
- export：第一輪之後還不同、或只在一邊的檔（有 .meta 的），改照內容一對一配對。AssetRipper 照列目錄的順序讀 level*、
  不排序，Mac（APFS）與手機（f2fs）的順序不同，沒有名字的 MonoBehaviour 編號互換、共用的 lightmap 歸到別的場景資料夾。
  內容的比法：去掉 timeCreated，這些候選檔自己的 GUID 遮成同一個記號，其餘的 id 照第一輪的對照表換。
  同一份內容兩邊各只有一個才配；兩邊都多於一個的，路徑相同的照舊、其餘算配不上。配完照新的對應重換 GUID，
  對照表要維持一對一，不是的話不採用照內容的配對。輸出列出照內容配對的檔數（連同 .meta）與配完之後還不同的檔。
  --map 寫 {"ids": GUID 對照, "paths": 換了名的檔（相對於 ExportedProject/Assets，另一份 → 基準）}。
- export 的同內容組（Phase 4 拆段 B，Jason 10/22 定「內容完全相同的一組檔可以互換，照組配對」）：有 .meta 的資產，內容（YAML 遮掉自己的 GUID、
  去掉 timeCreated、其餘的 id 照第一輪換）加上 .meta（同樣處理）完全相同的，兩邊各有 N 個（N > 1、兩邊個數相同）就算一組：組內成員的 GUID
  比對時都換成同一個組記號（feedface 開頭的 32 位十六進位），路徑配不上的成員在組內照路徑排序配對。兩邊個數不同的不成組，照舊算不同。
  例：12 張逐位元組相同的 FX_Flash*.png，讀檔順序不同時編號互換，材質引用的是組裡的哪一張不影響。--map 多寫 {"groups": {組記號: [基準的 GUID…]}}。
- import：路徑字串（importer 輸出的 "src" 這類整串 JSON 字串）照 paths 換；assets/glyphs.json 另外去掉 report.seconds
  （Phase 3 的第四個計時欄位）；assets/textures/builtin-*.png 位元組不同時比尺寸、模式、像素（Phase 3 的標準）。
  有 groups 的話，兩邊內容裡組成員的 GUID 都換成組記號（assets/materials.json 這類引用）。
  shaders/ 不用另外處理：檔名是 shader 名稱，裡面的 32 位十六進位只有 GLSL 的浮點數字與 keywordStateMask。

最後一行是 `RESULT same`、`RESULT same-except-order-or-timing` 或 `RESULT different`，different 時 exit 1。
"""
from __future__ import annotations

import hashlib
import io
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
ASSETS_PREFIX = "ExportedProject/Assets/"
MASK = b"<content-paired>"
OWN = b"<own-guid>"
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


def first_line_diff(a: bytes, b: bytes) -> str:
    for i, (x, y) in enumerate(zip(a.split(b"\n"), b.split(b"\n"))):
        if x != y:
            return f"line {i + 1}\n    base:  {x[:200].decode(errors='replace')}\n    other: {y[:200].decode(errors='replace')}"
    return f"length {len(a)} vs {len(b)}"


def canon(x):
    if isinstance(x, dict):
        return {k: canon(v) for k, v in sorted(x.items())}
    if isinstance(x, list):
        return sorted((canon(v) for v in x), key=lambda v: json.dumps(v, sort_keys=True))
    return x


def show(label: str, xs: list) -> None:
    if xs:
        print(f"  {label} first {min(SHOW, len(xs))}:", xs[:SHOW])


def meta_guids(files: dict[str, str]) -> dict[str, bytes]:
    return {rel[:-5]: g.group(1) for rel, p in files.items()
            if rel.endswith(".meta") and (g := META_GUID_RE.search(read(p)))}


def cmd_export(base_root: str, other_root: str, map_out: str | None) -> str:
    base, other = walk(base_root), walk(other_root)
    gb, go = meta_guids(base), meta_guids(other)
    table = {g: gb[path] for path, g in go.items() if path in gb}
    print(f"pass 1: .meta guids base={len(gb)} other={len(go)} mapped={len(table)}")

    common = sorted(base.keys() & other.keys())
    raw = [rel for rel in common if read(base[rel]) != read(other[rel])]
    print(f"raw different={len(raw)} binary(non-YAML) different={sum(not r.endswith(YAML_EXT) for r in raw)}")

    # 同內容的組：兩邊同一份內容各有 N 個（N > 1、個數相同），組內成員的 GUID 換成同一個組記號
    def content_key(files: dict[str, str], guids: dict[str, bytes], rel: str, t: dict | None) -> str:
        own = guids[rel]
        def n(b: bytes, yaml: bool) -> bytes:
            if not yaml:
                return b
            b = TIME_RE.sub(b"", b).replace(own, OWN)
            return sub_ids(b, t) if t is not None else b
        body = n(read(files[rel]), rel.endswith(YAML_EXT))
        meta = n(read(files[rel + ".meta"]), True)
        return os.path.splitext(rel)[1] + ":" + hashlib.sha256(body).hexdigest() + ":" + hashlib.sha256(meta).hexdigest()

    def dup_groups(files: dict[str, str], guids: dict[str, bytes], t: dict | None) -> dict[str, list[str]]:
        by: dict[str, list[str]] = defaultdict(list)
        for rel in guids:
            if rel in files and rel + ".meta" in files:
                by[content_key(files, guids, rel, t)].append(rel)
        return {k: sorted(v) for k, v in by.items() if len(v) > 1}

    dgb, dgo = dup_groups(base, gb, None), dup_groups(other, go, table)
    gtok: dict[bytes, bytes] = {}
    moved_g: dict[str, str] = {}
    group_list: dict[str, list[str]] = {}
    mismatch = sorted(k for k in dgb.keys() | dgo.keys() if len(dgb.get(k, [])) != len(dgo.get(k, [])))
    for i, k in enumerate(sorted(dgb.keys() & dgo.keys())):
        bs, os_ = dgb[k], dgo[k]
        if len(bs) != len(os_):
            continue
        tok = f"feedface{i:024x}".encode()
        for r in bs:
            gtok[gb[r]] = tok
        group_list[tok.decode()] = sorted(gb[r].decode() for r in bs)
        # 路徑配不上的成員在組內照路徑排序配對；配上的照第一輪
        for o, b in zip(sorted(set(os_) - set(bs)), sorted(set(bs) - set(os_))):
            table[go[o]] = gb[b]
            moved_g[o] = b
    grouped_b = {r for k in dgb.keys() & dgo.keys() if len(dgb[k]) == len(dgo[k]) for r in dgb[k]}
    grouped_o = {r for k in dgb.keys() & dgo.keys() if len(dgb[k]) == len(dgo[k]) for r in dgo[k]}
    n_files = sum(len(v) for v in group_list.values())
    print(f"pass group: groups={len(group_list)} files={n_files} (+{n_files} .meta) group-paired (name differs)={len(moved_g)} "
          f"size-mismatch={len(mismatch)}")
    show("groups (first member)", [f"{len(dgb[k])}x {dgb[k][0]}" for k in sorted(dgb.keys() & dgo.keys()) if len(dgb[k]) == len(dgo[k])])
    show("size-mismatch", [f"base {len(dgb.get(k, []))} other {len(dgo.get(k, []))}: {(dgb.get(k) or dgo.get(k))[0]}" for k in mismatch])

    def gsub(b: bytes) -> bytes:
        return sub_ids(b, gtok) if gtok else b

    def norm_base(rel: str) -> bytes:
        b = read(base[rel])
        return gsub(TIME_RE.sub(b"", b)) if rel.endswith(YAML_EXT) else b

    def norm_other(rel: str, t: dict) -> bytes:
        b = read(other[rel])
        return gsub(sub_ids(TIME_RE.sub(b"", b), t)) if rel.endswith(YAML_EXT) else b

    left = [rel for rel in raw if norm_base(rel) != norm_other(rel, table)]
    print(f"pass 1 left={len(left)}", dict(Counter(os.path.splitext(p)[1] for p in left).most_common()))

    # 照內容配對：第一輪之後還不同、或只在一邊的檔（有 .meta、本身不是 .meta）
    cand_b = sorted(({r for r in left if r in gb} | {r for r in base.keys() - other.keys() if r in gb}) - grouped_b)
    cand_o = sorted(({r for r in left if r in go} | {r for r in other.keys() - base.keys() if r in go}) - grouped_o)
    mask_b = {gb[r] for r in cand_b}
    mask_o = {go[r] for r in cand_o}

    def key_base(rel: str) -> str:
        b = read(base[rel])
        if rel.endswith(YAML_EXT):
            b = gsub(ID_RE.sub(lambda m: MASK if m.group(0) in mask_b else m.group(0), TIME_RE.sub(b"", b)))
        return os.path.splitext(rel)[1] + ":" + hashlib.sha256(b).hexdigest()

    def key_other(rel: str) -> str:
        b = read(other[rel])
        if rel.endswith(YAML_EXT):
            b = gsub(ID_RE.sub(lambda m: MASK if m.group(0) in mask_o else table.get(m.group(0), m.group(0)), TIME_RE.sub(b"", b)))
        return os.path.splitext(rel)[1] + ":" + hashlib.sha256(b).hexdigest()

    kb: dict[str, list[str]] = defaultdict(list)
    ko: dict[str, list[str]] = defaultdict(list)
    for r in cand_b:
        kb[key_base(r)].append(r)
    for r in cand_o:
        ko[key_other(r)].append(r)
    cpairs: dict[str, str] = {}
    ambiguous = []
    for k in kb.keys() & ko.keys():
        bs, os_ = kb[k], ko[k]
        if len(bs) == 1 and len(os_) == 1:
            cpairs[os_[0]] = bs[0]
        else:
            for r in sorted(set(bs) & set(os_)):
                cpairs[r] = r
            ambiguous += sorted(set(bs) ^ set(os_))
    moved = {o: b for o, b in sorted(cpairs.items()) if o != b}
    moved.update(moved_g)  # 組內照路徑排序配對的（table 已經換過）
    table_c = dict(table)
    for o, b in moved.items():
        table_c[go[o]] = gb[b]
    dup = [v for v, n in Counter(table_c.values()).items() if n > 1]
    print(f"pass content: candidates base={len(cand_b)} other={len(cand_o)} paired={len(cpairs)} "
          f"content-paired (name differs)={len(moved)} (+{len(moved)} .meta) ambiguous={len(ambiguous)} non-one-to-one={len(dup)}")
    print("  content-paired by extension:", dict(Counter(os.path.splitext(o)[1] for o in moved).most_common()))
    show("content-paired (other -> base)", [f"{o} -> {b}" for o, b in moved.items()])
    if dup:
        print("  content pairing rejected: GUID table would not be one-to-one")
        moved, table_c = {}, dict(table)

    # 檔案的配對：路徑相同的照舊，照內容配上的（連同 .meta）換成新的對象
    moved_full = dict(moved)
    moved_full.update({o + ".meta": b + ".meta" for o, b in moved.items()})
    taken_b = set(moved_full.values())
    pairs = [(r, r) for r in common if r not in moved_full and r not in taken_b]
    pairs += [(b, o) for o, b in moved_full.items()]
    only_base = sorted(base.keys() - {b for b, _ in pairs})
    only_other = sorted(other.keys() - {o for _, o in pairs})

    def nb(rel: str) -> bytes:
        return norm_base(rel)

    def no(rel: str, t: dict) -> bytes:
        return norm_other(rel, t)

    # 照內容換了 GUID 之後，原本位元組就不同的檔全部用新的對照表重比（不只第一輪剩下的）
    raw_set = set(raw)
    left2 = [(b, o) for b, o in pairs if (b != o or b in raw_set) and nb(b) != no(o, table_c)]
    print(f"after content pass left={len(left2)}", dict(Counter(os.path.splitext(b)[1] for b, _ in left2).most_common()))

    posit: dict[bytes, set[bytes]] = defaultdict(set)
    structural = []
    for b, o in left2:
        if not b.endswith(YAML_EXT):
            continue
        la, lb = nb(b).split(b"\n"), no(o, table_c).split(b"\n")
        if len(la) != len(lb):
            structural.append(b)
            continue
        for x, y in zip(la, lb):
            if x == y:
                continue
            ia, ib = ID_RE.findall(x), ID_RE.findall(y)
            if len(ia) != len(ib) or ID_RE.sub(b"G", x) != ID_RE.sub(b"G", y):
                structural.append(b)
                break
            for u, v in zip(ia, ib):
                if u != v:
                    posit[v].add(u)
    reverse: dict[bytes, set[bytes]] = defaultdict(set)
    for v, us in posit.items():
        for u in us:
            reverse[u].add(v)
    bad = sum(len(us) != 1 for us in posit.values()) + sum(len(vs) != 1 for vs in reverse.values())
    clash = sum(1 for v in posit if v in table_c)
    print(f"pass 2: positional ids={len(posit)} inconsistent={bad} already-mapped={clash} structural={len(structural)}")
    table2 = dict(table_c)
    if not bad and not clash:
        table2.update({v: next(iter(us)) for v, us in posit.items()})
    changed, order_only = [], []
    for b, o in left2:
        x, y = nb(b), no(o, table2)
        if x == y:
            continue
        # AuxiliaryFiles/path_id_map.json 的檔案順序照讀到的順序，不同檔案系統列目錄的順序不同
        if b.endswith(".json") and canon(json.loads(x)) == canon(json.loads(sub_ids(y, table2))):
            order_only.append(b)
            continue
        changed.append(b)
    for b in changed[:3]:
        o = next(o for bb, o in left2 if bb == b)
        print(f"  first difference {b}: {first_line_diff(nb(b), no(o, table2))}")
    print(f"order-or-timing only={len(order_only)}:", order_only[:SHOW])
    print(f"after content pass and pass 2: base={len(base)} other={len(other)} only_base={len(only_base)} "
          f"only_other={len(only_other)} different={len(changed)}")
    print("  different by extension:", dict(Counter(os.path.splitext(p)[1] for p in changed).most_common()))
    show("only_base", only_base)
    show("only_other", only_other)
    show("different", changed)
    if map_out:
        paths = {o[len(ASSETS_PREFIX):]: b[len(ASSETS_PREFIX):] for o, b in moved.items()
                 if o.startswith(ASSETS_PREFIX) and b.startswith(ASSETS_PREFIX)}
        with open(map_out, "w") as f:
            json.dump({"ids": {k.decode(): v.decode() for k, v in sorted(table2.items())}, "paths": paths, "groups": group_list}, f, indent=0)
        print(f"map: {len(table2)} ids, {len(paths)} paths, {len(group_list)} groups -> {map_out}")
    if only_base or only_other or changed or bad or clash:
        return "different"
    return "same-except-order-or-timing" if order_only else "same"


def same_pixels(a: bytes, b: bytes) -> bool:
    from PIL import Image
    ia, ib = Image.open(io.BytesIO(a)), Image.open(io.BytesIO(b))
    return ia.size == ib.size and ia.mode == ib.mode and ia.tobytes() == ib.tobytes()


def cmd_import(base_root: str, other_root: str, map_in: str) -> str:
    with open(map_in) as f:
        m = json.load(f)
    tstr, pstr = (m["ids"], m.get("paths", {})) if "ids" in m else (m, {})
    table = {k.encode(): v.encode() for k, v in tstr.items()}
    # 同內容的組：基準的 GUID → 組記號；另一份先照 ids 換成基準的，再換組記號
    gtok = {g.encode(): tok.encode() for tok, gs in m.get("groups", {}).items() for g in gs}
    # 報告的鍵名用 id 前 8 碼（「名稱（xxxxxxxx）」），前 8 碼撞號的不換
    p8count = Counter(k[:8] for k in tstr)
    prefix = {k[:8].encode(): v[:8].encode() for k, v in tstr.items() if p8count[k[:8]] == 1}
    prefix_re = re.compile("（([0-9a-f]{8})）".encode())
    # 換了名的資產路徑：只換整串的 JSON 字串（前後是雙引號）
    paths = {k.encode(): v.encode() for k, v in pstr.items()}
    path_re = re.compile(rb'(?<=")(' + b"|".join(re.escape(k) for k in sorted(paths, key=len, reverse=True)) + rb')(?=")') if paths else None
    print(f"map: {len(table)} ids, {len(prefix)} unique 8-char prefixes, {len(paths)} renamed asset paths, {len(gtok)} grouped ids")

    def gsub(b: bytes) -> bytes:
        return sub_ids(b, gtok) if gtok else b

    def sub_all(b: bytes) -> bytes:
        b = sub_ids(b, table)
        b = prefix_re.sub(lambda m: "（".encode() + prefix.get(m.group(1), m.group(1)) + "）".encode(), b)
        b = path_re.sub(lambda m: paths[m.group(1)], b) if path_re else b
        return gsub(b)

    base = walk(base_root)
    other = {}
    for rel, p in walk(other_root).items():
        other[ID_STR_RE.sub(lambda m: tstr.get(m.group(0), m.group(0)), rel)] = p
    changed, order_only, pixel_same = [], [], []
    for rel in sorted(base.keys() & other.keys()):
        a, b = read(base[rel]), read(other[rel])
        if a == b:
            continue
        if re.fullmatch(r"assets/textures/builtin-[^/]*\.png", rel):
            if same_pixels(a, b):
                pixel_same.append(rel)
                continue
            changed.append(rel)
            continue
        a, b = gsub(a), sub_all(b)
        if a == b:
            continue
        if rel.endswith(".json"):
            ja, jb = json.loads(a), json.loads(b)
            if isinstance(ja, dict) and isinstance(jb, dict):
                ja.pop("seconds", None)
                jb.pop("seconds", None)
                if rel == "assets/glyphs.json":
                    for j in (ja, jb):
                        if isinstance(j.get("report"), dict):
                            j["report"].pop("seconds", None)
            if canon(ja) == canon(jb):
                order_only.append(rel)
                continue
        changed.append(rel)
    for rel in changed[:3]:
        print(f"  first difference {rel}: {first_line_diff(gsub(read(base[rel])), sub_all(read(other[rel])))}")
    print(f"builtin PNG pixel-same={len(pixel_same)}:", pixel_same)
    print(f"order-or-timing only={len(order_only)}:", order_only[:SHOW])
    only_base, only_other = sorted(base.keys() - other.keys()), sorted(other.keys() - base.keys())
    print(f"import: base={len(base)} other={len(other)} only_base={len(only_base)} only_other={len(only_other)} different={len(changed)}")
    print("  different by extension:", dict(Counter(os.path.splitext(p)[1] for p in changed).most_common()))
    show("only_base", only_base)
    show("only_other", only_other)
    show("different", changed)
    if only_base or only_other or changed:
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


if __name__ == "__main__":
    main()
