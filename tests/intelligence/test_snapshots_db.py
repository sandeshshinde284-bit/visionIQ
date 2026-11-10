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


def _coverage_baseline() -> tuple[int, int]:
    """Current (eligible, served) counts for the default model, without changing served state.

    A "targeted" probe tolerates whatever is already published, so this never fails or moves
    the pointer unexpectedly -- it is a read, piggy-backed on the same code path under test.
    This file shares one accumulating database across its tests, so every test below asserts
    DELTAS against this baseline rather than hardcoded absolute counts.
    """
    probe = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="targeted")
    return probe.counts["eligible"], probe.counts["items"]


def test_a_targeted_pass_may_publish_far_below_full_coverage_but_full_scope_may_not() -> None:
    """LRN-08: an --only reindex must never be mistaken for a complete, ready-to-serve index."""
    eligible_before, items_before = _coverage_baseline()
    corpus = Corpus()
    keys = [corpus.add("work_item_current", bug(n, f"Kiosk fault {n}")) for n in range(100, 110)]
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=[keys[0]])  # 1 of 10, rest pending

    targeted = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="targeted")
    assert targeted.status == "published"
    assert targeted.counts["eligible"] == eligible_before + 10
    assert targeted.counts["items"] == items_before + 1
    assert targeted.counts["scope"] == "targeted"

    # The very same underlying state, asked for as a FULL pass, must not be reported as ready.
    full = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="full")
    assert full.status == "rejected" and full.checks["coverage"] is False
    assert "complete index" in full.reason

    # Finish indexing this corpus so later full-coverage assertions in this file are not left
    # with permanently-unindexed stragglers from this deliberately-partial scenario.
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=keys[1:])
    restored = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="full")
    assert restored.status == "published" and restored.counts["items"] == eligible_before + 10


def test_build_and_publish_rejects_an_unknown_scope() -> None:
    with pytest.raises(ValueError):
        snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="bogus")


def test_incremental_backfill_grows_coverage_without_regressing_then_a_full_pass_completes_it() -> None:
    """LRN-08 backfill: bootstrap -> partial -> repeated runs -> complete, never regressing."""
    _, items_before = _coverage_baseline()
    corpus = Corpus()
    keys = [corpus.add("work_item_current", bug(n, f"Backlog item {n}")) for n in range(200, 220)]

    # Bootstrap: nothing of this corpus has ever been published; a partial first batch is fine.
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=keys[:5])
    first = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="incremental")
    assert first.status == "published"
    assert first.counts["items"] == items_before + 5
    assert first.counts["coverage_status"] == "partial"

    # Repeated incremental run: coverage must grow, never drop below what's already published.
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=keys[5:10])
    second = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="incremental")
    assert second.status == "published"
    assert second.counts["items"] == items_before + 10
    assert second.counts["coverage_status"] == "partial"

    # Complete backfill: once every item is indexed, a normal FULL pass confirms and marks it done.
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=keys[10:])
    done = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="full")
    assert done.status == "published"
    assert done.counts["items"] == items_before + 20
    assert done.counts["coverage_status"] == "complete"


def test_an_incremental_pass_that_would_drop_already_published_items_is_rejected() -> None:
    corpus = Corpus()
    keys = [corpus.add("work_item_current", bug(n, f"Shrink test {n}")) for n in range(300, 310)]
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=keys)
    published = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="incremental")
    assert published.status == "published"

    class NewModel(HashingEmbedder):
        model_id = "test-hashing-768-regressed"

    # Only one of the ten items is indexed under the new model: everything else published so far
    # (this corpus's other 9 items, plus everything from earlier tests) no longer matches, so this
    # candidate would drop a large number of already-published items -- refused on sight.
    indexer.run(intel(), corpus.read, NewModel(), only=[keys[0]])
    regressed = snapshots.build_and_publish(intel(), NewModel().model_id, scope="incremental")
    assert regressed.status == "rejected" and regressed.checks["coverage"] is False
    assert "dropped" in regressed.reason


