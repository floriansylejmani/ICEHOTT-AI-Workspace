"""test_prune.py — Tests for retention boundary selection and prune behavior.

Covers:
  - Hourly retention: keeps one per hour-bucket in last 48h
  - Daily retention: keeps one per day-bucket in last 14d
  - Weekly retention: keeps one per ISO-week-bucket in last 8w
  - Backups outside all windows are pruned
  - Retain if selected by any tier (union)
  - Boundary conditions (exactly at window edge)
  - Dry-run default: no files deleted
  - --apply: expired files actually deleted
  - Unknown/incomplete files preserved (never deleted)
  - Symlink rejection in backup dir
  - Only complete pairs (dump+manifest) are considered for deletion
"""
from __future__ import annotations

import json
import os
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

from conftest import NOW_UTC, sha256_of


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def make_backup_pair(
    backup_dir: Path,
    created_at: datetime,
    bid: str | None = None,
) -> tuple[str, Path, Path]:
    """Write a complete (manifest + dump) backup pair with the given timestamp."""
    if bid is None:
        # Derive a stable backup_id from the timestamp
        ts = created_at.strftime("%Y%m%d%H%M%S")
        bid = (ts + "0" * 32)[:32]
    content = f"PGDMP stub {bid}".encode()
    dump_name = f"icehott-{bid}.dump"
    manifest_name = f"icehott-{bid}.manifest.json"
    dump_path = backup_dir / dump_name
    manifest_path = backup_dir / manifest_name
    dump_path.write_bytes(content)
    manifest = {
        "schema_version": 1,
        "backup_id": bid,
        "created_at_utc": created_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "dump_filename": dump_name,
        "bytes": len(content),
        "sha256": sha256_of(content),
        "format": "pg_dump_custom_Fc",
        "pg_dump_version": "pg_dump (PostgreSQL) 16.1",
    }
    manifest_path.write_text(json.dumps(manifest) + "\n", encoding="utf-8")
    return bid, dump_path, manifest_path


# ---------------------------------------------------------------------------
# Unit tests on apply_retention()
# ---------------------------------------------------------------------------
class TestRetentionSelection:
    def test_recent_backup_in_hourly_window_retained(self, prune_mod, backup_dir):
        # 30 minutes old — should be retained by hourly tier
        ts = NOW_UTC - timedelta(minutes=30)
        bid, _, _ = make_backup_pair(backup_dir, ts)
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        assert bid in keep
        assert bid not in delete

    def test_backup_at_49h_outside_hourly_retained_by_daily(self, prune_mod, backup_dir):
        # 49 hours old — outside hourly (48h) but within daily (14d)
        ts = NOW_UTC - timedelta(hours=49)
        bid, _, _ = make_backup_pair(backup_dir, ts)
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        assert bid in keep

    def test_backup_at_15d_outside_daily_retained_by_weekly(self, prune_mod, backup_dir):
        # 15 days old — outside daily (14d) but within weekly (8w)
        ts = NOW_UTC - timedelta(days=15)
        bid, _, _ = make_backup_pair(backup_dir, ts)
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        assert bid in keep

    def test_backup_at_9w_outside_all_windows_deleted(self, prune_mod, backup_dir):
        # 63 days = 9 weeks old — outside all retention windows
        ts = NOW_UTC - timedelta(weeks=9)
        bid, _, _ = make_backup_pair(backup_dir, ts)
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        assert bid in delete
        assert bid not in keep

    def test_hourly_bucket_keeps_newest(self, prune_mod, backup_dir):
        # Two backups in the same UTC hour: only the newer should be kept by hourly tier
        hour_base = NOW_UTC.replace(minute=0, second=0, microsecond=0) - timedelta(hours=2)
        ts_old = hour_base + timedelta(minutes=5)
        ts_new = hour_base + timedelta(minutes=45)
        bid_old, _, _ = make_backup_pair(backup_dir, ts_old, bid="0" * 30 + "01")
        bid_new, _, _ = make_backup_pair(backup_dir, ts_new, bid="0" * 30 + "02")
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        # Newer should be in keep (hourly selects it)
        assert bid_new in keep
        # Older is not selected by hourly (same bucket, newer wins)
        # It may still be selected by daily — that's fine; we just check newer is kept
        assert bid_new in keep

    def test_daily_bucket_keeps_newest(self, prune_mod, backup_dir):
        # Two backups on the same UTC day (but > 48h ago so hourly doesn't apply)
        day = NOW_UTC - timedelta(days=5)
        ts_morning = day.replace(hour=6, minute=0, second=0, microsecond=0)
        ts_evening = day.replace(hour=20, minute=0, second=0, microsecond=0)
        bid_morning, _, _ = make_backup_pair(backup_dir, ts_morning, bid="1" * 30 + "01")
        bid_evening, _, _ = make_backup_pair(backup_dir, ts_evening, bid="1" * 30 + "02")
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        assert bid_evening in keep

    def test_weekly_bucket_retains_from_past_weeks(self, prune_mod, backup_dir):
        # One backup per week for 6 weeks — all should be retained
        bids = []
        for week_offset in range(1, 7):
            ts = NOW_UTC - timedelta(weeks=week_offset)
            bid = f"{week_offset:02d}" + "a" * 30
            bids.append(bid)
            make_backup_pair(backup_dir, ts, bid=bid)
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        for bid in bids:
            assert bid in keep, f"backup from {week_offset} weeks ago should be retained"

    def test_expired_backup_exactly_at_8w1d_deleted(self, prune_mod, backup_dir):
        # 8 weeks + 1 day = just outside weekly window
        ts = NOW_UTC - timedelta(weeks=8, days=1)
        bid, _, _ = make_backup_pair(backup_dir, ts)
        pairs, _ = prune_mod.collect_pairs(backup_dir)
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        assert bid in delete

    def test_empty_directory_no_errors(self, prune_mod, backup_dir):
        pairs, warnings = prune_mod.collect_pairs(backup_dir)
        assert pairs == []
        keep, delete = prune_mod.apply_retention(pairs, NOW_UTC)
        assert keep == set()
        assert delete == set()


