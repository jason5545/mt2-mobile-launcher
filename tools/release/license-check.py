#!/usr/bin/env python3
"""比對 APK 中的 .NET 組件與原生函式庫是否列入第三方授權文件。"""

from __future__ import annotations

import argparse
import importlib.util
import struct
import sys
import zipfile
from collections import defaultdict
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
NOTICES_HEADING = "## APK 元件授權對照"


class AssemblyFormatError(ValueError):
    pass


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def u64(data: bytes, offset: int) -> int:
    return struct.unpack_from("<Q", data, offset)[0]


def align4(value: int) -> int:
    return (value + 3) & ~3


def rva_offset(data: bytes, sections: list[tuple[int, int, int, int]], rva: int) -> int:
    for virtual_address, virtual_size, raw_offset, raw_size in sections:
        span = max(virtual_size, raw_size)
        if virtual_address <= rva < virtual_address + span:
            offset = raw_offset + rva - virtual_address
            if offset >= raw_offset + raw_size or offset >= len(data):
                break
            return offset
    raise AssemblyFormatError(f"無法將 RVA 0x{rva:x} 對應到檔案位移")


def metadata_streams(data: bytes) -> dict[str, tuple[int, int]]:
    if data[:2] != b"MZ" or len(data) < 0x40:
        raise AssemblyFormatError("不是 PE 檔")
    pe = u32(data, 0x3C)
    if data[pe : pe + 4] != b"PE\0\0":
        raise AssemblyFormatError("PE 簽章不正確")

    section_count = u16(data, pe + 6)
    optional_size = u16(data, pe + 20)
    optional = pe + 24
    magic = u16(data, optional)
    if magic == 0x10B:
        directories = optional + 96
        directory_count = u32(data, optional + 92)
    elif magic == 0x20B:
        directories = optional + 112
        directory_count = u32(data, optional + 108)
    else:
        raise AssemblyFormatError("PE optional header 格式不支援")
    if directory_count <= 14:
        raise AssemblyFormatError("PE 沒有 CLI 資料目錄")

    cli_rva = u32(data, directories + 14 * 8)
    if not cli_rva:
        raise AssemblyFormatError("PE 沒有 CLI 標頭")

    section_start = optional + optional_size
    sections = []
    for index in range(section_count):
        offset = section_start + index * 40
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from(
            "<IIII", data, offset + 8
        )
        sections.append((virtual_address, virtual_size, raw_offset, raw_size))

    cli = rva_offset(data, sections, cli_rva)
    metadata_rva = u32(data, cli + 8)
    metadata = rva_offset(data, sections, metadata_rva)
    if data[metadata : metadata + 4] != b"BSJB":
        raise AssemblyFormatError("CLI metadata 簽章不正確")

    version_length = u32(data, metadata + 12)
    cursor = metadata + align4(16 + version_length)
    stream_count = u16(data, cursor + 2)
    cursor += 4
    streams: dict[str, tuple[int, int]] = {}
    for _ in range(stream_count):
        relative_offset, size = struct.unpack_from("<II", data, cursor)
        cursor += 8
        name_start = cursor
        end = data.find(b"\0", name_start)
        if end < 0:
            raise AssemblyFormatError("metadata stream 名稱未結束")
        name = data[name_start:end].decode("ascii")
        cursor = name_start + align4(end - name_start + 1)
        streams[name] = (metadata + relative_offset, size)
    return streams


CODED_TABLES: dict[str, tuple[int, tuple[int, ...]]] = {
    "ResolutionScope": (2, (0, 26, 35, 1)),
    "TypeDefOrRef": (2, (2, 1, 27)),
    "HasConstant": (2, (4, 8, 23)),
    "HasCustomAttribute": (5, (6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43)),
    "CustomAttributeType": (3, (6, 10)),
    "HasFieldMarshal": (1, (4, 8)),
    "HasDeclSecurity": (2, (2, 6, 32)),
    "MemberRefParent": (3, (2, 1, 26, 6, 27)),
    "HasSemantics": (1, (20, 23)),
    "MethodDefOrRef": (1, (6, 10)),
    "MemberForwarded": (1, (4, 6)),
}


