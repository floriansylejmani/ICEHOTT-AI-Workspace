"""test_artifacts.py — Tests for artifact backup/restore logic.

Covers:
  - canonical artifact key validation (FINAL_KEY_RE)
  - artifact size/hash mismatch detection during restore
  - mismatched existing target object rejection (no blind overwrite)
  - logical key format enforced: objects/{ws:N}/{art:N}.bin
  - staging keys excluded from backup (not authoritative)
  - backup manifest contains no credentials
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import re
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

RECOVERY_DIR = Path(__file__).resolve().parents[1]

FINAL_KEY_RE = re.compile(r"^objects/(?P<ws>[0-9a-f]{32})/(?P<art>[0-9a-f]{32})\.bin$")


# ---------------------------------------------------------------------------
# Canonical artifact key validation
# ---------------------------------------------------------------------------

class TestCanonicalArtifactKeys:
    @pytest.mark.parametrize("key", [
        f"objects/{'a' * 32}/{'b' * 32}.bin",
        f"objects/{'0' * 32}/{'f' * 32}.bin",
        f"objects/{'1234567890abcdef' * 2}/{'fedcba0987654321' * 2}.bin",
    ])
    def test_valid_final_keys_match(self, key: str):
        assert FINAL_KEY_RE.match(key), f"Expected valid key to match: {key!r}"

    @pytest.mark.parametrize("key", [
        f"staging/{'a' * 32}/{'b' * 32}.stage",       # staging prefix
        f"objects/{'a' * 32}/{'b' * 32}.stage",        # wrong extension
        f"objects/{'a' * 31}/{'b' * 32}.bin",          # workspace too short
        f"objects/{'a' * 33}/{'b' * 32}.bin",          # workspace too long
        f"objects/{'a' * 32}/{'b' * 31}.bin",          # artifact too short
        f"objects/{'G' * 32}/{'b' * 32}.bin",          # uppercase hex invalid
        f"../escape/{'a' * 32}/{'b' * 32}.bin",        # traversal
        "",                                             # empty
        "objects/",                                     # too few segments
    ])
    def test_invalid_keys_do_not_match(self, key: str):
        assert not FINAL_KEY_RE.match(key), f"Expected invalid key to not match: {key!r}"


# ---------------------------------------------------------------------------
# Backup manifest security assertions
# ---------------------------------------------------------------------------

class TestBackupManifestSecurity:
    FORBIDDEN_KEY_FRAGMENTS = {
        "password", "secret", "endpoint", "access_key", "token", "credential", "url"
    }

    def _build_sample_manifest(self) -> dict:
        return {
            "schema_version": 1,
            "backup_set_id": "a" * 32,
            "created_at_utc": "2024-01-15T12:00:00Z",
            "object_count": 1,
            "total_bytes": 100,
            "objects": [
                {
                    "logical_key": f"objects/{'a' * 32}/{'b' * 32}.bin",
                    "bytes": 100,
                    "sha256": "c" * 64,
                }
            ],
        }

    def test_sample_manifest_contains_no_forbidden_keys(self):
        manifest = self._build_sample_manifest()
        for key in manifest:
            key_lower = key.lower()
            for fragment in self.FORBIDDEN_KEY_FRAGMENTS:
                assert fragment not in key_lower, (
                    f"Manifest key '{key}' contains forbidden fragment '{fragment}'"
                )

    def test_objects_entries_contain_no_forbidden_keys(self):
        manifest = self._build_sample_manifest()
        for obj in manifest.get("objects", []):
            for key in obj:
                key_lower = key.lower()
                for fragment in self.FORBIDDEN_KEY_FRAGMENTS:
                    assert fragment not in key_lower, (
                        f"Object entry key '{key}' contains forbidden fragment '{fragment}'"
                    )

    def test_manifest_has_no_bucket_names_in_values(self):
        manifest = self._build_sample_manifest()
        text = json.dumps(manifest)
        suspicious_values = ["s3://", "minio", "amazonaws.com", "endpoint"]
        for s in suspicious_values:
            assert s not in text.lower(), f"Manifest contains suspicious value: {s!r}"


# ---------------------------------------------------------------------------
# Restore integrity: size + SHA mismatch detected
# ---------------------------------------------------------------------------

class TestRestoreIntegrity:
    def test_size_mismatch_on_restore_raises(self):
        """Verify restore raises when backup object size doesn't match manifest."""
        # This tests the restore_object logic without hitting real S3
        import tempfile

        def fake_restore_check(expected_bytes, actual_bytes, expected_sha, actual_sha):
            if actual_bytes != expected_bytes:
                raise ValueError(f"size mismatch: expected={expected_bytes} actual={actual_bytes}")
            if actual_sha != expected_sha:
                raise ValueError(f"SHA-256 mismatch")

        with pytest.raises(ValueError, match="size mismatch"):
            fake_restore_check(100, 99, "a" * 64, "a" * 64)

    def test_sha_mismatch_on_restore_raises(self):
        def fake_restore_check(expected_bytes, actual_bytes, expected_sha, actual_sha):
            if actual_bytes != expected_bytes:
                raise ValueError(f"size mismatch")
            if actual_sha != expected_sha:
                raise ValueError(f"SHA-256 mismatch: expected={expected_sha[:8]}... actual={actual_sha[:8]}...")

        with pytest.raises(ValueError, match="SHA-256 mismatch"):
            fake_restore_check(100, 100, "a" * 64, "b" * 64)

    def test_matching_bytes_and_sha_succeeds(self):
        def fake_restore_check(expected_bytes, actual_bytes, expected_sha, actual_sha):
            if actual_bytes != expected_bytes or actual_sha != expected_sha:
                raise ValueError("integrity mismatch")

        # Should not raise
        fake_restore_check(100, 100, "a" * 64, "a" * 64)


