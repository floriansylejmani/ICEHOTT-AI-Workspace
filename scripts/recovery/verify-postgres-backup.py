#!/usr/bin/env python3
"""Verify an ICEHOTT schema_version=1 PostgreSQL logical backup."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from recovery_common import RecoveryValidationError, safe_existing_file

SCHEMA_VERSION = 1
REQUIRED_FIELDS = {
    "schema_version",
    "backup_id",
    "created_at_utc",
    "dump_filename",
    "bytes",
    "sha256",
    "format",
    "pg_dump_version",
}
OPTIONAL_FIELDS = {"release_git_sha"}
BACKUP_ID_RE = re.compile(r"^[0-9a-f]{32}$")
SHA_RE = re.compile(r"^[0-9a-f]{64}$")
DUMP_RE = re.compile(r"^icehott-([0-9a-f]{32})\.dump$")
UTC_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
FORBIDDEN_KEY_FRAGMENTS = {
    "password",
    "secret",
    "connection",
    "conn_url",
    "pgpassword",
    "access_key",
    "token",
    "credential",
    "host",
    "username",
}


class BackupVerificationError(ValueError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_manifest_path(raw: str | Path) -> Path:
    try:
        return safe_existing_file(raw)
    except RecoveryValidationError as exc:
        raise BackupVerificationError("manifest path is unsafe or missing") from exc


def load_manifest(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise BackupVerificationError("manifest JSON is invalid") from exc
    if not isinstance(data, dict):
        raise BackupVerificationError("manifest must be a JSON object")

    unknown = set(data) - REQUIRED_FIELDS - OPTIONAL_FIELDS
    if unknown:
        raise BackupVerificationError("manifest contains unsupported fields")
    missing = REQUIRED_FIELDS - set(data)
    if missing:
        raise BackupVerificationError("manifest is missing required fields")

    for key in data:
        lower = key.lower()
        if any(fragment in lower for fragment in FORBIDDEN_KEY_FRAGMENTS):
            raise BackupVerificationError("manifest contains a forbidden field")

    if data["schema_version"] != SCHEMA_VERSION:
        raise BackupVerificationError("schema_version must be 1")
    if not isinstance(data["backup_id"], str) or not BACKUP_ID_RE.fullmatch(data["backup_id"]):
        raise BackupVerificationError("backup_id is invalid")
    if not isinstance(data["dump_filename"], str):
        raise BackupVerificationError("dump_filename is invalid")
    match = DUMP_RE.fullmatch(data["dump_filename"])
    if not match or match.group(1) != data["backup_id"]:
        raise BackupVerificationError("dump_filename is invalid")
    if not isinstance(data["bytes"], int) or data["bytes"] < 1:
        raise BackupVerificationError("bytes is invalid")
    if not isinstance(data["sha256"], str) or not SHA_RE.fullmatch(data["sha256"]):
        raise BackupVerificationError("sha256 is invalid")
    if data["format"] != "pg_dump_custom_Fc":
        raise BackupVerificationError("format is invalid")
    if not isinstance(data["pg_dump_version"], str) or not data["pg_dump_version"].startswith("pg_dump"):
        raise BackupVerificationError("pg_dump_version is invalid")
    if not isinstance(data["created_at_utc"], str) or not UTC_RE.fullmatch(data["created_at_utc"]):
        raise BackupVerificationError("created_at_utc is invalid")
    try:
        datetime.strptime(data["created_at_utc"], "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=timezone.utc
        )
    except ValueError as exc:
        raise BackupVerificationError("created_at_utc is invalid") from exc

    release_sha = data.get("release_git_sha")
    if release_sha is not None and (
        not isinstance(release_sha, str)
        or not re.fullmatch(r"[0-9a-f]{40}", release_sha)
    ):
        raise BackupVerificationError("release_git_sha must be a full Git SHA")

    return data


def _safe_dump_path(manifest_path: Path, dump_filename: str) -> Path:
    candidate = manifest_path.parent / dump_filename
    if candidate.is_symlink():
        raise BackupVerificationError("dump path may not be a symlink")
    resolved = candidate.resolve()
    try:
        resolved.relative_to(manifest_path.parent.resolve())
    except ValueError as exc:
        raise BackupVerificationError("dump path escapes the backup directory") from exc
    if not resolved.is_file():
        raise BackupVerificationError("dump file was not found")
    return resolved


def run_pg_restore_list(dump_path: Path, pg_restore: str = "pg_restore") -> int:
    binary = shutil.which(pg_restore)
    if not binary:
        raise BackupVerificationError("pg_restore is required for backup verification")
    result = subprocess.run(
        [binary, "--list", str(dump_path)],
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )
    if result.returncode != 0:
        raise BackupVerificationError("pg_restore structural verification failed")
    return sum(
        1
        for line in result.stdout.splitlines()
        if line.strip() and not line.startswith(";")
    )


def verify_backup(
    manifest_raw: str | Path,
    *,
    max_age_seconds: int | None = None,
    now: datetime | None = None,
    run_structural_check: bool = True,
) -> dict[str, Any]:
    manifest_path = _safe_manifest_path(manifest_raw)
    manifest = load_manifest(manifest_path)
    dump_path = _safe_dump_path(manifest_path, manifest["dump_filename"])

    actual_size = dump_path.stat().st_size
    if actual_size != manifest["bytes"]:
        raise BackupVerificationError("backup size verification failed")
    actual_sha = sha256_file(dump_path)
    if actual_sha != manifest["sha256"]:
        raise BackupVerificationError("backup checksum verification failed")

    created = datetime.strptime(
        manifest["created_at_utc"], "%Y-%m-%dT%H:%M:%SZ"
    ).replace(tzinfo=timezone.utc)
    current = now or datetime.now(timezone.utc)
    age = (current - created).total_seconds()
    if age < -300:
        raise BackupVerificationError("backup timestamp is in the future")
    age = max(age, 0.0)
    if max_age_seconds is not None:
        if max_age_seconds < 0:
            raise BackupVerificationError("max age must be non-negative")
        if age > max_age_seconds:
            raise BackupVerificationError("backup exceeds the configured maximum age")

    toc_entries = run_pg_restore_list(dump_path) if run_structural_check else None
    return {
        "backup_id": manifest["backup_id"],
        "bytes": actual_size,
        "sha256": actual_sha,
        "age_seconds": int(age),
        "toc_entries": toc_entries,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("manifest")
    parser.add_argument("--max-age-seconds", type=int)
    return parser.parse_args()


def main() -> int:
    options = parse_args()
    try:
        result = verify_backup(
            options.manifest,
            max_age_seconds=options.max_age_seconds,
            run_structural_check=True,
        )
        print(
            f"verify-postgres-backup: PASS backup_id={result['backup_id']} "
            f"bytes={result['bytes']} age_seconds={result['age_seconds']} "
            f"toc_entries={result['toc_entries']}"
        )
        return 0
    except BackupVerificationError as exc:
        print(f"verify-postgres-backup: FAIL reason={exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
