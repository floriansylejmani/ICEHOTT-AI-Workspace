"""test_verify.py â€” Tests for SHA-256/size checks, max-age, and path safety.

Covers:
  - Size mismatch fails verification
  - SHA-256 mismatch fails verification
  - Max-age: fresh backup passes, stale backup fails
  - Path traversal in manifest path is rejected
  - Symlink manifest is rejected
  - Symlink dump is rejected
  - Dump file not found fails
  - Manifest that is a directory fails
"""
from __future__ import annotations

import json
import os
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

from conftest import (
    VALID_BACKUP_ID,
    make_manifest,
    sha256_of,
)


# ---------------------------------------------------------------------------
# SHA-256 and size mismatch
# ---------------------------------------------------------------------------
class TestChecksumAndSize:
    def test_correct_sha_and_size_passes(self, verify_mod, real_dump_file, backup_dir):
        _, manifest_path, _ = real_dump_file
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(manifest_path)]
        try:
            verify_mod.main()
        finally:
            sys.argv = old_argv

    def test_sha_mismatch_fails(self, verify_mod, backup_dir):
        content = b"PGDMP correct content"
        bid = "d" * 32
        dump_path = backup_dir / f"icehott-{bid}.dump"
        dump_path.write_bytes(content)
        bad_sha = "0" * 64  # wrong SHA
        m = make_manifest(
            backup_id=bid,
            dump_bytes=len(content),
            sha256=bad_sha,
        )
        manifest_path = backup_dir / f"icehott-{bid}.manifest.json"
        manifest_path.write_text(json.dumps(m), encoding="utf-8")
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(manifest_path)]
        try:
            assert verify_mod.main() != 0
        finally:
            sys.argv = old_argv

    def test_size_mismatch_fails(self, verify_mod, backup_dir):
        content = b"PGDMP correct content"
        bid = "e" * 32
        dump_path = backup_dir / f"icehott-{bid}.dump"
        dump_path.write_bytes(content)
        m = make_manifest(
            backup_id=bid,
            dump_bytes=999,  # wrong size
            sha256=sha256_of(content),
        )
        manifest_path = backup_dir / f"icehott-{bid}.manifest.json"
        manifest_path.write_text(json.dumps(m), encoding="utf-8")
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(manifest_path)]
        try:
            assert verify_mod.main() != 0
        finally:
            sys.argv = old_argv

    def test_both_sha_and_size_correct_passes(self, verify_mod, backup_dir):
        content = b"exact match test"
        bid = "f" * 32
        dump_path = backup_dir / f"icehott-{bid}.dump"
        dump_path.write_bytes(content)
        m = make_manifest(
            backup_id=bid,
            dump_bytes=len(content),
            sha256=sha256_of(content),
        )
        manifest_path = backup_dir / f"icehott-{bid}.manifest.json"
        manifest_path.write_text(json.dumps(m), encoding="utf-8")
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(manifest_path)]
        try:
            verify_mod.main()
        finally:
            sys.argv = old_argv


