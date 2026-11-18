"""ENH-02: an optional starter pull request for an enhancement (master design 2.1, 8.5, 8.6.4).

"Enhancement: guided build ... optional starter PR when suitable, not mandatory." The guidance
(ENH-01) completes the enhancement path on its own. This module answers a request for more: a
narrow scaffold on a branch, so the engineer starts from the files, signatures and test stubs
the guidance calls for instead of a blank branch.

It reuses the fix pipeline's parts (app/defect) and its result contract, with ``kind`` set to
``starter``, so the API delivers it through the same draft-pull-request path (DEF-05):

* PROP-04, when a starter is attempted. The API decides who may ask (an engineer's comment, or
  policy). Here, code decides whether the change is understood well enough: the repository's
  owners allow starters (``starter_pr.enabled``), guidance for this ticket revision exists and
  is PUBLISHED, enough of its eight sections are covered, and the files it names exist at the
  pinned baseline. Otherwise nothing is created and the reason is stated.
* PROP-05, the scaffold. One routed model call per attempt (task ``starter_scaffold``). The
  model may decline. What it proposes passes the same patch rules as a fix (8.6.4), with one
  difference: it may ADD test files. A proposal in a restricted area stops for an engineer.
* Nothing is built or run. The result is therefore never VERIFIED and never green: its state is
  PARTIAL, its verdict INCONCLUSIVE, and every check that was not made is reported as not run.

Not producing a starter is never a failure of the enhancement path: the answer is BLOCKED with
a reason code, the guidance stays on the ticket, and no pull request is opened.
"""

from __future__ import annotations

import difflib
import re
import time
import uuid
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from typing import Any, Protocol

import structlog

from app.brief.guidance import SECTION_CODES, TITLES
from app.brief.history import TicketText
from app.defect import gates as gate_rules
from app.defect import guardrails
from app.defect.generate import MAX_CHANGES, Declined, clean
from app.defect.pipeline import (
    MAX_DIFF_CHARS,
    MAX_LIMITATIONS,
    REPOSITORY_NOT_CONFIGURED,
    SCOPE_NOT_RESOLVED,
    TICKET_NOT_READABLE,
    FixOutcome,
    FixSettings,
)
from app.defect.policy import DefectPolicy, RepositoryPolicy, matches
from app.defect.schema import (
    Attempt,
    FixRequest,
    FixResult,
    FixState,
    Gate,
    Generation,
    Patch,
    Performance,
    Regression,
    Reproduction,
    Starter,
    Timings,
    Verdict,
)
from app.defect.workspace import Workspace, WorkspaceError, is_safe_path, prepare
from app.knowledge.retrieval import extract_signals
from app.llm.gateway import Budget, RunLedger
from app.llm.gateway import ModelGateway as Gateway
from app.llm.injection import scan
from app.llm.routing import ROUTING_REVISION
from app.llm.types import CodeFile, EvidenceContext, EvidenceItem, ModelResult, TaskKind
from app.starter import report

# Reason codes (the API stores them with the run; ^[a-z][a-z0-9_]{1,79}$).
NOT_ALLOWED = "starter_not_allowed_by_policy"
GUIDANCE_NOT_FOUND = "starter_guidance_not_found"
NOT_WELL_UNDERSTOOD = "starter_change_not_well_understood"
DECLINED_BY_MODEL = "starter_declined_by_model"
SCAFFOLD_REJECTED = "starter_scaffold_rejected"
NEEDS_ENGINEER_REVIEW = "starter_needs_engineer_review"
# The reason of every starter that was produced: it says what a starter is not.
NOT_VERIFIED = "starter_scaffold_not_verified"

MAX_CHECKLIST = 8
MAX_GUIDANCE_ITEMS = 24
_WORKSPACE_TEXT = (
    "The repository could not be fetched at the pinned commit, so nothing was written."
)


