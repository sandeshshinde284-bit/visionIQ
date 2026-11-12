"""LRN-09 real Vertex retrieval benchmark; human-labelled non-sensitive cases only.

Case file JSON: {"cases": [{"query": "...", "expected_item_keys": ["..."],
"confirmed_by_human": true}, ...]}. Uses approved local operator scopes.
Do not interpret this as enterprise identity authorization testing.
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
from pathlib import Path

import psycopg

from app.knowledge.embeddings import from_environment
from app.knowledge.lrn09_demo import ALLOWED_SQL, SCOPES_SQL, connection_string
from app.knowledge.retrieval import retrieve


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", required=True, type=Path)
    parser.add_argument("--project", default="Genoa Kiosk")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    if not args.cases.is_file():
        parser.error("Provide a JSON file containing independently labelled real-history cases")
    cases = json.loads(args.cases.read_text(encoding="utf-8")).get("cases", [])
    confirmed = [
        case
        for case in cases
        if case.get("confirmed_by_human") is True
        and case.get("query", "").strip()
        and case.get("expected_item_keys")
    ]
    if len(confirmed) < 3:
        print("BLOCKED: at least three human-confirmed real-history cases required")
        return 2

    embedder = from_environment()
    if not embedder.model_id.startswith("vertex:"):
        parser.error("Use Vertex/Gemini, not the offline hashing embedder")
    conninfo = connection_string()
    with psycopg.connect(conninfo) as conn:
        scopes = [
            row[0]
            for row in conn.execute(SCOPES_SQL, (args.project,))
            if row[0].startswith("ado-project:")
        ]
        if len(scopes) != 1:
            print("BLOCKED: expected one indexed ADO project scope")
            return 2
        allowed = {
            row[0] for row in conn.execute(ALLOWED_SQL, {"project": args.project, "scopes": scopes})
        }

    recalls, precisions, reciprocal, latencies = [], [], [], []
    out_of_scope = 0
    outcome_rows = []
    for case in confirmed:
        relevant = set(case["expected_item_keys"])
        if not relevant <= allowed:
            print("BLOCKED: labelled case references absent or unapproved item keys")
            return 2
        started = time.perf_counter()
        bundle = retrieve(
            lambda: psycopg.connect(conninfo),
            embedder,
            case["query"],
            scopes,
            live_check=lambda key: key in allowed,
            limit=10,
        )
        latencies.append((time.perf_counter() - started) * 1000)
        ranked = [result.item_key for result in bundle.results]
        out_of_scope += len(set(ranked) - allowed)
        recalls.append(len(relevant.intersection(ranked[:10])) / len(relevant))
        precisions.append(len(relevant.intersection(ranked[:5])) / 5)
        reciprocal.append(
            next((1 / index for index, key in enumerate(ranked, 1) if key in relevant), 0.0)
        )
        outcome_rows.append(
            {
                "status": bundle.status,
                "matched_relevant": len(relevant.intersection(ranked)),
                "relevant_total": len(relevant),
                "returned": len(ranked),
                "snapshot": bundle.snapshot_id,
            }
        )
    ordered = sorted(latencies)
    p95_index = max(0, (95 * len(ordered) + 99) // 100 - 1)
    report = {
        "status": "MEASURED",
        "authorization_mode": "stored-scope-only-not-live-user-access",
        "project": args.project,
        "model": embedder.model_id,
        "confirmed_cases": len(confirmed),
        "recall_at_10": round(statistics.mean(recalls), 3),
        "precision_at_5": round(statistics.mean(precisions), 3),
        "mrr": round(statistics.mean(reciprocal), 3),
        "latency_ms_p95": round(ordered[p95_index], 1),
        "out_of_scope_hits": out_of_scope,
        "cases": outcome_rows,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if out_of_scope == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
