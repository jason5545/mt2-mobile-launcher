#!/usr/bin/env python3
"""End-to-end tests for the repository guard using synthetic Git repositories."""

from __future__ import annotations

import base64
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


GUARD = Path(__file__).with_name("guard.py").resolve()


class GuardCliTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="mt2-guard-test-")
        self.root = Path(self.temp.name)
        self.git("init", "--quiet")
        self.write(
            "tools/guard/allowlist.txt",
            "# synthetic test repository\n",
        )
        self.commit("baseline")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def git(self, *args: str) -> str:
        result = subprocess.run(
            ["git", *args],
            cwd=self.root,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            check=True,
        )
        return result.stdout.strip()

    def write(self, relative: str, content: str | bytes) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, str):
            path.write_text(content, encoding="utf-8")
        else:
            path.write_bytes(content)

    def commit(self, message: str) -> str:
        self.git("add", "-A")
        self.git(
            "-c",
            "user.name=Guard Test",
            "-c",
            "user.email=guard-test@example.invalid",
            "commit",
            "--quiet",
            "-m",
            message,
        )
        return self.git("rev-parse", "--short=8", "HEAD")

    def run_guard(self, *args: str, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        environment.pop("MT2_GUARD_DENYLIST", None)
        if env:
            environment.update(env)
        return subprocess.run(
            [sys.executable, str(GUARD), *args],
            cwd=self.root,
            env=environment,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )

    def test_tree_blocks_payload_paths_media_large_files_and_decompiler_signatures(self) -> None:
        for suffix in (
            "dll", "exe", "assets", "bundle", "resS", "resource", "apk", "aab", "jks", "keystore", "spv", "spirv"
        ):
            self.write(f"payload/fake.{suffix}", b"synthetic fixture")
        for path in (
            "Build_Data/fake.dat",
            "ExportedProject/Fake.cs",
            "decompiled/Fake.cs",
            "gen/fake.ts",
            "public/game/fake.js",
            "patch/Stubs.g.cs",
            "android/local.properties",
        ):
            self.write(path, b"synthetic fixture")
        self.write("assets/fake.png", b"fake image bytes")
        self.write("assets/fake.wav", b"fake audio bytes")
        self.write("cache/large.bin", b"x" * (5 * 1024 * 1024 + 1))
        decompiler_header = b"/" + b"/ Decompiled with ILSpy\n"
        private_details = b"<Private" + b"ImplementationDetails>\n"
        compiler_generated = (b"[" + b"CompilerGenerated" + b"]\n") * 4
        self.write("samples/Fake.cs", decompiler_header + private_details + compiler_generated)
        self.commit("add synthetic violations")

        result = self.run_guard("--tree", "HEAD")
        self.assertEqual(result.returncode, 1, result.stdout)
        for rule in ("forbidden-extension", "forbidden-path", "media-file", "file-size", "decompiled-source"):
            self.assertIn(f"FAIL {rule}:", result.stdout)

    def test_tree_allows_hash_fields_clean_files_and_exact_allowlist_entry(self) -> None:
        digest = "a" * 64
        commit_hash = "b" * 40
        self.write("targets/synthetic.json", f'{{"sha256":"{digest}","commit":"{commit_hash}"}}\n')
        self.write("src/Launcher/Resources/manual.svg", b"<svg><!-- synthetic hand-written resource --></svg>")
        self.write(
            "tools/guard/allowlist.txt",
            "src/Launcher/Resources/manual.svg\tmedia-file\tHand-written launcher vector art.\n",
        )
        self.write("src/clean.txt", "ordinary source text\n")
        self.commit("add allowed fixtures")

        result = self.run_guard("--tree", "HEAD")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("Guard passed", result.stdout)

    def test_long_hex_and_base64_are_blocked_only_in_secret_context(self) -> None:
        long_hex = "a" * 64
        long_base64 = base64.b64encode(bytes(range(48))).decode("ascii")
        self.write(
            "config/secrets.yml",
            "\n".join(
                (
                    f"apiToken: {long_hex}",
                    f"refreshToken: {long_base64}",
                    f"secretKey: {long_hex}",
                    "jsonCredentials:",
                    "  refreshToken:",
                    f"    {long_base64}",
                    '  "json": {',
                    '    "refreshToken":',
                    f'      "{long_base64}"',
                    "  }",
                )
            )
            + "\n",
        )
        self.commit("add synthetic secret fixtures")

        result = self.run_guard("--tree", "HEAD")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertGreaterEqual(result.stdout.count("FAIL secret-shaped-value:"), 5)
        self.assertNotIn(long_hex, result.stdout)
        self.assertNotIn(long_base64, result.stdout)

    def test_jwt_private_addresses_local_paths_and_steam_id_are_blocked(self) -> None:
        local_path = "/Users/" + "guard-fixture" + "/Library/example"
        home_path = "/home/" + "guard-fixture" + "/project"
        steam_id = "7656119" + "8000000000"
        private_addresses = (
            ".".join(("10", "1", "2", "3")),
            ".".join(("172", "16", "2", "3")),
            ".".join(("192", "168", "2", "3")),
            ".".join(("100", "64", "2", "3")),
        )
        jwt = "eyJ" + "a" * 12 + "." + "b" * 12 + "." + "c" * 12
        body = "\n".join((local_path, home_path, steam_id, *private_addresses, jwt)) + "\n"
        self.write("docs/synthetic.txt", body)
        self.commit("add synthetic private data")

        result = self.run_guard("--tree", "HEAD")
        self.assertEqual(result.returncode, 1, result.stdout)
        for rule in ("local-path", "steamid64", "private-ip", "jwt"):
            self.assertIn(f"FAIL {rule}:", result.stdout)
        for value in (local_path, home_path, steam_id, jwt, *private_addresses):
            self.assertNotIn(value, result.stdout)

    def test_staged_mode_reads_index_and_detects_private_ip(self) -> None:
        staged_address = ".".join(("192", "168", "20", "9"))
        self.write("docs/staged.txt", f"example {staged_address}\n")
        self.git("add", "docs/staged.txt")

        result = self.run_guard("--staged")
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("FAIL private-ip: docs/staged.txt:1", result.stdout)
        self.assertNotIn(staged_address, result.stdout)

    def test_history_scans_commit_messages_for_private_ip_and_denylist_value(self) -> None:
        private_ip = ".".join(("192", "168", "30", "4"))
        private_value = "TEST-" + "MESSAGE-HOST-DO-NOT-USE"
        denylist = self.root.parent / (self.root.name + "-message-private-list.txt")
        denylist.write_text(private_value + "\n", encoding="utf-8")

        self.write("docs/message-ip.txt", "synthetic message fixture\n")
        ip_commit = self.commit(f"connection observed at {private_ip}")
        self.write("docs/message-private.txt", "another synthetic fixture\n")
        private_commit = self.commit(f"connected from {private_value}")

        try:
            result = self.run_guard("--history", env={"MT2_GUARD_DENYLIST": str(denylist)})
        finally:
            denylist.unlink(missing_ok=True)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("private-ip: 1", result.stdout)
        self.assertIn("private-denylist: 1", result.stdout)
        self.assertIn(f"commit {ip_commit} message:1", result.stdout)
        self.assertIn(f"commit {private_commit} message:1", result.stdout)
        self.assertNotIn(private_ip, result.stdout)
        self.assertNotIn(private_value, result.stdout)

    def test_message_mode_rejects_local_absolute_path(self) -> None:
        local_path = "/Users/" + "message-fixture" + "/Library/private"
        message_file = self.root / "synthetic-commit-message.txt"
        message_file.write_text(f"include {local_path}\n", encoding="utf-8")

        result = self.run_guard("--message", str(message_file))
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("FAIL local-path: message:1", result.stdout)
        self.assertNotIn(local_path, result.stdout)

    def test_missing_allowlist_is_treated_as_empty(self) -> None:
        (self.root / "tools/guard/allowlist.txt").unlink()

        result = self.run_guard("--tree", "HEAD")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("Guard passed", result.stdout)

    def test_history_reports_added_content_even_after_file_removal(self) -> None:
        local_path = "/Users/" + "history-fixture" + "/Library/private"
        self.write("docs/old-note.txt", f"{local_path}\n")
        introduced_commit = self.commit("introduce synthetic historical finding")
        (self.root / "docs/old-note.txt").unlink()
        self.commit("remove synthetic historical finding")

        result = self.run_guard("--history")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("local-path: 1", result.stdout)
        self.assertIn(f"docs/old-note.txt:1 | commit {introduced_commit}", result.stdout)
        self.assertNotIn(local_path, result.stdout)

    def test_private_denylist_blocks_and_never_prints_the_value(self) -> None:
        private_value = "TEST-" + "DEVICE-NAME-DO-NOT-USE"
        denylist = self.root.parent / (self.root.name + "-private-list.txt")
        denylist.write_text(private_value + "\n", encoding="utf-8")
        self.write("docs/device.txt", f"record: {private_value}\n")
        self.commit("add synthetic denylist fixture")

        try:
            result = self.run_guard("--tree", "HEAD", env={"MT2_GUARD_DENYLIST": str(denylist)})
        finally:
            denylist.unlink(missing_ok=True)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn("FAIL private-denylist: docs/device.txt:1", result.stdout)
        self.assertNotIn(private_value, result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
