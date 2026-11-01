"""LRN-08 core: redaction before indexing, chunking with provenance, pinned embeddings."""

import json
import math

import pytest

from app.knowledge.chunking import MAX_CHARS, Section, chunk, html_to_text
from app.knowledge.embeddings import HashingEmbedder, VertexGeminiEmbedder
from app.knowledge.redaction import redact

# ---- Redaction -------------------------------------------------------------------------------

SECRETS = {
    "password": "connect with Password=Hunter2Secret! then",
    "connection": "Server=db;User Id=sa;Pwd=s3cr3t;AccountKey=abcDEF123456==;",
    "jwt": "token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk",
    "github": "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8",
    "ado_pat": "pat qz5x7k2mnb4vwcx3ryt6uop2lkj7hgf4dsa5zxcv6bnm2qwe3rty",
    "bearer": "Authorization: Bearer abcDEF123ghiJKL456mnoPQR",
    "email": "contact ana.lopez@contoso.com about it",
    "phone": "call +44 20 7946 0958 now",
    "card": "card 4111 1111 1111 1111 on file",
    "private_key": "-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----",
    "opaque": "key AbCdEfGh1234567890IjKlMnOpQrStUvWxYz0987654321 used",
}
LEAKS = [
    "Hunter2Secret",
    "s3cr3t",
    "abcDEF123456",
    "SflKxwRJ",
    "ghp_A1b2",
    "qz5x7k2mnb",
    "abcDEF123ghi",
    "ana.lopez",
    "7946 0958",
    "4111 1111",
    "MIIEow",
    "AbCdEfGh1234",
]


@pytest.mark.parametrize("name", sorted(SECRETS))
def test_each_kind_of_secret_and_personal_data_is_redacted(name):
    result = redact(SECRETS[name])

    assert "[REDACTED:" in result.text
    assert not any(leak in result.text for leak in LEAKS)
    assert sum(result.counts.values()) >= 1


def test_normal_engineering_text_is_left_intact():
    text = (
        "PAYMENT_CAPTURED in System.Collections.Generic at Retry.cs:42, version 4.2.1, "
        "commit 1f0c3b9e8d7a6f5e4d3c2b1a0f9e8d7c6b5a4f3e, id 3fa85f64-5717-4562-b3fc-2c963f66afa6, "
        "path /src/Payments/RetryPolicy.cs, order 1234567890, see https://dev.azure.com/org/p"
    )

    assert redact(text).text == text


def test_a_number_failing_the_card_checksum_is_kept():
    assert redact("ticket 4111 1111 1111 1112").text == "ticket 4111 1111 1111 1112"


# ---- Chunking --------------------------------------------------------------------------------


def test_html_becomes_readable_text_without_scripts():
    text = html_to_text(
        "<div>Steps:</div><ul><li>Pay</li><li>Retry&nbsp;print</li></ul><script>x()</script>"
    )

    assert text == "Steps:\n\n- Pay\n\n- Retry print"


def test_a_short_item_is_one_context_prefixed_chunk_with_stable_keys():
    sections = [Section("Description", "Barcode print fails after retry."), Section("Empty", "  ")]

    first = chunk("item", "rev", "Bug 7", "Barcode print fails", sections)
    again = chunk("item", "rev", "Bug 7", "Barcode print fails", sections)

    assert len(first) == 1
    assert (
        first[0].text
        == "Bug 7: Barcode print fails\n[Description]\nBarcode print fails after retry."
    )
    assert [c.chunk_key for c in first] == [c.chunk_key for c in again]
    assert (
        first[0].chunk_key
        != chunk("item", "rev2", "Bug 7", "Barcode print fails", sections)[0].chunk_key
    )


def test_long_text_is_split_within_limits_with_overlap_and_nothing_lost():
    sentences = [f"Sentence number {i} explains the retry behaviour in detail." for i in range(120)]
    chunks = chunk("item", "rev", "Bug 1", "Long", [Section("Repro steps", " ".join(sentences))])

    assert len(chunks) > 1
    assert all(len(c.text) <= MAX_CHARS for c in chunks)
    joined = "\n".join(c.text for c in chunks)
    assert all(s in joined for s in sentences)  # nothing dropped
    assert chunks[1].text.split("]\n", 1)[1][:30] in chunks[0].text  # overlap carries context


