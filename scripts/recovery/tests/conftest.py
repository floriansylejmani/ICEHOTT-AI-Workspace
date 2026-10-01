"""conftest.py — shared pytest fixtures for ICEHOTT Phase 7D recovery tests."""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

# ---------------------------------------------------------------------------
# Load recovery modules under test
# ---------------------------------------------------------------------------
_RECOVERY_DIR = Path(__file__).parent.parent


def _load_module(name: str, filename: str):
    """Load a .py file as a module, regardless of whether it is a package."""
    path = _RECOVERY_DIR / filename
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


@pytest.fixture(scope="session")
def verify_mod():
    """The verify-postgres-backup module."""
    return _load_module("verify_postgres_backup", "verify-postgres-backup.py")


@pytest.fixture(scope="session")
def prune_mod():
    """The prune-backups module."""
    return _load_module("prune_backups", "prune-backups.py")


# ---------------------------------------------------------------------------
# Backup directory / manifest helpers
# ---------------------------------------------------------------------------
VALID_BACKUP_ID = "a" * 32
VALID_SHA256 = "b" * 64
VALID_CREATED = "2024-01-15T12:00:00Z"


def make_dump_bytes(content: bytes = b"PGDMP fake content") -> bytes:
    return content


def sha256_of(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def make_manifest(
    backup_id: str = VALID_BACKUP_ID,
    created_at_utc: str = VALID_CREATED,
    dump_filename: str | None = None,
    dump_bytes: int = 18,
    sha256: str = VALID_SHA256,
    schema_version: int = 1,
    extra_fields: dict | None = None,
    omit_fields: list[str] | None = None,
) -> dict:
    if dump_filename is None:
        dump_filename = f"icehott-{backup_id}.dump"
    m = {
        "schema_version": schema_version,
        "backup_id": backup_id,
        "created_at_utc": created_at_utc,
        "dump_filename": dump_filename,
        "bytes": dump_bytes,
        "sha256": sha256,
        "format": "pg_dump_custom_Fc",
        "pg_dump_version": "pg_dump (PostgreSQL) 16.1",
    }
    if omit_fields:
        for f in omit_fields:
            m.pop(f, None)
    if extra_fields:
        m.update(extra_fields)
    return m


@pytest.fixture()
def backup_dir(tmp_path):
    """A temporary backup directory."""
    d = tmp_path / "backups"
    d.mkdir()
    return d


@pytest.fixture()
def real_dump_file(backup_dir):
    """Create a real dump file and matching manifest; return (dump_path, manifest_path, sha256)."""
    content = b"PGDMP fake content for testing"
    sha = sha256_of(content)
    bid = "c" * 32
    dump_name = f"icehott-{bid}.dump"
    manifest_name = f"icehott-{bid}.manifest.json"
    dump_path = backup_dir / dump_name
    manifest_path = backup_dir / manifest_name
    dump_path.write_bytes(content)
    manifest = make_manifest(
        backup_id=bid,
        dump_bytes=len(content),
        sha256=sha,
        dump_filename=dump_name,
    )
    manifest_path.write_text(json.dumps(manifest) + "\n", encoding="utf-8")
    return dump_path, manifest_path, sha


NOW_UTC = datetime(2024, 6, 15, 12, 0, 0, tzinfo=timezone.utc)
