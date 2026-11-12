"""PROP-07: activity-adjusted hotspot scores (master design 3.4, 7.5).

A busy area collects more fixes simply because more happens there. So an area is judged by its
incident RATE (defect fixes per recorded change), not its incident count, and compared with the
rate of its whole repository:

    incidents     = defect fixes + extra weight for each fix that was later reverted
    smoothed rate = (incidents + PRIOR * repository rate) / (changes + PRIOR)
    lift          = smoothed rate / repository rate          (1.0 = like the rest of the repository)

The smoothing pulls areas with little history towards the repository average, so three fixes in
four changes do not outrank thirty in sixty. The band also needs a minimum number of fixes, and
an area with too few changes is INSUFFICIENT_DATA rather than "normal". Scores rank areas for
attention; they are not failure probabilities.
"""

from __future__ import annotations

from collections import defaultdict
from collections.abc import Sequence
from dataclasses import dataclass

SCORING_VERSION = "hotspot-v1"
# Strength of the pull towards the repository average, in "changes".
PRIOR_CHANGES = 10
# A reverted fix is stronger evidence than a fix (7.5): it counts this many incidents.
REVERT_WEIGHT = 2
MIN_CHANGES = 5
HOT_LIFT, HOT_MIN_FIXES = 1.5, 3
WATCH_LIFT, WATCH_MIN_FIXES = 1.2, 2
_ORDER = {"HOT": 0, "WATCH": 1, "NORMAL": 2, "INSUFFICIENT_DATA": 3}
ROOT_AREA = "(root)"


@dataclass(frozen=True)
class AreaActivity:
    """What happened in one area of one repository during the window."""

    source_instance: str
    project_id: str
    repository_id: str
    area: str
    changes: int
    defect_fixes: int
    reverted_fixes: int
    # The fixes behind the counts: {"ticket_id", "fix_kind", "fix_id", "reverted"}.
    evidence: tuple[dict, ...] = ()


@dataclass(frozen=True)
class ScoredArea:
    activity: AreaActivity
    lift: float
    band: str
    rank: int


def area_of(path: str, depth: int) -> str:
    """The first ``depth`` directories of a path: ("src/Pay/Retry.cs", 2) -> "src/Pay"."""
    directories = [part for part in path.replace("\\", "/").split("/") if part][:-1]
    return "/".join(directories[:depth]) or ROOT_AREA


def score(areas: Sequence[AreaActivity]) -> list[ScoredArea]:
    """Score every area against its own repository and rank them, most concerning first."""
    repositories: dict[tuple[str, str, str], list[AreaActivity]] = defaultdict(list)
    for area in areas:
        repositories[(area.source_instance, area.project_id, area.repository_id)].append(area)

    scored: list[tuple[AreaActivity, float, str]] = []
    for members in repositories.values():
        changes = sum(area.changes for area in members)
        incidents = sum(_incidents(area) for area in members)
        baseline = incidents / changes if changes else 0.0
        for area in members:
            if baseline == 0.0:
                lift = 0.0
            else:
                smoothed = (_incidents(area) + PRIOR_CHANGES * baseline) / (
                    area.changes + PRIOR_CHANGES
                )
                lift = round(smoothed / baseline, 3)
            scored.append((area, lift, _band(area, lift)))

    scored.sort(key=lambda entry: (_ORDER[entry[2]], -entry[1], _identity(entry[0])))
    return [
        ScoredArea(area, lift, band, rank)
        for rank, (area, lift, band) in enumerate(scored, start=1)
    ]


def _incidents(area: AreaActivity) -> int:
    return area.defect_fixes + (REVERT_WEIGHT - 1) * area.reverted_fixes


def _band(area: AreaActivity, lift: float) -> str:
    if area.changes < MIN_CHANGES:
        return "INSUFFICIENT_DATA"
    if lift >= HOT_LIFT and area.defect_fixes >= HOT_MIN_FIXES:
        return "HOT"
    if lift >= WATCH_LIFT and area.defect_fixes >= WATCH_MIN_FIXES:
        return "WATCH"
    return "NORMAL"


def _identity(area: AreaActivity) -> tuple[str, str, str, str]:
    return (area.source_instance, area.project_id, area.repository_id, area.area)
