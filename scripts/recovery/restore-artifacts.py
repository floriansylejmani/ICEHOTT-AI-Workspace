#!/usr/bin/env python3
"""Restore an ICEHOTT artifact backup set to a fresh S3-compatible namespace."""
from __future__ import annotations

import argparse
import sys
import tempfile

from botocore.exceptions import BotoCoreError, ClientError

from recovery_common import (
    RecoveryValidationError,
    canonical_final_key,
    make_s3_client,
    normalize_prefix,
    read_verified_artifact_manifest,
    sha256_stream,
)

CHUNK = 1024 * 1024


def args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--backup-bucket", required=True)
    parser.add_argument("--target-bucket", required=True)
    parser.add_argument("--backup-prefix", default="")
    parser.add_argument("--target-prefix", default="")
    parser.add_argument("--backup-endpoint")
    parser.add_argument("--target-endpoint")
    parser.add_argument("--backup-region", default="us-east-1")
    parser.add_argument("--target-region", default="us-east-1")
    parser.add_argument("--allow-non-empty-target", action="store_true")
    return parser.parse_args()


def provider_code(exc: BaseException) -> str:
    if isinstance(exc, ClientError):
        return str(exc.response.get("Error", {}).get("Code", "client_error"))[:80]
    return exc.__class__.__name__


def hash_object(client, bucket: str, key: str) -> tuple[str, int]:
    response = client.get_object(Bucket=bucket, Key=key)
    body = response["Body"]
    try:
        return sha256_stream(body)
    finally:
        body.close()


def target_exists(client, bucket: str, key: str) -> bool:
    try:
        client.head_object(Bucket=bucket, Key=key)
        return True
    except ClientError as exc:
        code = str(exc.response.get("Error", {}).get("Code", ""))
        status = exc.response.get("ResponseMetadata", {}).get("HTTPStatusCode")
        if code in ("404", "NoSuchKey", "NotFound") or status == 404:
            return False
        raise


def restore_one(
    backup,
    target,
    backup_bucket: str,
    source_key: str,
    target_bucket: str,
    target_key: str,
    expected_sha: str,
    expected_size: int,
) -> str:
    if target_exists(target, target_bucket, target_key):
        actual_sha, actual_size = hash_object(target, target_bucket, target_key)
        if actual_sha == expected_sha and actual_size == expected_size:
            return "identical"
        raise RecoveryValidationError("target object exists with mismatched content")

    with tempfile.TemporaryFile() as temp:
        response = backup.get_object(Bucket=backup_bucket, Key=source_key)
        body = response["Body"]
        try:
            actual_sha, actual_size = sha256_stream_to_file(body, temp)
        finally:
            body.close()
        if actual_sha != expected_sha or actual_size != expected_size:
            raise RecoveryValidationError("backup object integrity verification failed")

        temp.seek(0)
        try:
            target.put_object(
                Bucket=target_bucket,
                Key=target_key,
                Body=temp,
                ContentLength=actual_size,
                IfNoneMatch="*",
            )
        except ClientError as exc:
            code = str(exc.response.get("Error", {}).get("Code", ""))
            status = exc.response.get("ResponseMetadata", {}).get("HTTPStatusCode")
            if code not in ("PreconditionFailed", "ConditionalRequestConflict") and status not in (409, 412):
                raise
            # A concurrent writer won. Verify the winner instead of overwriting.
            winner_sha, winner_size = hash_object(target, target_bucket, target_key)
            if winner_sha == expected_sha and winner_size == expected_size:
                return "identical"
            raise RecoveryValidationError("concurrent target object has mismatched content") from exc

    verified_sha, verified_size = hash_object(target, target_bucket, target_key)
    if verified_sha != expected_sha or verified_size != expected_size:
        raise RecoveryValidationError("restored object integrity verification failed")
    return "restored"


def sha256_stream_to_file(stream, temp) -> tuple[str, int]:
    import hashlib
    digest = hashlib.sha256()
    size = 0
    while True:
        chunk = stream.read(CHUNK)
        if not chunk:
            break
        digest.update(chunk)
        size += len(chunk)
        temp.write(chunk)
    return digest.hexdigest(), size


def main() -> int:
    options = args()
    try:
        manifest, _ = read_verified_artifact_manifest(options.manifest)
        backup_prefix = normalize_prefix(options.backup_prefix)
        target_prefix = normalize_prefix(options.target_prefix)

        backup = make_s3_client("backup", options.backup_endpoint, options.backup_region)
        target = make_s3_client("target", options.target_endpoint, options.target_region)
        backup.head_bucket(Bucket=options.backup_bucket)
        target.head_bucket(Bucket=options.target_bucket)

        if not options.allow_non_empty_target:
            result = target.list_objects_v2(
                Bucket=options.target_bucket,
                Prefix=target_prefix,
                MaxKeys=1,
            )
            if result.get("KeyCount", 0) or result.get("Contents"):
                raise RecoveryValidationError("target namespace must be empty")

        restored = 0
        identical = 0
        root = f"{backup_prefix}{manifest['backup_set_id']}/"
        for item in manifest["objects"]:
            logical_key = item["logical_key"]
            canonical_final_key(logical_key)
            outcome = restore_one(
                backup,
                target,
                options.backup_bucket,
                f"{root}{logical_key}",
                options.target_bucket,
                f"{target_prefix}{logical_key}",
                item["sha256"],
                item["bytes"],
            )
            if outcome == "restored":
                restored += 1
            else:
                identical += 1

        print(
            f"restore-artifacts: PASS set={manifest['backup_set_id']} "
            f"restored={restored} identical={identical}"
        )
        return 0
    except (RecoveryValidationError, BotoCoreError, ClientError, OSError) as exc:
        print(f"restore-artifacts: FAIL type={provider_code(exc)}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
