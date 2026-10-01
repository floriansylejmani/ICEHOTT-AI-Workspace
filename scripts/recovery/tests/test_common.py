from __future__ import annotations

import json
from pathlib import Path

import pytest

from recovery_common import (
    RecoveryValidationError,
    parse_postgres_url,
    safe_directory_path,
    safe_existing_file,
    safe_local_output_path,
)


def test_parse_postgres_url_decodes_credentials_and_ipv6() -> None:
    parsed = parse_postgres_url(
        "postgresql://user%2Bname:p%40ss%2Fword@[::1]:55432/icehott_drill?sslmode=require"
    )
    assert parsed == {
        "host": "::1",
        "port": "55432",
        "database": "icehott_drill",
        "user": "user+name",
        "password": "p@ss/word",
        "sslmode": "require",
    }


@pytest.mark.parametrize(
    "value",
    [
        "mysql://u:p@host/db",
        "postgresql://u@host/db",
        "postgresql://u:p@host/",
        "postgresql://u:p@host/db/extra",
        "postgresql://u:p@host/db?sslmode=invalid",
        "postgresql://u:p@host/db\nsecret",
    ],
)
def test_parse_postgres_url_rejects_unsafe_or_incomplete_values(value: str) -> None:
    with pytest.raises(RecoveryValidationError):
        parse_postgres_url(value)


def test_safe_directory_rejects_parent_traversal(tmp_path: Path) -> None:
    with pytest.raises(RecoveryValidationError):
        safe_directory_path(tmp_path / ".." / "escape", create=True)


def test_safe_existing_file_rejects_parent_traversal(tmp_path: Path) -> None:
    target = tmp_path / "file.txt"
    target.write_text("ok", encoding="utf-8")
    with pytest.raises(RecoveryValidationError):
        safe_existing_file(tmp_path / ".." / tmp_path.name / "file.txt")


def test_safe_output_is_confined_to_requested_parent(tmp_path: Path) -> None:
    target = safe_local_output_path(str(tmp_path / "evidence.json"))
    assert target.parent == tmp_path.resolve()
    target.write_text(json.dumps({"pass": True}), encoding="utf-8")
    assert safe_existing_file(target) == target.resolve()


def test_symlink_component_is_rejected_when_supported(tmp_path: Path) -> None:
    real = tmp_path / "real"
    real.mkdir()
    link = tmp_path / "link"
    try:
        link.symlink_to(real, target_is_directory=True)
    except (OSError, NotImplementedError):
        pytest.skip("directory symlinks are unavailable on this platform")

    with pytest.raises(RecoveryValidationError):
        safe_directory_path(link, create=False)
    with pytest.raises(RecoveryValidationError):
        safe_local_output_path(str(link / "out.json"))
