"""S11-14: the draft pull request's evidence trail (master design 8.6.1, 8.6.2).

Defect -> reproduction -> cause -> fix -> verification, with the commit SHAs, test ids, runner
and logs each statement rests on. The text is built from the stored result only: a gate that
did not pass is written as it is. Azure DevOps limits a PR description to 4000 characters, so
the longest part (the execution list) is cut first and the reader is pointed to the stored
result for the rest.
"""

from __future__ import annotations

import re

from app.defect import gates as gate_rules
from app.defect.schema import (
    Execution,
    FixResult,
    Gate,
    PullRequestDraft,
)

# Azure DevOps allows 4000; the API adds one line naming the commit it really pushed.
MAX_DESCRIPTION_CHARS = 3800
MAX_TITLE_CHARS = 200
# Characters that start Markdown, a link, an image, HTML, a mention or a work-item / PR reference.
_MARKUP = re.compile(r"([\\`*_{}\[\]()<>#!|@~])")


def _plain(text: str) -> str:
    """Text from a ticket or a model as plain words in Markdown.

    The pull request is written by the bot, so nothing a ticket author or a model wrote may
    become a link, an image (which a browser would fetch), a mention that notifies someone or a
    reference to another work item.
    """
    return _MARKUP.sub(r"\\\1", " ".join(text.split()))


_MARK = {
    "PASS": "PASS",
    "FAIL": "FAIL",
    "NOT_RUN": "NOT RUN",
    "INCONCLUSIVE": "INCONCLUSIVE",
    "UNAVAILABLE": "UNAVAILABLE",
    "REVIEW_REQUIRED": "REVIEW REQUIRED",
    "NOT_APPLICABLE": "not applicable",
}
_GATE_LABEL = {
    "reproduction": "Defect reproduced on the baseline",
    "candidate_test": "Same test passes on the fix",
    "historical_regression": "Past regression tests",
    "touched_suite": "Touched project's suite (sandbox)",
    "performance": "Performance of the touched path",
    "patch_policy": "Patch policy",
    "native_ci": "Native CI on this PR",
    "human_review": "Engineer review",
}


def _short(sha: str | None) -> str:
    return sha[:12] if sha else "none"


def _cell(text: str) -> str:
    return " ".join(text.replace("|", "/").split())


def _gate_row(gate: Gate) -> str:
    runs = ", ".join(gate.execution_ids[:6]) or "-"
    need = "required" if gate.required else "informational"
    return (
        f"| {_GATE_LABEL[gate.name]} | **{_MARK[gate.status]}** ({need}) | "
        f"{_cell(gate.detail)} | {runs} |"
    )


def _execution_row(execution: Execution) -> str:
    exit_code = "timeout" if execution.timed_out else execution.exit_code
    return (
        f"| {execution.execution_id} | {execution.purpose} | {execution.code} "
        f"`{_short(execution.commit_sha)}` | {execution.outcome} | {exit_code} | "
        f"`{execution.log_sha256[:12]}` |"
    )


def title(result: FixResult) -> str:
    text = f"[VisionIQ draft] Fix for #{result.ticket.id}: {result.ticket.title}".strip()
    return text[:MAX_TITLE_CHARS].rstrip(": ")


