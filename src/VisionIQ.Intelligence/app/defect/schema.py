"""S11-01: the fix request and fix result contracts (master design 8.6.1 to 8.6.4, 8.7).

The result is the contract between Intelligence, which runs the fix and stores what happened,
and the API, which reads it by id and opens the draft pull request. Every claim in it is backed
by a recorded runner execution: nothing in a result is green unless a run said so.
``contracts/fixes/*.schema.json`` are generated from these models (``python -m
app.defect.schema``) and a test fails when the two drift apart.
"""

from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

SCHEMA_VERSION = 1

FixState = Literal["PUBLISHED", "PARTIAL", "BLOCKED"]
# What the run established about the fix. Only VERIFIED means red on the baseline, green on the
# candidate and no known regression; it still needs native CI and an engineer's approval.
Verdict = Literal["VERIFIED", "FAIL", "INCONCLUSIVE", "NEEDS_REVIEW", "BLOCKED"]
# 8.6.3: the outcome of a test, kept apart from any approval.
Outcome = Literal[
    "PASS",
    "FAIL_TARGET_SIGNATURE",
    "FAIL_DIFFERENT_SIGNATURE",
    "FLAKY",
    "ENVIRONMENT_ERROR",
    "TIMEOUT",
    "NOT_RUN",
    "UNAVAILABLE",
    "INCONCLUSIVE",
]
Reproducibility = Literal[
    "REPRODUCED", "NOT_REPRODUCED", "UNAVAILABLE", "FLAKY", "ENVIRONMENT_ERROR", "INCONCLUSIVE"
]
GateStatus = Literal[
    "PASS", "FAIL", "NOT_RUN", "INCONCLUSIVE", "UNAVAILABLE", "REVIEW_REQUIRED", "NOT_APPLICABLE"
]
GateName = Literal[
    "reproduction",
    "candidate_test",
    "historical_regression",
    "touched_suite",
    "performance",
    "patch_policy",
    "native_ci",
    "human_review",
]


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class TicketRef(_Model):
    system: Literal["ado"] = "ado"
    project: str
    id: str
    revision: int = Field(ge=1)
    title: str = ""


class FixRequest(_Model):
    """What a fix run is asked to do. The baseline is a full commit SHA, never a branch name."""

    schema_version: Literal[1] = SCHEMA_VERSION
    run_id: uuid.UUID
    ticket: TicketRef
    repository_id: str = Field(pattern=r"^[A-Za-z0-9_.:/-]{1,256}$")
    base_commit_sha: str
    security_scope_id: str


class RunnerInfo(_Model):
    kind: Literal["local", "docker"]
    # A container image by digest (never a tag); None for the local runner.
    image: str | None = None
    os: str
    architecture: str
    network: Literal["none", "unrestricted"]
    filesystem: Literal["ephemeral_workspace"] = "ephemeral_workspace"


class ExecutionManifest(_Model):
    """8.6.3: written before generated code runs; every execution links to one manifest."""

    manifest_id: str = Field(pattern=r"^M[0-9]{1,3}$")
    ticket_id: str
    repository_id: str
    base_sha: str
    candidate_sha: str | None = None
    runner: RunnerInfo
    # Tool versions as the runner reports them, e.g. {"python": "Python 3.12.10"}.
    toolchain: dict[str, str] = Field(default_factory=dict)
    lock_hashes: dict[str, str] = Field(default_factory=dict)
    fixture_hashes: dict[str, str] = Field(default_factory=dict)
    dependency_restore: str = "none"
    # Names only: values are never recorded.
    environment_names: list[str] = Field(default_factory=list)
    timeouts_seconds: dict[str, float] = Field(default_factory=dict)
    commands: dict[str, list[str]] = Field(default_factory=dict)
    expected_test_ids: list[str] = Field(default_factory=list)
    policy_revision: str
    created_at: datetime
    sha256: str = Field(default="", pattern=r"^([0-9a-f]{64})?$")


class Execution(_Model):
    """8.6.1: one runner invocation with what it ran, on which commit, and where its log is."""

    execution_id: str = Field(pattern=r"^X[0-9]{1,4}$")
    manifest_id: str
    purpose: Literal["reproduction", "candidate", "regression", "suite", "benchmark"]
    code: Literal["baseline", "candidate"]
    commit_sha: str
    command: list[str]
    started_at: datetime
    duration_ms: int = Field(ge=0)
    exit_code: int | None
    timed_out: bool = False
    outcome: Outcome
    test_id: str | None = None
    failure_signature: str | None = None
    log_uri: str
    log_sha256: str = Field(pattern=r"^[0-9a-f]{64}$")


