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


def _runs(ids: list[str]) -> str:
    """Every run, with consecutive ones as a range: X3, X4, X17-X26."""
    numbered = [(int(i[1:]), i) for i in ids if i[:1] == "X" and i[1:].isdigit()]
    if len(numbered) != len(ids):
        return ", ".join(ids) or "-"
    parts: list[str] = []
    start = end = None
    for number, _ in sorted(numbered):
        if end is not None and number == end + 1:
            end = number
            continue
        if start is not None:
            parts.append(f"X{start}" if start == end else f"X{start}-X{end}")
        start = end = number
    if start is not None:
        parts.append(f"X{start}" if start == end else f"X{start}-X{end}")
    return ", ".join(parts) or "-"


def _sentences(text: str, limit: int = 320) -> str:
    """Whole sentences only: text cut off mid-sentence ends at its last full stop, or with '...'."""
    text = " ".join(text.split())
    if len(text) > limit:
        text = text[:limit]
    if text and text[-1] not in ".!?":
        stop = max(text.rfind(". "), text.rfind("! "), text.rfind("? "))
        text = text[: stop + 1] if stop > 0 else text.rstrip(",;: ") + "..."
    return text


def _distinct(lines: list[str]) -> list[str]:
    """Lines that do not repeat an earlier one (models restate the same limit each attempt)."""
    kept: list[str] = []
    seen: list[str] = []
    for line in lines:
        key = " ".join(re.sub(r"[^a-z0-9 ]", " ", line.lower()).split())[:80]
        if key and not any(key in other or other in key for other in seen):
            kept.append(line)
            seen.append(key)
    return kept


# Why a result is not verified, in words a reviewer reads.
_REASON = {
    "reproduction_indirect": "the new test checks an internal step, not the result the ticket "
    "describes",
    "existing_test_contradicts_ticket": "an existing test fails with this change and is stated "
    "to expect the behaviour the ticket reports as a defect",
    "regression_failed": "the change breaks existing tests that passed before",
    "regression_inconclusive": "some related tests gave no clear result",
    "suite_failed": "the project's test suite fails with the change",
    "suite_inconclusive": "the project's test suite gave no clear result",
    "candidate_test_failed": "the new test still fails with the change",
    "fix_budget_exhausted": "no attempt passed every check within the attempts allowed",
    "performance_regression": "the change is measurably slower",
    "performance_inconclusive": "its effect on performance could not be told from noise",
}
# Environment notes, kept with the technical details rather than in what the reviewer must check.
_ENVIRONMENT = ("Commands ran on the local runner",)


def _gate(result: FixResult, name: str) -> Gate | None:
    return next((gate for gate in result.gates if gate.name == name), None)


