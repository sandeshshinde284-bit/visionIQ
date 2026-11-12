"""LRN-13 operator safety contracts that need no database or cloud connection."""

from types import SimpleNamespace

import pytest

from app.knowledge import lrn13_manual_cli


def test_requires_valid_unique_sha256_item_keys() -> None:
    good = "a" * 64
    assert lrn13_manual_cli._validate_keys([good]) == [good]
    for invalid in ([], [good, good], ["A" * 64], ["not-a-hash"], [good] * 21):
        with pytest.raises(ValueError):
            lrn13_manual_cli._validate_keys(invalid)


def test_failed_snapshot_must_not_run_lesson_consolidation(monkeypatch) -> None:
    called = []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, *_args):
            if "MAX(event_id)" in sql:
                return SimpleNamespace(fetchone=lambda: (0,))
            raise AssertionError("Unexpected SQL")

    monkeypatch.setattr(lrn13_manual_cli.psycopg, "connect", lambda _dsn: FakeConnection())
    monkeypatch.setattr(
        lrn13_manual_cli,
        "_source_scopes",
        lambda _conn, _keys: {("org", "project")},
    )
    monkeypatch.setattr(lrn13_manual_cli, "_lesson_status", lambda _conn, _scope: {})
    monkeypatch.setattr(lrn13_manual_cli.snapshots, "current", lambda _conn: None)
    monkeypatch.setattr(
        lrn13_manual_cli.snapshots,
        "refresh",
        lambda *_a, **_kw: (
            SimpleNamespace(failed=["item"], indexed=0),
            SimpleNamespace(status="rejected"),
        ),
    )
    monkeypatch.setattr(
        lrn13_manual_cli.lessons_cli,
        "run",
        lambda _dsn: called.append("incorrect"),
    )

    with pytest.raises(RuntimeError, match="not published"):
        lrn13_manual_cli.run("fake", ["a" * 64], lambda _uri: b"{}", object())
    assert called == []


def test_missing_source_items_fail_before_indexing(monkeypatch) -> None:
    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, _sql, *_params):
            return SimpleNamespace(fetchall=lambda: [])

    monkeypatch.setattr(lrn13_manual_cli.psycopg, "connect", lambda _dsn: FakeConnection())
    monkeypatch.setattr(
        lrn13_manual_cli.snapshots,
        "refresh",
        lambda *_a, **_kw: pytest.fail("Missing source key must not attempt indexing"),
    )
    with pytest.raises(ValueError, match="does not exist"):
        lrn13_manual_cli.run("fake", ["f" * 64], lambda _uri: b"{}", object())
