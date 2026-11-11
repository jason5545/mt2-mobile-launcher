#!/usr/bin/env python3
"""Repository guard for game payloads, generated files, and private data."""

from __future__ import annotations

import argparse
import ipaddress
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


MAX_FILE_SIZE = 5 * 1024 * 1024
COMPILER_GENERATED_LIMIT = 4
RULES = (
    "forbidden-extension",
    "forbidden-path",
    "media-file",
    "file-size",
    "decompiled-source",
    "local-path",
    "steamid64",
    "private-ip",
    "jwt",
    "secret-shaped-value",
    "private-denylist",
)
MESSAGE_RULES = {
    "local-path",
    "steamid64",
    "private-ip",
    "jwt",
    "secret-shaped-value",
    "private-denylist",
}

FORBIDDEN_EXTENSION = re.compile(
    r"(?i)\.(?:dll|exe|assets|bundle|ress|resource|apk|aab|jks|keystore|spv|spirv)$"
)
FORBIDDEN_PATH = re.compile(
    r"(?i)(?:^|/)(?:[^/]*_Data|ExportedProject|decompiled|gen)(?:/|$)"
    r"|(?:^|/)Stubs\.g\.cs$|(?:^|/)local\.properties$|^public/game(?:/|$)"
)
MEDIA_FILE = re.compile(
    r"(?i)\.(?:png|jpe?g|gif|webp|bmp|tiff?|ico|icns|psd|tga|dds|ktx2?|exr|hdr|"
    r"avif|heic|heif|svg|wav|mp3|ogg|flac|aac|m4a|wma|aiff?|mid|midi|opus|caf|amr)$"
)
LOCAL_PATH = re.compile(r"/(?:Users|home)/[^/\s<>\"']+/", re.IGNORECASE)
STEAM_ID = re.compile(r"(?<!\d)7656119\d{10}(?!\d)")
IPV4 = re.compile(r"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")
JWT = re.compile(
    r"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"
)
LONG_HEX = re.compile(r"(?i)(?<![0-9a-f])[0-9a-f]{40,}(?![0-9a-f])")
LONG_BASE64 = re.compile(r"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{40,}={0,2}(?![A-Za-z0-9+/=])")
ILSPY_HEADER = re.compile(r"//\s*Decompiled\s+with\b", re.IGNORECASE)
PRIVATE_IMPLEMENTATION = re.compile(r"<Private" + r"ImplementationDetails>")
COMPILER_GENERATED = re.compile(r"\[CompilerGenerated\]")
JSON_KEY = re.compile(r"[\"']([A-Za-z0-9_.-]+)[\"']\s*:\s*")
YAML_KEY = re.compile(r"^\s*(?:-\s*)?[\"']?([A-Za-z0-9_.-]+)[\"']?\s*:\s*(.*)$")

SECRET_WORDS = {"key", "token", "secret", "password", "passwd", "auth", "bearer", "cookie", "session", "refresh", "guard"}
HASH_WORDS = {"hash", "digest", "fingerprint", "checksum", "manifest", "commit"}


@dataclass(frozen=True)
class Violation:
    rule: str
    path: str
    line: int = 0
    commit: str = ""
    context_line: int = 0


@dataclass(frozen=True)
class AllowEntry:
    path: str
    rule: str
    reason: str


class GuardError(Exception):
    pass


