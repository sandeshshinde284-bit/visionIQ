"""LRN-08 (S07-02): embedding providers with a pinned model and dimension.

The configured model ID and dimension are stored with every vector; vectors from different
models are never mixed (a model change re-embeds). Documents and queries use the provider's
distinct task types. Vectors are L2-normalised so cosine distance is well defined.
"""

from __future__ import annotations

import hashlib
import math
import os
import re
import time
from collections.abc import Sequence
from typing import Protocol

DIMENSION = 768  # the database column is vector(768): a different dimension needs a migration


class Embedder(Protocol):
    model_id: str
    dimension: int

    def embed_documents(self, texts: Sequence[str]) -> list[list[float]]: ...

    def embed_query(self, text: str) -> list[float]: ...


def normalize(vector: Sequence[float]) -> list[float]:
    norm = math.sqrt(sum(v * v for v in vector))
    if norm == 0 or not math.isfinite(norm):
        raise ValueError("Embedding is empty or not finite")
    return [v / norm for v in vector]


class HashingEmbedder:
    """Deterministic, dependency-free embedder for tests and offline demos (word hashing).

    It matches shared vocabulary only; it is NOT a semantic model and is never used in production.
    """

    model_id = "test-hashing-768"
    dimension = DIMENSION

    def _embed(self, text: str) -> list[float]:
        vector = [0.0] * self.dimension
        for word in re.findall(r"[a-z0-9]+", text.lower()):
            digest = hashlib.sha256(word.encode()).digest()
            vector[int.from_bytes(digest[:4], "big") % self.dimension] += (
                1.0 if digest[4] & 1 else -1.0
            )
        if not any(vector):
            vector[0] = 1.0
        return normalize(vector)

    def embed_documents(self, texts: Sequence[str]) -> list[list[float]]:
        return [self._embed(text) for text in texts]

    def embed_query(self, text: str) -> list[float]:
        return self._embed(text)


class VertexGeminiEmbedder:
    """Gemini embeddings on Vertex AI (D08: gemini-embedding-001 at 768 dimensions)."""

    def __init__(
        self,
        project: str,
        location: str,
        model: str = "gemini-embedding-001",
        dimension: int = DIMENSION,
        batch_size: int = 1,
        session=None,
        max_retries: int = 5,
        sleep=time.sleep,
    ) -> None:
        if dimension != DIMENSION:
            raise ValueError(f"Only {DIMENSION} dimensions are supported by the index schema")
        if not re.fullmatch(r"[a-z][a-z0-9-]{4,61}[a-z0-9]", project) or not re.fullmatch(
            r"[a-z0-9-]+", location
        ):
            raise ValueError("A valid Google Cloud project ID and location are required")
        self.model_id = f"vertex:{model}@{dimension}"
        self.dimension = dimension
        self.batch_size = max(1, batch_size)
        self.url = (
            f"https://{location}-aiplatform.googleapis.com/v1/projects/{project}/locations/"
            f"{location}/publishers/google/models/{model}:predict"
        )
        self.max_retries = max_retries
        self.sleep = sleep
        if session is None:
            import google.auth
            from google.auth.transport.requests import AuthorizedSession

            credentials, _ = google.auth.default(
                scopes=["https://www.googleapis.com/auth/cloud-platform"]
            )
            session = AuthorizedSession(credentials)
        self.session = session

    def _predict(self, texts: Sequence[str], task: str) -> list[list[float]]:
        body = {
            "instances": [{"content": text, "task_type": task} for text in texts],
            # autoTruncate=false: an over-long input is an error, never silently cut.
            "parameters": {"outputDimensionality": self.dimension, "autoTruncate": False},
        }
        for attempt in range(self.max_retries + 1):
            response = self.session.post(self.url, json=body, timeout=60)
            if response.status_code in (429, 500, 502, 503, 504) and attempt < self.max_retries:
                self.sleep(min(60.0, 2.0**attempt))
                continue
            if response.status_code != 200:
                raise RuntimeError(
                    f"Vertex AI embedding request failed: HTTP {response.status_code}"
                )
            predictions = response.json().get("predictions", [])
            vectors = [p.get("embeddings", {}).get("values") for p in predictions]
            if len(vectors) != len(texts) or any(
                not v or len(v) != self.dimension for v in vectors
            ):
                raise RuntimeError("Vertex AI returned an unexpected embedding shape")
            return [normalize(v) for v in vectors]
        raise RuntimeError("Vertex AI embedding request kept failing")

    def embed_documents(self, texts: Sequence[str]) -> list[list[float]]:
        vectors: list[list[float]] = []
        for start in range(0, len(texts), self.batch_size):
            vectors.extend(
                self._predict(texts[start : start + self.batch_size], "RETRIEVAL_DOCUMENT")
            )
        return vectors

    def embed_query(self, text: str) -> list[float]:
        return self._predict([text], "RETRIEVAL_QUERY")[0]


def from_environment() -> Embedder:
    """EMBEDDING_PROVIDER=vertex (default; needs VERTEX_PROJECT) or test (offline only)."""
    provider = os.environ.get("EMBEDDING_PROVIDER", "vertex")
    if provider == "test":
        return HashingEmbedder()
    if provider != "vertex":
        raise ValueError(f"Unknown EMBEDDING_PROVIDER {provider!r}")
    return VertexGeminiEmbedder(
        project=os.environ["VERTEX_PROJECT"],
        location=os.environ.get("VERTEX_LOCATION", "europe-west1"),
        model=os.environ.get("EMBEDDING_MODEL", "gemini-embedding-001"),
        batch_size=int(os.environ.get("EMBEDDING_BATCH_SIZE", "1")),
    )
