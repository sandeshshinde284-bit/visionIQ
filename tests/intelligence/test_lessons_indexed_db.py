"""LRN-10: real published LRN-08 redacted text qualification, isolated by its own DB."""

import os

import psycopg
import pytest
from corpus_support import Corpus, conninfo

from app.knowledge import lessons_cli, snapshots
from app.knowledge.embeddings import HashingEmbedder

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Run only with a disposable migrated PostgreSQL database",
)
FILES = ["/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs"]


def test_published_redacted_chunks_qualify_when_source_metadata_has_no_title():
    """The production LRN-03 source payload stores no title or reproduction text."""
    corpus = Corpus()
    for number, title in (
        (901, "Barcode print fails after payment retry"),
        (902, "Barcode printing fails after payment retry"),
    ):
        corpus.add(
            "work_item_current",
            {
                "id": number,
                "workItemType": "Bug",
                "title": title,
                "reproSteps": "Payment captured; barcode print fails on retry.",
            },
        )
        corpus.fix(str(number), str(number + 100), FILES)

    with psycopg.connect(conninfo("API")) as conn:
        assert conn.execute(
            "SELECT count(*) FROM evidence.source_revisions AS r "
            "JOIN evidence.source_items AS i ON i.item_key = r.item_key "
            "WHERE i.source_instance = %s AND r.redacted_payload ? 'title'",
            (corpus.org,),
        ).fetchone()[0] == 0

    indexed, snapshot = snapshots.refresh(
        conninfo("INTELLIGENCE"), corpus.read, HashingEmbedder()
    )
    assert indexed.failed == []
    assert snapshot.status == "published"
    result = lessons_cli.run(conninfo("INTELLIGENCE"))
    assert result["new_lessons"] == 1

    with psycopg.connect(conninfo("API")) as conn:
        rows = conn.execute(
            "SELECT r.status, r.cause_status FROM knowledge.lesson_patterns AS p "
            "JOIN knowledge.lesson_revisions AS r "
            "ON r.pattern_id = p.pattern_id AND r.revision = p.latest_revision "
            "WHERE p.source_instance = %s",
            (corpus.org,),
        ).fetchall()
        assert rows == [("candidate", "unverified")]