def run_git(root: Path, *args: str, input_bytes: bytes | None = None) -> bytes:
    result = subprocess.run(
        ["git", *args], cwd=root, input=input_bytes, stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    if result.returncode:
        message = result.stderr.decode("utf-8", "replace").strip()
        raise GuardError(message or f"git {' '.join(args)} failed")
    return result.stdout


def repository_root(start: Path) -> Path:
    try:
        raw = run_git(start, "rev-parse", "--show-toplevel")
    except GuardError as exc:
        raise GuardError("run this command inside a Git worktree") from exc
    return Path(os.fsdecode(raw.rstrip(b"\n")))


def read_allowlist(root: Path) -> set[tuple[str, str]]:
    allowlist = root / "tools" / "guard" / "allowlist.txt"
    if not allowlist.exists():
        return set()
    entries: set[tuple[str, str]] = set()
    for number, raw in enumerate(allowlist.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        fields = raw.split("\t")
        if len(fields) != 3 or not all(field.strip() for field in fields):
            raise GuardError(f"allowlist line {number} must contain path, rule, and reason separated by tabs")
        path, rule, _reason = (field.strip() for field in fields)
        if path.startswith("/") or ".." in Path(path).parts:
            raise GuardError(f"allowlist line {number} must use a repository-relative path")
        if rule not in RULES:
            raise GuardError(f"allowlist line {number} names unknown rule {rule!r}")
        entries.add((path.replace(os.sep, "/"), rule))
    return entries


def read_denylist() -> tuple[list[str], bool]:
    configured = os.environ.get("MT2_GUARD_DENYLIST")
    if not configured:
        return [], False
    try:
        with open(configured, encoding="utf-8") as handle:
            values = [line.strip() for line in handle if line.strip() and not line.lstrip().startswith("#")]
    except OSError as exc:
        raise GuardError("MT2_GUARD_DENYLIST is set but its file cannot be read") from exc
    return list(dict.fromkeys(values)), True


def is_private_or_tailscale(address: ipaddress.IPv4Address) -> bool:
    first, second = int(address) >> 24, (int(address) >> 16) & 0xFF
    return (
        first == 10
        or (first == 172 and 16 <= second <= 31)
        or (first == 192 and second == 168)
        or (first == 100 and 64 <= second <= 127)
    )


def identifier_words(value: str) -> list[tuple[str, int, int]]:
    """Split camelCase and underscore names while retaining source offsets."""
    result: list[tuple[str, int, int]] = []
    for outer in re.finditer(r"[A-Za-z0-9_]+", value):
        token = outer.group()
        for part in re.finditer(r"[sS][hH][aA]\d+|[A-Z]+(?=[A-Z][a-z]|\d|$)|[A-Z]?[a-z]+|\d+", token):
            word = part.group().lower()
            result.append((word, outer.start() + part.start(), outer.start() + part.end()))
    return result


def key_kind(name: str) -> str:
    words = {word for word, _start, _end in identifier_words(name)}
    has_hash = bool(words & HASH_WORDS) or any(re.fullmatch(r"sha\d+", word) for word in words)
    if has_hash:
        return "hash"
    if words & SECRET_WORDS:
        return "secret"
    return "other"


def same_line_context(line: str, start: int, end: int) -> str:
    nearby_start = max(0, start - 120)
    nearby_end = min(len(line), end + 120)
    nearby = [item for item in identifier_words(line[nearby_start:nearby_end])]
    markers: list[tuple[int, str]] = []
    for word, word_start, word_end in nearby:
        absolute_start = nearby_start + word_start
        absolute_end = nearby_start + word_end
        if word in SECRET_WORDS:
            markers.append((0 if absolute_start <= end and absolute_end >= start else min(abs(absolute_start - end), abs(start - absolute_end)), "secret"))
        elif word in HASH_WORDS or re.fullmatch(r"sha\d+", word):
            markers.append((0 if absolute_start <= end and absolute_end >= start else min(abs(absolute_start - end), abs(start - absolute_end)), "hash"))
    if not markers:
        return "other"
    distance = min(distance for distance, _kind in markers)
    nearest = {kind for marker_distance, kind in markers if marker_distance == distance}
    return "hash" if "hash" in nearest else "secret"


def mapping_key_for_value(line: str, value_start: int) -> str | None:
    matches: list[tuple[int, str]] = []
    for match in JSON_KEY.finditer(line):
        if match.end() <= value_start:
            matches.append((match.end(), match.group(1)))
    yaml = YAML_KEY.match(line)
    if yaml and yaml.end(2) <= value_start:
        matches.append((yaml.end(1), yaml.group(1)))
    return max(matches, default=(0, ""), key=lambda item: item[0])[1] or None


def is_base64_candidate(value: str) -> bool:
    body = value.rstrip("=")
    # Hex digests are also legal Base64 alphabet strings; do not report them twice.
    return bool(body) and ("=" in value or any(char.lower() not in "0123456789abcdef" for char in body))


def candidate_spans(line: str) -> list[tuple[int, int, str]]:
    spans: list[tuple[int, int, str]] = []
    for match in LONG_HEX.finditer(line):
        spans.append((match.start(), match.end(), "secret-shaped-value"))
    for match in LONG_BASE64.finditer(line):
        if is_base64_candidate(match.group()):
            spans.append((match.start(), match.end(), "secret-shaped-value"))
    return spans


def scan_content(path: str, data: bytes, denylist: Iterable[str]) -> list[Violation]:
    if b"\0" in data[:8192]:
        return []
    text = data.decode("utf-8", "replace")
    lines = text.splitlines()
    found: set[Violation] = set()
    yaml_stack: list[tuple[int, str, int]] = []
    compiler_generated_lines: list[int] = []

    for number, line in enumerate(lines, 1):
        indent = len(line) - len(line.lstrip(" \t"))
        while yaml_stack and indent <= yaml_stack[-1][0]:
            yaml_stack.pop()

        for match in LOCAL_PATH.finditer(line):
            found.add(Violation("local-path", path, number))
        for match in STEAM_ID.finditer(line):
            found.add(Violation("steamid64", path, number))
        for match in IPV4.finditer(line):
            try:
                address = ipaddress.IPv4Address(match.group())
            except ipaddress.AddressValueError:
                continue
            if is_private_or_tailscale(address):
                found.add(Violation("private-ip", path, number))
        if JWT.search(line):
            found.add(Violation("jwt", path, number))

        if ILSPY_HEADER.search(line) or PRIVATE_IMPLEMENTATION.search(line):
            found.add(Violation("decompiled-source", path, number))
        if COMPILER_GENERATED.search(line):
            compiler_generated_lines.append(number)

        for value_start, value_end, rule in candidate_spans(line):
            mapped_key = mapping_key_for_value(line, value_start)
            if mapped_key is not None:
                context = key_kind(mapped_key)
                context_line = number
            elif yaml_stack:
                _indent, active_key, key_line = yaml_stack[-1]
                context = key_kind(active_key)
                context_line = key_line
            else:
                context = same_line_context(line, value_start, value_end)
                context_line = number
            if context == "secret":
                found.add(Violation(rule, path, number, context_line=context_line))

        if any(value and value in line for value in denylist):
            found.add(Violation("private-denylist", path, number))

        yaml_match = YAML_KEY.match(line)
        if yaml_match:
            value = yaml_match.group(2).strip()
            if not value or value.startswith(("|", ">")):
                yaml_stack.append((indent, yaml_match.group(1), number))

    if len(compiler_generated_lines) >= COMPILER_GENERATED_LIMIT:
        line = compiler_generated_lines[COMPILER_GENERATED_LIMIT - 1]
        found.add(Violation("decompiled-source", path, line))
    return sorted(found, key=lambda item: (item.line, item.rule))


def path_violations(path: str) -> list[Violation]:
    found: list[Violation] = []
    if FORBIDDEN_EXTENSION.search(path):
        found.append(Violation("forbidden-extension", path))
    if FORBIDDEN_PATH.search(path):
        found.append(Violation("forbidden-path", path))
    if MEDIA_FILE.search(path):
        found.append(Violation("media-file", path))
    return found


def filter_allowlist(violations: Iterable[Violation], allowed: set[tuple[str, str]]) -> list[Violation]:
    return [item for item in violations if (item.path, item.rule) not in allowed]


def scan_file(path: str, data: bytes, denylist: list[str], declared_size: int | None = None) -> list[Violation]:
    violations = path_violations(path)
    size = len(data) if declared_size is None else declared_size
    if size > MAX_FILE_SIZE:
        violations.append(Violation("file-size", path))
    else:
        violations.extend(scan_content(path, data, denylist))
    return violations


def tree_files(root: Path, revision: str) -> Iterable[tuple[str, bytes, int]]:
    raw = run_git(root, "ls-tree", "-r", "-l", "-z", "--full-tree", revision)
    for entry in raw.split(b"\0"):
        if not entry:
            continue
        metadata, path_bytes = entry.split(b"\t", 1)
        _mode, object_type, object_id, size_text = metadata.decode("ascii").split()
        if object_type != "blob":
            continue
        path = os.fsdecode(path_bytes)
        size = int(size_text)
        data = run_git(root, "cat-file", "blob", object_id) if size <= MAX_FILE_SIZE else b""
        if size <= MAX_FILE_SIZE and len(data) != size:
            raise GuardError(f"could not read complete Git object for {path}")
        yield path, data, size


def staged_files(root: Path) -> Iterable[tuple[str, bytes, int]]:
    changed = run_git(root, "diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z")
    changed_paths = [os.fsdecode(item) for item in changed.split(b"\0") if item]
    if not changed_paths:
        return
    entries = run_git(root, "ls-files", "--stage", "-z")
    index: dict[str, tuple[str, int]] = {}
    for entry in entries.split(b"\0"):
        if not entry:
            continue
        metadata, path_bytes = entry.split(b"\t", 1)
        mode, object_id, stage = metadata.decode("ascii").split()
        if stage == "0":
            index[os.fsdecode(path_bytes)] = (object_id, int(mode, 8))
    for path in changed_paths:
        if path not in index:
            raise GuardError(f"staged path {path!r} has no stage-0 index entry")
        object_id, _mode = index[path]
        size = int(run_git(root, "cat-file", "-s", object_id).decode("ascii").strip())
        data = run_git(root, "cat-file", "blob", object_id) if size <= MAX_FILE_SIZE else b""
        yield path, data, size


def parse_added_lines(patch: bytes) -> set[int]:
    added: set[int] = set()
    current = 0
    in_hunk = False
    for raw in patch.splitlines():
        if raw.startswith(b"@@ "):
            match = re.search(rb"\+(\d+)(?:,(\d+))?", raw)
            current = int(match.group(1)) if match else 0
            in_hunk = True
        elif not in_hunk or raw.startswith(b"\\"):
            continue
        elif raw.startswith(b"+"):
            added.add(current)
            current += 1
        elif raw.startswith(b" "):
            current += 1
    return added


def history_changes(root: Path, denylist: list[str]) -> list[Violation]:
    commits = run_git(root, "rev-list", "--all", "--reverse").decode("ascii").splitlines()
    empty_tree = run_git(root, "hash-object", "-t", "tree", "--stdin", input_bytes=b"").decode("ascii").strip()
    results: set[Violation] = set()
    for commit in commits:
        short_commit = commit[:8]
        message = run_git(root, "show", "-s", "--format=%B", commit)
        for item in scan_content("message", message, denylist):
            if item.rule in MESSAGE_RULES:
                results.add(Violation(item.rule, "message", item.line, short_commit))

        parents = run_git(root, "rev-list", "--parents", "-n", "1", commit).decode("ascii").split()[1:]
        parent = parents[0] if parents else empty_tree
        names = run_git(
            root, "diff-tree", "--root", "-r", "--no-renames", "--no-commit-id", "--name-status", "-z", commit
        )
        fields = names.split(b"\0")
        changes: list[tuple[str, str]] = []
        index = 0
        while index + 1 < len(fields):
            status = os.fsdecode(fields[index])
            path = os.fsdecode(fields[index + 1])
            index += 2
            if status and path and status[0] != "D":
                changes.append((status, path))

        for status, path in changes:
            spec = f"{commit}:{path}"
            try:
                size = int(run_git(root, "cat-file", "-s", spec).decode("ascii").strip())
            except GuardError:
                continue
            if status[0] == "A":
                results.update(
                    Violation(item.rule, item.path, item.line, short_commit, item.context_line)
                    for item in path_violations(path)
                )
            if size > MAX_FILE_SIZE:
                results.add(Violation("file-size", path, 0, short_commit))
                continue
            patch = run_git(root, "diff", "--no-ext-diff", "--no-renames", "--unified=0", parent, commit, "--", path)
            added_lines = parse_added_lines(patch)
            data = run_git(root, "show", spec)

            for item in scan_content(path, data, denylist=denylist):
                report_line = item.line
                if item.rule == "secret-shaped-value":
                    if item.line not in added_lines and item.context_line not in added_lines:
                        continue
                    if item.line not in added_lines and item.context_line:
                        report_line = item.context_line
                elif item.line not in added_lines:
                    continue
                results.add(Violation(item.rule, path, report_line, short_commit))

    return sorted(results, key=lambda item: (item.rule, item.path, item.commit, item.line))


def print_violation(item: Violation) -> None:
    location = f"{item.path}:{item.line}" if item.line else item.path
    suffix = f" (commit {item.commit})" if item.commit else ""
    print(f"FAIL {item.rule}: {location}{suffix}")


def run_tree_or_staged(root: Path, mode: str, revision: str, allowed: set[tuple[str, str]], denylist: list[str], denylist_enabled: bool) -> int:
    files = tree_files(root, revision) if mode == "tree" else staged_files(root)
    file_count = 0
    violations: list[Violation] = []
    for path, data, size in files:
        file_count += 1
        violations.extend(scan_file(path, data, denylist, size))
    violations = filter_allowlist(violations, allowed)
    print(f"Guard: {mode} ({revision if mode == 'tree' else 'index'}), {file_count} file(s)")
    print("Private denylist: enabled" if denylist_enabled else "Private denylist: disabled (MT2_GUARD_DENYLIST is not set)")
    if violations:
        for item in sorted(set(violations), key=lambda item: (item.path, item.line, item.rule)):
            print_violation(item)
        print(f"Guard failed: {len(set(violations))} violation(s)")
        return 1
    print("Guard passed")
    return 0


def run_history(root: Path, allowed: set[tuple[str, str]], denylist: list[str], denylist_enabled: bool) -> int:
    # History's report scans added lines from every reachable commit.
    violations = filter_allowlist(history_changes(root, denylist), allowed)
    commits = run_git(root, "rev-list", "--all", "--count").decode("ascii").strip()
    print(f"History report: {commits} commit(s) scanned")
    print("Private denylist: enabled" if denylist_enabled else "Private denylist: disabled (MT2_GUARD_DENYLIST is not set)")
    print(f"Total matches: {len(violations)}")
    for rule in RULES:
        matches = [item for item in violations if item.rule == rule]
        print(f"{rule}: {len(matches)}")
        for item in matches[:10]:
            if item.path == "message":
                print(f"  commit {item.commit} message:{item.line}")
            else:
                location = f"{item.path}:{item.line}" if item.line else item.path
                print(f"  {location} | commit {item.commit}")
    return 0


def run_message(message_file: str, denylist: list[str], denylist_enabled: bool) -> int:
    path = Path(message_file)
    if not path.is_absolute():
        path = Path.cwd() / path
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise GuardError("commit message file cannot be read") from exc
    violations = [item for item in scan_content("message", data, denylist) if item.rule in MESSAGE_RULES]
    print("Guard: commit message")
    print("Private denylist: enabled" if denylist_enabled else "Private denylist: disabled (MT2_GUARD_DENYLIST is not set)")
    if violations:
        for item in sorted(set(violations), key=lambda item: (item.line, item.rule)):
            print(f"FAIL {item.rule}: message:{item.line}")
        print(f"Guard failed: {len(set(violations))} violation(s)")
        return 1
    print("Guard passed")
    return 0


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Reject game payloads, generated files, and private data.")
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument("--staged", action="store_true", help="scan staged index changes")
    modes.add_argument("--tree", nargs="?", const="HEAD", metavar="REV", help="scan a Git tree (default: HEAD)")
    modes.add_argument("--history", action="store_true", help="report findings introduced throughout Git history")
    modes.add_argument("--message", metavar="FILE", help="scan a commit message file")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    try:
        args = parse_args(sys.argv[1:] if argv is None else argv)
        root = repository_root(Path.cwd())
        allowed = read_allowlist(root)
        denylist, denylist_enabled = read_denylist()
        if args.message is not None:
            return run_message(args.message, denylist, denylist_enabled)
        if args.staged:
            return run_tree_or_staged(root, "staged", "", allowed, denylist, denylist_enabled)
        if args.tree is not None:
            return run_tree_or_staged(root, "tree", args.tree, allowed, denylist, denylist_enabled)
        return run_history(root, allowed, denylist, denylist_enabled)
    except (GuardError, OSError, UnicodeError) as exc:
        print(f"Guard error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
