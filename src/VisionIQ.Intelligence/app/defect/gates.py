"""S15-12 / S11-17 / S13-06: honest gates (master design 8.6.1, 8.6.3, 11.1.1).

Sandbox reproduction, the candidate's test, historical regressions, the touched suite,
performance, patch policy, native CI and the engineer's review are separate pieces of evidence.
The rules here are the whole of "green means green":

* GREEN needs every required gate to be PASS. NOT_RUN, INCONCLUSIVE, UNAVAILABLE and
  REVIEW_REQUIRED are never shown as passed.
* A result belongs to the commit it was measured on. After a new commit, every earlier result
  is stale and counts as NOT_RUN until it is measured again.
* Native CI and the engineer's decision are not known inside Intelligence, so a fresh result is
  never GREEN: the API fills those two in from the pull request.
"""

from __future__ import annotations

from collections.abc import Sequence

from app.defect.schema import (
    CheckEvidence,
    Gate,
    GateName,
    GateStatus,
    GateSummary,
    Patch,
    Performance,
    PullRequestCheck,
    Regression,
    Reproduction,
)

_FROM_OUTCOME: dict[str, GateStatus] = {
    "PASS": "PASS",
    "FAIL_TARGET_SIGNATURE": "FAIL",
    "FAIL_DIFFERENT_SIGNATURE": "FAIL",
    "FLAKY": "INCONCLUSIVE",
    "ENVIRONMENT_ERROR": "INCONCLUSIVE",
    "TIMEOUT": "INCONCLUSIVE",
    "INCONCLUSIVE": "INCONCLUSIVE",
    "NOT_RUN": "NOT_RUN",
    "UNAVAILABLE": "UNAVAILABLE",
}
_CHECK_NAMES: dict[str, str] = {
    "reproduction": "visioniq/reproduction",
    "candidate_test": "visioniq/fix-test",
    "historical_regression": "visioniq/historical-regression",
    "touched_suite": "visioniq/touched-suite",
    "performance": "visioniq/performance",
    "patch_policy": "visioniq/patch-policy",
}


def _reproduction(reproduction: Reproduction, head: str | None) -> Gate:
    evidence = reproduction.evidence
    reproduced = reproduction.reproducibility == "REPRODUCED"
    if reproduced:
        status: GateStatus = "PASS"
        detail = "The test failed on the baseline with the predicted failure."
    elif reproduction.reproducibility == "UNAVAILABLE":
        status, detail = "UNAVAILABLE", "The defect cannot be reproduced in the sandbox."
    elif reproduction.reproducibility == "NOT_REPRODUCED":
        status, detail = "FAIL", "The test did not fail on the baseline: no red was shown."
    elif evidence is None:
        status, detail = "NOT_RUN", "No reproduction test was run."
    else:
        status = "INCONCLUSIVE"
        detail = f"The baseline run ended {evidence.outcome}, which does not establish a red."
    return Gate(
        name="reproduction",
        required=True,
        status=status,
        commit_sha=head,
        detail=detail,
        execution_ids=evidence.execution_ids if evidence else [],
    )


def _check(
    name: GateName, evidence: CheckEvidence | None, head: str | None, missing: str, required: bool
) -> Gate:
    if evidence is None:
        return Gate(name=name, required=required, status="NOT_RUN", commit_sha=head, detail=missing)
    status = _FROM_OUTCOME[evidence.outcome]
    detail = f"Outcome {evidence.outcome}"
    if evidence.first_outcome != evidence.outcome:
        detail += f" (first run: {evidence.first_outcome})"
    return Gate(
        name=name,
        required=required,
        status=status,
        commit_sha=head,
        detail=detail + ".",
        execution_ids=evidence.execution_ids,
    )


def _regression(regression: Regression, head: str | None) -> Gate:
    status: GateStatus = {
        "PASS": "PASS",
        "FAIL": "FAIL",
        "INCONCLUSIVE": "INCONCLUSIVE",
        "NOT_FOUND": "UNAVAILABLE",
        "NOT_RUN": "NOT_RUN",
    }[regression.status]
    return Gate(
        name="historical_regression",
        # With no runnable old test there is nothing to require; the gate still says so.
        required=regression.status != "NOT_FOUND",
        status=status,
        commit_sha=head,
        detail=regression.detail,
        execution_ids=[i for test in regression.tests for i in test.execution_ids],
    )