def test_a_huge_unbroken_line_is_still_bounded():
    chunks = chunk("item", "rev", "Bug 1", "Trace", [Section("System info", "x" * 5000)])

    assert all(len(c.text) <= MAX_CHARS for c in chunks)
    assert sum(c.text.count("x") for c in chunks) >= 5000


# ---- Embeddings ------------------------------------------------------------------------------


def _cos(a, b):
    return sum(x * y for x, y in zip(a, b, strict=True))


def test_the_test_embedder_is_deterministic_normalised_and_pinned():
    embedder = HashingEmbedder()
    a = embedder.embed_query("barcode print retry")

    assert a == embedder.embed_documents(["barcode print retry"])[0]
    assert math.isclose(sum(v * v for v in a), 1.0, rel_tol=1e-9)
    assert (embedder.model_id, embedder.dimension, len(a)) == ("test-hashing-768", 768, 768)
    assert _cos(a, embedder.embed_query("printer barcode retry fails")) > _cos(
        a, embedder.embed_query("login page css")
    )


class FakeResponse:
    def __init__(self, status, body=None):
        self.status_code = status
        self._body = body or {}

    def json(self):
        return self._body


class FakeSession:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []

    def post(self, url, json, timeout):
        self.calls.append((url, json))
        return self.responses.pop(0)


def _vector(value=1.0):
    return {"embeddings": {"values": [value] * 768}}


def test_vertex_request_uses_task_types_pinned_dimension_and_no_silent_truncation():
    session = FakeSession(
        [
            FakeResponse(200, {"predictions": [_vector()]}),
            FakeResponse(200, {"predictions": [_vector(2.0)]}),
        ]
    )
    embedder = VertexGeminiEmbedder(
        "my-gcp-project", "europe-west1", session=session, sleep=lambda _: None
    )

    document = embedder.embed_documents(["chunk"])[0]
    query = embedder.embed_query("question")

    url, body = session.calls[0]
    assert url == (
        "https://europe-west1-aiplatform.googleapis.com/v1/projects/my-gcp-project/locations/"
        "europe-west1/publishers/google/models/gemini-embedding-001:predict"
    )
    assert body["instances"] == [{"content": "chunk", "task_type": "RETRIEVAL_DOCUMENT"}]
    assert body["parameters"] == {"outputDimensionality": 768, "autoTruncate": False}
    assert session.calls[1][1]["instances"][0]["task_type"] == "RETRIEVAL_QUERY"
    assert math.isclose(sum(v * v for v in document), 1.0) and math.isclose(
        sum(v * v for v in query), 1.0
    )
    assert embedder.model_id == "vertex:gemini-embedding-001@768"


def test_vertex_retries_throttling_and_rejects_bad_answers():
    session = FakeSession([FakeResponse(429), FakeResponse(200, {"predictions": [_vector()]})])
    assert (
        len(
            VertexGeminiEmbedder(
                "my-gcp-project", "europe-west1", session=session, sleep=lambda _: None
            ).embed_query("q")
        )
        == 768
    )

    for response in (
        FakeResponse(400),
        FakeResponse(200, {"predictions": [{"embeddings": {"values": [1.0] * 3072}}]}),
    ):
        with pytest.raises(RuntimeError):
            VertexGeminiEmbedder(
                "my-gcp-project",
                "europe-west1",
                session=FakeSession([response]),
                sleep=lambda _: None,
            ).embed_query("q")


def test_only_the_schema_dimension_and_valid_projects_are_accepted():
    with pytest.raises(ValueError):
        VertexGeminiEmbedder(
            "my-gcp-project", "europe-west1", dimension=1536, session=FakeSession([])
        )
    with pytest.raises(ValueError):
        VertexGeminiEmbedder("Bad Project!", "europe-west1", session=FakeSession([]))
    assert json.dumps({"ok": True})
