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


class VertexEmbeddingError(RuntimeError):
    """A Vertex AI request failed; ``status`` is the HTTP status (None for a malformed answer)."""

    def __init__(self, message: str, status: int | None = None, detail: str = "") -> None:
        super().__init__(message)
        self.status = status
        self.detail = detail


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
        # Vertex batches count against per-request quota; 1 is the safe default.
        batch_size: int = 1,
        session=None,
        max_retries: int = 5,
        request_timeout: int = 60,
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
        self.max_retries = max(1, max_retries)
        self.request_timeout = max(10, request_timeout)
        self.sleep = sleep
        if session is None:
            # Imported lazily: google-auth is only needed when Vertex is actually used, so the
            # test/hashing path (and anything importing this module) has no hard dependency on it.
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
            response = self.session.post(self.url, json=body, timeout=self.request_timeout)
            if response.status_code in (429, 500, 502, 503, 504) and attempt < self.max_retries:
                self.sleep(min(60.0, 2.0**attempt))
                continue
            if response.status_code != 200:
                status = response.status_code
                try:
                    detail = str(response.json().get("error", {}).get("message", ""))[:300]
                except Exception:  # not JSON
                    detail = ""
                raise VertexEmbeddingError(
                    f"Vertex AI embedding request failed: HTTP {status}", status, detail
                )
            predictions = response.json().get("predictions", [])
            vectors = [p.get("embeddings", {}).get("values") for p in predictions]
            if len(vectors) != len(texts) or any(
                not v or len(v) != self.dimension for v in vectors
            ):
                raise VertexEmbeddingError("Vertex AI returned an unexpected embedding shape")
            return [normalize(v) for v in vectors]
        raise VertexEmbeddingError("Vertex AI embedding request kept failing")

    def embed_documents(self, texts: Sequence[str]) -> list[list[float]]:
        vectors: list[list[float]] = []
        for start in range(0, len(texts), self.batch_size):
            vectors.extend(
                self._predict(texts[start : start + self.batch_size], "RETRIEVAL_DOCUMENT")
            )
        return vectors

    def embed_query(self, text: str) -> list[float]:
        return self._predict([text], "RETRIEVAL_QUERY")[0]


def setting(name: str, default: str = "") -> str:
    """An environment value where empty counts as unset (a .env copied from the template)."""
    return os.environ.get(name) or default


def vertex_from_environment() -> VertexGeminiEmbedder:
    """Vertex AI settings shared with the Google Cloud section of .env.example."""
    project = setting("GCP_PROJECT_ID")
    if not project:
        raise KeyError("GCP_PROJECT_ID is required for Vertex AI embeddings")
    return VertexGeminiEmbedder(
        project=project,
        location=setting("VERTEX_LOCATION") or setting("GCP_REGION", "europe-west1"),
        model=setting("EMBEDDING_MODEL", "gemini-embedding-001"),
        dimension=int(setting("EMBEDDING_DIMENSION", str(DIMENSION))),
        batch_size=int(setting("EMBEDDING_BATCH_SIZE", "1")),
        request_timeout=int(setting("EMBEDDING_REQUEST_TIMEOUT", "60")),
        max_retries=int(setting("EMBEDDING_MAX_RETRIES", "5")),
    )


def from_environment() -> Embedder:
    """EMBEDDING_PROVIDER=vertex (default) or test (offline only, never production)."""
    provider = setting("EMBEDDING_PROVIDER", "vertex")
    if provider == "test":
        return HashingEmbedder()
    if provider != "vertex":
        raise ValueError(f"Unknown EMBEDDING_PROVIDER {provider!r}")
    return vertex_from_environment()