class CheckEvidence(_Model):
    """One check: every execution made for it under the rerun policy, and the result.

    ``first_outcome`` is what the first execution said. Reruns never replace it: a test that
    failed once and then passed is FLAKY, not PASS.
    """

    test_path: str | None
    test_id: str | None
    outcome: Outcome
    first_outcome: Outcome
    execution_ids: list[str] = Field(min_length=1)
    failure_signature: str | None = None


class ExpectedFailure(_Model):
    """The failure the test's author predicted for the baseline, stated before it ran."""

    exception: str | None = None
    message_contains: list[str] = Field(min_length=1, max_length=3)


class GeneratedTest(_Model):
    path: str
    test_id: str
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    content: str
    rationale: str = ""


class Reproduction(_Model):
    """DEF-01: was the defect shown on the unchanged baseline, for the reported reason?"""

    reproducibility: Reproducibility
    test: GeneratedTest | None = None
    expected_failure: ExpectedFailure | None = None
    evidence: CheckEvidence | None = None
    # Why there is no usable red: stated by the model (UNAVAILABLE) or by the runner.
    detail: str = ""


class FileChange(_Model):
    """One changed file with its complete new content, so the change can be recreated."""

    path: str
    change: Literal["added", "modified"]
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    added_lines: int = Field(ge=0)
    removed_lines: int = Field(ge=0)
    content: str


class PolicyFinding(_Model):
    """S11-06: one rule the patch ran into. ``review`` stops the run for an engineer."""

    code: str = Field(pattern=r"^[a-z_]{1,40}$")
    severity: Literal["block", "review"]
    path: str | None = None
    detail: str


class Patch(_Model):
    """DEF-02: the proposed change. ``files`` exclude the reproduction test."""

    iteration: int = Field(ge=1)
    candidate_sha: str | None = None
    # The Git tree of the candidate: the identity of its content. A commit recreated from
    # ``files`` has another commit SHA but the same tree, so the results still apply to it.
    tree_sha: str | None = None
    branch: str | None = None
    # A git bundle of the candidate commit (baseline..branch), for a push that keeps the SHA.
    bundle_uri: str | None = None
    bundle_sha256: str | None = None
    files: list[FileChange]
    diff: str
    diff_truncated: bool = False
    cause: str
    summary: str
    policy_status: Literal["PASS", "REVIEW_REQUIRED", "BLOCKED"]
    findings: list[PolicyFinding] = Field(default_factory=list)


class Attempt(_Model):
    """One iteration of the budget, kept whether it worked or not."""

    iteration: int = Field(ge=1)
    stage: Literal["reproduction_test", "patch"]
    candidate_sha: str | None = None
    result: str = Field(pattern=r"^[a-z_]{1,60}$")
    detail: str = ""


class RegressionTest(_Model):
    """4.5.1: a test from the area's history, how it was found and what it did on the fix."""

    test_path: str
    # explicit: declared ticket/test link. historical: changed by a linked past fix.
    # inferred: found by naming only; never proof on its own.
    tier: Literal["explicit", "historical", "inferred"]
    discovery_method: str
    ticket_id: str | None = None
    fix_reference: str | None = None
    availability: Literal["AVAILABLE", "UNAVAILABLE"]
    outcome: Outcome
    # Run on the baseline only when the candidate failed, to tell a regression from a test
    # that was already failing.
    baseline_outcome: Outcome | None = None
    execution_ids: list[str] = Field(default_factory=list)
    failure_signature: str | None = None


class Regression(_Model):
    """DEF-04. ``historical_found`` False is the stated HISTORICAL_TEST_NOT_FOUND."""

    status: Literal["PASS", "FAIL", "INCONCLUSIVE", "NOT_FOUND", "NOT_RUN"]
    historical_found: bool
    tests: list[RegressionTest] = Field(default_factory=list)
    detail: str = ""


