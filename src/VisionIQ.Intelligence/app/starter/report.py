"""ENH-02 (PROP-06): the title and description of a draft starter pull request.

The text is built from the stored result only and says plainly what a starter is: a scaffold
that was not built, run or tested, opened as a draft by a bot that cannot approve or merge it.
Azure DevOps limits a description to 4000 characters; the guidance list is cut first.
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import Protocol

from app.brief.guidance import TITLES
from app.defect import gates as gate_rules
from app.defect.report import MAX_DESCRIPTION_CHARS, MAX_TITLE_CHARS, plain
from app.defect.schema import FixResult, Gate, PullRequestDraft

_GATE_LABEL = {
    "reproduction": "Defect reproduction",
    "candidate_test": "Fix test",
    "historical_regression": "Past regression tests",
    "touched_suite": "Touched project's suite",
    "performance": "Performance of the touched path",
    "patch_policy": "Patch policy",
    "native_ci": "Native CI on this PR",
    "human_review": "Engineer review",
}
_MARK = {
    "PASS": "PASS",
    "FAIL": "FAIL",
    "NOT_RUN": "NOT RUN",
    "INCONCLUSIVE": "INCONCLUSIVE",
    "UNAVAILABLE": "UNAVAILABLE",
    "REVIEW_REQUIRED": "REVIEW REQUIRED",
    "NOT_APPLICABLE": "not applicable",
}
MAX_LISTED_FILES = 12
MAX_LISTED_ITEMS = 12


class _Item(Protocol):
    advice_id: str
    section: str
    statement: str


def _cell(text: str) -> str:
    return " ".join(text.replace("|", "/").split())


def _gate_row(gate: Gate) -> str:
    return f"| {_GATE_LABEL[gate.name]} | **{_MARK[gate.status]}** | {_cell(gate.detail)} |"


def title(result: FixResult) -> str:
    text = f"[VisionIQ draft] Starter for #{result.ticket.id}: {result.ticket.title}".strip()
    return text[:MAX_TITLE_CHARS].rstrip(": ")


MAX_PATH_CHARS = 120
MAX_CHECKLIST_CHARS = 160
# Room kept for the checks table, the limits and the footer, which are never cut.
_LISTS_BUDGET = 1500


def _fit(lines: list[str], budget: int, what: str) -> list[str]:
    """As many of ``lines`` as fit in ``budget`` characters, and a note for the rest."""
    kept: list[str] = []
    used = 0
    for index, line in enumerate(lines):
        if used + len(line) + 1 > budget:
            return [*kept, f"- and {len(lines) - index} more {what} (in the stored result)"]
        kept.append(line)
        used += len(line) + 1
    return kept


def description(result: FixResult, guidance: Sequence[_Item]) -> str:
    """Markdown for the PR description, at most 4000 characters.

    The status line, the checks table and the limits are always shown in full. The lists a
    model or a long path can make long (files, the checklist, the guidance) are what gives way.
    """
    patch = result.patch
    starter = result.starter
    head = [
        f"Draft **starter** for enhancement #{result.ticket.id}, opened by the VisionIQ bot. "
        "It is a scaffold to begin from, not a finished change: the bot cannot approve or "
        "merge it.",
        "",
        "**Status: NOT VERIFIED.** VisionIQ did not build, run or test this scaffold. "
        "Only the patch policy was checked.",
        "",
        "### Enhancement",
        f"{plain(result.ticket.title or 'Work item ' + result.ticket.id)} "
        f"(baseline `{result.base_sha}`).",
    ]
    if patch is not None:
        files = _fit(
            [
                f"- `{file.path[:MAX_PATH_CHARS]}` "
                f"({file.change}, +{file.added_lines}/-{file.removed_lines})"
                for file in patch.files
            ][:MAX_LISTED_FILES]
            + (
                [f"- and {len(patch.files) - MAX_LISTED_FILES} more file(s)"]
                if len(patch.files) > MAX_LISTED_FILES
                else []
            ),
            _LISTS_BUDGET // 2,
            "file(s)",
        )
        summary = starter.summary if starter is not None else patch.summary
        head += [
            "",
            "### What is in the scaffold (written by a model)",
            f"{plain((summary or 'See the diff').rstrip('.'))}.",
            *files,
        ]
    if starter is not None and starter.checklist:
        head += [
            "",
            "### Left for you",
            *_fit(
                [f"- [ ] {plain(line[:MAX_CHECKLIST_CHARS])}" for line in starter.checklist],
                _LISTS_BUDGET // 2,
                "item(s)",
            ),
        ]
    checks = [
        "",
        "### Checks",
        "| Check | Result | Detail |",
        "| --- | --- | --- |",
        *(_gate_row(gate) for gate in result.gates),
    ]
    limits: list[str] = []
    if result.limitations:
        limits = ["", "### Limits", *(f"- {plain(line)}" for line in result.limitations[:6])]
    footer = [
        "",
        f"Full result: starter `{result.fix_id}`, run `{result.run_id}`"
        + (f", guidance `{starter.guidance_brief_id}`." if starter is not None else "."),
    ]
    fixed = "\n".join([*head, *checks, *limits])
    tail = "\n".join(footer)
    rows: list[str] = []
    if guidance:
        intro = "\n\n### Guidance it follows (on the work item)\n"
        used = len(fixed) + len(intro) + len(tail) + 1
        for index, item in enumerate(guidance[:MAX_LISTED_ITEMS]):
            row = (
                f"- {item.advice_id} ({TITLES.get(item.section, item.section)}): "
                f"{plain(item.statement)[:200]}"
            )
            more = f"\n({len(guidance) - index} more item(s) are in the guidance.)"
            if used + sum(len(r) + 1 for r in rows) + len(row) + 1 + len(more) > (
                MAX_DESCRIPTION_CHARS
            ):
                tail = more + "\n" + tail
                break
            rows.append(row)
        else:
            if len(guidance) > MAX_LISTED_ITEMS:
                tail = (
                    f"\n({len(guidance) - MAX_LISTED_ITEMS} more item(s) are in the guidance.)\n"
                    + tail
                )
        if rows:
            fixed += intro + "\n".join(rows)
    return (fixed + "\n" + tail)[:MAX_DESCRIPTION_CHARS]


def pull_request(
    result: FixResult, target_branch: str, guidance: Sequence[_Item]
) -> PullRequestDraft | None:
    """The draft PR for a starter that has a committed scaffold; None otherwise."""
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
        description=description(result, guidance),
        checks=gate_rules.pull_request_checks(result.gates, patch.candidate_sha),
    )
