"""LRN-13 controlled manual fix/revert replay; disposable PostgreSQL only."""

import os

import psycopg
import pytest
from corpus_support import Corpus, conninfo

from app.knowledge import lrn13_manual_cli, snapshots
from app.knowledge.embeddings import HashingEmbedder

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Only run under guarded isolated-pytest disposable DB setup",
)

FILES = ["/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs"]


def test_manual_refresh_replays_fix_revert_preserves_history_and_is_idempotent() -> None:
    corpus = Corpus()
    for bug_id in ("101", "102"):
        corpus.add("work_item_current", {
            "id": int(bug_id),
            "workItemType": "Bug",
            "title": "Barcode print fails after payment retry",
            "reproSteps": "Payment captured; barcode print fails when retrying.",
        })
    pr = corpus.add(
        "pull_request",
        {"id": 201, "title": "RetryPolicy patch", "description": "Fix barcode retry"},
    )
    corpus.fix("101", "201", FILES)
    corpus.fix("102", "202", FILES)

    embedder = HashingEmbedder()
    index_report, baseline = snapshots.refresh(
        conninfo("INTELLIGENCE"), corpus.read, embedder,
    )
    assert not index_report.failed and baseline.status == "published"

    first = lrn13_manual_cli.run(
        conninfo("INTELLIGENCE"), [pr], corpus.read, embedder,
    )
    assert first["snapshot_status"] == "published"
    assert len(first["changed_lessons"]) == 1
    initial = first["changed_lessons"][0]
    assert initial["after"]["status"] == "candidate"
    assert initial["after"]["cause"] == "unverified"
    assert initial["after"]["revision"] == 1

    no_change = lrn13_manual_cli.run(
        conninfo("INTELLIGENCE"), [pr], corpus.read, embedder,
    )
    assert no_change["changed_lessons"] == []
    assert no_change["lesson_run"]["new_revisions"] == 0

    # Synthetic proxy for a later, independently ingested real revert.
    # The production replay must use source evidence, not manually edit SQL.
    with psycopg.connect(conninfo("API")) as conn:
        conn.execute(
            "UPDATE evidence.ticket_fix_links SET revert_commit_shas = %s "
            "WHERE source_instance = %s AND pull_request_id = '201'",
            ('["0123456789abcdef0123456789abcdef01234567"]', corpus.org),
        )
    corpus.add(
        "pull_request",
        {"id": 201, "title": "RetryPolicy patch (reverted)",
         "description": "Fix reverted after regressions"},
        revision="2",
    )
    disputed = lrn13_manual_cli.run(
        conninfo("INTELLIGENCE"), [pr], corpus.read, embedder,
    )
    assert disputed["snapshot_status"] == "published"
    assert len(disputed["changed_lessons"]) == 1
    changed = disputed["changed_lessons"][0]
    assert changed["before"]["status"] == "candidate"
    assert changed["after"]["status"] == "disputed"
    assert changed["after"]["revision"] == 2
    assert [(r["revision"], r["status"]) for r in changed["history"]] == [
        (1, "candidate"), (2, "disputed"),
    ]
    assert all(r["cause"] == "unverified" for r in changed["history"])
    assert any(e["type"] == "lesson.revised" for e in disputed["change_events"])

    repeated = lrn13_manual_cli.run(
        conninfo("INTELLIGENCE"), [pr], corpus.read, embedder,
    )
    assert repeated["changed_lessons"] == []
    assert repeated["lesson_run"]["new_revisions"] == 0
