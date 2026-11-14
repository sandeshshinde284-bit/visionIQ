"""Run the fix pipeline on one ticket from the command line: ``python -m app.defect.cli``.

This needs no database, no Azure DevOps and no event bus: the ticket comes from a JSON file and
the result is printed and written next to the run's evidence. It is the local way to try
DEF-01 to DEF-06 on a repository, and what ``scripts/defect-demo`` uses.

    python -m app.defect.cli --policy defect-policy.json --repository kiosk-demo
        --base-sha <40-hex commit> --ticket ticket.json --offline-script offline-script.json

Without ``--offline-script`` the models configured in the environment are used (LLM_MODE,
CLAUDE_ROUTE, ...). Exit code 0 when the fix was verified in the sandbox, 3 otherwise.

The ticket file::

    {"project": "Demo", "id": "4711", "revision": 1, "title": "...", "text": "...",
     "scope": ["src/retry_policy.py"],
     "historical_fixes": [{"ticket_id": "101", "fix_reference": "PR 201", "paths": ["tests/..."]}]}
"""

from __future__ import annotations

import argparse
import json
import sys
import tempfile
import uuid
from collections.abc import Sequence
from pathlib import Path

from app.brief.history import TicketText
from app.config import get_settings
from app.defect.pipeline import FixOutcome, FixSettings, run_fix
from app.defect.policy import load_policy
from app.defect.schema import FixRequest, FixResult, TicketRef
from app.defect.sources import HistoricalFix
from app.llm.gateway import ModelGateway
from app.llm.providers.offline import OfflineProvider
from app.llm.routing import RoutingPolicy
from app.logging_config import configure_from_environment


class FileSources:
    """:class:`app.defect.sources.FixSources` read from a ticket file."""

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

    def brief_scope(self, project_id: str, ticket_id: str) -> tuple[str, ...]:
        return tuple(str(path) for path in self._ticket.get("scope", []))

    def brief_risk_band(self, project_id: str, ticket_id: str) -> str | None:
        """Optional "risk_band" of the ticket file (LOW, MODERATE, HIGH); used only when models
        are selected by complexity."""
        band = str(self._ticket.get("risk_band", "")).upper()
        return band if band in {"LOW", "MODERATE", "HIGH", "INSUFFICIENT_DATA"} else None

    def historical_fixes(
        self, ticket: TicketText, file_names: Sequence[str]
    ) -> list[HistoricalFix]:
        return [
            HistoricalFix(
                str(fix["ticket_id"]),
                str(fix["fix_reference"]),
                tuple(str(path) for path in fix["paths"]),
                "inferred" if fix.get("link") == "inferred" else "explicit",
            )
            for fix in self._ticket.get("historical_fixes", [])
        ]


def _fix_settings(root: Path, out: Path, keep_workspace: bool, live: bool) -> FixSettings:
    """With live models the service configuration applies (the read token for a private
    repository, the repository cache, model selection, budgets), exactly as for the background
    worker. An offline demo needs no configuration and keeps the built-in defaults."""
    if live:
        return FixSettings.from_app(get_settings(), root, out, keep_workspace=keep_workspace)
    return FixSettings(work_dir=root / "work", artifact_dir=out, keep_workspace=keep_workspace)


def _gateway(offline_script: Path | None) -> ModelGateway:
    if offline_script is None:
        from app.llm.factory import build_gateway

        return build_gateway(get_settings())
    script = json.loads(offline_script.read_text(encoding="utf-8"))
    return ModelGateway(RoutingPolicy({}, {}, override=OfflineProvider(script)))


def summary(result: FixResult) -> str:
    """The result as plain text: what was shown, gate by gate."""
    reproduction = result.reproduction
    lines = [
        f"Fix run {result.run_id} for ticket #{result.ticket.id} ({result.repository_id})",
        f"State {result.state}, verdict {result.verdict}"
        + (f", reasons: {', '.join(result.reason_codes)}" if result.reason_codes else ""),
        f"Baseline  {result.base_sha}",
        f"Candidate {result.candidate_sha or 'none'}",
        "",
        f"Reproduction: {reproduction.reproducibility}"
        + (
            f" ({reproduction.test.path}, {reproduction.test.test_id})" if reproduction.test else ""
        ),
    ]
    if reproduction.evidence and reproduction.evidence.failure_signature:
        lines.append(f"  failure on the baseline: {reproduction.evidence.failure_signature}")
    if result.patch is not None:
        files = ", ".join(file.path for file in result.patch.files) or "none"
        lines.append(f"Patch (iteration {result.patch.iteration}): {files}")
    lines += ["", f"Gates: {result.gate_summary.overall}"]
    for gate in result.gates:
        need = "required" if gate.required else "info"
        lines.append(f"  {gate.status:<16} {gate.name:<22} [{need}] {gate.detail}")
    if result.attempts:
        lines += ["", "Attempts:"]
        lines += [
            f"  {attempt.stage} {attempt.iteration}: {attempt.result}"
            + (f" - {attempt.detail}" if attempt.detail else "")
            for attempt in result.attempts
        ]
    if result.limitations:
        lines += ["", "Limits:"] + [f"  - {line}" for line in result.limitations]
    lines += ["", f"Executions: {len(result.executions)}, model calls: {result.generation.calls}"]
    return "\n".join(lines)


def write_evidence(outcome: FixOutcome, directory: Path) -> Path:
    """Write the result, the PR description and the diff beside the run's logs and manifests."""
    result = outcome.result
    target = directory / str(result.run_id)
    target.mkdir(parents=True, exist_ok=True)
    (target / "result.json").write_text(result.model_dump_json(indent=2), encoding="utf-8")
    if result.pull_request is not None:
        (target / "pr-description.md").write_text(
            f"# {result.pull_request.title}\n\n{result.pull_request.description}\n",
            encoding="utf-8",
        )
    if result.patch is not None and result.patch.diff:
        (target / "candidate.patch").write_text(result.patch.diff + "\n", encoding="utf-8")
    return target


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Reproduce a defect and propose a verified fix.")
    parser.add_argument("--policy", type=Path, required=True, help="the fix policy file (JSON)")
    parser.add_argument("--repository", required=True, help="repository_id from the policy")
    parser.add_argument("--base-sha", required=True, help="the full baseline commit SHA")
    parser.add_argument("--ticket", type=Path, required=True, help="the ticket file (JSON)")
    parser.add_argument("--offline-script", type=Path, help="prepared model answers (demo only)")
    parser.add_argument("--out", type=Path, help="where the evidence is written")
    parser.add_argument("--keep-workspace", action="store_true", help="keep the cloned trees")
    parser.add_argument("--json", action="store_true", help="print the result as JSON")
    arguments = parser.parse_args(argv)

    configure_from_environment()
    ticket = json.loads(arguments.ticket.read_text(encoding="utf-8"))
    root = Path(tempfile.gettempdir()) / "visioniq-fix"
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
    outcome = run_fix(
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
        sys.stdout.write(f"{summary(outcome.result)}\n\nEvidence: {evidence}\n")
    return 0 if outcome.result.verdict == "VERIFIED" else 3


if __name__ == "__main__":
    raise SystemExit(main())