class StarterSources(Protocol):
    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None: ...

    def guidance(self, project_id: str, ticket_id: str, ticket_revision: int) -> dict | None:
        """The stored enhancement guidance (a brief document) of this ticket revision."""
        ...


@dataclass(frozen=True)
class GuidanceItem:
    advice_id: str
    section: str
    statement: str


@dataclass(frozen=True)
class Guidance:
    """The part of the stored guidance a starter is built from."""

    brief_id: uuid.UUID
    state: str
    covered: tuple[str, ...]
    items: tuple[GuidanceItem, ...]
    scope: tuple[str, ...]


def parse_guidance(document: Mapping[str, Any] | None) -> Guidance | None:
    """Read a stored brief as guidance; None when it is not usable enhancement guidance."""
    if not isinstance(document, Mapping) or document.get("kind") != "enhancement":
        return None
    try:
        brief_id = uuid.UUID(str(document.get("brief_id")))
    except ValueError:
        return None
    sections = [s for s in document.get("guidance") or [] if isinstance(s, Mapping)]
    covered = tuple(
        code
        for code in SECTION_CODES
        if any(s.get("section") == code and s.get("status") == "covered" for s in sections)
    )
    items = tuple(
        GuidanceItem(str(a["advice_id"]), str(a["section"]), " ".join(str(a["statement"]).split()))
        for a in document.get("advice") or []
        if isinstance(a, Mapping)
        and a.get("section") in SECTION_CODES
        and re.fullmatch(r"ADV-[0-9]{3}", str(a.get("advice_id", "")))
        and str(a.get("statement", "")).strip()
    )[:MAX_GUIDANCE_ITEMS]
    scope = tuple(
        str(item["path"])
        for item in document.get("predicted_scope") or []
        if isinstance(item, Mapping) and item.get("path")
    )
    return Guidance(brief_id, str(document.get("state", "")), covered, items, scope)


def not_well_understood(guidance: Guidance, repo: RepositoryPolicy) -> str | None:
    """PROP-04, the rule for "the change is well understood". None when it is; else why not."""
    if guidance.state != "PUBLISHED":
        return (
            "The enhancement guidance for this ticket is incomplete "
            f"({guidance.state or 'no state'}), so no scaffold was started from it."
        )
    need = repo.starter_pr.min_covered_sections
    if len(guidance.covered) < need or not guidance.items:
        return (
            f"Only {len(guidance.covered)} of the eight guidance sections are covered by "
            f"evidence; this repository asks for {need} before a starter is attempted."
        )
    return None


@dataclass(frozen=True)
class ScaffoldProposal:
    changes: tuple[guardrails.ProposedChange, ...]
    summary: str
    checklist: tuple[str, ...]
    limitations: tuple[str, ...]
    result: ModelResult


def _lines(items: object, limit: int) -> tuple[str, ...]:
    values = items if isinstance(items, list) else []
    return tuple(line for line in (clean(text, 240) for text in values[:limit]) if line)


def propose_scaffold(
    gateway: Gateway, ledger: RunLedger, context: EvidenceContext
) -> ScaffoldProposal | Declined:
    """PROP-05: ask the routed model for the scaffold. The model proposes; code decides."""
    outcome = gateway.generate(TaskKind.STARTER_SCAFFOLD, context, ledger)
    if outcome.result is None:
        return Declined(outcome.reason or "llm_unavailable", "No model produced a scaffold.", True)
    output = outcome.result.output
    if not output.get("suitable", False):
        reason = clean(output.get("unsuitable_reason")) or "The model gave no reason."
        return Declined(DECLINED_BY_MODEL, reason)
    changes = tuple(
        guardrails.ProposedChange(str(change["path"]), str(change["content"]))
        for change in output.get("changes", [])[:MAX_CHANGES]
    )
    return ScaffoldProposal(
        changes,
        clean(output.get("summary")),
        _lines(output.get("checklist"), MAX_CHECKLIST),
        _lines(output.get("limitations"), 3),
        outcome.result,
    )


