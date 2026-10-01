#!/usr/bin/env python3
"""Back up final ICEHOTT artifact objects to an immutable S3-compatible backup set."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

from botocore.exceptions import BotoCoreError, ClientError

from recovery_common import (
    RecoveryValidationError,
    atomic_write_text,
    canonical_final_key,
    make_s3_client,
    normalize_prefix,
    safe_local_output_path,
    sha256_stream,
    validate_artifact_manifest,
)

CHUNK = 1024 * 1024


def args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-bucket", required=True)
    parser.add_argument("--backup-bucket", required=True)
    parser.add_argument("--source-prefix", default="")
    parser.add_argument("--backup-prefix", default="")
    parser.add_argument("--source-endpoint")
    parser.add_argument("--backup-endpoint")
    parser.add_argument("--source-region", default="us-east-1")
    parser.add_argument("--backup-region", default="us-east-1")
    parser.add_argument("--manifest-out", required=True)
    parser.add_argument("--release-sha")
    return parser.parse_args()


def safe_provider_code(exc: BaseException) -> str:
    if isinstance(exc, ClientError):
        return str(exc.response.get("Error", {}).get("Code", "client_error"))[:80]
    return exc.__class__.__name__


def get_hash_size(client, bucket: str, key: str) -> tuple[str, int]:
    response = client.get_object(Bucket=bucket, Key=key)
    body = response["Body"]
    try:
        return sha256_stream(body)
    finally:
        body.close()


def put_from_temp_immutable(client, bucket: str, key: str, temp, size: int) -> None:
    temp.seek(0)
    client.put_object(
        Bucket=bucket,
        Key=key,
        Body=temp,
        ContentLength=size,
        IfNoneMatch="*",
    )


def backup_one(
    source,
    backup,
    source_bucket: str,
    source_key: str,
    backup_bucket: str,
    backup_key: str,
) -> tuple[str, int]:
    with tempfile.TemporaryFile() as temp:
        response = source.get_object(Bucket=source_bucket, Key=source_key)
        body = response["Body"]
        digest = hashlib.sha256()
        size = 0
        try:
            while True:
                chunk = body.read(CHUNK)
                if not chunk:
                    break
                digest.update(chunk)
                size += len(chunk)
                temp.write(chunk)
        finally:
            body.close()

        if size < 1:
            raise RecoveryValidationError("zero-byte final artifact cannot be backed up")
        checksum = digest.hexdigest()
        put_from_temp_immutable(backup, backup_bucket, backup_key, temp, size)
        verified_sha, verified_size = get_hash_size(backup, backup_bucket, backup_key)
        if verified_sha != checksum or verified_size != size:
            raise RecoveryValidationError("backup object integrity verification failed")
        return checksum, size


def main() -> int:
    options = args()
    try:
        source_prefix = normalize_prefix(options.source_prefix)
        backup_prefix = normalize_prefix(options.backup_prefix)
        if options.release_sha and not __import__("re").fullmatch(r"[0-9a-fA-F]{40}", options.release_sha):
            raise RecoveryValidationError("--release-sha must be the full 40-character Git SHA")
        manifest_path = safe_local_output_path(options.manifest_out)
        source = make_s3_client("source", options.source_endpoint, options.source_region)
        backup = make_s3_client("backup", options.backup_endpoint, options.backup_region)

        source.head_bucket(Bucket=options.source_bucket)
        backup.head_bucket(Bucket=options.backup_bucket)

        backup_set_id = uuid.uuid4().hex
        remote_root = f"{backup_prefix}{backup_set_id}/"
        records: list[dict] = []
        total_bytes = 0

        paginator = source.get_paginator("list_objects_v2")
        listed = paginator.paginate(
            Bucket=options.source_bucket,
            Prefix=f"{source_prefix}objects/",
        )
        for page in listed:
            for item in page.get("Contents", []):
                remote_source_key = item["Key"]
                logical_key = remote_source_key[len(source_prefix):]
                try:
                    canonical_final_key(logical_key)
                except RecoveryValidationError as exc:
                    raise RecoveryValidationError(
                        "source final namespace contains a non-canonical artifact key"
                    ) from exc
                remote_backup_key = f"{remote_root}{logical_key}"
                try:
                    checksum, size = backup_one(
                        source,
                        backup,
                        options.source_bucket,
                        remote_source_key,
                        options.backup_bucket,
                        remote_backup_key,
                    )
                except ClientError as exc:
                    code = exc.response.get("Error", {}).get("Code", "")
                    if str(code) in ("PreconditionFailed", "412"):
                        raise RecoveryValidationError("immutable backup destination already exists") from exc
                    raise
                records.append(
                    {"logical_key": logical_key, "bytes": size, "sha256": checksum}
                )
                total_bytes += size

        manifest = validate_artifact_manifest(
            {
                "schema_version": 1,
                "backup_set_id": backup_set_id,
                "created_at_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                **(
                    {"source_release_sha": options.release_sha.lower()}
                    if options.release_sha
                    else {}
                ),
                "object_count": len(records),
                "total_bytes": total_bytes,
                "objects": records,
            }
        )
        manifest_text = json.dumps(manifest, indent=2, sort_keys=True) + "\n"
        manifest_sha = hashlib.sha256(manifest_text.encode("utf-8")).hexdigest()

        # Publish the checksum sidecar first and manifest last. Manifest presence is
        # the immutable backup-set publication signal.
        backup.put_object(
            Bucket=options.backup_bucket,
            Key=f"{remote_root}manifest.sha256",
            Body=(manifest_sha + "\n").encode("ascii"),
            ContentLength=65,
            ContentType="text/plain",
            IfNoneMatch="*",
        )
        backup.put_object(
            Bucket=options.backup_bucket,
            Key=f"{remote_root}manifest.json",
            Body=manifest_text.encode("utf-8"),
            ContentLength=len(manifest_text.encode("utf-8")),
            ContentType="application/json",
            IfNoneMatch="*",
        )

        atomic_write_text(manifest_path, manifest_text)
        atomic_write_text(Path(str(manifest_path) + ".sha256"), manifest_sha + "\n")
        print(
            f"backup-artifacts: PASS set={backup_set_id} objects={len(records)} bytes={total_bytes} "
            f"manifest_sha256={manifest_sha}"
        )
        return 0
    except (RecoveryValidationError, BotoCoreError, ClientError, OSError) as exc:
        print(f"backup-artifacts: FAIL type={safe_provider_code(exc)}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
