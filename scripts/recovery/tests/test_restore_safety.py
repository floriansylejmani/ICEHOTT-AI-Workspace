"""test_restore_safety.py — Tests for restore safety helpers.

Covers:
  - Unsafe (non-isolated) target database names are rejected without override
  - Isolated target names (containing restore/drill/test) are accepted
  - Missing ICEHOTT_RESTORE_CONFIRM or wrong value rejects restore
  - Correct confirmation value RESTORE is accepted
  - Missing ICEHOTT_RESTORE_DATABASE_URL is detected

These tests exercise the Python helper logic extracted from restore-postgres.sh
as pure functions, and test subprocess invocation for the shell-level guards.
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

import pytest


# ---------------------------------------------------------------------------
# Pure-Python safety helpers (mirroring the shell logic for testability)
# ---------------------------------------------------------------------------
ISOLATION_KEYWORDS = ("restore", "drill", "test")
ISOLATION_RE = re.compile("|".join(ISOLATION_KEYWORDS), re.IGNORECASE)


def is_isolated_target(dbname: str) -> bool:
    """Return True if the database name contains an isolation keyword."""
    return bool(ISOLATION_RE.search(dbname))


def is_confirmed(confirm_value: str) -> bool:
    """Return True if the confirmation value is the literal string RESTORE."""
    return confirm_value == "RESTORE"


def extract_dbname_from_url(url: str) -> str:
    """Extract the database name from a libpq DSN (for safety checks only)."""
    m = re.match(r"^postgres(?:ql)?://[^@]+@[^/]+/([^?#]+)", url)
    if m:
        return m.group(1)
    m2 = re.search(r"(?:^|\s)dbname=([^ ]+)", url)
    if m2:
        return m2.group(1)
    return ""


# ---------------------------------------------------------------------------
# Target database name isolation checks
# ---------------------------------------------------------------------------
class TestTargetDatabaseNameIsolation:
    @pytest.mark.parametrize("dbname", [
        "icehott_restore",
        "restore_2024",
        "drill_20240615",
        "testdb",
        "icehott_test",
        "RESTORE_db",
        "icehott_DRILL",
    ])
    def test_isolated_names_accepted(self, dbname):
        assert is_isolated_target(dbname), f"Expected {dbname!r} to be accepted"

    @pytest.mark.parametrize("dbname", [
        "icehott_production",
        "icehott_prod",
        "icehott_live",
        "icehott",
        "production",
        "app_db",
        "icehott_staging",
        "main",
    ])
    def test_non_isolated_names_rejected(self, dbname):
        assert not is_isolated_target(dbname), f"Expected {dbname!r} to be rejected"


# ---------------------------------------------------------------------------
# Confirmation logic
# ---------------------------------------------------------------------------
class TestConfirmationLogic:
    def test_correct_confirmation_passes(self):
        assert is_confirmed("RESTORE") is True

    def test_empty_confirmation_fails(self):
        assert is_confirmed("") is False

    def test_lowercase_restore_fails(self):
        assert is_confirmed("restore") is False

    def test_wrong_word_fails(self):
        assert is_confirmed("YES") is False

    def test_partial_word_fails(self):
        assert is_confirmed("RESTOR") is False

    def test_with_whitespace_fails(self):
        assert is_confirmed(" RESTORE") is False
        assert is_confirmed("RESTORE ") is False


# ---------------------------------------------------------------------------
# URL parsing for database name extraction
# ---------------------------------------------------------------------------
class TestUrlDatabaseNameExtraction:
    @pytest.mark.parametrize("url,expected", [
        ("postgres://user:pass@host:5432/icehott_restore", "icehott_restore"),
        ("postgresql://user:pass@host/testdb", "testdb"),
        ("postgres://user:pass@host/icehott_drill?sslmode=require", "icehott_drill"),
        ("postgresql://user:pass@host:5432/app_prod", "app_prod"),
    ])
    def test_url_dbname_extraction(self, url, expected):
        result = extract_dbname_from_url(url)
        assert result == expected, f"URL={url!r}: expected {expected!r}, got {result!r}"

    def test_url_with_no_path_returns_empty(self):
        url = "not-a-valid-url"
        result = extract_dbname_from_url(url)
        assert result == ""


# ---------------------------------------------------------------------------
# Shell-level guard: restore-postgres.sh environment variable checks
# ---------------------------------------------------------------------------
RESTORE_SCRIPT = Path(__file__).parent.parent / "restore-postgres.sh"


@pytest.mark.skipif(
    os.name == "nt" or not RESTORE_SCRIPT.exists(),
    reason="shell guard integration runs on Linux CI; Windows bash does not translate native paths reliably",
)
class TestRestoreShellGuards:
    def _run_restore(self, env_overrides: dict, extra_args: list[str] | None = None) -> subprocess.CompletedProcess:
        env = dict(os.environ)
        for key in (
            "ICEHOTT_RESTORE_DATABASE_URL",
            "ICEHOTT_RESTORE_CONFIRM",
            "ICEHOTT_ALLOW_DANGEROUS_RESTORE",
        ):
            env.pop(key, None)
        env.update(env_overrides)
        cmd = ["bash", str(RESTORE_SCRIPT)] + (extra_args or [])
        return subprocess.run(cmd, capture_output=True, text=True, env=env)

    def test_missing_manifest_arg_exits_nonzero(self):
        result = self._run_restore({
            "ICEHOTT_RESTORE_DATABASE_URL": "postgres://u:p@h/restore_test",
            "ICEHOTT_RESTORE_CONFIRM": "RESTORE",
        })
        assert result.returncode != 0

    def test_missing_restore_database_url_exits_nonzero(self, tmp_path):
        # Provide a dummy manifest just to get past arg parsing
        m = tmp_path / "test.manifest.json"
        m.write_text('{"schema_version":1}')
        result = self._run_restore(
            {"ICEHOTT_RESTORE_CONFIRM": "RESTORE"},
            extra_args=["--manifest", str(m)],
        )
        assert result.returncode != 0
        assert "ICEHOTT_RESTORE_DATABASE_URL" in result.stderr

    def test_missing_restore_confirm_exits_nonzero(self, tmp_path):
        m = tmp_path / "test.manifest.json"
        m.write_text('{"schema_version":1}')
        result = self._run_restore(
            {"ICEHOTT_RESTORE_DATABASE_URL": "postgres://u:p@h/restore_test"},
            extra_args=["--manifest", str(m)],
        )
        assert result.returncode != 0
        assert "ICEHOTT_RESTORE_CONFIRM" in result.stderr

    def test_wrong_confirm_value_exits_nonzero(self, tmp_path):
        m = tmp_path / "test.manifest.json"
        m.write_text('{"schema_version":1}')
        result = self._run_restore(
            {
                "ICEHOTT_RESTORE_DATABASE_URL": "postgres://u:p@h/restore_test",
                "ICEHOTT_RESTORE_CONFIRM": "yes",
            },
            extra_args=["--manifest", str(m)],
        )
        assert result.returncode != 0

    def test_non_isolated_db_name_rejected_without_override(self, tmp_path):
        m = tmp_path / "some.manifest.json"
        m.write_text('{"schema_version":1,"backup_id":"' + "a" * 32 + '","dump_filename":"icehott-' + "a" * 32 + '.dump","created_at_utc":"2024-01-15T12:00:00Z","bytes":5,"sha256":"' + "0" * 64 + '","format":"pg_dump_custom_Fc","pg_dump_version":"16.1"}')
        result = self._run_restore(
            {
                "ICEHOTT_RESTORE_DATABASE_URL": "postgres://u:p@h/icehott_production",
                "ICEHOTT_RESTORE_CONFIRM": "RESTORE",
            },
            extra_args=["--manifest", str(m)],
        )
        assert result.returncode != 0
        # Should mention isolation keyword requirement, not leak URL
        assert "isolation" in result.stderr.lower() or "restore" in result.stderr.lower() or "drill" in result.stderr.lower()
        # Must not echo the URL or password in stderr
        assert "p@h" not in result.stderr
        assert "u:p" not in result.stderr

    def test_url_not_echoed_in_output(self, tmp_path):
        """URL/password must not appear in any output (stdout or stderr)."""
        m = tmp_path / "some.manifest.json"
        m.write_text('{"schema_version":1}')
        secret_pass = "supersecretpassword123"
        result = self._run_restore(
            {
                "ICEHOTT_RESTORE_DATABASE_URL": f"postgres://user:{secret_pass}@host/somedb",
                "ICEHOTT_RESTORE_CONFIRM": "RESTORE",
            },
            extra_args=["--manifest", str(m)],
        )
        assert secret_pass not in result.stdout
        assert secret_pass not in result.stderr