class Performance(_Model):
    """8.7: baseline against candidate on the same workload, in paired, repeated runs."""

    status: Literal["NO_REGRESSION_DEMONSTRATED", "REGRESSION", "INCONCLUSIVE", "NOT_MEASURED"]
    metric: str | None = None
    unit: str | None = None
    pairs: int = 0
    baseline_samples: list[float] = Field(default_factory=list)
    candidate_samples: list[float] = Field(default_factory=list)
    baseline_median: float | None = None
    candidate_median: float | None = None
    change_percent: float | None = None
    noise_percent: float | None = None
    threshold_percent: float | None = None
    noise_limit_percent: float | None = None
    execution_ids: list[str] = Field(default_factory=list)
    detail: str = ""


class Gate(_Model):
    """One piece of evidence, kept separate from the others (8.6.1)."""

    name: GateName
    required: bool
    status: GateStatus
    # The commit the result is about. A result for another commit says nothing about the head.
    commit_sha: str | None = None
    detail: str
    execution_ids: list[str] = Field(default_factory=list)


class GateSummary(_Model):
    head_sha: str | None
    overall: Literal["GREEN", "RED", "NOT_GREEN"]
    blocking: list[str] = Field(default_factory=list)


class PullRequestCheck(_Model):
    """S13-06: one PR status, with the executions and logs it stands for."""

    name: str
    state: Literal["succeeded", "failed", "pending", "not_applicable"]
    description: str
    execution_ids: list[str] = Field(default_factory=list)


class PullRequestDraft(_Model):
    """DEF-05: everything needed to open the draft PR. Intelligence never opens it."""

    source_branch: str
    target_branch: str
    base_sha: str
    head_sha: str
    work_item_id: str
    title: str
    description: str
    is_draft: Literal[True] = True
    checks: list[PullRequestCheck]


class Generation(_Model):
    routing_revision: str
    test_provider: str | None = None
    test_prompt_version: str | None = None
    fix_provider: str | None = None
    fix_model_id: str | None = None
    fix_prompt_version: str | None = None
    calls: int = 0
    input_tokens: int = 0
    output_tokens: int = 0


class Timings(_Model):
    reproduction_ms: int = Field(default=0, ge=0)
    fix_ms: int = Field(default=0, ge=0)
    total_ms: int = Field(default=0, ge=0)


class FixResult(_Model):
    schema_version: Literal[1] = SCHEMA_VERSION
    fix_id: uuid.UUID
    run_id: uuid.UUID
    ticket: TicketRef
    repository_id: str
    base_sha: str
    candidate_sha: str | None = None
    state: FixState
    verdict: Verdict
    reason_codes: list[str] = Field(default_factory=list)
    reproduction: Reproduction
    patch: Patch | None = None
    attempts: list[Attempt] = Field(default_factory=list)
    candidate_test: CheckEvidence | None = None
    regression: Regression
    suite: CheckEvidence | None = None
    performance: Performance
    gates: list[Gate]
    gate_summary: GateSummary
    manifests: list[ExecutionManifest] = Field(default_factory=list)
    executions: list[Execution] = Field(default_factory=list)
    pull_request: PullRequestDraft | None = None
    limitations: list[str] = Field(default_factory=list)
    generation: Generation
    policy_revision: str | None = None
    timings: Timings

    def execution_ids(self) -> list[str]:
        return [execution.execution_id for execution in self.executions]


_CONTRACTS = {
    "fix-request.v1.schema.json": (FixRequest, "Fix request v1 (S11-01)"),
    "fix-result.v1.schema.json": (FixResult, "Fix result v1 (S11-01)"),
}


def json_schema(name: str) -> dict:
    model, title = _CONTRACTS[name]
    schema = model.model_json_schema()
    schema["$schema"] = "https://json-schema.org/draft/2020-12/schema"
    schema["$id"] = f"https://visioniq.internal/schemas/fixes/{name}"
    schema["title"] = title
    return schema


def schema_text(name: str) -> str:
    return json.dumps(json_schema(name), indent=2, sort_keys=True) + "\n"


def schema_files(start: Path | None = None) -> dict[str, Path]:
    for directory in (start or Path(__file__).resolve()).parents:
        if (directory / "contracts" / "events").is_dir():
            return {name: directory / "contracts" / "fixes" / name for name in _CONTRACTS}
    raise FileNotFoundError("contracts directory not found")


if __name__ == "__main__":  # regenerate the committed schemas after changing the models
    for name, target in schema_files().items():
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(schema_text(name), encoding="utf-8", newline="\n")
        sys.stdout.write(f"wrote {target}\n")