def description(result: FixResult) -> str:
    """Markdown for the PR description, at most 4000 characters."""
    reproduction = result.reproduction
    patch = result.patch
    summary = result.gate_summary
    head = [
        f"Draft fix for work item #{result.ticket.id}, opened by the VisionIQ bot. "
        "It is a proposal: the bot cannot approve or merge it.",
        "",
        f"**Status: {summary.overall.replace('_', ' ')}** (sandbox verdict: {result.verdict}). "
        + (
            f"Still open: {', '.join(_GATE_LABEL[name] for name in summary.blocking)}."
            if summary.blocking
            else "Every required gate passed for this commit."
        ),
        "",
        "### Defect",
        f"{_plain(result.ticket.title or 'Work item ' + result.ticket.id)} "
        f"(baseline `{result.base_sha}`).",
    ]
    if reproduction.test is not None and reproduction.evidence is not None:
        head += [
            "",
            "### Reproduction",
            f"Test `{reproduction.test.test_id}` in `{reproduction.test.path}` ran on the "
            f"unchanged baseline: **{reproduction.evidence.outcome}**.",
        ]
        if reproduction.evidence.failure_signature:
            # Inside a code span nothing is markup; a backtick in the text would end the span.
            failure = _cell(reproduction.evidence.failure_signature).replace("`", "'")[:200]
            head.append(f"Failure: `{failure}`")
    if patch is not None:
        files = ", ".join(
            f"`{file.path}` (+{file.added_lines}/-{file.removed_lines})" for file in patch.files[:8]
        )
        head += [
            "",
            "### Cause (proposed by the model, not verified)",
            _plain(patch.cause) if patch.cause else "Not stated.",
            "",
            "### Fix",
            f"{_plain((patch.summary or 'See the diff').rstrip('.'))}. "
            f"Sandbox candidate `{patch.candidate_sha or 'none'}`, "
            f"iteration {patch.iteration}. Files: {files}.",
        ]
    gates = [
        "",
        "### Verification",
        "| Check | Result | Detail | Runs |",
        "| --- | --- | --- | --- |",
        *(_gate_row(gate) for gate in result.gates),
    ]
    risk: list[str] = []
    if result.limitations:
        # Limitations can quote a model's own words (a reason it gave), so they are plain text too.
        risk = [
            "",
            "### Risks and limits",
            *(f"- {_plain(line)}" for line in result.limitations[:6]),
        ]
    manifest = result.manifests[-1] if result.manifests else None
    trail = ["", "### Evidence"]
    if manifest is not None:
        runner = manifest.runner
        trail.append(
            f"Runner: {runner.kind}"
            + (f" `{runner.image}`" if runner.image else "")
            + f", network {runner.network}. Policy `{manifest.policy_revision}`. "
            f"Manifest `{manifest.sha256[:12]}`."
        )
    trail += [
        "| Run | Purpose | Code | Outcome | Exit | Log SHA-256 |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    footer = [
        "",
        f"Full result: fix `{result.fix_id}`, run `{result.run_id}`.",
    ]

    fixed = "\n".join([*head, *gates, *risk, *trail])
    tail = "\n".join(footer)
    # Benchmark runs are many and all alike: one line for them, a row for every other run.
    benchmarks = [e.execution_id for e in result.executions if e.purpose == "benchmark"]
    if benchmarks:
        tail = (
            f"\n{len(benchmarks)} benchmark run(s): {benchmarks[0]} to {benchmarks[-1]}.\n" + tail
        )
    rows = [_execution_row(e) for e in result.executions if e.purpose != "benchmark"]
    kept: list[str] = []
    for index, row in enumerate(rows):
        remaining = len(rows) - index
        note = f"\n({remaining} more run(s) are in the stored result.)"
        if len(fixed) + sum(len(r) + 1 for r in kept) + len(row) + 1 + len(note) + len(tail) > (
            MAX_DESCRIPTION_CHARS
        ):
            tail = note + "\n" + tail
            break
        kept.append(row)
    text = "\n".join([fixed, *kept]) + "\n" + tail
    return text[:MAX_DESCRIPTION_CHARS]


def pull_request(result: FixResult, target_branch: str) -> PullRequestDraft | None:
    """The draft PR for a result that has a committed candidate; None otherwise."""
    patch = result.patch
    if patch is None or patch.candidate_sha is None or patch.branch is None:
        return None
    return PullRequestDraft(
        source_branch=patch.branch,
        target_branch=target_branch,
        base_sha=result.base_sha,
        head_sha=patch.candidate_sha,
        work_item_id=result.ticket.id,
        title=title(result),
        description=description(result),
        checks=gate_rules.pull_request_checks(result.gates, patch.candidate_sha),
    )
