"""S15-12 / S11-17 / S13-06: green means green, a new commit invalidates results, PR checks."""

import pytest

from app.defect import gates
from app.defect.schema import (
    CheckEvidence,
    FileChange,
    Patch,
    Performance,
    Regression,
    Reproduction,
)

HEAD = "a" * 40
NEXT = "b" * 40


def evidence(outcome: str = "PASS", first: str | None = None) -> CheckEvidence:
    return CheckEvidence(
        test_path="tests/test_x.py",
        test_id="test_x",
        outcome=outcome,  # type: ignore[arg-type]
        first_outcome=first or outcome,  # type: ignore[arg-type]
        execution_ids=["X1"],
    )


def patch(status: str = "PASS") -> Patch:
    change = FileChange(
        path="src/a.py",
        change="modified",
        sha256="0" * 64,
        added_lines=1,
        removed_lines=1,
        content="x",
    )
    return Patch(
        iteration=1,
        candidate_sha=HEAD,
        branch="visioniq/fix/1-abc",
        files=[change],
        diff="",
        cause="",
        summary="",
        policy_status=status,  # type: ignore[arg-type]
    )


def build(
    reproducibility: str = "REPRODUCED",
    candidate: CheckEvidence | None = None,
    regression: str = "PASS",
    suite: CheckEvidence | None = None,
    performance: str = "NO_REGRESSION_DEMONSTRATED",
    policy: str = "PASS",
    suite_configured: bool = True,
):
    red = evidence("FAIL_TARGET_SIGNATURE") if reproducibility != "UNAVAILABLE" else None
    return gates.build(
        Reproduction(reproducibility=reproducibility, evidence=red),  # type: ignore[arg-type]
        patch(policy),
        candidate or evidence(),
        Regression(status=regression, historical_found=True, detail="d"),  # type: ignore[arg-type]
        suite or evidence(),
        suite_configured,
        Performance(status=performance, detail="d"),  # type: ignore[arg-type]
        HEAD,
    )


def after_ci_and_review(built):
    """What the API does once the pull request exists: the platform's own results."""
    built = gates.record(built, "native_ci", "PASS", HEAD, "Build 812 succeeded.")
    return gates.record(built, "human_review", "PASS", HEAD, "Approved by an engineer.")


def status(built, name: str) -> str:
    return next(gate.status for gate in built if gate.name == name)


def test_a_fresh_result_is_never_green_ci_and_the_engineer_are_still_open() -> None:
    summary = gates.summarize(build(), HEAD)

    assert summary.overall == "NOT_GREEN"
    assert summary.blocking == ["native_ci", "human_review"]


def test_green_needs_every_required_gate_to_pass_on_the_head_commit() -> None:
    assert gates.summarize(after_ci_and_review(build()), HEAD).overall == "GREEN"


@pytest.mark.parametrize(
    ("change", "gate", "gate_status", "overall"),
    [
        ({"candidate": evidence("FAIL_TARGET_SIGNATURE")}, "candidate_test", "FAIL", "RED"),
        (
            {"candidate": evidence("FLAKY", "FAIL_TARGET_SIGNATURE")},
            "candidate_test",
            "INCONCLUSIVE",
            "NOT_GREEN",
        ),
        ({"candidate": evidence("TIMEOUT")}, "candidate_test", "INCONCLUSIVE", "NOT_GREEN"),
        (
            {"candidate": evidence("ENVIRONMENT_ERROR")},
            "candidate_test",
            "INCONCLUSIVE",
            "NOT_GREEN",
        ),
        ({"candidate": evidence("NOT_RUN")}, "candidate_test", "NOT_RUN", "NOT_GREEN"),
        ({"reproducibility": "NOT_REPRODUCED"}, "reproduction", "FAIL", "RED"),
        ({"reproducibility": "FLAKY"}, "reproduction", "INCONCLUSIVE", "NOT_GREEN"),
        ({"reproducibility": "UNAVAILABLE"}, "reproduction", "UNAVAILABLE", "NOT_GREEN"),
        ({"regression": "FAIL"}, "historical_regression", "FAIL", "RED"),
        ({"regression": "INCONCLUSIVE"}, "historical_regression", "INCONCLUSIVE", "NOT_GREEN"),
        ({"regression": "NOT_RUN"}, "historical_regression", "NOT_RUN", "NOT_GREEN"),
        ({"suite": evidence("INCONCLUSIVE")}, "touched_suite", "INCONCLUSIVE", "NOT_GREEN"),
        ({"performance": "REGRESSION"}, "performance", "FAIL", "RED"),
        ({"performance": "INCONCLUSIVE"}, "performance", "INCONCLUSIVE", "NOT_GREEN"),
        ({"policy": "REVIEW_REQUIRED"}, "patch_policy", "REVIEW_REQUIRED", "NOT_GREEN"),
        ({"policy": "BLOCKED"}, "patch_policy", "FAIL", "RED"),
    ],
)
def test_a_required_gate_that_did_not_pass_is_never_shown_as_green(
    change: dict, gate: str, gate_status: str, overall: str
) -> None:
    # Even with native CI green and an engineer's approval.
    built = after_ci_and_review(build(**change))
    summary = gates.summarize(built, HEAD)

    assert status(built, gate) == gate_status
    assert summary.overall == overall and gate in summary.blocking
    check = next(
        c for c in gates.pull_request_checks(built, HEAD) if c.name.endswith(_SUFFIX[gate])
    )
    assert check.state != "succeeded"


