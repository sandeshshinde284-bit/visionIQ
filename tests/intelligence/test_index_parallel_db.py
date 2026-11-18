"""Indexing with several embedding requests in flight: the same entries as one at a time, a
failure stays with its own item, and the work really overlaps."""

import os
import threading
import time

import pytest

psycopg = pytest.importorskip("psycopg")

from corpus_support import Corpus, conninfo  # noqa: E402
from test_lrn13_db import pull_request as _pull_request  # noqa: E402

from app.knowledge import indexer  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)


def pull_request(comment: str, number: int) -> dict:
    """A pull request of its own: each needs its own id to be its own item."""
    return {**_pull_request(comment), "id": 1000 + number}


class SlowEmbedder(HashingEmbedder):
    """Like a remote model: each request takes a while. Records how many overlap."""

    def __init__(self, concurrency: int, fail_on: str | None = None) -> None:
        self.concurrency = concurrency
        self.fail_on = fail_on
        self.active = 0
        self.most = 0
        self.lock = threading.Lock()

    def embed_documents(self, texts):
        with self.lock:
            self.active += 1
            self.most = max(self.most, self.active)
        try:
            time.sleep(0.05)
            if self.fail_on and any(self.fail_on in text for text in texts):
                raise RuntimeError("the model refused this item")
            return super().embed_documents(texts)
        finally:
            with self.lock:
                self.active -= 1


def stored(keys: list[str]) -> dict[str, list[tuple]]:
    with psycopg.connect(conninfo("API")) as conn:
        rows = conn.execute(
            "SELECT c.item_key, c.chunk_index, c.content_sha256, e.embedding::text "
            "FROM knowledge.index_chunks c JOIN knowledge.chunk_embeddings e USING (chunk_key) "
            "WHERE c.item_key = ANY(%s) ORDER BY 1, 2",
            (keys,),
        ).fetchall()
    found: dict[str, list[tuple]] = {}
    for key, index, sha, vector in rows:
        found.setdefault(key, []).append((index, sha, vector))
    return found


def test_parallel_indexing_stores_what_sequential_indexing_stores() -> None:
    texts = [f"Retry change {n}: an idempotency key per captured payment." for n in range(12)]
    one, many = Corpus(), Corpus()
    first = [one.add("pull_request", pull_request(t, n)) for n, t in enumerate(texts)]
    second = [many.add("pull_request", pull_request(t, n)) for n, t in enumerate(texts)]

    sequential = indexer.run(conninfo("INTELLIGENCE"), one.read, SlowEmbedder(1), only=first)
    embedder = SlowEmbedder(4)
    parallel = indexer.run(conninfo("INTELLIGENCE"), many.read, embedder, only=second)

    assert (parallel.indexed, parallel.chunks, parallel.failed) == (
        sequential.indexed,
        sequential.chunks,
        [],
    )
    assert embedder.most > 1  # the requests really overlapped
    # The same chunks and vectors, item for item (the corpora differ only in their keys).
    a, b = stored(first), stored(second)
    assert [a[key] for key in first] == [b[key] for key in second]


def test_a_failed_item_does_not_stop_or_change_the_others() -> None:
    corpus = Corpus()
    good = [corpus.add("pull_request", pull_request(f"Receipt fix {n}.", n)) for n in range(6)]
    bad = corpus.add("pull_request", pull_request("This one says boom.", 99))

    report = indexer.run(
        conninfo("INTELLIGENCE"),
        corpus.read,
        SlowEmbedder(4, fail_on="boom"),
        only=[*good, bad],
    )

    assert report.failed == [bad]
    assert report.indexed == len(good)
    assert set(stored(good)) == set(good) and bad not in stored([bad])
