"""S10-11 / S15-07: time-to-brief, measured from real runs.

Time-to-brief is pickup to posted comment: from the moment the API stored the pickup (webhook
or sweep) to the moment the brief was on the ticket. The API records it per run in
``workflow.brief_publications``. It covers the brief only: PR and CI time are a different
measurement and are not in it. Intelligence's own stages (retrieval, generation) are recorded
with each brief so a slow run can be explained.

``python -m app.brief.latency [--days N] [--json]`` prints the median and p95.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from collections.abc import Sequence
from dataclasses import asdict, dataclass

import psycopg

from app.config import get_settings
from app.db import connect


def percentile(values: Sequence[float], fraction: float) -> float | None:
    """Nearest-rank percentile (the value at or below which ``fraction`` of the runs fall)."""
    if not values:
        return None
    if not 0 < fraction <= 1:
        raise ValueError("fraction must be in (0, 1]")
    ordered = sorted(values)
    return ordered[max(0, math.ceil(fraction * len(ordered)) - 1)]


@dataclass(frozen=True)
class Distribution:
    runs: int
    p50_ms: float | None
    p95_ms: float | None
    max_ms: float | None

    @classmethod
    def of(cls, values: Sequence[float]) -> Distribution:
        return cls(
            len(values),
            percentile(values, 0.5),
            percentile(values, 0.95),
            max(values) if values else None,
        )


@dataclass(frozen=True)
class LatencyReport:
    days: int
    # Pickup to posted comment, per result state and overall.
    time_to_brief: dict[str, Distribution]
    # Inside Intelligence: where the time went.
    retrieval: Distribution
    generation: Distribution
    analysis_total: Distribution

    def to_dict(self) -> dict:
        return asdict(self)


PUBLISHED_SQL = """
SELECT result_state, time_to_brief_ms
FROM workflow.brief_publications
WHERE status = 'published' AND published_at >= now() - make_interval(days => %s)
"""

STAGES_SQL = """
SELECT retrieval_ms, generation_ms, total_ms
FROM knowledge.briefs
WHERE created_at >= now() - make_interval(days => %s)
"""


def measure(connection: psycopg.Connection, days: int) -> LatencyReport:
    published = connection.execute(PUBLISHED_SQL, (days,)).fetchall()
    stages = connection.execute(STAGES_SQL, (days,)).fetchall()
    by_state: dict[str, list[float]] = {}
    for state, elapsed in published:
        by_state.setdefault(state, []).append(float(elapsed))
        by_state.setdefault("ALL", []).append(float(elapsed))
    return LatencyReport(
        days,
        {state: Distribution.of(values) for state, values in sorted(by_state.items())},
        Distribution.of([float(row[0]) for row in stages]),
        Distribution.of([float(row[1]) for row in stages]),
        Distribution.of([float(row[2]) for row in stages]),
    )


def _line(name: str, d: Distribution) -> str:
    def seconds(value: float | None) -> str:
        if value is None:
            return "-"
        return f"{value:.0f} ms" if value < 1000 else f"{value / 1000:.1f}s"

    return (
        f"{name:<24} runs {d.runs:>5}   median {seconds(d.p50_ms):>8}   "
        f"p95 {seconds(d.p95_ms):>8}   max {seconds(d.max_ms):>8}"
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Median and p95 pickup-to-brief time.")
    parser.add_argument("--days", type=int, default=30, help="runs of the last N days (default 30)")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    with connect(get_settings()) as connection:
        result = measure(connection, args.days)
    if args.json:
        sys.stdout.write(json.dumps(result.to_dict(), indent=2) + "\n")
        return 0
    lines = [f"Time to brief (pickup -> comment posted), last {result.days} days"]
    lines += [_line(state, d) for state, d in result.time_to_brief.items()] or [
        "  no published briefs"
    ]
    lines += [
        "",
        "Inside Intelligence (request accepted -> brief stored)",
        _line("retrieval", result.retrieval),
        _line("generation", result.generation),
        _line("total", result.analysis_total),
    ]
    sys.stdout.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
