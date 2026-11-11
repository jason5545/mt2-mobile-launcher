"""第 6 題：APK 裡沒有任何遊戲檔或從遊戲檔產生的東西（docs/phase4.md「比對標準」）。

  python3 -I tools/phase4/apk-check.py <APK> [--target targets/x.json …] [--forbid-dir <資料夾> …] [--forbid-list <sha256 清單> …]

APK 裡要比的：每個 zip 條目的 SHA256，以及 assembly store（lib/<abi>/libassemblies.<abi>.blob.so）裡每個組件解壓後的 SHA256。
assembly store 照檔頭後面的描述表找每個組件（store_assemblies）。壓過的每塊開頭是 "XALZ"、descriptor 索引（uint32）、解壓後大小（uint32），
接著是 LZ4 block；這裡自己解 LZ4 block、解到指定大小就停（最後一塊後面接著索引資料，不能照切片解）。沒壓的照原樣取。
不能出現的雜湊：target 的 managed（原版 Managed 的每個檔）、--forbid-dir 底下每個檔（例如原版的 <名稱>_Data）、
--forbid-list 的 `shasum -a 256` 清單（例如手機 patch 的輸出、安裝的輸出）。
另外列出跟原版 Managed 同名的組件（同名不一定是遊戲檔，例如 NuGet 的 Newtonsoft.Json，雜湊不同就不是）。
最後一行 RESULT ok|FOUND，FOUND 時 exit 1。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
import zipfile
from pathlib import Path, PurePosixPath


def lz4_block(src: bytes, pos: int, size: int) -> bytes:
    out = bytearray()
    while len(out) < size:
        token = src[pos]; pos += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[pos]; pos += 1; lit += b
                if b != 255: break
        out += src[pos:pos + lit]; pos += lit
        if len(out) >= size: break
        off = src[pos] | (src[pos + 1] << 8); pos += 2
        ml = token & 15
        if ml == 15:
            while True:
                b = src[pos]; pos += 1; ml += b
                if b != 255: break
        ml += 4
        start = len(out) - off
        if off <= 0 or start < 0: raise ValueError("壞掉的 LZ4 block")
        if off >= ml: out += out[start:start + ml]
        else:
            for i in range(ml): out.append(out[start + i])  # 重疊的複製要一個一個來
    if len(out) != size: raise ValueError("LZ4 解壓後大小不對")
    return bytes(out)


def store_assemblies(blob: bytes) -> list[bytes]:
    """照 store 的描述表（XABA 檔頭 v3：magic、version、entry_count、index_entry_count、index_size，接著 index，
    再接 entry_count 個 7×uint32 描述：mapping、data 偏移、data 大小、debug、config）逐一取組件。偏移從 XABA 算。
    壓縮（XALZ）的解開，沒壓的（AndroidEnableAssemblyCompression=false，開頭 MZ）照原樣；格式對不上就報錯，不默默少算。"""
    base = blob.find(b"XABA")
    if base < 0:
        raise ValueError("assembly store 找不到 XABA 檔頭")
    _, version, entry_count, _, index_size = struct.unpack_from("<5I", blob, base)
    if version & 0xFFFF != 3:
        raise ValueError(f"assembly store 版本 0x{version:08x} 不是 v3，要更新這支檢查")
    table = base + 20 + index_size
    seen: set[int] = set()
    res = []
    for k in range(entry_count):
        _, offset, size, *_ = struct.unpack_from("<7I", blob, table + k * 28)
        if size == 0 or offset in seen:
            continue
        seen.add(offset)
        pos = base + offset
        if blob[pos:pos + 4] == b"XALZ":
            _, length = struct.unpack_from("<II", blob, pos + 4)
            res.append(lz4_block(blob, pos + 12, length))
        elif blob[pos:pos + 2] == b"MZ":
            res.append(blob[pos:pos + size])
        else:
            raise ValueError(f"assembly store 偏移 {offset} 不是 XALZ 也不是 PE")
    if not res:
        raise ValueError("assembly store 裡沒有讀到任何組件")
    return res


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("apk")
    ap.add_argument("--target", action="append", default=[])
    ap.add_argument("--forbid-dir", action="append", default=[])
    ap.add_argument("--forbid-list", action="append", default=[])
    a = ap.parse_args()

    forbid: dict[str, str] = {}
    names: set[str] = set()
    for t in a.target:
        j = json.load(open(t, encoding="utf-8"))
        for k, h in j["managed"].items():
            forbid[h] = f"{j['id']}:Managed/{k}"
            names.add(PurePosixPath(k).name)
    for d in a.forbid_dir:
        for p in Path(d).rglob("*"):
            if p.is_file() and p.stat().st_size > 0:
                forbid.setdefault(hashlib.sha256(p.read_bytes()).hexdigest(), f"{d}:{p.relative_to(d)}")
    for lst in a.forbid_list:
        for line in open(lst, encoding="utf-8"):
            h, _, path = line.rstrip("\n").partition("  ")
            if h != hashlib.sha256(b"").hexdigest():  # 空檔不算
                forbid.setdefault(h, f"{lst}:{path}")
    print(f"不能出現的雜湊：{len(forbid)} 個（不算 0 位元組的檔）")

    z = zipfile.ZipFile(a.apk)
    have: dict[str, str] = {}
    entries = 0
    for info in z.infolist():
        if info.is_dir(): continue
        data = z.read(info)
        entries += 1
        have.setdefault(hashlib.sha256(data).hexdigest(), info.filename)
        if PurePosixPath(info.filename).name.startswith("libassemblies.") and info.filename.endswith(".blob.so"):
            asms = store_assemblies(data)
            print(f"{info.filename}：{len(asms)} 個組件")
            for n, asm in enumerate(asms):
                have.setdefault(hashlib.sha256(asm).hexdigest(), f"{info.filename}#{n}")
    print(f"APK：{entries} 個條目，加上 assembly store 共 {len(have)} 個不同的雜湊")

    same_name = sorted(n for n in names if any(PurePosixPath(f).name == n for f in z.namelist()))
    if same_name: print("跟原版 Managed 同名的 zip 條目：" + "、".join(same_name))

    found = sorted(set(have) & set(forbid))
    for h in found: print(f"FOUND {have[h]} = {forbid[h]}")
    print(f"交集：{len(found)} 個")
    print("RESULT", "ok" if not found else "FOUND")
    return 0 if not found else 1


if __name__ == "__main__":
    sys.exit(main())
