"""Try a starter scaffold on one enhancement from the command line: ``python -m app.starter.cli``.

Like ``app.defect.cli`` this needs no database, no Azure DevOps and no event bus: the ticket and
its guidance come from a JSON file, and the result is printed and written to a folder. It is the
local way to try ENH-02 on a repository, and what ``scripts/defect-demo`` uses.

    python -m app.starter.cli --policy starter-policy.json --repository kiosk-demo
        --base-sha <40-hex commit> --ticket enhancement.json --offline-script offline-script.json

Without ``--offline-script`` the models configured in the environment are used (LLM_MODE,
CLAUDE_ROUTE, ...). Exit code 0 when a draft starter pull request was prepared, 3 when none was
(which is an answer, not a failure: the reason is printed).

The ticket file::

    {"project": "Demo", "id": "4712", "revision": 1, "title": "...", "text": "...",
     "guidance": { ...the enhancement's stored brief (ENH-01): kind, state, brief_id,
                   predicted_scope, advice, guidance... }}

Nothing is pushed and no pull request is opened here: the API does that from the stored result.
"""

from __future__ import annotations

import argparse
import json
import sys
import tempfile
import uuid
from pathlib import Path

from app.brief.history import TicketText
from app.defect.cli import _fix_settings, _gateway, write_evidence
from app.defect.policy import load_policy
from app.defect.schema import FixRequest, FixResult, TicketRef
from app.logging_config import configure_from_environment
from app.starter.pipeline import run_starter


class FileSources:
    """:class:`app.starter.pipeline.StarterSources` read from a ticket file."""

    def __init__(self, ticket: dict) -> None:
        self._ticket = ticket

    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None:
        return TicketText(
            item_key="0" * 64,
            source_instance="file",
            title=str(self._ticket.get("title", "")),
            text=str(self._ticket.get("text", "")),
            project_id=project_id,
        )

    def guidance(self, project_id: str, ticket_id: str, ticket_revision: int) -> dict | None:
        guidance = self._ticket.get("guidance")
        return guidance if isinstance(guidance, dict) else None


def summary(result: FixResult) -> str:
    """The result as plain text: what was created, and what was and was not checked."""
    lines = [
        f"Starter run {result.run_id} for enhancement #{result.ticket.id} ({result.repository_id})",
        f"State {result.state}, verdict {result.verdict}"
        + (f", reasons: {', '.join(result.reason_codes)}" if result.reason_codes else ""),
        f"Baseline {result.base_sha}",
        f"Scaffold {result.candidate_sha or 'none'}",
    ]
    if result.patch is not None:
        lines += ["", f"Files (iteration {result.patch.iteration}, {result.patch.policy_status}):"]
        lines += [
            f"  {file.change:<9} {file.path} (+{file.added_lines}/-{file.removed_lines})"
            for file in result.patch.files
        ]
        lines += [
            f"  finding: {finding.code}" + (f" ({finding.path})" if finding.path else "")
            for finding in result.patch.findings
        ]
    if result.pull_request is not None:
        draft = result.pull_request
        lines += [
            "",
            "Draft pull request (prepared, not opened here):",
            f"  {draft.title}",
            f"  {draft.source_branch} -> {draft.target_branch}, work item #{draft.work_item_id}",
        ]
    else:
        lines += ["", "No starter pull request. The guidance on the ticket stands on its own."]
    if result.starter is not None and result.starter.checklist:
        lines += ["", "Left for the engineer:"]
        lines += [f"  - {line}" for line in result.starter.checklist]
    lines += ["", f"Checks: {result.gate_summary.overall}"]
    lines += [f"  {gate.status:<16} {gate.name:<22} {gate.detail}" for gate in result.gates]
    if result.limitations:
        lines += ["", "Limits:"] + [f"  - {line}" for line in result.limitations]
    lines += ["", f"Executions: {len(result.executions)}, model calls: {result.generation.calls}"]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Prepare a starter scaffold for an enhancement.")
    parser.add_argument("--policy", type=Path, required=True, help="the fix policy file (JSON)")
    parser.add_argument("--repository", required=True, help="repository_id from the policy")
    parser.add_argument("--base-sha", required=True, help="the full baseline commit SHA")
    parser.add_argument("--ticket", type=Path, required=True, help="ticket + guidance (JSON)")
    parser.add_argument("--offline-script", type=Path, help="prepared model answers (demo only)")
    parser.add_argument("--out", type=Path, help="where the result is written")
    parser.add_argument("--keep-workspace", action="store_true", help="keep the cloned trees")
    parser.add_argument("--json", action="store_true", help="print the result as JSON")
    arguments = parser.parse_args(argv)

    configure_from_environment()
    ticket = json.loads(arguments.ticket.read_text(encoding="utf-8"))
    root = Path(tempfile.gettempdir()) / "visioniq-starter"
    out = arguments.out or root / "artifacts"
    request = FixRequest(
        run_id=uuid.uuid4(),
        ticket=TicketRef(
            project=str(ticket.get("project", "local")),
            id=str(ticket["id"]),
            revision=int(ticket.get("revision", 1)),
        ),
        repository_id=arguments.repository,
        base_commit_sha=arguments.base_sha,
        security_scope_id="local",
    )
    outcome = run_starter(
        request,
        FileSources(ticket),
        _gateway(arguments.offline_script),
        load_policy(arguments.policy),
        _fix_settings(root, out, arguments.keep_workspace, arguments.offline_script is None),
    )
    evidence = write_evidence(outcome, out)
    if arguments.json:
        sys.stdout.write(outcome.result.model_dump_json(indent=2) + "\n")
    else:
        sys.stdout.write(f"{summary(outcome.result)}\n\nResult: {evidence}\n")
    return 0 if outcome.result.pull_request is not None else 3


if __name__ == "__main__":
    raise SystemExit(main())
