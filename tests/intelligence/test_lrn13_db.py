"""LRN-13 on PostgreSQL: targeted re-index, material-only lesson versions, change events, and
the fix-then-revert replay (completion evidence: the lesson moves from supported to disputed)."""

import json
import os
from pathlib import Path

import pytest

psycopg = pytest.importorskip("psycopg")

from corpus_support import Corpus, conninfo  # noqa: E402

from app.knowledge import indexer, lesson_history, lessons_cli, snapshots  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
FILES = ["/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs"]


class CountingEmbedder(HashingEmbedder):
    def __init__(self) -> None:
        self.texts: list[str] = []

    def embed_documents(self, texts):
        self.texts.extend(texts)
        return super().embed_documents(texts)


def pull_request(comment: str) -> dict:
    return {
        "id": 245,
        "title": "Retry printing after captured payment",
        "description": "Adds an idempotency key so retries do not double charge.",
        "reviewThreads": [
            {"filePath": "/src/Retry.cs", "comments": [{"content": comment}]},
            {"filePath": "/src/Printer.cs", "comments": [{"content": "Printer timeout is fine."}]},
        ],
    }


def events(corpus_keys: list[str]) -> list[tuple]:
    with psycopg.connect(conninfo("API")) as conn:
        return conn.execute(
            "SELECT event_type, subject, payload FROM knowledge.change_events "
            "WHERE subject = ANY(%s) ORDER BY event_id",
            (corpus_keys,),
        ).fetchall()


def indexed_at(item: str):
    with psycopg.connect(conninfo("API")) as conn:
        return conn.execute(
            "SELECT max(indexed_at) FROM knowledge.index_entries WHERE item_key = %s", (item,)
        ).fetchone()


def test_a_pr_event_re_indexes_only_that_pr_and_only_its_changed_chunks() -> None:
    corpus = Corpus()
    bug = corpus.add(
        "work_item_current", {"id": 101, "workItemType": "Bug", "title": "Barcode fails"}
    )
    pr = corpus.add("pull_request", pull_request("Retry-only patches were reverted."))
    snapshots.refresh(conninfo("INTELLIGENCE"), corpus.read, HashingEmbedder())
    bug_before = indexed_at(bug)

    corpus.add("pull_request", pull_request("Use an idempotency key, not a retry."), revision="2")
    counting = CountingEmbedder()
    report = indexer.run(conninfo("INTELLIGENCE"), corpus.read, counting, only=[pr])

    assert report.indexed == 1  # only the PR the event named
    assert report.embedded == 1 and report.reused == 2  # only the touched chunk re-embedded
    assert counting.texts == [t for t in counting.texts if "idempotency key, not a retry" in t]
    assert indexed_at(bug) == bug_before  # other items untouched
    updates = [e for e in events([pr]) if e[0] == "index.item_updated"]
    assert updates[-1][2]["revision"] == "2" and updates[-1][2]["re_embedded"] == 1


def test_a_removed_item_emits_an_event() -> None:
    corpus = Corpus()
    bug = corpus.add("work_item_current", {"id": 7, "workItemType": "Bug", "title": "Gone soon"})
    snapshots.refresh(conninfo("INTELLIGENCE"), corpus.read, HashingEmbedder())
    corpus.add(
        "work_item_current",
        {"id": 7, "workItemType": "Bug", "title": "x"},
        revision="2",
        deleted=True,
    )

    snapshots.refresh(conninfo("INTELLIGENCE"), corpus.read, HashingEmbedder())

    assert ("index.item_removed", bug) in [(e[0], e[1]) for e in events([bug])]


def test_replay_a_fix_then_revert_the_lesson_moves_from_supported_to_disputed(tmp_path) -> None:
    corpus = Corpus()
    for number in ("101", "102"):
        corpus.add(
            "work_item_current",
            {"id": int(number), "workItemType": "Bug", "title": f"Barcode print fails after payment retry ({number})"},
        )
    timeline = []

    corpus.fix("101", "201", FILES)
    lessons_cli.run(conninfo("INTELLIGENCE"))
    timeline.append({"step": "ticket 101 fixed by PR 201", "lesson": None})

    corpus.fix("102", "202", FILES)
    lessons_cli.run(conninfo("INTELLIGENCE"))
    with psycopg.connect(conninfo("API")) as conn:
        (pattern,) = conn.execute(
            "SELECT pattern_id::text FROM knowledge.lesson_patterns WHERE source_instance = %s",
            (corpus.org,),
        ).fetchone()
    timeline.append({"step": "ticket 102 fixed by PR 202 (same files)", "lesson": "candidate"})

    lessons_cli.run(conninfo("INTELLIGENCE"))  # a PR update with no material change
    timeline.append({"step": "PR 202 updated, no material change", "lesson": "candidate"})

    with psycopg.connect(conninfo("API")) as conn:
        conn.execute(
            "UPDATE evidence.ticket_fix_links SET revert_commit_shas = '[\"deadbeef\"]' "
            "WHERE source_instance = %s AND pull_request_id = '202'",
            (corpus.org,),
        )
    lessons_cli.run(conninfo("INTELLIGENCE"))
    timeline.append({"step": "PR 202 reverted by commit deadbeef", "lesson": "disputed"})

    with psycopg.connect(conninfo("API")) as conn:
        history = lesson_history.timeline(conn, pattern)
    statuses = [(r["revision"], r["status"], r["decision"]) for r in history["revisions"]]
    out = Path(os.environ.get("VISIONIQ_BENCHMARK_DIR", tmp_path)) / "lesson-replay.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(
            {"pattern_id": pattern, "timeline": timeline, "history": history}, indent=2, default=str
        )
    )

    assert statuses == [(1, "candidate", "CREATE_NEW"), (2, "disputed", "REVISE_EXISTING")]
    assert all(r["cause"] == "unverified" for r in history["revisions"])
    assert [e[0] for e in events([pattern])] == ["lesson.created", "lesson.revised"]
    assert "deadbeef" in history["revisions"][1]["rationale"]


def test_lesson_history_lists_and_prints_a_timeline(capsys, monkeypatch) -> None:
    corpus = Corpus()
    for number in ("1", "2"):
        corpus.add("work_item_current", {"id": int(number), "workItemType": "Bug", "title": "Barcode print fails after payment retry"})
        corpus.fix(number, f"9{number}", FILES)
    lessons_cli.run(conninfo("INTELLIGENCE"))
    with psycopg.connect(conninfo("API")) as conn:
        (pattern,) = conn.execute(
            "SELECT pattern_id::text FROM knowledge.lesson_patterns WHERE source_instance = %s",
            (corpus.org,),
        ).fetchone()
    monkeypatch.setenv("DB_HOST", os.environ.get("DB_HOST", "127.0.0.1"))

    assert lesson_history.main([pattern]) == 0
    printed = capsys.readouterr().out
    assert "rev 1  candidate" in printed and "CREATE_NEW" in printed
    assert lesson_history.main([]) == 0 and pattern in capsys.readouterr().out
