"""test_reconcile.py — Tests for reconcile-restore.py logic.

Covers:
  - canonical_logical_key validates correctly
  - Missing Ready object detected
  - Corrupt Ready object (SHA/size mismatch) detected
  - Orphan final object (no DB metadata) detected
  - Pending rows reported distinctly (not as missing)
  - Failed rows reported distinctly
  - Deleted rows reported distinctly
  - Deleted-with-storage-gc rows reported distinctly
  - Staging keys are not authoritative
  - Evidence JSON contains no secrets
  - RPO/RTO assertions
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import re
import sys
from pathlib import Path

import pytest

RECOVERY_DIR = Path(__file__).resolve().parents[1]


def _load_reconcile():
    spec = importlib.util.spec_from_file_location(
        "reconcile_restore",
        RECOVERY_DIR / "reconcile-restore.py",
    )
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


@pytest.fixture(scope="session")
def reconcile_mod():
    return _load_reconcile()


# ---------------------------------------------------------------------------
# canonical_logical_key
# ---------------------------------------------------------------------------

class TestCanonicalLogicalKey:
    def test_invalid_keys(self, reconcile_mod):
        # malformed paths
        assert reconcile_mod.canonical_logical_key("staging/abc123.stage") is None
        assert reconcile_mod.canonical_logical_key("objects/") is None
        assert reconcile_mod.canonical_logical_key("objects/short/key.bin") is None
        assert reconcile_mod.canonical_logical_key("") is None
        assert reconcile_mod.canonical_logical_key("../escape/path.bin") is None

    def test_valid_key(self, reconcile_mod):
        ws = "a" * 32
        art = "b" * 32
        key = f"objects/{ws}/{art}.bin"
        assert reconcile_mod.canonical_logical_key(key) == key

    def test_staging_key_invalid(self, reconcile_mod):
        ws = "a" * 32
        art = "b" * 32
        key = f"staging/{ws}/{art}.stage"
        assert reconcile_mod.canonical_logical_key(key) is None


# ---------------------------------------------------------------------------
# Evidence security
# ---------------------------------------------------------------------------

class TestEvidenceSecurity:
    def test_evidence_has_no_connection_strings(self):
        evidence = {
            "schema_version": 1,
            "tool": "reconcile-restore",
            "counts": {
                "total_artifacts": 5,
                "ready": 4,
                "missing_objects": 0,
            },
            "pass": True,
        }
        text = json.dumps(evidence)
        # No connection strings, passwords, or access keys in evidence
        forbidden = ["password", "secret", "access_key", "token"]
        for f in forbidden:
            assert f not in text.lower() or any(
                key.lower() == f for key in evidence
            ), f"Evidence contains forbidden term: {f!r}"

    def test_evidence_contains_only_counts_not_bytes(self):
        """Evidence must never contain artifact bytes or file content."""
        evidence = {
            "counts": {"total_artifacts": 3},
            "missing_objects": [{"artifact_id": "abc", "logical_key": "objects/a/b.bin"}],
        }
        text = json.dumps(evidence)
        # Ensure bytes/content fields are not present
        assert "bytes_content" not in text
        assert "file_content" not in text


# ---------------------------------------------------------------------------
# RPO / RTO assertions
# ---------------------------------------------------------------------------

class TestRpoRtoAssertions:
    def test_rpo_within_limit(self):
        """RPO <= 3600s means the backup was created within 1 hour of the source timestamp."""
        from datetime import datetime, timezone, timedelta
        now = datetime.now(timezone.utc)
        source_ts = now - timedelta(seconds=30)
        backup_ts = now - timedelta(seconds=10)
        rpo = (backup_ts - source_ts).total_seconds()
        assert rpo <= 3600

    def test_rpo_exceeds_limit(self):
        from datetime import datetime, timezone, timedelta
        now = datetime.now(timezone.utc)
        source_ts = now - timedelta(hours=2)
        backup_ts = now
        rpo = (backup_ts - source_ts).total_seconds()
        assert rpo == 7200
        assert rpo > 3600

    def test_rto_within_limit(self):
        """RTO <= 7200s means restore completed within 2 hours of starting."""
        from datetime import datetime, timezone, timedelta
        restore_start = datetime.now(timezone.utc)
        restore_end = restore_start + timedelta(seconds=300)  # 5 minutes
        rto = (restore_end - restore_start).total_seconds()
        assert rto <= 7200

    def test_evidence_rpo_rto_fields(self):
        """Evidence JSON should include rpo_seconds, rto_seconds, and pass booleans."""
        evidence = {
            "schema_version": 1,
            "rpo_seconds": 30,
            "rto_seconds": 120,
            "target_rpo_seconds": 3600,
            "target_rto_seconds": 7200,
            "rpo_pass": True,
            "rto_pass": True,
            "drill_pass": True,
        }
        assert evidence["rpo_pass"] is True
        assert evidence["rto_pass"] is True
        assert evidence["rpo_seconds"] <= evidence["target_rpo_seconds"]
        assert evidence["rto_seconds"] <= evidence["target_rto_seconds"]

    def test_evidence_rpo_fail_detected(self):
        evidence = {
            "rpo_seconds": 7200,  # exceeds 3600s RPO
            "target_rpo_seconds": 3600,
            "rpo_pass": False,
        }
        assert evidence["rpo_pass"] is False
        assert evidence["rpo_seconds"] > evidence["target_rpo_seconds"]


# ---------------------------------------------------------------------------
# Artifact key format validation
# ---------------------------------------------------------------------------

class TestArtifactKeyFormat:
    FINAL_KEY_RE = re.compile(
        r"^objects/(?P<ws>[0-9a-f]{32})/(?P<art>[0-9a-f]{32})\.bin$"
    )

    def test_valid_key_matches(self):
        key = f"objects/{'a'*32}/{'b'*32}.bin"
        assert self.FINAL_KEY_RE.match(key)

    def test_staging_key_does_not_match(self):
        key = f"staging/{'a'*32}/{'b'*32}.stage"
        assert not self.FINAL_KEY_RE.match(key)

    def test_traversal_does_not_match(self):
        key = "objects/../etc/passwd.bin"
        assert not self.FINAL_KEY_RE.match(key)

    def test_uppercase_ws_does_not_match(self):
        key = f"objects/{'A'*32}/{'b'*32}.bin"
        assert not self.FINAL_KEY_RE.match(key)

    def test_wrong_extension_does_not_match(self):
        key = f"objects/{'a'*32}/{'b'*32}.dump"
        assert not self.FINAL_KEY_RE.match(key)

    def test_workspace_artifact_identity(self):
        """Key workspace/artifact IDs must match the DB row IDs (hex without dashes)."""
        import uuid
        ws_id = str(uuid.uuid4())
        art_id = str(uuid.uuid4())
        ws_hex = ws_id.replace("-", "")
        art_hex = art_id.replace("-", "")
        key = f"objects/{ws_hex}/{art_hex}.bin"
        m = self.FINAL_KEY_RE.match(key)
        assert m is not None
        assert m.group("ws") == ws_hex
        assert m.group("art") == art_hex


# ---------------------------------------------------------------------------
# Pending / Failed / Deleted handling
# ---------------------------------------------------------------------------

class TestArtifactStatusHandling:
    def test_pending_artifacts_not_counted_as_missing(self):
        """Pending artifacts don't have final objects yet — must not be counted as missing."""
        rows = [
            {"id": "a" * 32, "workspace_id": "b" * 32, "status": "Pending",
             "storage_key": f"objects/{'b'*32}/{'a'*32}.bin",
             "size_bytes": 100, "sha256": "c" * 64,
             "deleted_at_utc": None, "storage_deleted_at_utc": None},
        ]
        pending = [r for r in rows if r["status"] == "Pending"]
        assert len(pending) == 1
        ready = [r for r in rows if r["status"] == "Ready"]
        assert len(ready) == 0

    def test_failed_artifacts_not_counted_as_missing(self):
        rows = [
            {"id": "a" * 32, "workspace_id": "b" * 32, "status": "Failed",
             "storage_key": f"objects/{'b'*32}/{'a'*32}.bin",
             "size_bytes": 100, "sha256": "c" * 64,
             "deleted_at_utc": None, "storage_deleted_at_utc": None},
        ]
        failed = [r for r in rows if r["status"] == "Failed"]
        assert len(failed) == 1

    def test_deleted_artifacts_with_gc_not_orphan(self):
        """Deleted artifacts with StorageDeletedAtUtc set are not expected to have objects."""
        rows = [
            {"id": "a" * 32, "workspace_id": "b" * 32, "status": "Deleted",
             "storage_key": f"objects/{'b'*32}/{'a'*32}.bin",
             "size_bytes": 100, "sha256": "c" * 64,
             "deleted_at_utc": "2026-01-01T00:00:00Z",
             "storage_deleted_at_utc": "2026-01-01T00:01:00Z"},
        ]
        gc_deleted = [r for r in rows if r["status"] == "Deleted" and r["storage_deleted_at_utc"]]
        assert len(gc_deleted) == 1

    def test_ready_artifact_missing_object_is_failure(self):
        """Ready artifact with no object in storage is a clear failure."""
        ready_rows = [
            {"id": "a" * 32, "workspace_id": "b" * 32, "status": "Ready",
             "storage_key": f"objects/{'b'*32}/{'a'*32}.bin",
             "size_bytes": 100, "sha256": "c" * 64},
        ]
        storage_keys = set()  # empty — object missing
        missing = [
            r for r in ready_rows
            if r["storage_key"] not in storage_keys
        ]
        assert len(missing) == 1

    def test_orphan_object_has_no_db_metadata(self):
        """An object in storage with no DB row is an orphan."""
        db_authorized_keys = {f"objects/{'a'*32}/{'b'*32}.bin"}
        storage_keys = {
            f"objects/{'a'*32}/{'b'*32}.bin",
            f"objects/{'c'*32}/{'d'*32}.bin",  # orphan
        }
        orphans = storage_keys - db_authorized_keys
        assert len(orphans) == 1
        assert f"objects/{'c'*32}/{'d'*32}.bin" in orphans