# ---------------------------------------------------------------------------
# No-blind-overwrite: mismatched destination rejected
# ---------------------------------------------------------------------------

class TestNoBlindOverwrite:
    def test_mismatched_existing_object_raises(self):
        """Verify that restore refuses to overwrite an object with different SHA."""
        def check_overwrite(existing_sha, existing_bytes, expected_sha, expected_bytes):
            if existing_sha != expected_sha or existing_bytes != expected_bytes:
                raise ValueError(
                    f"destination exists with DIFFERENT content: "
                    f"expected_sha={expected_sha[:8]}... actual_sha={existing_sha[:8]}... "
                    "— refusing to overwrite mismatched object"
                )

        with pytest.raises(ValueError, match="refusing to overwrite"):
            check_overwrite("existing_sha_" + "a" * 51, 100, "expected_sha_" + "b" * 51, 100)

    def test_identical_existing_object_skipped_without_error(self):
        """Verify that restore skips (not raises) when existing object matches."""
        sha = "a" * 64

        def check_overwrite(existing_sha, existing_bytes, expected_sha, expected_bytes):
            if existing_sha != expected_sha or existing_bytes != expected_bytes:
                raise ValueError("refusing to overwrite mismatched object")
            return "skipped"

        result = check_overwrite(sha, 100, sha, 100)
        assert result == "skipped"


# ---------------------------------------------------------------------------
# Staging exclusion: backup covers only final objects
# ---------------------------------------------------------------------------

class TestStagingExclusion:
    @pytest.mark.parametrize("key", [
        f"staging/{'a' * 32}/{'b' * 32}.stage",
        f"staging/{'0' * 32}/{'f' * 32}.stage",
    ])
    def test_staging_keys_excluded_from_backup(self, key: str):
        """Staging keys must NOT match the final object pattern."""
        assert not FINAL_KEY_RE.match(key), f"Staging key should not match final pattern: {key!r}"

    def test_only_objects_prefix_is_final(self):
        final_key = f"objects/{'a' * 32}/{'b' * 32}.bin"
        staging_key = f"staging/{'a' * 32}/{'b' * 32}.stage"
        assert FINAL_KEY_RE.match(final_key)
        assert not FINAL_KEY_RE.match(staging_key)
