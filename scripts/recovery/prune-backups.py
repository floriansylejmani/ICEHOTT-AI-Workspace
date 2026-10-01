#!/usr/bin/env python3
"""prune-backups.py — ICEHOTT Phase 7D deterministic retention for PostgreSQL backups.

Retention tiers (applied cumulatively — a backup is kept if ANY tier selects it):
  hourly  last 48 hours   → keep the most-recent backup in each UTC hour bucket
  daily   last 14 days    → keep the most-recent backup in each UTC day  bucket
  weekly  last 8 weeks    → keep the most-recent backup in each UTC ISO week bucket

Usage:
    prune-backups.py --backup-dir <dir> [--dry-run | --apply]
                     [--now-utc YYYY-MM-DDTHH:MM:SSZ]

  --dry-run   (default) Print what would be deleted but make no changes.
  --apply     Actually delete files that no retention tier selects.
  --now-utc   Override current UTC time (for testing).

Safety:
  - Operates ONLY within the resolved --backup-dir; rejects symlinks and traversal.
  - Only deletes .dump and .manifest.json files that form complete, verified pairs.
  - Unknown file types and incomplete pairs (only .dump or only .manifest.json) are
    never deleted; a warning is printed instead.
  - Uses UTC buckets exclusively.
  - Nonzero exit if unexpected errors occur.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

from recovery_common import RecoveryValidationError, safe_directory_path

DUMP_RE = re.compile(r"^icehott-([0-9a-f]{32})\.dump$")
MANIFEST_RE = re.compile(r"^icehott-([0-9a-f]{32})\.manifest\.json$")
DATETIME_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Apply retention policy to ICEHOTT PostgreSQL backups.",
    )
    p.add_argument("--backup-dir", required=True, help="Directory containing backup files.")
    mode = p.add_mutually_exclusive_group()
    mode.add_argument("--dry-run", action="store_true", default=False, help="Print actions; make no changes (default).")
    mode.add_argument("--apply", action="store_true", default=False, help="Actually delete expired backups.")
    p.add_argument(
        "--now-utc",
        default=None,
        help="Override current UTC time as YYYY-MM-DDTHH:MM:SSZ (testing only).",
    )
    return p.parse_args()


def resolve_backup_dir(raw: str) -> Path:
    try:
        return safe_directory_path(raw, create=False)
    except RecoveryValidationError as exc:
        raise SystemExit(f"prune: unsafe backup directory: {exc}") from exc


def safe_child(backup_dir: Path, filename: str) -> Path:
    """Resolve filename within backup_dir; raise if it would escape."""
    child = (backup_dir / filename).resolve()
    try:
        child.relative_to(backup_dir)
    except ValueError:
        raise SystemExit(f"prune: file path escapes backup directory: {filename}")
    return child


def load_manifest(manifest_path: Path) -> datetime | None:
    """Return the backup creation time from a manifest, or None if unparseable."""
    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
        if data.get("schema_version") != 1:
            return None
        ts_str = data.get("created_at_utc", "")
        if not DATETIME_RE.match(ts_str):
            return None
        return datetime.strptime(ts_str, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except Exception:
        return None


class BackupPair:
    """A complete manifest+dump pair with a parsed creation timestamp."""

    def __init__(self, backup_id: str, dump_path: Path, manifest_path: Path, created_at: datetime):
        self.backup_id = backup_id
        self.dump_path = dump_path
        self.manifest_path = manifest_path
        self.created_at = created_at

    @property
    def hour_bucket(self) -> tuple[int, int, int, int]:
        dt = self.created_at
        return (dt.year, dt.month, dt.day, dt.hour)

    @property
    def day_bucket(self) -> tuple[int, int, int]:
        dt = self.created_at
        return (dt.year, dt.month, dt.day)

    @property
    def week_bucket(self) -> tuple[int, int]:
        iso = self.created_at.isocalendar()
        return (iso.year, iso.week)


def collect_pairs(backup_dir: Path) -> tuple[list[BackupPair], list[str]]:
    """
    Return (pairs, warnings).
    pairs: complete, parseable backup pairs
    warnings: messages about incomplete/unknown files
    """
    dumps: dict[str, Path] = {}
    manifests: dict[str, Path] = {}
    unknown: list[Path] = []

    for entry in backup_dir.iterdir():
        if entry.is_symlink():
            unknown.append(entry)
            continue
        if not entry.is_file():
            continue
        m = DUMP_RE.match(entry.name)
        if m:
            dumps[m.group(1)] = entry
            continue
        m = MANIFEST_RE.match(entry.name)
        if m:
            manifests[m.group(1)] = entry
            continue
        # Ignore hidden/temp files silently (e.g. .tmp-backup-*)
        if entry.name.startswith("."):
            continue
        unknown.append(entry)

    warnings: list[str] = []
    pairs: list[BackupPair] = []

    for bid in sorted(set(dumps) | set(manifests)):
        has_dump = bid in dumps
        has_manifest = bid in manifests
        if not has_dump or not has_manifest:
            # Incomplete pair — NEVER delete
            missing = "dump" if not has_dump else "manifest"
            warnings.append(
                f"incomplete pair backup_id={bid}: missing {missing} — skipping (will not delete)"
            )
            continue

        manifest_path = manifests[bid]
        dump_path = dumps[bid]

        created_at = load_manifest(manifest_path)
        if created_at is None:
            warnings.append(
                f"cannot parse manifest for backup_id={bid} — skipping (will not delete)"
            )
            continue

        pairs.append(BackupPair(bid, dump_path, manifest_path, created_at))

    for p in unknown:
        warnings.append(f"unknown file in backup directory: {p.name} — will not delete")

    return pairs, warnings


def apply_retention(pairs: list[BackupPair], now: datetime) -> tuple[set[str], set[str]]:
    """
    Returns (keep_ids, delete_ids).

    Retention windows (open on the past end, closed at `now`):
      hourly: now - 48h  → keep newest backup per UTC hour bucket
      daily:  now - 14d  → keep newest backup per UTC day  bucket
      weekly: now - 8w   → keep newest backup per UTC ISO week bucket
    """
    hourly_cutoff = now - timedelta(hours=48)
    daily_cutoff  = now - timedelta(days=14)
    weekly_cutoff = now - timedelta(weeks=8)

    # Newest-per-bucket helpers
    def best_per_bucket(candidates: list[BackupPair], key_fn) -> set[str]:
        bucket_best: dict = {}
        for p in candidates:
            k = key_fn(p)
            if k not in bucket_best or p.created_at > bucket_best[k].created_at:
                bucket_best[k] = p
        return {v.backup_id for v in bucket_best.values()}

    # Future-dated backups are never auto-deleted. Clock skew/timestamp anomalies
    # require operator review rather than destructive retention.
    future_ids = {p.backup_id for p in pairs if p.created_at > now}
    hourly_candidates = [p for p in pairs if hourly_cutoff <= p.created_at <= now]
    daily_candidates  = [p for p in pairs if daily_cutoff <= p.created_at <= now]
    weekly_candidates = [p for p in pairs if weekly_cutoff <= p.created_at <= now]

    keep_ids: set[str] = set(future_ids)
    keep_ids |= best_per_bucket(hourly_candidates, lambda p: p.hour_bucket)
    keep_ids |= best_per_bucket(daily_candidates,  lambda p: p.day_bucket)
    keep_ids |= best_per_bucket(weekly_candidates, lambda p: p.week_bucket)

    all_ids  = {p.backup_id for p in pairs}
    delete_ids = all_ids - keep_ids
    return keep_ids, delete_ids


def main() -> None:
    args = parse_args()

    # Default to dry-run if neither flag given
    apply = args.apply

    backup_dir = resolve_backup_dir(args.backup_dir)

    if args.now_utc:
        if not DATETIME_RE.match(args.now_utc):
            raise SystemExit(f"prune: --now-utc must be YYYY-MM-DDTHH:MM:SSZ, got: {args.now_utc!r}")
        now = datetime.strptime(args.now_utc, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        print(f"prune: using override now={args.now_utc}")
    else:
        now = datetime.now(timezone.utc)
        print(f"prune: now={now.strftime('%Y-%m-%dT%H:%M:%SZ')}")

    print(f"prune: backup_dir={backup_dir}")
    print(f"prune: mode={'apply' if apply else 'dry-run'}")

    pairs, warnings = collect_pairs(backup_dir)

    for w in warnings:
        print(f"WARN: {w}")

    keep_ids, delete_ids = apply_retention(pairs, now)

    print(f"prune: total_pairs={len(pairs)} keep={len(keep_ids)} delete={len(delete_ids)}")

    pairs_by_id = {p.backup_id: p for p in pairs}
    deleted = 0

    for bid in sorted(delete_ids, key=lambda b: pairs_by_id[b].created_at):
        pair = pairs_by_id[bid]
        age_days = (now - pair.created_at).days
        msg = (
            f"  {'DELETE' if apply else 'WOULD DELETE'} "
            f"backup_id={bid} created={pair.created_at.strftime('%Y-%m-%dT%H:%M:%SZ')} "
            f"age_days={age_days}"
        )
        print(msg)
        if apply:
            # Delete dump first; if interrupted the incomplete pair is preserved (won't be deleted next run)
            if pair.dump_path.exists():
                pair.dump_path.unlink()
            if pair.manifest_path.exists():
                pair.manifest_path.unlink()
            deleted += 1

    for bid in sorted(keep_ids, key=lambda b: pairs_by_id[b].created_at):
        pair = pairs_by_id[bid]
        print(
            f"  KEEP backup_id={bid} created={pair.created_at.strftime('%Y-%m-%dT%H:%M:%SZ')}"
        )

    if apply:
        print(f"prune: deleted {deleted} backup pair(s)")
    else:
        print(f"prune: dry-run complete; pass --apply to delete {len(delete_ids)} backup pair(s)")


if __name__ == "__main__":
    main()
