#!/usr/bin/env python3
"""Shared, side-effect-free recovery helpers for ICEHOTT Phase 7D."""
from __future__ import annotations

import hashlib
import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, unquote, urlsplit

FINAL_KEY_RE = re.compile(
    r"^objects/(?P<workspace>[0-9a-f]{32})/(?P<artifact>[0-9a-f]{32})\.bin$"
)
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
BACKUP_SET_ID_RE = re.compile(r"^[0-9a-f]{32}$")
UTC_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")


class RecoveryValidationError(ValueError):
    pass


def normalize_prefix(value: str | None) -> str:
    """Return '' or a canonical prefix ending in exactly one slash."""
    raw = (value or "").strip()
    if not raw:
        return ""
    if (
        raw.startswith("/")
        or raw.endswith("/")
        or "\\" in raw
        or "//" in raw
        or any(ord(ch) < 32 for ch in raw)
    ):
        raise RecoveryValidationError("object-storage prefix is invalid")
    parts = raw.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise RecoveryValidationError("object-storage prefix is invalid")
    if any(not re.fullmatch(r"[A-Za-z0-9._-]+", part) for part in parts):
        raise RecoveryValidationError("object-storage prefix is invalid")
    return raw + "/"


def canonical_final_key(value: str) -> tuple[str, str]:
    match = FINAL_KEY_RE.fullmatch(value)
    if not match:
        raise RecoveryValidationError("artifact logical key is not canonical")
    return match.group("workspace"), match.group("artifact")


def sha256_stream(stream, chunk_size: int = 1024 * 1024) -> tuple[str, int]:
    digest = hashlib.sha256()
    size = 0
    while True:
        chunk = stream.read(chunk_size)
        if not chunk:
            break
        digest.update(chunk)
        size += len(chunk)
    return digest.hexdigest(), size


def sha256_file(path: Path) -> str:
    with path.open("rb") as handle:
        digest, _ = sha256_stream(handle)
    return digest


def _absolute_unresolved(path: Path) -> Path:
    return path if path.is_absolute() else Path.cwd() / path


def _reject_parent_traversal(path: Path) -> None:
    if any(part == ".." for part in path.parts):
        raise RecoveryValidationError("path traversal is not permitted")


def _reject_symlink_components(path: Path) -> None:
    absolute = _absolute_unresolved(path)
    anchor = Path(absolute.anchor) if absolute.anchor else Path.cwd()
    parts = absolute.parts[1:] if absolute.anchor else absolute.parts
    current = anchor
    for part in parts:
        current = current / part
        if current.exists() and current.is_symlink():
            raise RecoveryValidationError("path may not traverse a symlink")


def safe_directory_path(raw: str | Path, *, create: bool = False) -> Path:
    """Resolve a directory only after rejecting traversal and symlink components."""
    original = Path(raw).expanduser()
    _reject_parent_traversal(original)
    absolute = _absolute_unresolved(original)
    _reject_symlink_components(absolute)
    if create:
        absolute.mkdir(parents=True, exist_ok=True)
        _reject_symlink_components(absolute)
    if not absolute.exists() or not absolute.is_dir():
        raise RecoveryValidationError("directory was not found")
    return absolute.resolve()


def safe_existing_file(raw: str | Path) -> Path:
    """Return a regular file after rejecting traversal and every symlink component."""
    original = Path(raw).expanduser()
    _reject_parent_traversal(original)
    absolute = _absolute_unresolved(original)
    _reject_symlink_components(absolute)
    if not absolute.exists() or not absolute.is_file():
        raise RecoveryValidationError("file was not found")
    return absolute.resolve()


def atomic_write_text(path: Path, text: str) -> None:
    safe_path = safe_local_output_path(str(path))
    temp = safe_path.with_name(safe_path.name + ".tmp")
    temp.write_text(text, encoding="utf-8", newline="\n")
    os.replace(temp, safe_path)


def safe_local_output_path(raw: str) -> Path:
    """Return a safe output file path without allowing traversal or symlink escape."""
    original = Path(raw).expanduser()
    _reject_parent_traversal(original)
    if original.exists() and original.is_symlink():
        raise RecoveryValidationError("output path may not be a symlink")
    parent = safe_directory_path(original.parent, create=True)
    candidate = parent / original.name
    if candidate.exists() and candidate.is_symlink():
        raise RecoveryValidationError("output path may not be a symlink")
    return candidate