def starter_gates(patch: Patch | None, head: str | None) -> list[Gate]:
    """What was and was not checked. Nothing is run for a starter, and it says so: the only
    gate that can pass is the patch policy. Native CI and the engineer's review are the
    platform's, as for a fix. A gate that does not apply names no commit: it is not a measurement,
    so a later commit on the branch does not make it "stale"."""
    not_run = "Not run: VisionIQ does not build or test a starter scaffold."
    if patch is None:
        policy_status, policy_detail = "NOT_RUN", "No scaffold was proposed."
    elif patch.policy_status == "PASS":
        policy_status, policy_detail = "PASS", "The scaffold stays inside the repository's policy."
    elif patch.policy_status == "REVIEW_REQUIRED":
        policy_status = "REVIEW_REQUIRED"
        policy_detail = "The scaffold touches an area that needs an engineer's review."
    else:
        policy_status, policy_detail = "FAIL", "The scaffold breaks the repository's policy."
    return [
        Gate(
            name="reproduction",
            required=False,
            status="NOT_APPLICABLE",
            detail="An enhancement has no defect to reproduce.",
        ),
        Gate(
            name="candidate_test",
            required=False,
            status="NOT_APPLICABLE",
            detail="A starter scaffold has no failing test to turn green.",
        ),
        Gate(
            name="historical_regression",
            required=False,
            status="NOT_RUN",
            commit_sha=head,
            detail=not_run,
        ),
        Gate(
            name="touched_suite", required=True, status="NOT_RUN", commit_sha=head, detail=not_run
        ),
        Gate(name="performance", required=False, status="NOT_RUN", commit_sha=head, detail=not_run),
        Gate(
            name="patch_policy",
            required=True,
            status=policy_status,  # type: ignore[arg-type]
            commit_sha=head,
            detail=policy_detail,
        ),
        Gate(
            name="native_ci",
            required=True,
            status="NOT_RUN",
            detail="Reported by the repository's own CI on the pull request.",
        ),
        Gate(
            name="human_review",
            required=True,
            status="NOT_RUN",
            detail="An engineer decides; the bot cannot approve or merge.",
        ),
    ]


def _log() -> structlog.stdlib.BoundLogger:
    """Ids, codes, counts and paths only: never ticket text, source code or model output."""
    return structlog.get_logger()


def _unique(items: Sequence[str]) -> list[str]:
    return list(dict.fromkeys(items))


