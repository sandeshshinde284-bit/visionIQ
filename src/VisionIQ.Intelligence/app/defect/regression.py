"""S11-08: find the area's historical regression tests (master design 4.5, 4.5.1).

Tests are found through the ticket -> fix -> changed-test links recorded in LEARN, and ranked by
how the link was made. A test found only because its name resembles the changed file is listed
as INFERRED: it is run, but it is never called historical proof. A test that no longer exists at
the baseline is UNAVAILABLE. Nothing is invented when no test is found.
"""

from __future__ import annotations

import re
from collections.abc import Collection, Sequence
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import Literal

from app.defect.policy import RepositoryPolicy, matches
from app.defect.schema import Regression, RegressionTest
from app.defect.sources import HistoricalFix

Tier = Literal["explicit", "historical", "inferred"]
_TIER_ORDER: dict[str, int] = {"explicit": 0, "historical": 1, "inferred": 2}
_TEST_AFFIXES = re.compile(r"(?i)^(test_?|tests_?)|(_?tests?|\.tests?|\.spec|_spec)$")


@dataclass(frozen=True)
class RegressionCandidate:
    test_path: str
    tier: Tier
    discovery_method: str
    ticket_id: str | None
    fix_reference: str | None
    available: bool


def stem(path: str) -> str:
    """The subject of a file name: ``tests/test_retry_policy.py`` -> ``retry_policy``."""
    name = PurePosixPath(path).name.split(".", 1)[0]
    return _TEST_AFFIXES.sub("", name).lower()


def discover(
    fixes: Sequence[HistoricalFix],
    baseline_files: Collection[str],
    changed_paths: Sequence[str],
    policy: RepositoryPolicy,
    exclude: Collection[str] = (),
    limit: bool = True,
) -> list[RegressionCandidate]:
    """Candidates for replay, best evidence first, at most the repository's limit (or all of
    them with ``limit=False``, for a caller that reports how many it left out)."""
    found: dict[str, RegressionCandidate] = {}

    def add(candidate: RegressionCandidate) -> None:
        current = found.get(candidate.test_path)
        if current is None or _TIER_ORDER[candidate.tier] < _TIER_ORDER[current.tier]:
            found[candidate.test_path] = candidate

    for fix in fixes:
        for raw in fix.paths:
            path = raw.lstrip("/")
            if path in exclude or not matches(path, policy.test_file_patterns):
                continue
            # A test changed by a fix whose ticket link was itself inferred is inferred too.
            explicit = fix.link == "explicit"
            add(
                RegressionCandidate(
                    path,
                    "historical" if explicit else "inferred",
                    "test_changed_by_linked_fix" if explicit else "test_changed_by_inferred_fix",
                    fix.ticket_id,
                    fix.fix_reference,
                    path in baseline_files,
                )
            )

    subjects = {stem(path) for path in changed_paths} - {""}
    for path in sorted(baseline_files):
        if path in exclude or not matches(path, policy.test_file_patterns):
            continue
        if stem(path) in subjects:
            add(
                RegressionCandidate(
                    path, "inferred", "test_name_matches_changed_file", None, None, True
                )
            )

    ordered = sorted(
        found.values(), key=lambda c: (_TIER_ORDER[c.tier], not c.available, c.test_path)
    )
    return ordered[: policy.limits.max_regression_tests] if limit else ordered


_FAILED = ("FAIL_TARGET_SIGNATURE", "FAIL_DIFFERENT_SIGNATURE")
NOT_FOUND = "HISTORICAL_TEST_NOT_FOUND"


def summarize(tests: Sequence[RegressionTest]) -> Regression:
    """S11-09: what the replay showed. A regression is a test that passes on the baseline and
    fails on the candidate; a test that fails on both proves nothing about the change."""
    runnable = [test for test in tests if test.availability == "AVAILABLE"]
    historical = [test for test in runnable if test.tier != "inferred"]
    missing = len(tests) - len(runnable)
    gone = f" {missing} recorded test(s) no longer exist at the baseline." if missing else ""
    if not runnable:
        return Regression(
            status="NOT_FOUND",
            historical_found=False,
            tests=list(tests),
            detail=f"{NOT_FOUND}: no runnable regression test was found for this area.{gone}",
        )
    broke = [t for t in runnable if t.outcome in _FAILED and t.baseline_outcome == "PASS"]
    if broke:
        paths = ", ".join(test.test_path for test in broke[:5])
        return Regression(
            status="FAIL",
            historical_found=bool(historical),
            tests=list(tests),
            detail=f"{len(broke)} test(s) pass on the baseline and fail on the candidate: {paths}.",
        )
    unclear = [test for test in runnable if test.outcome != "PASS"]
    if unclear:
        parts = ", ".join(f"{test.test_path} ({test.outcome})" for test in unclear[:5])
        return Regression(
            status="INCONCLUSIVE",
            historical_found=bool(historical),
            tests=list(tests),
            detail=f"{len(unclear)} test(s) gave no clear result on the candidate: {parts}.{gone}",
        )
    inferred = len(runnable) - len(historical)
    note = "" if historical else f" None is linked to a past fix ({NOT_FOUND})."
    return Regression(
        status="PASS",
        historical_found=bool(historical),
        tests=list(tests),
        detail=f"{len(runnable)} test(s) passed on the candidate: {len(historical)} linked to "
        f"past fixes, {inferred} found by name only.{note}{gone}",
    )
