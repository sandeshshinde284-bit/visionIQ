"""Regression coverage for noncanonical archives and failed targeted LRN-08 batches.

These tests are intentionally DB/GCS/Vertex independent. They prove that a
fixture-backed archive cannot enter the production indexer and that a failed
targeted batch does not move the published snapshot pointer.
"""

import pytest

from app.knowledge import indexer, snapshots


def _row(uri: str):
    return (
        "item-key", "azure_devops", "work_item_current", "Payments",
        None, "revision-1", "sha", uri, ["ado-project:Payments"],
        None, None,
    )


@pytest.mark.parametrize("uri", ["mem://fixture/payments/123", "file:///tmp/fixture.json", "", None])
def test_noncanonical_archive_rejected_before_read_or_embedding(uri):
    calls = []

    def read_raw(location):
        calls.append(location)
        raise AssertionError("Must not attempt to read noncanonical evidence")

    with pytest.raises(indexer.UnsupportedArchiveError, match="durable gs://"):
        indexer._index_item(None, _row(uri), read_raw, None)

    assert calls == []


def test_all_failed_targeted_batch_does_not_publish(monkeypatch):
    failed = ["payments-key-1", "payments-key-2"]
    monkeypatch.setattr(
        snapshots.indexer, "run",
        lambda *args, **kwargs: indexer.IndexReport(failed=failed),
    )

    def unexpected_publish(*args, **kwargs):
        raise AssertionError("Must not publish a snapshot when every target fails")

    monkeypatch.setattr(snapshots, "build_and_publish", unexpected_publish)
    monkeypatch.setattr(snapshots, "cleanup", unexpected_publish)

    report, result = snapshots.refresh(
        "unused", lambda uri: b"{}", object(), only=failed
    )

    assert report.failed == failed
    assert report.indexed == 0
    assert result.status == "rejected"
    assert result.checks["indexing"] is False
    assert result.counts["failed"] == 2
    assert "All 2" in result.reason


def test_full_refresh_still_publishes_for_revocation_even_when_indexing_fails(monkeypatch):
    class Embedder:
        model_id = "test-model"

    failure = indexer.IndexReport(failed=["unreadable-item"])
    monkeypatch.setattr(snapshots.indexer, "run", lambda *args, **kwargs: failure)
    published = snapshots.SnapshotResult(
        "revocation-snapshot", "published", "prior-snapshot"
    )
    observed = []

    def publish(*args, **kwargs):
        observed.append("publish")
        return published

    monkeypatch.setattr(snapshots, "build_and_publish", publish)
    monkeypatch.setattr(snapshots, "cleanup", lambda *args: observed.append("cleanup"))

    report, result = snapshots.refresh("unused", lambda uri: b"{}", Embedder())

    assert report.failed == ["unreadable-item"]
    assert result is published
    assert observed == ["publish", "cleanup"]


def test_partially_successful_targeted_batch_uses_existing_policy(monkeypatch):
    class Embedder:
        model_id = "test-model"

    report = indexer.IndexReport(indexed=1, failed=["invalid-item"])
    monkeypatch.setattr(snapshots.indexer, "run", lambda *args, **kwargs: report)
    published = snapshots.SnapshotResult("partial-snapshot", "published", "prior")
    monkeypatch.setattr(snapshots, "build_and_publish", lambda *args, **kwargs: published)
    monkeypatch.setattr(snapshots, "cleanup", lambda *args: None)

    returned, result = snapshots.refresh(
        "unused", lambda uri: b"{}", Embedder(), only=["valid-item", "invalid-item"]
    )

    assert returned is report
    assert result is published
