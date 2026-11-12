"""BRF-08: activity-adjusted hotspot scores (PROP-07) and ranking / false positives (PROP-08)."""

import json
import uuid
from datetime import UTC, datetime

import pytest

from app.hotspots import cli
from app.hotspots.scoring import (
    HOT_MIN_FIXES,
    MIN_CHANGES,
    ROOT_AREA,
    AreaActivity,
    area_of,
    score,
)
from app.hotspots.sweep import SweepResult, report

ORG = "https://dev.azure.com/contoso"


def area(
    name: str, changes: int, fixes: int, reverted: int = 0, repo: str = "shop"
) -> AreaActivity:
    evidence = tuple(
        {
            "ticket_id": str(100 + n),
            "fix_kind": "pull_request",
            "fix_id": str(200 + n),
            "reverted": n < reverted,
        }
        for n in range(fixes)
    )
    return AreaActivity(ORG, "Payments", repo, name, changes, fixes, reverted, evidence)


def bands(areas: list[AreaActivity]) -> dict[str, str]:
    return {scored.activity.area: scored.band for scored in score(areas)}


@pytest.mark.parametrize(
    ("path", "depth", "expected"),
    [
        ("/src/Payments/Retry/RetryPolicy.cs", 2, "src/Payments"),
        ("src/Payments/RetryPolicy.cs", 3, "src/Payments"),
        ("src\\Kiosk\\Printer.cs", 1, "src"),
        ("/README.md", 2, ROOT_AREA),
        ("build.ps1", 2, ROOT_AREA),
    ],
)
def test_an_area_is_the_first_directories_of_a_path(path: str, depth: int, expected: str) -> None:
    assert area_of(path, depth) == expected


def test_a_busy_area_is_not_flagged_just_for_being_busy() -> None:
    """The acceptance criterion: same number of fixes, very different amounts of activity."""
    areas = [
        area("src/Checkout", changes=300, fixes=12),  # busy: 4% of changes are defect fixes
        area("src/Printing", changes=40, fixes=12),  # quiet: 30% are
        area("src/Catalog", changes=200, fixes=8),
        area("src/Search", changes=160, fixes=6),
    ]

    scored = {s.activity.area: s for s in score(areas)}

    assert scored["src/Printing"].band == "HOT" and scored["src/Printing"].rank == 1
    assert scored["src/Checkout"].band == "NORMAL"  # most fixes in absolute terms, average rate
    assert scored["src/Checkout"].lift < 1.0 < scored["src/Printing"].lift
    # Ranking by raw fix count would have put Checkout level with Printing.
    assert (
        scored["src/Checkout"].activity.defect_fixes == scored["src/Printing"].activity.defect_fixes
    )


def test_a_tiny_sample_is_not_a_hotspot() -> None:
    """False positive guard: 2 fixes in 2 changes looks like 100% but proves little."""
    result = bands(
        [
            area("src/New", changes=2, fixes=2),
            area("src/Edge", changes=6, fixes=2),
            area("src/Core", changes=400, fixes=40),
        ]
    )

    assert result["src/New"] == "INSUFFICIENT_DATA"  # below MIN_CHANGES: unknown, not "normal"
    assert result["src/Edge"] != "HOT"  # enough changes, but under the minimum number of fixes
    assert MIN_CHANGES == 5 and HOT_MIN_FIXES == 3


def test_smoothing_pulls_small_areas_toward_the_repository_rate() -> None:
    small = area("src/Small", changes=6, fixes=3)
    large = area("src/Large", changes=60, fixes=30)
    rest = area("src/Rest", changes=400, fixes=20)

    scored = {s.activity.area: s for s in score([small, large, rest])}

    # Both have a 50% raw rate; the one with ten times the evidence scores higher.
    assert scored["src/Large"].lift > scored["src/Small"].lift > 1.0
    assert scored["src/Large"].rank < scored["src/Small"].rank


def test_reverted_fixes_weigh_more_than_plain_fixes() -> None:
    base = [area("src/Rest", changes=300, fixes=15)]
    plain = {s.activity.area: s for s in score([*base, area("src/A", changes=30, fixes=4)])}
    reverted = {
        s.activity.area: s for s in score([*base, area("src/A", changes=30, fixes=4, reverted=3)])
    }

    assert reverted["src/A"].lift > plain["src/A"].lift


def test_each_repository_is_judged_against_its_own_rate() -> None:
    """A repository where everything is a bug fix must not flag every one of its areas."""
    noisy = [area(f"svc/M{n}", changes=20, fixes=10, repo="legacy") for n in range(4)]
    clean = [
        area("src/Calm", changes=100, fixes=2, repo="shop"),
        area("src/Odd", changes=30, fixes=9, repo="shop"),
    ]

    result = {(s.activity.repository_id, s.activity.area): s.band for s in score(noisy + clean)}

    assert all(band == "NORMAL" for (repo, _), band in result.items() if repo == "legacy")
    assert result[("shop", "src/Odd")] == "HOT" and result[("shop", "src/Calm")] == "NORMAL"


def test_no_fixes_anywhere_flags_nothing_and_ranks_are_stable() -> None:
    areas = [area("src/B", changes=50, fixes=0), area("src/A", changes=50, fixes=0)]

    scored = score(areas)

    assert [(s.activity.area, s.band, s.lift, s.rank) for s in scored] == [
        ("src/A", "NORMAL", 0.0, 1),
        ("src/B", "NORMAL", 0.0, 2),
    ]
    assert score(list(reversed(areas))) == scored  # input order does not change the ranking
    assert score([]) == []


def sweep_result() -> SweepResult:
    areas = score(
        [area("src/Printing", changes=40, fixes=12, reverted=2), area("src/Checkout", 300, 12)]
    )
    now = datetime(2026, 11, 10, 6, 0, tzinfo=UTC)
    return SweepResult(uuid.uuid4(), "schedule", 180, 2, now, now, tuple(areas))


def test_the_report_shows_the_evidence_behind_each_area() -> None:
    summary = report(sweep_result())

    assert (summary["areas_scored"], summary["areas_flagged"]) == (2, 1)
    assert summary["scoring_version"] == "hotspot-v1" and summary["window_days"] == 180
    top = summary["areas"][0]
    assert (top["area"], top["band"], top["defect_fixes"], top["reverted_fixes"]) == (
        "src/Printing",
        "HOT",
        12,
        2,
    )
    assert top["evidence"][0] == {
        "ticket_id": "100",
        "fix_kind": "pull_request",
        "fix_id": "200",
        "reverted": True,
    }
    assert json.loads(json.dumps(summary)) == summary  # the report is plain JSON


def test_the_cli_runs_on_demand_and_reports_a_concurrent_sweep(
    monkeypatch, capsys, test_settings
) -> None:
    triggers: list[str] = []
    results = [sweep_result(), None]

    def fake_sweep(settings, trigger):
        triggers.append(trigger)
        return results.pop(0)

    monkeypatch.setattr(cli, "get_settings", lambda: test_settings)  # never the developer's .env
    monkeypatch.setattr(cli, "configure_from_environment", lambda: None)
    monkeypatch.setattr(cli, "sweep_now", fake_sweep)

    assert cli.main(["--limit", "5"]) == 0
    printed = capsys.readouterr().out
    assert cli.main(["--scheduled"]) == 3  # another sweep holds the lock

    assert triggers == ["on_demand", "schedule"]
    assert "src/Printing" in printed and "HOT" in printed
    assert "ticket 100 -> PR 200 (reverted)" in printed
