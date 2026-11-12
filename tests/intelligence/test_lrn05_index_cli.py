"""LRN-05: changed canonical evidence can be targeted for downstream reindexing.

These are CLI contract tests, not a claim that the cloud scheduler is deployed.
"""

from types import SimpleNamespace

import pytest

from app.knowledge import index_cli


@pytest.fixture
def cli(monkeypatch):
    calls = []
    monkeypatch.setattr(index_cli, "configure_from_environment", lambda: None)
    monkeypatch.setattr(index_cli, "_conninfo", lambda: "test-only-db")
    monkeypatch.setattr(index_cli, "from_environment", lambda: SimpleNamespace(model_id="test:768"))

    def refresh(conninfo, read_raw, embedder, **kwargs):
        calls.append((conninfo, embedder.model_id, kwargs))
        return (
            SimpleNamespace(indexed=1, chunks=1, embedded=1, reused=0, failed=[]),
            SimpleNamespace(
                snapshot_id="snapshot-test",
                status="published",
                reason="",
                counts={"eligible": 1, "items": 1},
            ),
        )

    monkeypatch.setattr(index_cli.snapshots, "refresh", refresh)
    return calls


def test_default_refresh_uses_configured_batch_limit(monkeypatch, cli):
    monkeypatch.setenv("INDEX_BATCH_LIMIT", "37")
    assert index_cli.main([]) == 0
    assert cli == [
        (
            "test-only-db",
            "test:768",
            {"only": None, "limit": 37, "min_full_coverage": 0.95, "allow_partial": False},
        )
    ]


def test_explicit_targeted_delta_refresh(monkeypatch, cli):
    monkeypatch.setenv("INDEX_BATCH_LIMIT", "100")
    assert index_cli.main(["--only", "item-1", "--only", "item-2", "--limit", "2"]) == 0
    assert cli == [
        (
            "test-only-db",
            "test:768",
            {
                "only": ["item-1", "item-2"],
                "limit": 2,
                "min_full_coverage": 0.95,
                "allow_partial": False,
            },
        )
    ]


def test_invalid_batch_limit_does_not_start_indexing(cli):
    with pytest.raises(SystemExit) as error:
        index_cli.main(["--limit", "0"])
    assert error.value.code == 2
    assert not cli


def test_allow_partial_runs_an_incremental_backfill_pass(monkeypatch, cli):
    assert index_cli.main(["--allow-partial"]) == 0
    assert cli == [
        (
            "test-only-db",
            "test:768",
            {"only": None, "limit": 500, "min_full_coverage": 0.95, "allow_partial": True},
        )
    ]


def test_allow_partial_is_rejected_together_with_only(cli):
    with pytest.raises(SystemExit) as error:
        index_cli.main(["--only", "item-1", "--allow-partial"])
    assert error.value.code == 2
    assert not cli
