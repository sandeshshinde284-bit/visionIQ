"""LRN-15: publish a consistent knowledge snapshot (master design 6.6 stage 9, 6.15, KB-5).

Completion evidence: a candidate that fails its checks is rejected while the previous snapshot keeps
serving, and a rollback restores the last good snapshot.
"""

import os

import pytest

psycopg = pytest.importorskip("psycopg")

from corpus_support import Corpus, conninfo  # noqa: E402

from app.knowledge import indexer, search, snapshots  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402
from app.knowledge.retrieval import retrieve  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)


def intel() -> str:
    return conninfo("INTELLIGENCE")


def bug(number: int, title: str) -> dict:
    return {"id": number, "workItemType": "Bug", "title": title, "reproSteps": f"{title} details"}


def served_titles(corpus: Corpus, query: str) -> set[str]:
    with psycopg.connect(intel()) as conn:
        return {
            h.content.split("\n", 1)[0]
            for h in search.keyword(conn, query, [corpus.scope], match="any")
        }


def events(snapshot_id: str) -> list[str]:
    with psycopg.connect(conninfo("API")) as conn:
        return [
            r[0]
            for r in conn.execute(
                "SELECT event_type FROM knowledge.change_events "
                "WHERE subject = %s ORDER BY event_id",
                (snapshot_id,),
            )
        ]


def test_a_consistent_candidate_is_published_and_answers_carry_its_id_and_age() -> None:
    corpus = Corpus()
    corpus.add("work_item_current", bug(1, "Barcode print fails after retry"))

    _, result = snapshots.refresh(intel(), corpus.read, HashingEmbedder())

    assert result.status == "published" and all(result.checks.values())
    with psycopg.connect(intel()) as conn:
        assert snapshots.current(conn)[0] == result.snapshot_id
    assert "snapshot.published" in events(result.snapshot_id)
    bundle = retrieve(
        lambda: psycopg.connect(intel()),
        HashingEmbedder(),
        "Barcode print fails after retry",
        [corpus.scope],
        live_check=lambda key: True,
    )
    assert bundle.snapshot_id == result.snapshot_id and bundle.snapshot_age_seconds >= 0


def test_a_corrupt_entry_is_never_served_and_is_repaired_on_the_next_run() -> None:
    corpus = Corpus()
    corpus.add("work_item_current", bug(2, "Receipt printer jams"))
    snapshots.refresh(intel(), corpus.read, HashingEmbedder())
    corpus.add("work_item_current", bug(2, "Receipt printer jams v2"), revision="2")
    indexer.run(intel(), corpus.read, HashingEmbedder())
    with psycopg.connect(conninfo("INTELLIGENCE")) as conn:  # corrupt the new entry: a lost vector
        conn.execute(
            "DELETE FROM knowledge.chunk_embeddings WHERE chunk_key IN (SELECT c.chunk_key "
            "FROM knowledge.index_chunks c WHERE c.provider_revision_id = '2' "
            "AND c.acl_principals ?| %s)",
            ([corpus.scope],),
        )

    fallback = snapshots.build_and_publish(intel(), HashingEmbedder().model_id)

    # One bad entry never blocks publishing: the last good revision is served, labelled outdated.
    assert fallback.status == "published" and fallback.counts["outdated"] >= 1
    assert served_titles(corpus, "receipt printer jams") == {"Bug 2: Receipt printer jams"}

    report, repaired = snapshots.refresh(intel(), corpus.read, HashingEmbedder())

    assert corpus.key("work_item_current", "2") not in report.failed
    assert repaired.status == "published"
    assert served_titles(corpus, "receipt printer jams") == {"Bug 2: Receipt printer jams v2"}


def test_an_interrupted_model_change_is_rejected_but_revocations_are_never_blocked() -> None:
    corpus = Corpus()
    for n in range(10, 15):
        corpus.add("work_item_current", bug(n, f"Kiosk fault {n}"))
    snapshots.refresh(intel(), corpus.read, HashingEmbedder())

    class NewModel(HashingEmbedder):
        model_id = "test-hashing-768-unfinished"

    indexer.run(intel(), corpus.read, NewModel(), only=[corpus.key("work_item_current", "10")])
    unfinished = snapshots.build_and_publish(intel(), NewModel().model_id)
    assert unfinished.status == "rejected" and unfinished.checks["coverage"] is False

    for n in range(10, 15):  # every item revoked at once: must leave immediately
        corpus.add("work_item_current", bug(n, "x"), revision="2", scope="ado-project:o/Secret")
    _, revoked = snapshots.refresh(intel(), corpus.read, HashingEmbedder())
    assert revoked.status == "published"
    assert served_titles(corpus, "kiosk fault") == set()


def test_rollback_restores_the_last_good_snapshot_in_one_step() -> None:
    corpus = Corpus()
    corpus.add("work_item_current", bug(20, "Payment captured twice"))
    _, first = snapshots.refresh(intel(), corpus.read, HashingEmbedder())
    corpus.add("work_item_current", bug(20, "Payment captured twice (bad rewrite)"), revision="2")
    snapshots.refresh(intel(), corpus.read, HashingEmbedder())
    assert served_titles(corpus, "payment captured twice") == {
        "Bug 20: Payment captured twice (bad rewrite)"
    }

    restored = snapshots.rollback(intel())

    assert restored == first.snapshot_id
    assert served_titles(corpus, "payment captured twice") == {"Bug 20: Payment captured twice"}
    assert "snapshot.rolled_back" in events(first.snapshot_id)
    _, again = snapshots.refresh(intel(), corpus.read, HashingEmbedder())  # publishing resumes
    assert again.status == "published"


def test_cleanup_never_deletes_entries_waiting_to_be_published() -> None:
    corpus = Corpus()
    corpus.add("work_item_current", bug(30, "Pending entry"))
    snapshots.refresh(intel(), corpus.read, HashingEmbedder())
    corpus.add("work_item_current", bug(30, "Pending entry v2"), revision="2")
    indexer.run(intel(), corpus.read, HashingEmbedder())  # indexed, not yet published

    snapshots.cleanup(intel(), keep=1)

    with psycopg.connect(conninfo("API")) as conn:
        pending = conn.execute(
            "SELECT count(*) FROM knowledge.index_entries WHERE item_key = %s "
            "AND provider_revision_id = '2'",
            (corpus.key("work_item_current", "30"),),
        ).fetchone()[0]
    assert pending == 1