def test_a_same_count_item_swap_is_still_caught_as_lost_coverage_not_masked_by_new_additions() -> (
    None
):
    """The specific gap a count-only check would miss: total served count can stay identical
    while specific, still-readable, previously-published items silently vanish from search."""
    _, items_before = _coverage_baseline()
    corpus = Corpus()
    keys = [corpus.add("work_item_current", bug(n, f"Swap test {n}")) for n in range(600, 605)]
    _, baseline = snapshots.refresh(intel(), corpus.read, HashingEmbedder())
    assert baseline.status == "published"
    assert baseline.counts["items"] == items_before + 5

    # Corrupt 2 of these 5 items' vectors directly: still readable at the source and never
    # deleted, but their only index entry is no longer complete, so they have no valid entry at
    # all (this is their first and only indexing pass -- there is no earlier good entry to fall
    # back to, unlike test_a_corrupt_entry_is_never_served_and_is_repaired_on_the_next_run).
    with psycopg.connect(intel()) as conn:
        conn.execute(
            "DELETE FROM knowledge.chunk_embeddings WHERE chunk_key IN (SELECT c.chunk_key "
            "FROM knowledge.index_chunks c WHERE c.item_key = ANY(%s))",
            ([keys[0], keys[1]],),
        )
    # ...and index 2 brand-new, different items under the same model. The candidate's TOTAL
    # served count can come out identical to `baseline` (5 - 2 corrupted + 2 new = 5), which a
    # count-only regression check would wave through as "not regressed".
    new_keys = [corpus.add("work_item_current", bug(n, f"Swap test {n}")) for n in range(900, 902)]
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=new_keys)

    swapped = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="incremental")
    assert swapped.status == "rejected" and swapped.checks["coverage"] is False
    assert swapped.counts["lost"] >= 2
    assert "dropped" in swapped.reason


def test_targeted_indexing_never_regresses_previously_published_full_coverage() -> None:
    """LRN-08: an --only update must preserve every item the last full pass already served."""
    _, items_before = _coverage_baseline()
    corpus = Corpus()
    keys = [corpus.add("work_item_current", bug(n, f"Stable item {n}")) for n in range(400, 406)]
    _, full = snapshots.refresh(intel(), corpus.read, HashingEmbedder())
    assert full.status == "published"
    assert full.counts["items"] == items_before + 6

    corpus.add("work_item_current", bug(400, "Stable item 400 v2"), revision="2")
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=[keys[0]])
    targeted = snapshots.build_and_publish(intel(), HashingEmbedder().model_id, scope="targeted")

    assert targeted.status == "published"
    assert targeted.counts["items"] == items_before + 6  # the other 5 carried over, none dropped
    assert served_titles(corpus, "stable item") == {
        f"Bug {n}: Stable item {n}" for n in (401, 402, 403, 404, 405)
    } | {"Bug 400: Stable item 400 v2"}


def test_full_scope_rejects_partial_indexing_caused_by_embedding_failures() -> None:
    """LRN-08 failed indexing: items that fail to embed must not count toward full coverage."""
    eligible_before, _ = _coverage_baseline()
    corpus = Corpus()
    keys = [corpus.add("work_item_current", bug(n, f"Flaky item {n}")) for n in range(500, 520)]

    class FailsAfterFive(HashingEmbedder):
        calls = 0

        def embed_documents(self, texts):
            type(self).calls += 1
            if type(self).calls > 5:
                raise ConnectionError("embedding outage mid-run")
            return super().embed_documents(texts)

    report = indexer.run(intel(), corpus.read, FailsAfterFive())
    assert len(report.failed) == 15  # 5 of 20 succeed before the simulated outage

    rejected = snapshots.build_and_publish(intel(), FailsAfterFive().model_id, scope="full")
    assert rejected.status == "rejected" and rejected.checks["coverage"] is False
    assert rejected.counts["eligible"] >= eligible_before + 20
    assert rejected.counts["not_indexed"] >= 15  # the failed items are never silently "served"

    # Clean up: fully index this corpus under the default model so it doesn't strand unindexed
    # items under FailsAfterFive's model_id forever (harmless either way, but tidy).
    indexer.run(intel(), corpus.read, HashingEmbedder(), only=keys)


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