class _StarterRun:
    def __init__(
        self,
        request: FixRequest,
        sources: StarterSources,
        gateway: Gateway,
        policy: DefectPolicy | None,
        settings: FixSettings,
        clock: Callable[[], float],
    ) -> None:
        self.request = request
        self.sources = sources
        self.gateway = gateway
        self.policy = policy
        self.settings = settings
        self.clock = clock
        self.started = clock()
        self.ledger = RunLedger(
            request.run_id,
            settings.classification,
            Budget(settings.max_calls, settings.max_tokens, settings.max_seconds),
        )
        self.artifacts = settings.artifact_dir / str(request.run_id)
        self.repo: RepositoryPolicy | None = None
        self.workspace: Workspace | None = None
        self.title = request.ticket.title
        self.reasons: list[str] = []
        self.limitations: list[str] = []
        self.attempts: list[Attempt] = []
        self.generation: dict[str, str | None] = {}
        self.guidance: Guidance | None = None
        self.patch: Patch | None = None
        self.candidate_sha: str | None = None
        self.summary = ""
        self.checklist: tuple[str, ...] = ()
        self.target_branch = ""

    def close(self) -> None:
        if self.workspace is not None and not self.settings.keep_workspace:
            self.workspace.cleanup()

    # ---- The run ---------------------------------------------------------------------------

    def execute(self) -> FixOutcome:
        request = self.request
        _log().info(
            "starter_started",
            project_id=request.ticket.project,
            ticket_id=request.ticket.id,
            ticket_revision=request.ticket.revision,
            repository_id=request.repository_id,
            base_sha=request.base_commit_sha,
            policy_revision=self.policy.revision if self.policy else None,
        )
        repo = self.policy.repository(request.repository_id) if self.policy else None
        if repo is None:
            return self._none(
                REPOSITORY_NOT_CONFIGURED,
                "This repository is not in the fix policy, so no code was fetched or written.",
            )
        if not repo.starter_pr.enabled:
            return self._none(
                NOT_ALLOWED,
                "Starter pull requests are not switched on for this repository "
                "(starter_pr.enabled in the fix policy).",
            )
        self.repo = repo
        self.target_branch = repo.target_branch
        ticket = self.sources.load_ticket(request.ticket.project, request.ticket.id)
        if ticket is None:
            return self._none(
                TICKET_NOT_READABLE,
                "The ticket is not in the knowledge base under this project's access scope.",
            )
        self.title = ticket.title
        guidance = parse_guidance(
            self.sources.guidance(
                request.ticket.project, request.ticket.id, request.ticket.revision
            )
        )
        if guidance is None:
            return self._none(
                GUIDANCE_NOT_FOUND,
                "There is no enhancement guidance for this revision of the ticket to start from.",
            )
        self.guidance = guidance
        unclear = not_well_understood(guidance, repo)
        if unclear is not None:
            return self._none(NOT_WELL_UNDERSTOOD, unclear)
        if scan("ticket", f"{ticket.title}\n{ticket.text}"):
            self.limitations.append(
                "The ticket contains instructions addressed to an AI assistant. "
                "They were treated as data and not followed."
            )
        try:
            self.workspace = prepare(
                repo.source,
                request.base_commit_sha,
                self.settings.work_dir / str(request.run_id),
                self.settings.bot_name,
                self.settings.bot_email,
                self.settings.repo_access_token,
                self.settings.repo_organization_url,
                self.settings.repo_cache_dir,
                self.settings.repo_fetch,
            )
        except WorkspaceError as error:
            return self._none(error.code, _WORKSPACE_TEXT)
        files = self.workspace.files()
        code = self._context_files(ticket, guidance, files)
        _log().info(
            "starter_workspace_ready",
            baseline_files=len(files),
            context_files=[file.path for file in code],
            truncated_files=sum(1 for file in code if file.truncated),
            guidance_items=len(guidance.items),
            covered_sections=len(guidance.covered),
        )
        if not code:
            return self._none(
                SCOPE_NOT_RESOLVED,
                "No source file named by the ticket or its guidance was found at the baseline, "
                "so there was nothing to show where the change belongs.",
            )
        state, verdict = self._scaffold(ticket, guidance, code, files)
        return self._finish(state, verdict)

    def _none(self, reason: str, limitation: str) -> FixOutcome:
        """No starter. Never a failure of the enhancement path: the guidance stands on its own."""
        self.reasons.append(reason)
        self.limitations.append(limitation)
        _log().info("starter_not_created", reason=reason)
        return self._finish("BLOCKED", "BLOCKED")

    def _context_files(
        self, ticket: TicketText, guidance: Guidance, files: Sequence[str]
    ) -> list[CodeFile]:
        """The source files the model is shown: those the guidance predicted or the ticket names."""
        assert self.repo is not None and self.workspace is not None
        repo, workspace = self.repo, self.workspace
        wanted = [*guidance.scope, *extract_signals(f"{ticket.title}\n{ticket.text}").files]
        eligible = [
            path
            for path in files
            if is_safe_path(path)
            and not matches(path, repo.test_file_patterns)
            and not matches(path, repo.secret_paths)
            and not matches(path, repo.generated_paths)
        ]
        by_lower = {path.lower(): path for path in eligible}
        chosen: list[str] = []
        for raw in wanted:
            target = raw.replace("\\", "/").lstrip("/").lower()
            if not target:
                continue
            name = target.rsplit("/", 1)[-1]
            hits = (
                [by_lower[target]]
                if target in by_lower
                else [path for path in eligible if path.lower().endswith(f"/{target}")]
                or [path for path in eligible if path.lower().rsplit("/", 1)[-1] == name]
            )
            chosen += [hit for hit in hits[:2] if hit not in chosen]
        limit = repo.limits.max_file_bytes
        code = []
        for path in chosen[: repo.limits.max_context_files]:
            content = workspace.read(path, "base", limit=limit)
            if content is not None:
                code.append(
                    CodeFile(path=path, content=content, truncated=workspace.size(path) > limit)
                )
        return code

    def _scaffold(
        self,
        ticket: TicketText,
        guidance: Guidance,
        code: Sequence[CodeFile],
        files: Sequence[str],
    ) -> tuple[FixState, Verdict]:
        assert self.repo is not None and self.workspace is not None
        repo, workspace = self.repo, self.workspace
        starter = repo.starter_pr
        # The same rules as a fix, with the starter's own (smaller) size budget.
        rules = repo.model_copy(
            update={
                "limits": repo.limits.model_copy(
                    update={
                        "max_changed_files": starter.max_files,
                        "max_changed_lines": starter.max_lines,
                    }
                )
            }
        )
        baseline = set(files)
        shown = {file.path for file in code if not file.truncated}
        evidence = tuple(
            EvidenceItem(
                evidence_id=item.advice_id,
                kind="guidance",
                title=TITLES.get(item.section, item.section),
                text=item.statement,
            )
            for item in guidance.items
        )
        facts = (
            "This ticket is an ENHANCEMENT. The evidence items are its guidance; their ids "
            "(ADV-nnn) are what a TODO comment refers to.",
            f"New or changed source files must be under: {', '.join(repo.writable_paths)}.",
            f"New test files must be under: {', '.join(repo.new_test_paths)}.",
            f"Keep the scaffold within {starter.max_files} file(s) and {starter.max_lines} "
            "changed line(s).",
            "A file marked [file truncated] is shown for context only and may not be edited.",
        )
        feedback: list[str] = []

        def read_baseline(path: str) -> str | None:
            return workspace.read(path, "base")

        for iteration in range(1, starter.max_iterations + 1):
            proposal = propose_scaffold(
                self.gateway,
                self.ledger,
                EvidenceContext(
                    snapshot_id=None,
                    ticket_title=ticket.title,
                    ticket_text=ticket.text,
                    evidence=evidence,
                    facts=facts,
                    code=tuple(code),
                    feedback=tuple(feedback),
                ),
            )
            if isinstance(proposal, Declined):
                _log().info("starter_declined", iteration=iteration, reason=proposal.code)
                self.reasons.append(proposal.code)
                self.limitations.append(
                    "No model could be asked for a scaffold (or the model budget was used up)."
                    if proposal.llm
                    else f"The model did not start a scaffold: {proposal.detail}"
                )
                self.patch = None
                return "BLOCKED", "BLOCKED"
            self.generation.update(
                fix_provider=proposal.result.provider,
                fix_model_id=proposal.result.model_version or proposal.result.model_id,
                fix_prompt_version=proposal.result.prompt_template_version,
            )
            checked = guardrails.check(
                proposal.changes, rules, read_baseline, "", shown, baseline, allow_new_tests=True
            )
            paths = [file.path for file in checked.files]
            self.summary, self.checklist = proposal.summary, proposal.checklist
            self.patch = Patch(
                iteration=iteration,
                files=list(checked.files),
                diff=_proposal_diff(proposal.changes, paths, read_baseline)[:MAX_DIFF_CHARS],
                cause="",
                summary=proposal.summary,
                policy_status=checked.status,
                findings=list(checked.findings),
            )
            (_log().info if checked.status == "PASS" else _log().warning)(
                "starter_scaffold_checked",
                iteration=iteration,
                policy_status=checked.status,
                files=paths,
                changed_lines=sum(f.added_lines + f.removed_lines for f in checked.files),
                findings=[
                    {"code": f.code, "severity": f.severity, "path": f.path}
                    for f in checked.findings
                ],
            )
            if checked.status == "BLOCKED":
                self._attempt(iteration, "patch_rejected", checked.feedback())
                feedback.append(
                    f"Attempt {iteration} was rejected by the repository's policy: "
                    f"{checked.feedback()}"
                )
                continue
            if checked.status == "REVIEW_REQUIRED":
                # 8.6.4: stopped for an engineer whatever the size of the change. Not pushed.
                self._attempt(iteration, "needs_engineer_review", checked.feedback())
                self.reasons.append(NEEDS_ENGINEER_REVIEW)
                self.limitations.append(
                    "The scaffold needs an engineer's review before it may be pushed, so no "
                    f"pull request was opened: {checked.feedback()}."
                )
                return "PARTIAL", "NEEDS_REVIEW"
            try:
                sha = self._commit(ticket, proposal)
            except (OSError, WorkspaceError) as error:
                if isinstance(error, WorkspaceError) and error.code == "git_unavailable":
                    raise  # infrastructure: the worker retries the run
                self._attempt(iteration, "patch_not_applicable", "The change could not be applied.")
                feedback.append(f"Attempt {iteration} could not be applied to the repository.")
                continue
            diff = workspace.diff(sha, paths)
            branch = self._branch()
            bundle = self.artifacts / "starter.bundle"
            bundle.unlink(missing_ok=True)
            self.candidate_sha = sha
            self.patch = self.patch.model_copy(
                update={
                    "candidate_sha": sha,
                    "tree_sha": workspace.tree_sha(sha),
                    "branch": branch,
                    "bundle_sha256": workspace.bundle(bundle, branch),
                    "bundle_uri": f"{self.settings.artifact_base_uri}{self.request.run_id}/"
                    "starter.bundle",
                    "diff": diff[:MAX_DIFF_CHARS],
                    "diff_truncated": len(diff) > MAX_DIFF_CHARS,
                }
            )
            self._attempt(iteration, "candidate_accepted", "")
            _log().info(
                "starter_committed",
                iteration=iteration,
                candidate_sha=sha,
                tree_sha=self.patch.tree_sha,
                branch=branch,
            )
            self.limitations += [
                "This is a starter scaffold. VisionIQ did not build it, run it or test it.",
                *proposal.limitations,
            ]
            self.reasons.append(NOT_VERIFIED)
            return "PARTIAL", "INCONCLUSIVE"

        self.reasons.append(SCAFFOLD_REJECTED)
        self.limitations.append(
            "No proposed scaffold could be kept: each one broke the repository's policy or "
            "could not be applied to the repository."
        )
        return "BLOCKED", "BLOCKED"

    def _attempt(self, iteration: int, result: str, detail: str) -> None:
        self.attempts.append(
            Attempt(
                iteration=iteration,
                stage="patch",
                candidate_sha=self.candidate_sha,
                result=result,
                detail=detail[:600],
            )
        )

    def _branch(self) -> str:
        ticket = re.sub(r"[^A-Za-z0-9_-]", "-", self.request.ticket.id)[:40]
        return f"visioniq/starter/{ticket}-{self.request.run_id.hex[:8]}"

    def _commit(self, ticket: TicketText, proposal: ScaffoldProposal) -> str:
        """Baseline + the scaffold, as one commit on the run's branch."""
        assert self.workspace is not None
        workspace = self.workspace
        workspace.reset_candidate()
        for change in proposal.changes:
            workspace.write("candidate", change.path, change.content)
        subject = f"Starter for #{self.request.ticket.id}: {proposal.summary or ticket.title}"[:72]
        message = (
            f"{subject}\n\nScaffold proposed by VisionIQ, run {self.request.run_id}. "
            "Not built, not tested, not reviewed.\n"
            f"Baseline: {self.request.base_commit_sha}\n"
        )
        return workspace.commit_candidate(self._branch(), message)

    def _finish(self, state: FixState, verdict: Verdict) -> FixOutcome:
        request = self.request
        head = self.candidate_sha
        gates = starter_gates(self.patch, head)
        calls = self.ledger.calls
        guidance = self.guidance
        total_ms = int((self.clock() - self.started) * 1000)
        result = FixResult(
            fix_id=uuid.uuid4(),
            run_id=request.run_id,
            kind="starter",
            ticket=request.ticket.model_copy(update={"title": self.title}),
            repository_id=request.repository_id,
            base_sha=request.base_commit_sha,
            candidate_sha=head,
            state=state,
            verdict=verdict,
            reason_codes=_unique(self.reasons),
            reproduction=Reproduction(
                reproducibility="UNAVAILABLE",
                detail="Not applicable: an enhancement has no defect to reproduce.",
            ),
            patch=self.patch,
            attempts=self.attempts,
            regression=Regression(
                status="NOT_RUN", historical_found=False, detail="Not run for a starter scaffold."
            ),
            performance=Performance(
                status="NOT_MEASURED", detail="Not measured for a starter scaffold."
            ),
            gates=gates,
            gate_summary=gate_rules.summarize(gates, head),
            starter=(
                Starter(
                    guidance_brief_id=guidance.brief_id,
                    advice_ids=[item.advice_id for item in guidance.items],
                    covered_sections=list(guidance.covered),
                    summary=self.summary,
                    checklist=list(self.checklist),
                )
                if guidance is not None
                else None
            ),
            limitations=_unique(self.limitations)[:MAX_LIMITATIONS],
            generation=Generation(
                routing_revision=ROUTING_REVISION,
                calls=len(calls),
                input_tokens=sum(call.result.input_tokens for call in calls),
                output_tokens=sum(call.result.output_tokens for call in calls),
                **self.generation,
            ),
            policy_revision=self.policy.revision if self.policy else None,
            timings=Timings(fix_ms=total_ms, total_ms=total_ms),
        )
        if verdict == "INCONCLUSIVE" and head is not None and guidance is not None:
            items = {item.advice_id: item for item in guidance.items}
            result = result.model_copy(
                update={
                    "pull_request": report.pull_request(
                        result, self.target_branch, [items[i] for i in result.starter.advice_ids]
                    )
                }
            )
        _log().info(
            "starter_built",
            state=state,
            verdict=verdict,
            reason_codes=result.reason_codes,
            iterations=len(self.attempts),
            draft_pr=result.pull_request is not None,
            total_ms=total_ms,
        )
        return FixOutcome(result, self.ledger)


def _proposal_diff(
    changes: Sequence[guardrails.ProposedChange],
    paths: Sequence[str],
    read_baseline: Callable[[str], str | None],
) -> str:
    lines: list[str] = []
    for change in changes:
        if change.path not in paths:
            continue
        old = read_baseline(change.path)
        lines += difflib.unified_diff(
            (old or "").splitlines(),
            change.content.splitlines(),
            f"a/{change.path}" if old is not None else "/dev/null",
            f"b/{change.path}",
            lineterm="",
        )
    return "\n".join(lines)


def run_starter(
    request: FixRequest,
    sources: StarterSources,
    gateway: Gateway,
    policy: DefectPolicy | None,
    settings: FixSettings,
    clock: Callable[[], float] = time.perf_counter,
) -> FixOutcome:
    """Answer one accepted starter request. Raises only on infrastructure failures."""
    run = _StarterRun(request, sources, gateway, policy, settings, clock)
    with structlog.contextvars.bound_contextvars(run_id=str(request.run_id)):
        try:
            return run.execute()
        finally:
            run.close()
