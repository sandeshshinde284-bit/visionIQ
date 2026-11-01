"""LRN-09 on PostgreSQL + pgvector: hybrid retrieval, double access checks, abstention, benchmark.

Completion evidence: ranked results for the live defect, and an abstention on a vague query.
"""

import json
import os
import statistics
import time
from pathlib import Path

import pytest

psycopg = pytest.importorskip("psycopg")

from corpus_support import Corpus, conninfo  # noqa: E402

from app.knowledge import indexer, lessons_cli  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402
from app.knowledge.retrieval import READABLE_NOW_SQL, retrieve  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
RETRY_FIX = ["/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs"]
LIVE_DEFECT = (
    "Kiosk payment captured, barcode print failed after retry.\n"
    "   at Shop.Payments.RetryPolicy.Execute(Int32 attempt) in /src/Payments/RetryPolicy.cs:line 42"
)


def bug(number, title, repro):
    return {"id": number, "workItemType": "Bug", "title": title, "reproSteps": repro}


def demo_history(corpus: Corpus) -> dict[str, str]:
    """The master design's worked example (10.10) as a small labelled history."""
    keys = {
        "printed": corpus.add(
            "work_item_current",
            bug(
                101,
                "Barcode print fails after payment retry",
                "Pay at kiosk, payment captured, retry printing: no barcode on the receipt.",
            ),
        ),
        "duplicate": corpus.add(
            "work_item_current",
            bug(
                102,
                "Duplicate receipt printed on retry",
                "Retrying a captured payment prints twice.",
            ),
        ),
        "css": corpus.add(
            "work_item_current",
            bug(103, "Login page CSS broken", "Login button overlaps the logo on small screens."),
        ),
        "network": corpus.add(
            "work_item_current",
            bug(  # same symptoms, different root cause
                104,
                "Payment captured twice when network drops",
                "Gateway timeout during payment; the retry submits again.",
            ),
        ),
    }
    corpus.fix("101", "201", RETRY_FIX)
    corpus.fix("102", "202", RETRY_FIX, reverted=True)
    corpus.fix("103", "203", ["/web/login.css"])
    corpus.fix("104", "204", ["/src/Network/Gateway.cs"])
    indexer.run(conninfo("INTELLIGENCE"), corpus.read, HashingEmbedder())
    lessons_cli.run(conninfo("INTELLIGENCE"))
    return keys


def ask(corpus: Corpus, query: str, **options):
    return retrieve(
        lambda: psycopg.connect(conninfo("INTELLIGENCE")),
        HashingEmbedder(),
        query,
        [corpus.scope],
        **options,
    )


def test_the_live_defect_gets_ranked_cited_results_with_counterevidence_and_a_lesson() -> None:
    corpus = Corpus()
    keys = demo_history(corpus)

    bundle = ask(corpus, LIVE_DEFECT)

    assert bundle.status == "answered"
    top = [r.item_key for r in bundle.results[:2]]
    assert set(top) == {keys["printed"], keys["duplicate"]}  # both code-path matches lead
    first = bundle.results[0]
    assert {"keyword", "code_path"} <= set(first.lanes)  # several lanes agree
    assert (
        first.citation["revision"] == "1" and first.citation["url"].endswith("/edit/101")
    ) or first.citation["url"].endswith("/edit/102")
    reverted = next(r for r in bundle.results if r.item_key == keys["duplicate"])
    assert "later reverted" in reverted.counterevidence[0]  # shown, not hidden
    assert all(r.item_key != keys["css"] for r in bundle.results[:3])
    (lesson,) = bundle.lessons
    assert lesson.status == "disputed" and "/src/Payments/RetryPolicy.cs" in lesson.signature_paths


def test_a_vague_query_abstains_instead_of_guessing() -> None:
    corpus = Corpus()
    demo_history(corpus)

    vague = ask(corpus, "it does not work")
    unrelated = ask(corpus, "quarterly marketing banner colour palette")

    assert vague.status == "insufficient_query" and vague.suggestions
    assert unrelated.status == "weak_evidence" and unrelated.reasons


