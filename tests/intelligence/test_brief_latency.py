"""BRF-07: time-to-brief percentiles (S10-11) and a repeatable pipeline benchmark (S15-07)."""

import statistics
import time
from typing import Any

import pytest
from brief_support import FakeSources, offline_gateway, request

from app.brief import latency
from app.brief.latency import Distribution, measure, percentile
from app.brief.pipeline import BriefSettings, build_brief


@pytest.mark.parametrize(
    ("values", "fraction", "expected"),
    [
        ([10], 0.5, 10),
        ([10, 20], 0.5, 10),
        ([10, 20, 30, 40], 0.5, 20),
        (list(range(1, 101)), 0.95, 95),
        (list(range(1, 21)), 0.95, 19),
        ([5, 1, 3], 1.0, 5),
    ],
)
def test_percentile_is_nearest_rank(values, fraction, expected) -> None:
    assert percentile(values, fraction) == expected


def test_percentile_of_nothing_is_unknown_not_zero() -> None:
    assert percentile([], 0.95) is None
    assert Distribution.of([]) == Distribution(0, None, None, None)
    with pytest.raises(ValueError, match="fraction"):
        percentile([1], 0)


class FakeConnection:
    def __init__(self, published: list[tuple], stages: list[tuple]) -> None:
        self.results = {"brief_publications": published, "knowledge.briefs": stages}
        self.queries: list[tuple[str, Any]] = []

    def execute(self, sql: str, params: Any) -> "FakeConnection":
        self.queries.append((sql, params))
        self._rows = next(rows for table, rows in self.results.items() if table in sql)
        return self

    def fetchall(self) -> list[tuple]:
        return self._rows


def test_the_report_gives_median_and_p95_per_state_and_per_stage() -> None:
    published = [("PUBLISHED", 40_000 + 1_000 * n) for n in range(20)] + [("PARTIAL", 9_000)]
    stages = [(300, 12_000, 13_000), (500, 20_000, 21_500), (400, 16_000, 17_000)]
    connection = FakeConnection(published, stages)

    report = measure(connection, days=7)

    assert report.time_to_brief["PUBLISHED"] == Distribution(20, 49_000, 58_000, 59_000)
    assert report.time_to_brief["PARTIAL"].runs == 1
    assert report.time_to_brief["ALL"].runs == 21
    assert (report.retrieval.p50_ms, report.generation.p95_ms) == (400, 20_000)
    assert report.analysis_total.max_ms == 21_500
    assert all(params == (7,) for _, params in connection.queries)
    # Only briefs that reached the ticket count, and only brief time: no PR or CI tables.
    sql = " ".join(query for query, _ in connection.queries)
    assert "status = 'published'" in sql
    assert "pull_request" not in sql and "ci_" not in sql and "build" not in sql


def test_the_cli_prints_median_and_p95(monkeypatch, capsys, test_settings) -> None:
    connection = FakeConnection(
        [("PUBLISHED", 42_000), ("PUBLISHED", 61_000)], [(250, 9_000, 9_600)]
    )

    class Opened:
        def __enter__(self):
            return connection

        def __exit__(self, *_: object) -> None:
            pass

    monkeypatch.setattr(latency, "get_settings", lambda: test_settings)
    monkeypatch.setattr(latency, "connect", lambda settings: Opened())

    assert latency.main(["--days", "14"]) == 0
    text = capsys.readouterr().out

    assert "last 14 days" in text and "pickup -> comment posted" in text
    assert "median    42.0s" in text and "p95    61.0s" in text
    assert latency.main(["--json"]) == 0
    assert '"p95_ms": 61000.0' in capsys.readouterr().out


def test_benchmark_the_pipeline_overhead_is_small_and_repeatable() -> None:
    """S15-07: everything VisionIQ adds around retrieval and the model, measured the same way
    each time (in-memory sources, no model latency). Real end-to-end numbers come from
    ``python -m app.brief.latency`` over actual runs; this guards the code path against
    regressions that would eat the time budget."""
    sources, gateway, settings = FakeSources(), offline_gateway(), BriefSettings()
    build_brief(request(), sources, gateway, settings)  # warm-up
    timings: list[float] = []
    for _ in range(50):
        started = time.perf_counter()
        outcome = build_brief(request(), sources, gateway, settings)
        timings.append((time.perf_counter() - started) * 1000)
        assert outcome.brief.state == "PUBLISHED"

    p50, p95 = statistics.median(timings), percentile(timings, 0.95)

    assert p95 is not None and p50 < 50 and p95 < 250, f"p50={p50:.1f} ms p95={p95:.1f} ms"