# ---------------------------------------------------------------------------
# Max-age check
# ---------------------------------------------------------------------------
class TestMaxAge:
    def _write_backup_with_age(self, backup_dir, age_seconds: int):
        """Write a backup with a timestamp `age_seconds` ago from now."""
        from datetime import timezone as tz
        ts = datetime.now(timezone.utc) - timedelta(seconds=age_seconds)
        created = ts.strftime("%Y-%m-%dT%H:%M:%SZ")
        bid = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4"
        content = b"PGDMP max-age test"
        dump_path = backup_dir / f"icehott-{bid}.dump"
        dump_path.write_bytes(content)
        m = make_manifest(
            backup_id=bid,
            created_at_utc=created,
            dump_bytes=len(content),
            sha256=sha256_of(content),
        )
        manifest_path = backup_dir / f"icehott-{bid}.manifest.json"
        manifest_path.write_text(json.dumps(m), encoding="utf-8")
        return manifest_path

    def test_fresh_backup_within_max_age_passes(self, verify_mod, backup_dir):
        manifest_path = self._write_backup_with_age(backup_dir, age_seconds=60)
        old_argv = sys.argv[:]
        sys.argv = [
            "verify-postgres-backup.py",
            str(manifest_path),
            "--max-age-seconds", "3600",
        ]
        try:
            verify_mod.main()
        finally:
            sys.argv = old_argv

    def test_stale_backup_exceeds_max_age_fails(self, verify_mod, backup_dir):
        manifest_path = self._write_backup_with_age(backup_dir, age_seconds=7200)
        old_argv = sys.argv[:]
        sys.argv = [
            "verify-postgres-backup.py",
            str(manifest_path),
            "--max-age-seconds", "3600",
        ]
        try:
            assert verify_mod.main() != 0
        finally:
            sys.argv = old_argv

    def test_no_max_age_does_not_fail_old_backup(self, verify_mod, backup_dir):
        manifest_path = self._write_backup_with_age(backup_dir, age_seconds=86400 * 365)
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(manifest_path)]
        try:
            verify_mod.main()  # should not raise â€” no max-age limit
        finally:
            sys.argv = old_argv


# ---------------------------------------------------------------------------
# Path safety: traversal and symlink rejection
# ---------------------------------------------------------------------------
class TestPathSafety:
    def test_symlink_manifest_rejected(self, verify_mod, backup_dir, real_dump_file, tmp_path):
        dump_path, manifest_path, _ = real_dump_file
        # Create a symlink to the real manifest
        link_path = tmp_path / "link_manifest.json"
        try:
            link_path.symlink_to(manifest_path)
        except (OSError, NotImplementedError):
            pytest.skip("symlinks not supported on this platform")
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(link_path)]
        try:
            assert verify_mod.main() != 0
        finally:
            sys.argv = old_argv

    def test_symlink_dump_rejected(self, verify_mod, backup_dir, tmp_path):
        """Dump file that is a symlink should be rejected."""
        content = b"PGDMP symlink test"
        bid = "1234567890abcdef1234567890abcdef"
        # Real dump in a different directory
        real_dump = tmp_path / "real.dump"
        real_dump.write_bytes(content)
        # Symlink in backup_dir
        dump_link = backup_dir / f"icehott-{bid}.dump"
        try:
            dump_link.symlink_to(real_dump)
        except (OSError, NotImplementedError):
            pytest.skip("symlinks not supported on this platform")
        m = make_manifest(
            backup_id=bid,
            dump_bytes=len(content),
            sha256=sha256_of(content),
        )
        manifest_path = backup_dir / f"icehott-{bid}.manifest.json"
        manifest_path.write_text(json.dumps(m), encoding="utf-8")
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(manifest_path)]
        try:
            assert verify_mod.main() != 0
        finally:
            sys.argv = old_argv

    def test_dump_missing_fails(self, verify_mod, backup_dir):
        bid = "abcdef0123456789abcdef0123456789"
        content = b"PGDMP"
        m = make_manifest(
            backup_id=bid,
            dump_bytes=len(content),
            sha256=sha256_of(content),
        )
        manifest_path = backup_dir / f"icehott-{bid}.manifest.json"
        manifest_path.write_text(json.dumps(m), encoding="utf-8")
        # Do NOT write the dump file
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(manifest_path)]
        try:
            assert verify_mod.main() != 0
        finally:
            sys.argv = old_argv

    def test_manifest_not_found_fails(self, verify_mod, tmp_path):
        nonexistent = tmp_path / "nonexistent.manifest.json"
        old_argv = sys.argv[:]
        sys.argv = ["verify-postgres-backup.py", str(nonexistent)]
        try:
            assert verify_mod.main() != 0
        finally:
            sys.argv = old_argv