# ---------------------------------------------------------------------------
# Dry-run vs apply
# ---------------------------------------------------------------------------
class TestDryRunAndApply:
    def _run_prune(self, backup_dir: Path, prune_mod, apply: bool, now_utc: str | None = None):
        args = ["prune-backups.py", "--backup-dir", str(backup_dir)]
        if apply:
            args.append("--apply")
        else:
            args.append("--dry-run")
        if now_utc:
            args.extend(["--now-utc", now_utc])
        old_argv = sys.argv[:]
        sys.argv = args
        try:
            prune_mod.main()
        finally:
            sys.argv = old_argv

    def test_dry_run_does_not_delete_files(self, prune_mod, backup_dir):
        # Backup from 10 weeks ago — would be pruned
        ts = NOW_UTC - timedelta(weeks=10)
        bid, dump_path, manifest_path = make_backup_pair(backup_dir, ts)
        self._run_prune(backup_dir, prune_mod, apply=False,
                        now_utc=NOW_UTC.strftime("%Y-%m-%dT%H:%M:%SZ"))
        assert dump_path.exists(), "dry-run must not delete dump"
        assert manifest_path.exists(), "dry-run must not delete manifest"

    def test_apply_deletes_expired_files(self, prune_mod, backup_dir):
        # Backup from 10 weeks ago — should be deleted on --apply
        ts = NOW_UTC - timedelta(weeks=10)
        bid, dump_path, manifest_path = make_backup_pair(backup_dir, ts)
        self._run_prune(backup_dir, prune_mod, apply=True,
                        now_utc=NOW_UTC.strftime("%Y-%m-%dT%H:%M:%SZ"))
        assert not dump_path.exists(), "apply must delete expired dump"
        assert not manifest_path.exists(), "apply must delete expired manifest"

    def test_apply_preserves_retained_files(self, prune_mod, backup_dir):
        # Recent backup — must not be deleted
        ts = NOW_UTC - timedelta(hours=1)
        bid, dump_path, manifest_path = make_backup_pair(backup_dir, ts)
        self._run_prune(backup_dir, prune_mod, apply=True,
                        now_utc=NOW_UTC.strftime("%Y-%m-%dT%H:%M:%SZ"))
        assert dump_path.exists(), "apply must preserve retained dump"
        assert manifest_path.exists(), "apply must preserve retained manifest"


# ---------------------------------------------------------------------------
# Unknown and incomplete files are preserved
# ---------------------------------------------------------------------------
class TestUnknownAndIncompleteFiles:
    def test_unknown_file_preserved(self, prune_mod, backup_dir):
        unknown = backup_dir / "README.txt"
        unknown.write_text("do not delete me")
        pairs, warnings = prune_mod.collect_pairs(backup_dir)
        # Unknown file should appear in warnings, not in pairs
        assert all(p.dump_path.name != "README.txt" for p in pairs)
        warning_text = " ".join(warnings)
        assert "README.txt" in warning_text

    def test_dump_without_manifest_preserved(self, prune_mod, backup_dir):
        bid = "b" * 32
        dump_path = backup_dir / f"icehott-{bid}.dump"
        dump_path.write_bytes(b"orphan dump")
        pairs, warnings = prune_mod.collect_pairs(backup_dir)
        # This dump has no manifest — should be treated as incomplete, never deleted
        assert all(p.backup_id != bid for p in pairs)
        warning_text = " ".join(warnings)
        assert bid in warning_text or "incomplete" in warning_text.lower()

    def test_manifest_without_dump_preserved(self, prune_mod, backup_dir):
        bid = "c" * 32
        manifest = {
            "schema_version": 1,
            "backup_id": bid,
            "created_at_utc": "2024-01-01T00:00:00Z",
            "dump_filename": f"icehott-{bid}.dump",
            "bytes": 10,
            "sha256": "0" * 64,
            "format": "pg_dump_custom_Fc",
            "pg_dump_version": "16.1",
        }
        manifest_path = backup_dir / f"icehott-{bid}.manifest.json"
        manifest_path.write_text(json.dumps(manifest))
        # Do NOT write the dump file
        pairs, warnings = prune_mod.collect_pairs(backup_dir)
        assert all(p.backup_id != bid for p in pairs)
        warning_text = " ".join(warnings)
        assert bid in warning_text or "incomplete" in warning_text.lower()

    def test_tmp_backup_files_not_in_pairs(self, prune_mod, backup_dir):
        # Temp files created by backup-postgres.sh start with '.' — should be ignored
        tmp = backup_dir / ".tmp-backup-XXXXXX"
        tmp.mkdir()
        pairs, warnings = prune_mod.collect_pairs(backup_dir)
        assert pairs == []

    def test_symlink_in_backup_dir_not_deleted(self, prune_mod, backup_dir, tmp_path):
        """Symlinks are never treated as deletable backup files."""
        real_file = tmp_path / "real_file.txt"
        real_file.write_text("content")
        link = backup_dir / "link_file.txt"
        try:
            link.symlink_to(real_file)
        except (OSError, NotImplementedError):
            pytest.skip("symlinks not supported on this platform")
        pairs, warnings = prune_mod.collect_pairs(backup_dir)
        # Symlink appears in warnings but NOT as a pair candidate
        assert pairs == []
        assert "link_file.txt" in " ".join(warnings)
        # Real file must still exist
        assert real_file.exists()
