#!/usr/bin/env python3
"""Read-only reconciliation of restored artifact metadata against object storage."""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

import psycopg2
import psycopg2.extras
from botocore.exceptions import BotoCoreError, ClientError

from recovery_common import (
    RecoveryValidationError,
    SHA256_RE,
    canonical_final_key,
    make_s3_client,
    normalize_prefix,
    sha256_stream,
    safe_local_output_path,
    atomic_write_text,
)

CHUNK = 1024 * 1024


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bucket", required=True)
    parser.add_argument("--prefix", default="")
    parser.add_argument("--endpoint")
    parser.add_argument("--region", default="us-east-1")
    parser.add_argument("--evidence-out", required=True)
    parser.add_argument("--strict", action="store_true")
    return parser.parse_args()


def provider_code(exc: BaseException) -> str:
    if isinstance(exc, ClientError):
        return str(exc.response.get("Error", {}).get("Code", "client_error"))[:80]
    return exc.__class__.__name__


def canonical_logical_key(value: str) -> str | None:
    """Return the canonical final-artifact key, or None for any invalid key."""
    try:
        canonical_final_key(value)
        return value
    except RecoveryValidationError:
        return None


def fetch_artifacts(connection) -> list[dict]:
    with connection.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cursor:
        cursor.execute(
            """
            SELECT
                "Id"::text AS id,
                "WorkspaceId"::text AS workspace_id,
                "Status" AS status,
                "StorageKey" AS storage_key,
                "SizeBytes" AS size_bytes,
                "Sha256" AS sha256,
                "StorageDeletedAtUtc" AS storage_deleted_at_utc
            FROM artifacts
            """
        )
        return [dict(row) for row in cursor.fetchall()]


def list_final_namespace(client, bucket: str, prefix: str) -> tuple[set[str], int]:
    canonical: set[str] = set()
    malformed = 0
    paginator = client.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=bucket, Prefix=f"{prefix}objects/"):
        for item in page.get("Contents", []):
            remote_key = str(item["Key"])
            if not remote_key.startswith(prefix):
                malformed += 1
                continue
            logical = remote_key[len(prefix):]
            try:
                canonical_final_key(logical)
            except RecoveryValidationError:
                malformed += 1
                continue
            canonical.add(logical)
    return canonical, malformed


def object_hash_size(client, bucket: str, key: str) -> tuple[str, int]:
    response = client.get_object(Bucket=bucket, Key=key)
    body = response["Body"]
    try:
        return sha256_stream(body, CHUNK)
    finally:
        body.close()


def safe_evidence_path(raw: str) -> Path:
    return safe_local_output_path(raw)


def main() -> int:
    options = arguments()
    db_url = os.getenv("ICEHOTT_RECONCILE_DATABASE_URL")
    if not db_url:
        print("reconcile: FAIL configuration=database_url_missing", file=sys.stderr)
        return 2

    started = datetime.now(timezone.utc)
    counts = {
        "total_artifacts": 0,
        "ready": 0,
        "pending": 0,
        "failed": 0,
        "deleted_pending_gc": 0,
        "deleted_storage_gc": 0,
        "unknown_status": 0,
        "storage_final_objects": 0,
        "storage_malformed_final_objects": 0,
        "ready_missing": 0,
        "ready_corrupt": 0,
        "ready_key_mismatch": 0,
        "orphan_final_objects": 0,
    }

    try:
        prefix = normalize_prefix(options.prefix)
        evidence_path = safe_evidence_path(options.evidence_out)

        # No DB URL is ever placed into process arguments by this tool.
        connection = psycopg2.connect(db_url)
        try:
            connection.set_session(readonly=True, autocommit=True)
            rows = fetch_artifacts(connection)
        finally:
            connection.close()

        s3 = make_s3_client("target", options.endpoint, options.region)
        s3.head_bucket(Bucket=options.bucket)
        storage_keys, malformed_count = list_final_namespace(s3, options.bucket, prefix)

        counts["total_artifacts"] = len(rows)
        counts["storage_final_objects"] = len(storage_keys)
        counts["storage_malformed_final_objects"] = malformed_count

        authorized_keys: set[str] = set()
        pending_gc_keys: set[str] = set()

        for row in rows:
            status = row.get("status")
            if status == "Ready":
                counts["ready"] += 1
                key = row.get("storage_key")
                size = row.get("size_bytes")
                checksum = row.get("sha256")
                try:
                    if not isinstance(key, str):
                        raise RecoveryValidationError("missing key")
                    ws_key, art_key = canonical_final_key(key)
                    db_ws = str(row["workspace_id"]).replace("-", "").lower()
                    db_art = str(row["id"]).replace("-", "").lower()
                    if ws_key != db_ws or art_key != db_art:
                        raise RecoveryValidationError("identity mismatch")
                    if not isinstance(size, int) or size < 1:
                        raise RecoveryValidationError("invalid size")
                    if not isinstance(checksum, str) or not SHA256_RE.fullmatch(checksum):
                        raise RecoveryValidationError("invalid checksum")
                except RecoveryValidationError:
                    counts["ready_key_mismatch"] += 1
                    continue

                authorized_keys.add(key)
                if key not in storage_keys:
                    counts["ready_missing"] += 1
                    continue
                try:
                    actual_sha, actual_size = object_hash_size(
                        s3, options.bucket, f"{prefix}{key}"
                    )
                except (BotoCoreError, ClientError, OSError):
                    counts["ready_corrupt"] += 1
                    continue
                if actual_sha != checksum or actual_size != size:
                    counts["ready_corrupt"] += 1

            elif status == "Pending":
                counts["pending"] += 1
            elif status == "Failed":
                counts["failed"] += 1
            elif status == "Deleted":
                key = row.get("storage_key")
                if row.get("storage_deleted_at_utc") is None:
                    counts["deleted_pending_gc"] += 1
                    if isinstance(key, str):
                        try:
                            canonical_final_key(key)
                            pending_gc_keys.add(key)
                        except RecoveryValidationError:
                            pass
                else:
                    counts["deleted_storage_gc"] += 1
            else:
                counts["unknown_status"] += 1

        orphan_keys = storage_keys - authorized_keys - pending_gc_keys
        counts["orphan_final_objects"] = len(orphan_keys)

        strict_failures = (
            counts["ready_missing"]
            + counts["ready_corrupt"]
            + counts["ready_key_mismatch"]
            + counts["storage_malformed_final_objects"]
            + counts["unknown_status"]
        )
        if options.strict:
            strict_failures += counts["orphan_final_objects"]

        completed = datetime.now(timezone.utc)
        evidence = {
            "schema_version": 1,
            "tool": "reconcile-restore",
            "started_utc": started.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "completed_utc": completed.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "strict_mode": options.strict,
            "counts": counts,
            "pass": strict_failures == 0,
        }
        atomic_write_text(
            evidence_path,
            json.dumps(evidence, indent=2, sort_keys=True) + "\n",
        )
        print(
            "reconcile: "
            + ("PASS" if evidence["pass"] else "FAIL")
            + " "
            + " ".join(f"{key}={value}" for key, value in counts.items())
        )
        return 0 if evidence["pass"] else 1

    except (RecoveryValidationError, psycopg2.Error, BotoCoreError, ClientError, OSError) as exc:
        print(f"reconcile: FAIL type={provider_code(exc)}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