def assembly_name(data: bytes) -> str:
    try:
        streams = metadata_streams(data)
        table_stream = streams.get("#~") or streams.get("#-")
        strings_stream = streams.get("#Strings")
        if table_stream is None or strings_stream is None:
            raise AssemblyFormatError("缺少 metadata tables 或 strings stream")

        table_start, _table_size = table_stream
        heap_sizes = data[table_start + 6]
        valid = u64(data, table_start + 8)
        row_cursor = table_start + 24
        row_counts = [0] * 64
        for table in range(64):
            if valid & (1 << table):
                row_counts[table] = u32(data, row_cursor)
                row_cursor += 4

        string_size = 4 if heap_sizes & 0x01 else 2
        guid_size = 4 if heap_sizes & 0x02 else 2
        blob_size = 4 if heap_sizes & 0x04 else 2

        def table_size(table: int) -> int:
            return 4 if row_counts[table] >= 0x10000 else 2

        def coded_size(name: str) -> int:
            tag_bits, tables = CODED_TABLES[name]
            maximum = max((row_counts[table] for table in tables), default=0)
            return 2 if maximum < (1 << (16 - tag_bits)) else 4

        row_sizes = {
            0: 2 + string_size + 3 * guid_size,
            1: coded_size("ResolutionScope") + 2 * string_size,
            2: 4 + 2 * string_size + coded_size("TypeDefOrRef") + table_size(4) + table_size(6),
            3: table_size(4),
            4: 2 + string_size + blob_size,
            5: table_size(6),
            6: 8 + string_size + blob_size + table_size(8),
            7: table_size(8),
            8: 4 + string_size,
            9: table_size(2) + coded_size("TypeDefOrRef"),
            10: coded_size("MemberRefParent") + string_size + blob_size,
            11: 2 + coded_size("HasConstant") + blob_size,
            12: coded_size("HasCustomAttribute") + coded_size("CustomAttributeType") + blob_size,
            13: coded_size("HasFieldMarshal") + blob_size,
            14: 2 + coded_size("HasDeclSecurity") + blob_size,
            15: 6 + table_size(2),
            16: 4 + table_size(4),
            17: blob_size,
            18: table_size(2) + table_size(20),
            19: table_size(20),
            20: 2 + string_size + coded_size("TypeDefOrRef"),
            21: table_size(2) + table_size(23),
            22: table_size(23),
            23: 2 + string_size + blob_size,
            24: 2 + table_size(6) + coded_size("HasSemantics"),
            25: table_size(2) + 2 * coded_size("MethodDefOrRef"),
            26: string_size,
            27: blob_size,
            28: 2 + coded_size("MemberForwarded") + string_size + table_size(26),
            29: 4 + table_size(4),
            30: 8,
            31: 4,
        }

        assembly_rows = row_counts[32]
        if assembly_rows < 1:
            raise AssemblyFormatError("metadata 沒有 Assembly row")
        assembly_offset = row_cursor + sum(
            row_counts[table] * row_sizes[table] for table in range(32)
        )
        name_offset_in_row = 16 + blob_size
        name_index_size = string_size
        name_index = int.from_bytes(
            data[assembly_offset + name_offset_in_row : assembly_offset + name_offset_in_row + name_index_size],
            "little",
        )
        strings_start, strings_size = strings_stream
        if name_index >= strings_size:
            raise AssemblyFormatError("Assembly name 的 strings index 超出範圍")
        start = strings_start + name_index
        end = data.find(b"\0", start, strings_start + strings_size)
        if end < 0:
            raise AssemblyFormatError("Assembly name 未結束")
        name = data[start:end].decode("utf-8")
        if not name:
            raise AssemblyFormatError("Assembly name 是空字串")
        return name
    except (IndexError, struct.error, UnicodeDecodeError) as exc:
        raise AssemblyFormatError(f"metadata 讀取失敗：{exc}") from exc


def notices_components(path: Path) -> set[str]:
    text = path.read_text(encoding="utf-8")
    start = text.find(NOTICES_HEADING)
    if start < 0:
        raise ValueError(f"授權文件缺少「{NOTICES_HEADING}」章節")
    following = text.find("\n## ", start + len(NOTICES_HEADING))
    section = text[start:] if following < 0 else text[start:following]
    components = set()
    for line in section.splitlines():
        if not line.startswith("|"):
            continue
        first_cell = line.split("|", 2)[1].strip()
        if len(first_cell) >= 2 and first_cell[0] == "`" and first_cell[-1] == "`":
            components.add(first_cell[1:-1])
    return components


def load_apk_checker():
    path = ROOT / "tools" / "phase4" / "apk-check.py"
    spec = importlib.util.spec_from_file_location("mt2_apk_check", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("無法載入 tools/phase4/apk-check.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path, help="要比對的 APK")
    parser.add_argument(
        "--notices", type=Path, default=ROOT / "THIRD_PARTY_NOTICES.md", help="授權文件（預設為 repo 根目錄的 THIRD_PARTY_NOTICES.md）"
    )
    args = parser.parse_args()
    if not args.apk.is_file():
        parser.error(f"找不到 APK：{args.apk}")
    if not args.notices.is_file():
        parser.error(f"找不到授權文件：{args.notices}")

    try:
        documented = notices_components(args.notices)
        apkcheck = load_apk_checker()
        assembly_locations: dict[str, list[str]] = defaultdict(list)
        store_count = 0
        loose_count = 0
        native_paths: set[str] = set()

        with zipfile.ZipFile(args.apk) as archive:
            for info in archive.infolist():
                if info.is_dir():
                    continue
                if info.filename.endswith(".so"):
                    native_paths.add(info.filename)
                    if info.filename.endswith(".blob.so"):
                        for index, blob in enumerate(apkcheck.store_assemblies(archive.read(info))):
                            store_count += 1
                            name = assembly_name(blob)
                            assembly_locations[name].append(f"{info.filename}#{index}")
                elif info.filename.endswith(".dll"):
                    loose_count += 1
                    name = assembly_name(archive.read(info))
                    assembly_locations[name].append(info.filename)

        apk_components = {f"assembly:{name}" for name in assembly_locations}
        apk_components.update(f"native:{path}" for path in native_paths)
        missing = sorted(apk_components - documented)

        print(
            f"APK：assembly store {store_count} 個、APK loose .NET 組件 {loose_count} 個、"
            f"不同 .NET 組件 {len(assembly_locations)} 個、.so {len(native_paths)} 個"
        )
        print(f"對不上：{len(missing)} 個")
        for component in missing:
            print(f"MISSING {component}")
        print("RESULT", "ok" if not missing else "MISSING")
        return 1 if missing else 0
    except (AssemblyFormatError, OSError, ValueError, zipfile.BadZipFile, RuntimeError) as exc:
        print(f"ERROR {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