def test_evidence_revoked_or_deleted_after_indexing_never_appears() -> None:
    corpus = Corpus()
    keys = demo_history(corpus)
    # Changed at the source AFTER indexing; the index still holds the old chunks.
    corpus.add(
        "work_item_current", bug(101, "x", "y"), revision="2", scope="ado-project:other/Secret"
    )
    corpus.add("work_item_current", bug(102, "x", "y"), revision="2", deleted=True)

    bundle = ask(corpus, LIVE_DEFECT)

    shown = {r.item_key for r in bundle.results}
    assert keys["printed"] not in shown and keys["duplicate"] not in shown


def test_the_live_check_hook_and_other_scopes_are_honoured() -> None:
    corpus = Corpus()
    keys = demo_history(corpus)

    denied = ask(corpus, LIVE_DEFECT, live_check=lambda key: key != keys["printed"])
    stranger = retrieve(
        lambda: psycopg.connect(conninfo("INTELLIGENCE")),
        HashingEmbedder(),
        LIVE_DEFECT,
        ["ado-project:https://other/Secret"],
    )

    assert keys["printed"] not in {r.item_key for r in denied.results}
    assert stranger.results == [] and stranger.lessons == []


def test_a_lesson_is_hidden_when_any_supporting_ticket_is_unreadable() -> None:
    corpus = Corpus()
    demo_history(corpus)
    corpus.add(
        "work_item_current", bug(102, "x", "y"), revision="2", scope="ado-project:other/Secret"
    )

    assert ask(corpus, LIVE_DEFECT).lessons == []


def test_benchmark_retrieval_quality_speed_and_access_leakage(tmp_path) -> None:
    """S07-13: labelled queries -> Recall@10, Precision@5, MRR, ACL leakage, p95 latency."""
    corpus = Corpus()
    keys = demo_history(corpus)
    secret = Corpus()  # another team's history must never leak into this caller's results
    demo_history(secret)
    labelled = [
        (LIVE_DEFECT, {keys["printed"], keys["duplicate"]}),
        ("Receipt printed twice after retrying a captured payment", {keys["duplicate"]}),
        ("Login button overlaps logo, CSS broken on small screens", {keys["css"]}),
        ("Gateway timeout, network drop, payment submitted again", {keys["network"]}),
    ]
    recalls, precisions, reciprocal, latencies, leaked, shown = [], [], [], [], 0, 0
    for _ in range(5):
        for query, relevant in labelled:
            started = time.perf_counter()
            bundle = ask(corpus, query)
            latencies.append((time.perf_counter() - started) * 1000)
            ranked = [r.item_key for r in bundle.results]
            recalls.append(len(relevant & set(ranked[:10])) / len(relevant))
            precisions.append(len(relevant & set(ranked[:5])) / min(5, max(1, len(ranked[:5]))))
            reciprocal.append(next((1 / i for i, k in enumerate(ranked, 1) if k in relevant), 0.0))
            with psycopg.connect(conninfo("INTELLIGENCE")) as conn:
                readable = {
                    row[0]
                    for row in conn.execute(
                        READABLE_NOW_SQL, {"keys": ranked, "scopes": [corpus.scope]}
                    )
                }
            leaked += len(set(ranked) - readable)
            shown += len(ranked)
    report = {
        "embedder": "test-hashing-768 (lexical stand-in; re-run with Gemini on real data)",
        "queries": len(labelled),
        "recall_at_10": round(statistics.mean(recalls), 3),
        "precision_at_5": round(statistics.mean(precisions), 3),
        "mrr": round(statistics.mean(reciprocal), 3),
        "acl_leakage_rate": leaked / max(1, shown),
        "p95_latency_ms": round(statistics.quantiles(latencies, n=20)[18], 1),
    }
    out = Path(os.environ.get("VISIONIQ_BENCHMARK_DIR", tmp_path)) / "retrieval-benchmark.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(report, indent=2))
    print(json.dumps(report))

    assert report["acl_leakage_rate"] == 0
    assert report["recall_at_10"] == 1.0
    assert report["mrr"] >= 0.75