def _performance(performance: Performance, head: str | None) -> Gate:
    status: GateStatus = {
        "NO_REGRESSION_DEMONSTRATED": "PASS",
        "REGRESSION": "FAIL",
        "INCONCLUSIVE": "INCONCLUSIVE",
        "NOT_MEASURED": "NOT_APPLICABLE",
    }[performance.status]
    return Gate(
        name="performance",
        required=performance.status != "NOT_MEASURED",
        status=status,
        commit_sha=head,
        detail=performance.detail,
        execution_ids=performance.execution_ids,
    )


def _policy(patch: Patch | None, head: str | None) -> Gate:
    if patch is None:
        return Gate(
            name="patch_policy",
            required=True,
            status="NOT_RUN",
            commit_sha=head,
            detail="No patch was proposed.",
        )
    status: GateStatus = {"PASS": "PASS", "REVIEW_REQUIRED": "REVIEW_REQUIRED", "BLOCKED": "FAIL"}[
        patch.policy_status
    ]
    codes = ", ".join(sorted({finding.code for finding in patch.findings}))
    return Gate(
        name="patch_policy",
        required=True,
        status=status,
        commit_sha=head,
        detail="The change is inside the repository's policy."
        if not codes
        else f"Rules hit: {codes}.",
    )


def build(
    reproduction: Reproduction,
    patch: Patch | None,
    candidate_test: CheckEvidence | None,
    regression: Regression,
    suite: CheckEvidence | None,
    suite_configured: bool,
    performance: Performance,
    head_sha: str | None,
) -> list[Gate]:
    """The gates of a fresh result. CI and review are always still open at this point."""
    return [
        _reproduction(reproduction, head_sha),
        _check("candidate_test", candidate_test, head_sha, "No candidate was tested.", True),
        _regression(regression, head_sha),
        _check(
            "touched_suite",
            suite,
            head_sha,
            "The touched project's suite was not run."
            if suite_configured
            else "No suite command is configured for this repository.",
            suite_configured,
        ),
        _performance(performance, head_sha),
        _policy(patch, head_sha),
        Gate(
            name="native_ci",
            required=True,
            status="NOT_RUN",
            detail="The repository's own pipeline runs on the pull request, after it exists.",
        ),
        Gate(
            name="human_review",
            required=True,
            status="REVIEW_REQUIRED",
            detail="An engineer approves or rejects. The bot cannot approve or merge.",
        ),
    ]


def effective(gate: Gate, head_sha: str | None) -> Gate:
    """The gate as it stands for ``head_sha``: a result for another commit is stale (S11-17)."""
    if gate.commit_sha is None or head_sha is None or gate.commit_sha == head_sha:
        return gate
    return gate.model_copy(
        update={
            "status": "NOT_RUN",
            "detail": f"Stale: measured on {gate.commit_sha[:12]}, the head is now "
            f"{head_sha[:12]}.",
        }
    )


def summarize(gates: Sequence[Gate], head_sha: str | None) -> GateSummary:
    current = [effective(gate, head_sha) for gate in gates]
    required = [gate for gate in current if gate.required]
    blocking = [gate.name for gate in required if gate.status != "PASS"]
    if any(gate.status == "FAIL" for gate in required):
        overall = "RED"
    elif not blocking and required:
        overall = "GREEN"
    else:
        overall = "NOT_GREEN"
    return GateSummary(head_sha=head_sha, overall=overall, blocking=blocking)  # type: ignore[arg-type]


def record(
    gates: Sequence[Gate], name: GateName, status: GateStatus, commit_sha: str | None, detail: str
) -> list[Gate]:
    """The gates with one result replaced, e.g. native CI reported on the pull request."""
    return [
        gate.model_copy(update={"status": status, "commit_sha": commit_sha, "detail": detail})
        if gate.name == name
        else gate
        for gate in gates
    ]


def pull_request_checks(gates: Sequence[Gate], head_sha: str | None) -> list[PullRequestCheck]:
    """S13-06: the PR statuses VisionIQ posts. Anything short of PASS is never 'succeeded'."""
    checks = []
    for gate in (effective(gate, head_sha) for gate in gates):
        name = _CHECK_NAMES.get(gate.name)
        if name is None:
            continue  # native CI and the review are the platform's own, not ours to post
        if gate.status == "PASS":
            state = "succeeded"
        elif gate.status == "FAIL":
            state = "failed"
        elif gate.status == "NOT_APPLICABLE":
            state = "not_applicable"
        else:
            state = "pending"
        checks.append(
            PullRequestCheck(
                name=name,
                state=state,  # type: ignore[arg-type]
                description=f"{gate.status}: {gate.detail}"[:400],
                execution_ids=gate.execution_ids,
            )
        )
    return checks
