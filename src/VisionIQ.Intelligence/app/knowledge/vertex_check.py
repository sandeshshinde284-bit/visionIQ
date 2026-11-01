"""Verify Vertex AI embeddings in YOUR environment: ``python -m app.knowledge.vertex_check``.

Checks credentials, access, model availability, the pinned 768 dimensions, that the model is
truly semantic (same meaning in different words ranks above an unrelated sentence) and latency.
Each step prints PASS or FAIL with a concrete fix; it stops at the first failure. Exit code 0
only if all pass. Sends three short synthetic sentences; no project data.
"""

from __future__ import annotations

import json
import math
import statistics
import sys
import time
from collections.abc import Callable
from dataclasses import asdict, dataclass

from app.knowledge.embeddings import (
    DIMENSION,
    VertexEmbeddingError,
    VertexGeminiEmbedder,
    vertex_from_environment,
)

QUERY = "Printer fails after payment retry"
SAME_MEANING = "Receipt printing broken when retrying a card payment"  # different words
UNRELATED = "Update the colours of the marketing banner on the homepage"

_FIXES = {
    401: "Credentials were rejected. Locally run `gcloud auth application-default login`; "
    "on Cloud Run check the service account attached to the job.",
    403: "Enable the Vertex AI API (`gcloud services enable aiplatform.googleapis.com`) and "
    "grant the caller the role roles/aiplatform.user on the project.",
    404: "The model is not available in this region or project. Check EMBEDDING_MODEL and "
    "VERTEX_LOCATION against the Vertex AI model availability list.",
    429: "Quota exceeded. Wait and retry, or request a higher Vertex AI quota for this region.",
}


@dataclass
class Step:
    name: str
    passed: bool
    detail: str
    fix: str = ""


def _cos(a: list[float], b: list[float]) -> float:
    return sum(x * y for x, y in zip(a, b, strict=True))


def run_checks(make_embedder: Callable[[], VertexGeminiEmbedder]) -> list[Step]:
    steps: list[Step] = []
    try:
        embedder = make_embedder()
    except Exception as error:
        steps.append(
            Step(
                "credentials and configuration",
                False,
                type(error).__name__ + ": " + str(error)[:200],
                "Set GCP_PROJECT_ID and VERTEX_LOCATION in .env; provide credentials with "
                "`gcloud auth application-default login` or a service account.",
            )
        )
        return steps
    steps.append(Step("credentials and configuration", True, f"model {embedder.model_id}"))

    try:
        started = time.perf_counter()
        document = embedder.embed_documents([SAME_MEANING])[0]
        first_ms = (time.perf_counter() - started) * 1000
    except VertexEmbeddingError as error:
        fix = _FIXES.get(error.status or 0, "Check the Vertex AI configuration and logs.")
        steps.append(Step("Vertex AI access", False, f"{error} {error.detail}".strip(), fix))
        return steps
    steps.append(Step("Vertex AI access", True, f"first call {first_ms:.0f} ms"))

    norm = math.sqrt(sum(v * v for v in document))
    shape_ok = len(document) == DIMENSION and abs(norm - 1.0) < 1e-6
    steps.append(
        Step(
            "pinned dimension",
            shape_ok,
            f"{len(document)} dimensions, norm {norm:.6f}",
            "" if shape_ok else "The model returned an unexpected size; check EMBEDDING_MODEL.",
        )
    )
    if not shape_ok:
        return steps

    query = embedder.embed_query(QUERY)
    unrelated = embedder.embed_documents([UNRELATED])[0]
    same, other = _cos(query, document), _cos(query, unrelated)
    semantic = same > other + 0.05
    steps.append(
        Step(
            "semantic matching",
            semantic,
            f"same meaning {same:.3f} vs unrelated {other:.3f}",
            ""
            if semantic
            else "The model does not separate meaning; do not use it for LRN-08 search.",
        )
    )
    if not semantic:
        return steps

    timings = []
    for _ in range(5):
        started = time.perf_counter()
        embedder.embed_query(QUERY)
        timings.append((time.perf_counter() - started) * 1000)
    steps.append(
        Step("latency", True, f"median {statistics.median(timings):.0f} ms over 5 query calls")
    )
    return steps


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    make = vertex_from_environment
    steps = run_checks(make)
    for step in steps:
        print(f"{'PASS' if step.passed else 'FAIL'}  {step.name}: {step.detail}")
        if step.fix:
            print(f"      fix: {step.fix}")
    ok = len(steps) == 5 and all(s.passed for s in steps)
    print("Vertex AI embeddings are ready for LRN-08." if ok else "Vertex AI is NOT ready yet.")
    if "--json" in args:
        print(json.dumps([asdict(s) for s in steps]))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