def _gate_row(gate: Gate) -> str:
    need = "required" if gate.required else "informational"
    return (
        f"| {_GATE_LABEL[gate.name]} | {_MARK[gate.status]} ({need}) | "
        f"{_cell(gate.detail)} | {_runs(gate.execution_ids)} |"
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


def _status(result: FixResult) -> str:
    reasons = [_REASON.get(code, code.replace("_", " ")) for code in result.reason_codes]
    because = "; ".join(reasons)
    if result.verdict == "VERIFIED":
        return (
            "VisionIQ reproduced the defect with a test, and this change passed every check it "
            "ran in a sandbox. Before it is merged, this repository's own pipeline has to pass "
            "and an engineer has to review it."
        )
    if result.verdict == "FAIL":
        return (
            f"**Not verified: the change did not pass VisionIQ's checks** ({because}). It is "
            "shared as a draft so an engineer can see what was tried."
        )
    return (
        f"**Not verified.** VisionIQ proposes this change, but its checks did not confirm it "
        f"({because or 'see the checks below'}). Treat it as a starting point and review it "
        "with care."
    )


def _checked(result: FixResult) -> list[str]:
    """How it was checked, as sentences; one line per sandbox check that ran."""
    lines: list[str] = []
    reproduced = _gate(result, "reproduction")
    candidate = _gate(result, "candidate_test")
    if reproduced is not None and reproduced.status == "PASS":
        lines.append(
            "The new test fails on the code before this change, for the predicted reason "
            f"(runs {_runs(reproduced.execution_ids)})."
        )
    elif reproduced is not None:
        lines.append(f"Reproduction: {_MARK[reproduced.status]}. {_cell(reproduced.detail)}")
    if candidate is not None and candidate.status == "PASS":
        lines.append(f"It passes with this change (run {_runs(candidate.execution_ids)}).")
    elif candidate is not None and candidate.status != "NOT_RUN":
        lines.append(f"With this change the new test is {_MARK[candidate.status]}.")
    regression = _gate(result, "historical_regression")
    if regression is not None and regression.status not in ("NOT_RUN", "NOT_APPLICABLE"):
        tests = result.regression.tests
        passed = sum(1 for test in tests if test.outcome == "PASS")
        runs = f" (runs {_runs(regression.execution_ids)})" if regression.execution_ids else ""
        if regression.status == "PASS":
            still = "test still passes" if passed == 1 else "tests still pass"
            lines.append(f"{passed} existing related {still} with this change{runs}.")
        elif regression.status == "UNAVAILABLE":
            lines.append("No existing test of this area was found to run again.")
        else:
            lines.append(f"Existing related tests: {_cell(regression.detail)}{runs}")
    suite = _gate(result, "touched_suite")
    if suite is not None and suite.status not in ("NOT_RUN", "NOT_APPLICABLE"):
        said = {
            "PASS": "passes",
            "FAIL": "fails",
            "INCONCLUSIVE": "gave no clear result",
        }.get(suite.status, _MARK[suite.status].lower())
        runs = _runs(suite.execution_ids)
        lines.append(f"The project's test suite {said} in the sandbox (runs {runs}).")
    performance = _gate(result, "performance")
    if performance is not None and performance.status == "PASS":
        lines.append("No slowdown was measured on the changed code path.")
    elif performance is not None and performance.status not in ("NOT_RUN", "NOT_APPLICABLE"):
        lines.append(f"Performance: {_cell(performance.detail)}")
    policy = _gate(result, "patch_policy")
    if policy is not None and policy.status == "PASS":
        lines.append("The change stays within the files and size this repository allows.")
    elif policy is not None and policy.status not in ("NOT_RUN", "NOT_APPLICABLE"):
        lines.append(f"Repository rules: {_cell(policy.detail)}")
    if suite is None or suite.status in ("NOT_RUN", "NOT_APPLICABLE"):
        lines.append(
            "The project's full test suite was not run here; this repository's pipeline runs it "
            "on this pull request."
        )
    return lines


def description(result: FixResult) -> str:
    """Markdown for the PR description, at most 4000 characters: what a reviewer needs first,
    the technical trail after it."""
    reproduction = result.reproduction
    patch = result.patch
    ticket = _plain(result.ticket.title or "Work item " + result.ticket.id)
    head = [
        f"VisionIQ prepared this draft for work item #{result.ticket.id}: {ticket}. It is a "
        "proposal for an engineer to review; VisionIQ cannot approve or merge it.",
        "",
        _status(result),
    ]
    if patch is not None:
        head += [
            "",
            "### What was wrong",
            (_plain(_sentences(patch.cause, 600)) if patch.cause else "Not stated.")
            + " _(The model's analysis, checked by the tests below but not yet by a person.)_",
            "",
            "### What changed",
            _plain(_sentences((patch.summary or "See the diff").rstrip(".") + ".", 600)),
            "",
            "Files: "
            + ", ".join(
                f"`{file.path}` (+{file.added_lines} -{file.removed_lines})"
                for file in patch.files[:8]
            )
            + ".",
        ]
    if reproduction.test is not None and reproduction.evidence is not None:
        shown = [
            "",
            "### How the defect was shown",
            f"Test `{reproduction.test.test_id}` in `{reproduction.test.path}`.",
        ]
        if reproduction.evidence.failure_signature:
            # Inside a code span nothing is markup; a backtick in the text would end the span.
            failure = _cell(reproduction.evidence.failure_signature).replace("`", "'")[:200]
            shown.append(f"Before this change it fails with: `{failure}`")
        head += shown
    head += ["", "### How it was checked", *(f"- {line}" for line in _checked(result))]
    review = _distinct(
        [_sentences(line) for line in result.limitations if not line.startswith(_ENVIRONMENT)]
    )
    if review:
        # Limitations can quote a model's own words, so they are plain text too.
        head += ["", "### Please check", *(f"- {_plain(line)}" for line in review[:5])]
    manifest = result.manifests[-1] if result.manifests else None
    trail = [
        "",
        "### Details",
        f"Baseline `{result.base_sha}`, sandbox candidate "
        f"`{patch.candidate_sha if patch and patch.candidate_sha else 'none'}`"
        + (f", iteration {patch.iteration}" if patch else "")
        + ".",
    ]
    if manifest is not None:
        runner = manifest.runner
        trail.append(
            f"Runner: {runner.kind}"
            + (f" `{runner.image}`" if runner.image else "")
            + f", network {runner.network}. Policy `{manifest.policy_revision}`. "
            f"Manifest `{manifest.sha256[:12]}`."
        )
    trail += [
        "",
        "| Check | Result | Detail | Runs |",
        "| --- | --- | --- | --- |",
        *(_gate_row(gate) for gate in result.gates),
        "",
        "| Run | Purpose | Code | Outcome | Exit | Log SHA-256 |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    footer = [
        "",
        f"Full result: fix `{result.fix_id}`, run `{result.run_id}`.",
    ]

    fixed = "\n".join([*head, *trail])
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