_SUFFIX = {
    "reproduction": "/reproduction",
    "candidate_test": "/fix-test",
    "historical_regression": "/historical-regression",
    "touched_suite": "/touched-suite",
    "performance": "/performance",
    "patch_policy": "/patch-policy",
}


def test_failing_native_ci_blocks_green_whatever_the_sandbox_said() -> None:
    built = gates.record(build(), "native_ci", "FAIL", HEAD, "Build 813 failed.")
    built = gates.record(built, "human_review", "PASS", HEAD, "Approved.")

    assert gates.summarize(built, HEAD).overall == "RED"


def test_what_is_stated_as_absent_does_not_block_but_is_not_called_passed() -> None:
    built = after_ci_and_review(
        build(regression="NOT_FOUND", performance="NOT_MEASURED", suite_configured=False)
    )

    assert gates.summarize(built, HEAD).overall == "GREEN"
    assert status(built, "historical_regression") == "UNAVAILABLE"
    assert status(built, "performance") == "NOT_APPLICABLE"
    states = {check.name: check.state for check in gates.pull_request_checks(built, HEAD)}
    assert states["visioniq/historical-regression"] == "pending"
    assert states["visioniq/performance"] == "not_applicable"


def test_a_new_commit_invalidates_every_earlier_result() -> None:
    built = after_ci_and_review(build())
    assert gates.summarize(built, HEAD).overall == "GREEN"

    summary = gates.summarize(built, NEXT)  # someone pushed to the PR

    assert summary.overall == "NOT_GREEN" and summary.head_sha == NEXT
    assert set(summary.blocking) == {
        "reproduction",
        "candidate_test",
        "historical_regression",
        "touched_suite",
        "performance",
        "patch_policy",
        "native_ci",
        "human_review",
    }
    stale = gates.effective(built[1], NEXT)
    assert stale.status == "NOT_RUN" and HEAD[:12] in stale.detail and NEXT[:12] in stale.detail
    assert all(c.state == "pending" for c in gates.pull_request_checks(built, NEXT))
    assert built[1].status == "PASS"  # the stored result itself is not rewritten


def test_results_measured_again_on_the_new_commit_count_again() -> None:
    built = [
        gate.model_copy(update={"commit_sha": NEXT}) if gate.commit_sha else gate
        for gate in after_ci_and_review(build())
    ]
    built = gates.record(built, "native_ci", "PASS", NEXT, "Build 814 succeeded.")
    built = gates.record(built, "human_review", "PASS", NEXT, "Approved.")

    assert gates.summarize(built, NEXT).overall == "GREEN"


def test_pr_checks_cover_the_sandbox_evidence_and_link_its_runs() -> None:
    checks = gates.pull_request_checks(build(), HEAD)

    assert [check.name for check in checks] == [
        "visioniq/reproduction",
        "visioniq/fix-test",
        "visioniq/historical-regression",
        "visioniq/touched-suite",
        "visioniq/performance",
        "visioniq/patch-policy",
    ]  # native CI and the review belong to the platform; the bot posts neither
    assert all(check.state == "succeeded" for check in checks)
    assert checks[0].execution_ids == ["X1"] and checks[0].description.startswith("PASS:")
