#!/usr/bin/env python3
"""PostgreSQL backup/restore operations for ICEHOTT Phase 7D."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

from recovery_common import (
    RecoveryValidationError,
    is_safe_restore_database,
    parse_postgres_url,
    safe_directory_path,
    safe_existing_file,
)
from importlib.util import module_from_spec, spec_from_file_location

_HERE = Path(__file__).resolve().parent
_verify_spec = spec_from_file_location(
    "verify_postgres_backup", _HERE / "verify-postgres-backup.py"
)
if _verify_spec is None or _verify_spec.loader is None:
    raise RuntimeError("cannot load backup verifier")
_verify = module_from_spec(_verify_spec)
_verify_spec.loader.exec_module(_verify)


def fail(message: str) -> int:
    print(f"postgres-recovery: FAIL reason={message}", file=sys.stderr)
    return 1


def pg_env(url_env_name: str) -> tuple[dict[str, str], str]:
    value = os.getenv(url_env_name)
    if not value:
        raise RecoveryValidationError(f"{url_env_name} is required")
    parsed = parse_postgres_url(value)
    env = os.environ.copy()
    # Remove raw connection URLs before invoking PostgreSQL child processes.
    env.pop("ICEHOTT_BACKUP_DATABASE_URL", None)
    env.pop("ICEHOTT_RESTORE_DATABASE_URL", None)
    env["PGHOST"] = parsed["host"]
    env["PGPORT"] = parsed["port"]
    env["PGDATABASE"] = parsed["database"]
    env["PGUSER"] = parsed["user"]
    env["PGPASSWORD"] = parsed["password"]
    if parsed["sslmode"]:
        env["PGSSLMODE"] = parsed["sslmode"]
    return env, parsed["database"]


def safe_backup_dir(raw: str) -> Path:
    return safe_directory_path(raw, create=True)


def command(name: str) -> str:
    path = shutil.which(name)
    if not path:
        raise RecoveryValidationError(f"{name} is required")
    return path


def backup(options: argparse.Namespace) -> int:
    try:
        env, _ = pg_env("ICEHOTT_BACKUP_DATABASE_URL")
        backup_dir = safe_backup_dir(options.backup_dir)
        pg_dump = command("pg_dump")
        pg_restore = command("pg_restore")

        release_sha = options.release_sha or os.getenv("RELEASE_GIT_SHA") or ""
        if release_sha and not __import__("re").fullmatch(r"[0-9a-fA-F]{40}", release_sha):
            raise RecoveryValidationError("release SHA must be the full 40-character Git SHA")
        release_sha = release_sha.lower()

        backup_id = uuid.uuid4().hex
        dump_name = f"icehott-{backup_id}.dump"
        manifest_name = f"icehott-{backup_id}.manifest.json"
        created = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

        with tempfile.TemporaryDirectory(prefix=".tmp-backup-", dir=backup_dir) as temp_dir:
            temp_root = Path(temp_dir)
            temp_dump = temp_root / dump_name
            temp_manifest = temp_root / manifest_name

            result = subprocess.run(
                [
                    pg_dump,
                    "--format=custom",
                    "--no-owner",
                    "--no-privileges",
                    f"--file={temp_dump}",
                ],
                env=env,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                text=True,
                timeout=3600,
                check=False,
            )
            if result.returncode != 0:
                raise RecoveryValidationError("pg_dump failed")

            structural = subprocess.run(
                [pg_restore, "--list", str(temp_dump)],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=120,
                check=False,
            )
            if structural.returncode != 0:
                raise RecoveryValidationError("pg_restore structural verification failed")

            size = temp_dump.stat().st_size
            if size < 1:
                raise RecoveryValidationError("pg_dump produced an empty backup")
            digest = hashlib.sha256()
            with temp_dump.open("rb") as handle:
                for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                    digest.update(chunk)
            checksum = digest.hexdigest()

            version = subprocess.run(
                [pg_dump, "--version"],
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
                timeout=30,
                check=False,
            ).stdout.strip()
            if not version.startswith("pg_dump"):
                raise RecoveryValidationError("pg_dump version could not be determined")

            manifest = {
                "schema_version": 1,
                "backup_id": backup_id,
                "created_at_utc": created,
                "dump_filename": dump_name,
                "bytes": size,
                "sha256": checksum,
                "format": "pg_dump_custom_Fc",
                "pg_dump_version": version,
            }
            if release_sha:
                manifest["release_git_sha"] = release_sha
            temp_manifest.write_text(
                json.dumps(manifest, indent=2, sort_keys=True) + "\n",
                encoding="utf-8",
                newline="\n",
            )

            final_dump = backup_dir / dump_name
            final_manifest = backup_dir / manifest_name
            os.replace(temp_dump, final_dump)
            os.replace(temp_manifest, final_manifest)

        print(
            f"backup-postgres: PASS backup_id={backup_id} bytes={size} sha256={checksum}"
        )
        return 0
    except (
        RecoveryValidationError,
        OSError,
        subprocess.SubprocessError,
    ) as exc:
        return fail(str(exc))


def restore(options: argparse.Namespace) -> int:
    try:
        if os.getenv("ICEHOTT_RESTORE_CONFIRM") != "RESTORE":
            raise RecoveryValidationError(
                "ICEHOTT_RESTORE_CONFIRM must equal RESTORE"
            )
        env, database = pg_env("ICEHOTT_RESTORE_DATABASE_URL")
        dangerous = os.getenv("ICEHOTT_ALLOW_DANGEROUS_RESTORE", "").lower() == "true"
        if not is_safe_restore_database(database) and not dangerous:
            raise RecoveryValidationError(
                "target database is not an isolated restore/drill/test target"
            )

        verified = _verify.verify_backup(
            options.manifest,
            max_age_seconds=options.max_age_seconds,
            run_structural_check=True,
        )
        manifest_path = safe_existing_file(options.manifest)
        manifest = _verify.load_manifest(manifest_path)
        dump_path = _verify._safe_dump_path(manifest_path, manifest["dump_filename"])

        psql = command("psql")
        pg_restore = command("pg_restore")

        # Target must already exist and accept a connection.
        probe = subprocess.run(
            [psql, "--no-psqlrc", "--tuples-only", "--no-align", "--command=SELECT 1;"],
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=30,
            check=False,
        )
        if probe.returncode != 0 or probe.stdout.strip() != "1":
            raise RecoveryValidationError("target database is not reachable or does not exist")

        result = subprocess.run(
            [
                pg_restore,
                "--clean",
                "--if-exists",
                "--no-owner",
                "--no-privileges",
                "--exit-on-error",
                f"--dbname={database}",
                str(dump_path),
            ],
            env=env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
            timeout=3600,
            check=False,
        )
        if result.returncode != 0:
            raise RecoveryValidationError("pg_restore failed")

        print(
            f"restore-postgres: PASS backup_id={verified['backup_id']} bytes={verified['bytes']}"
        )
        return 0
    except (
        RecoveryValidationError,
        _verify.BackupVerificationError,
        OSError,
        subprocess.SubprocessError,
    ) as exc:
        return fail(str(exc))


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser()
    sub = root.add_subparsers(dest="operation", required=True)
    backup_cmd = sub.add_parser("backup")
    backup_cmd.add_argument("--backup-dir", required=True)
    backup_cmd.add_argument("--release-sha")

    restore_cmd = sub.add_parser("restore")
    restore_cmd.add_argument("--manifest", required=True)
    restore_cmd.add_argument("--max-age-seconds", type=int)

    return root


def main() -> int:
    options = parser().parse_args()
    return backup(options) if options.operation == "backup" else restore(options)


if __name__ == "__main__":
    raise SystemExit(main())