def validate_artifact_manifest(data: Any) -> dict[str, Any]:
    if not isinstance(data, dict):
        raise RecoveryValidationError("artifact manifest must be an object")
    allowed = {
        "schema_version",
        "backup_set_id",
        "created_at_utc",
        "source_release_sha",
        "object_count",
        "total_bytes",
        "objects",
    }
    unknown = set(data) - allowed
    if unknown:
        raise RecoveryValidationError("artifact manifest contains unsupported fields")
    if data.get("schema_version") != 1:
        raise RecoveryValidationError("artifact manifest schema_version must be 1")

    backup_set_id = data.get("backup_set_id")
    if not isinstance(backup_set_id, str) or not BACKUP_SET_ID_RE.fullmatch(backup_set_id):
        raise RecoveryValidationError("artifact manifest backup_set_id is invalid")

    created = data.get("created_at_utc")
    if not isinstance(created, str) or not UTC_RE.fullmatch(created):
        raise RecoveryValidationError("artifact manifest created_at_utc is invalid")
    try:
        datetime.strptime(created, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError as exc:
        raise RecoveryValidationError("artifact manifest created_at_utc is invalid") from exc

    release_sha = data.get("source_release_sha")
    if release_sha is not None and (
        not isinstance(release_sha, str)
        or not re.fullmatch(r"[0-9a-f]{40}", release_sha)
    ):
        raise RecoveryValidationError("artifact manifest source_release_sha must be a full Git SHA")

    object_count = data.get("object_count")
    total_bytes = data.get("total_bytes")
    objects = data.get("objects")
    if not isinstance(object_count, int) or object_count < 0:
        raise RecoveryValidationError("artifact manifest object_count is invalid")
    if not isinstance(total_bytes, int) or total_bytes < 0:
        raise RecoveryValidationError("artifact manifest total_bytes is invalid")
    if not isinstance(objects, list):
        raise RecoveryValidationError("artifact manifest objects must be a list")

    seen: set[str] = set()
    computed_bytes = 0
    normalized: list[dict[str, Any]] = []
    for item in objects:
        if not isinstance(item, dict) or set(item) != {"logical_key", "bytes", "sha256"}:
            raise RecoveryValidationError("artifact manifest object entry is invalid")
        key = item.get("logical_key")
        size = item.get("bytes")
        checksum = item.get("sha256")
        if not isinstance(key, str):
            raise RecoveryValidationError("artifact manifest logical key is invalid")
        canonical_final_key(key)
        if key in seen:
            raise RecoveryValidationError("artifact manifest contains duplicate logical keys")
        seen.add(key)
        if not isinstance(size, int) or size < 1:
            raise RecoveryValidationError("artifact manifest object size is invalid")
        if not isinstance(checksum, str) or not SHA256_RE.fullmatch(checksum):
            raise RecoveryValidationError("artifact manifest object checksum is invalid")
        computed_bytes += size
        normalized.append({"logical_key": key, "bytes": size, "sha256": checksum})

    if object_count != len(normalized):
        raise RecoveryValidationError("artifact manifest object_count does not match objects")
    if total_bytes != computed_bytes:
        raise RecoveryValidationError("artifact manifest total_bytes does not match objects")

    result = dict(data)
    result["objects"] = sorted(normalized, key=lambda item: item["logical_key"])
    return result


def read_verified_artifact_manifest(path_raw: str, *, require_sidecar: bool = True) -> tuple[dict[str, Any], str]:
    path = safe_existing_file(path_raw)
    text = path.read_text(encoding="utf-8")
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
    if require_sidecar:
        try:
            sidecar = safe_existing_file(str(path) + ".sha256")
        except RecoveryValidationError as exc:
            raise RecoveryValidationError("artifact manifest checksum sidecar is required") from exc
        expected = sidecar.read_text(encoding="utf-8").strip()
        if not SHA256_RE.fullmatch(expected) or expected != digest:
            raise RecoveryValidationError("artifact manifest checksum verification failed")
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise RecoveryValidationError("artifact manifest JSON is invalid") from exc
    return validate_artifact_manifest(data), digest


def parse_postgres_url(value: str) -> dict[str, str]:
    """Parse a PostgreSQL URI without logging it."""
    if not isinstance(value, str) or "\n" in value or "\r" in value or "\t" in value:
        raise RecoveryValidationError("PostgreSQL connection URL is invalid")
    parts = urlsplit(value)
    if parts.scheme not in ("postgres", "postgresql"):
        raise RecoveryValidationError("PostgreSQL connection URL must use postgres/postgresql")
    if parts.username is None or parts.password is None or not parts.hostname:
        raise RecoveryValidationError("PostgreSQL connection URL is incomplete")
    database = unquote(parts.path.lstrip("/"))
    if not database or "/" in database:
        raise RecoveryValidationError("PostgreSQL database name is invalid")
    try:
        port = str(parts.port or 5432)
    except ValueError as exc:
        raise RecoveryValidationError("PostgreSQL connection URL port is invalid") from exc
    query = parse_qs(parts.query, keep_blank_values=False)
    sslmode = query.get("sslmode", [""])[-1]
    if sslmode and sslmode not in {
        "disable", "allow", "prefer", "require", "verify-ca", "verify-full"
    }:
        raise RecoveryValidationError("PostgreSQL sslmode is invalid")
    return {
        "host": parts.hostname,
        "port": port,
        "database": database,
        "user": unquote(parts.username),
        "password": unquote(parts.password),
        "sslmode": sslmode,
    }


def is_safe_restore_database(database: str) -> bool:
    lowered = database.lower()
    return any(marker in lowered for marker in ("restore", "drill", "test"))


def role_credentials(role: str) -> tuple[str | None, str | None]:
    upper = role.upper()
    access = os.getenv(f"ICEHOTT_{upper}_S3_ACCESS_KEY_ID")
    secret = os.getenv(f"ICEHOTT_{upper}_S3_SECRET_ACCESS_KEY")
    if bool(access) != bool(secret):
        raise RecoveryValidationError(f"{role} S3 credentials must be configured as a pair")
    return access, secret


def make_s3_client(role: str, endpoint: str | None, region: str):
    import boto3
    access, secret = role_credentials(role)
    kwargs: dict[str, Any] = {"region_name": region}
    if endpoint:
        kwargs["endpoint_url"] = endpoint
    if access and secret:
        kwargs["aws_access_key_id"] = access
        kwargs["aws_secret_access_key"] = secret
    return boto3.client("s3", **kwargs)
