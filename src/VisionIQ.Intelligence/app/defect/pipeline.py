"""DEF-01 to DEF-06: reproduce the defect, propose a bounded fix and prove it.

    pinned workspace -> reproduction test (model) -> RED on the baseline, for the predicted reason
    -> patch (model) -> policy check -> commit on a branch -> the SAME test GREEN on the candidate
    -> past regression tests -> touched suite -> paired performance runs -> gates and draft PR

Code decides every step: which commands run, where a file may go, whether a failure is the
defect's failure, whether a patch may be applied and what the runs add up to. A model only writes
the test and the patch. Every way of falling short has its own state and reason code, and a run
that used up its budget is FAIL or INCONCLUSIVE, never green:

* BLOCKED   - the defect was not reproduced, so nothing was fixed. An engineer is needed.
* PARTIAL   - reproduced, but there is no verified fix (failed, inconclusive or stopped for review).
* PUBLISHED - red on the baseline, green on the candidate, no known regression. It is still only
              a draft PR: native CI and an engineer's approval are separate gates.

Nothing here pushes, approves or merges.
"""

from __future__ import annotations

import difflib
import json
import math
import re
import shutil
import time
import uuid
from collections.abc import Callable, Collection, Mapping, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath

import structlog

from app.brief.history import TicketText
from app.defect import gates as gate_rules
from app.defect import guardrails, report
from app.defect import regression as regression_rules
from app.defect.generate import (
    REPRODUCTION_UNAVAILABLE,
    TEST_INVALID,
    Declined,
    TestProposal,
    context_for,
    propose_patch,
    propose_test,
)
from app.defect.performance import compare, measure_pairs
from app.defect.policy import DefectPolicy, RepositoryPolicy, RunnerPolicy, matches, render
from app.defect.results import CheckVerdict, Observation, classify, parse_junit, run_with_policy
from app.defect.runner import Runner, RunResult, build_runner, store_log
from app.defect.schema import (
    Attempt,
    CheckEvidence,
    Execution,
    ExecutionManifest,
    ExpectedFailure,
    FixRequest,
    FixResult,
    FixState,
    GeneratedTest,
    Generation,
    Outcome,
    Patch,
    Performance,
    Regression,
    RegressionTest,
    Reproducibility,
    Reproduction,
    Timings,
    Verdict,
)
from app.defect.sources import FixSources
from app.defect.workspace import (
    OUTPUT_DIR,
    Tree,
    Workspace,
    WorkspaceError,
    is_safe_path,
    prepare,
    sha256_text,
)
from app.knowledge.redaction import redact
from app.knowledge.retrieval import extract_signals
from app.llm.gateway import Budget, RunLedger
from app.llm.gateway import ModelGateway as Gateway
from app.llm.injection import scan
from app.llm.routing import ROUTING_REVISION
from app.llm.types import CodeFile, DataClassification

# Reason codes (analysis.completed carries them).
REPOSITORY_NOT_CONFIGURED = "repository_not_configured"
RUNNER_NOT_ALLOWED = "runner_not_allowed"
TICKET_NOT_READABLE = "ticket_not_readable"
SCOPE_NOT_RESOLVED = "scope_not_resolved"
NEEDS_ENGINEER_REVIEW = "fix_needs_engineer_review"
BUDGET_EXHAUSTED = "fix_budget_exhausted"
CANDIDATE_TEST_FAILED = "candidate_test_failed"
CANDIDATE_TEST_INCONCLUSIVE = "candidate_test_inconclusive"
REGRESSION_FAILED = "regression_failed"
REGRESSION_INCONCLUSIVE = "regression_inconclusive"
SUITE_FAILED = "suite_failed"
SUITE_INCONCLUSIVE = "suite_inconclusive"
PERFORMANCE_REGRESSION = "performance_regression"
PERFORMANCE_INCONCLUSIVE = "performance_inconclusive"

_REPRODUCIBILITY: dict[str, Reproducibility] = {
    "FAIL_TARGET_SIGNATURE": "REPRODUCED",
    "PASS": "NOT_REPRODUCED",
    "FAIL_DIFFERENT_SIGNATURE": "INCONCLUSIVE",
    "FLAKY": "FLAKY",
    "ENVIRONMENT_ERROR": "ENVIRONMENT_ERROR",
    "NOT_RUN": "ENVIRONMENT_ERROR",
    "TIMEOUT": "INCONCLUSIVE",
}
_REPRODUCTION_REASON = {
    "PASS": "reproduction_not_red",
    "FAIL_DIFFERENT_SIGNATURE": "reproduction_different_failure",
    "FLAKY": "reproduction_flaky",
    "ENVIRONMENT_ERROR": "reproduction_environment_error",
    "NOT_RUN": "reproduction_test_not_run",
    "TIMEOUT": "reproduction_timeout",
}
_WORKSPACE_TEXT = {
    "baseline_not_pinned": "The baseline is not a full commit SHA, so the run could not be pinned.",
    "baseline_not_found": "The baseline commit is not in the repository.",
    "repository_unavailable": "The repository could not be cloned.",
}
_FAILED: tuple[Outcome, ...] = ("FAIL_TARGET_SIGNATURE", "FAIL_DIFFERENT_SIGNATURE")

MAX_LIMITATIONS = 12
MAX_DIFF_CHARS = 60_000
MAX_LISTED_TESTS = 20
TOOLCHAIN_TIMEOUT_SECONDS = 60.0
# Less time than this left in the run's budget: an execution is not started any more.
MIN_EXECUTION_SECONDS = 1.0


@dataclass(frozen=True)
class FixSettings:
    """Where a run works and what it may spend. The time budget covers the runner too."""

    work_dir: Path
    artifact_dir: Path
    classification: DataClassification = DataClassification.INTERNAL
    max_calls: int = 6
    max_tokens: int = 400_000
    max_seconds: float = 1800.0
    keep_workspace: bool = False
    bot_name: str = "VisionIQ Bot"
    bot_email: str = "visioniq-bot@users.noreply.invalid"
    # Prefix of a log's address in the result, e.g. "gs://bucket/fix-runs/". Empty: relative.
    artifact_base_uri: str = ""
    production: bool = False
    # Read-only token for cloning a private repository, and the hosts it may be sent to.
    repo_access_token: str | None = None
    repo_token_hosts: tuple[str, ...] = ("dev.azure.com",)


@dataclass(frozen=True)
class FixOutcome:
    result: FixResult
    ledger: RunLedger


def _log() -> structlog.stdlib.BoundLogger:
    """The logger of the current run. Events carry ids, codes, counts and paths only: never
    ticket text, source code, model output or test output."""
    return structlog.get_logger()


def _unique(items: Sequence[str]) -> list[str]:
    return list(dict.fromkeys(items))


def _proposal_diff(
    changes: Sequence[guardrails.ProposedChange],
    paths: Collection[str],
    read_baseline: Callable[[str], str | None],
) -> str:
    """A unified diff of a proposal that was not committed (rejected or stopped for review)."""
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


class _FixRun:
    """One run and everything it recorded. ``execute`` never raises for a failed fix, only for
    infrastructure failures (database, disk), which the worker retries."""

    def __init__(
        self,
        request: FixRequest,
        sources: FixSources,
        gateway: Gateway,
        policy: DefectPolicy | None,
        settings: FixSettings,
        runner_for: Callable[[RunnerPolicy], Runner],
        clock: Callable[[], float],
    ) -> None:
        self.request = request
        self.sources = sources
        self.gateway = gateway
        self.policy = policy
        self.settings = settings
        self.runner_for = runner_for
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
        self.runner: Runner | None = None
        self.title = request.ticket.title
        self.reasons: list[str] = []
        self.limitations: list[str] = []
        self.attempts: list[Attempt] = []
        self.executions: list[Execution] = []
        self.manifests: list[ExecutionManifest] = []
        self.generation: dict[str, str | None] = {}
        self.reproduction = Reproduction(reproducibility="INCONCLUSIVE", detail="Not attempted.")
        self.reproduction_ms = self.fix_ms = 0
        self._toolchain: dict[str, str] | None = None
        # The export the last execution ran in (see _run).
        self._stage: str | None = None
        self._clear_candidate()

    def _clear_candidate(self) -> None:
        """A result describes one proposal: the last. Earlier ones stay in ``attempts``."""
        self.patch: Patch | None = None
        self.candidate_sha: str | None = None
        self.candidate_test: CheckEvidence | None = None
        self.suite: CheckEvidence | None = None
        self.regression = Regression(
            status="NOT_RUN", historical_found=False, detail="No candidate reached this step."
        )
        self.performance = Performance(
            status="NOT_MEASURED", detail="No candidate reached this step."
        )

    def close(self) -> None:
        if self.workspace is not None and not self.settings.keep_workspace:
            self.workspace.cleanup()
            _log().debug("fix_workspace_removed")

    # ---- The run -------------------------------------------------------------------------

    def execute(self) -> FixOutcome:
        request = self.request
        _log().info(
            "fix_started",
            project_id=request.ticket.project,
            ticket_id=request.ticket.id,
            ticket_revision=request.ticket.revision,
            repository_id=request.repository_id,
            base_sha=request.base_commit_sha,
            policy_revision=self.policy.revision if self.policy else None,
        )
        repo = self.policy.repository(request.repository_id) if self.policy else None
        if repo is None:
            self.limitations.append(
                "This repository is not in the fix policy, so no code was fetched or run."
            )
            return self._blocked(REPOSITORY_NOT_CONFIGURED)
        self.repo = repo
        ticket = self.sources.load_ticket(request.ticket.project, request.ticket.id)
        if ticket is None:
            self.limitations.append(
                "The ticket is not in the knowledge base under this project's access scope."
            )
            return self._blocked(TICKET_NOT_READABLE)
        self.title = ticket.title
        # A retried run starts its evidence again; manifests are written once and never edited.
        shutil.rmtree(self.artifacts, ignore_errors=True)
        if scan("ticket", f"{ticket.title}\n{ticket.text}"):
            self.limitations.append(
                "The ticket contains instructions addressed to an AI assistant. "
                "They were treated as data and not followed."
            )
        try:
            self.runner = self.runner_for(repo.runner)
        except ValueError:
            self.limitations.append("The configured runner is not allowed in this environment.")
            return self._blocked(RUNNER_NOT_ALLOWED)
        try:
            self.workspace = prepare(
                repo.source,
                request.base_commit_sha,
                self.settings.work_dir / str(request.run_id),
                self.settings.bot_name,
                self.settings.bot_email,
                self.settings.repo_access_token,
                self.settings.repo_token_hosts,
            )
        except WorkspaceError as error:
            self.limitations.append(
                _WORKSPACE_TEXT.get(error.code, "The workspace could not be prepared.")
            )
            return self._blocked(error.code)
        if self.runner.describe().network != "none":
            self.limitations.append(
                "Commands ran on the local runner, which has no network isolation."
            )
        files = self.workspace.files()
        code = self._context_files(ticket, files)
        described = self.runner.describe()
        _log().info(
            "fix_workspace_ready",
            runner=described.kind,
            runner_image=described.image,
            network=described.network,
            baseline_files=len(files),
            # Paths only: file content and ticket text are never logged.
            context_files=[file.path for file in code],
            truncated_files=sum(1 for file in code if file.truncated),
        )
        if not code:
            self.limitations.append(
                "No source file named by the ticket or its brief was found at the baseline, "
                "so there was nothing to base a test or a fix on."
            )
            return self._blocked(SCOPE_NOT_RESOLVED)

        step = self.clock()
        test = self._reproduce(ticket, code, files)
        self.reproduction_ms = int((self.clock() - step) * 1000)
        if test is None:
            return self._finish("BLOCKED", "BLOCKED")
        step = self.clock()
        verdict = self._fix(ticket, code, files, test)
        self.fix_ms = int((self.clock() - step) * 1000)
        return self._finish("PUBLISHED" if verdict == "VERIFIED" else "PARTIAL", verdict)

    def _blocked(self, reason: str) -> FixOutcome:
        self.reasons.append(reason)
        _log().warning("fix_not_started", reason=reason)
        return self._finish("BLOCKED", "BLOCKED")

    # ---- Context -------------------------------------------------------------------------

    def _context_files(self, ticket: TicketText, files: Sequence[str]) -> list[CodeFile]:
        """The source files a model is shown: those the brief predicted or the ticket names."""
        repo, _ = self._ready()
        wanted = [
            *self.sources.brief_scope(self.request.ticket.project, self.request.ticket.id),
            *extract_signals(f"{ticket.title}\n{ticket.text}").files,
        ]
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
        return self._read(chosen[: repo.limits.max_context_files])

    def _read(self, paths: Sequence[str]) -> list[CodeFile]:
        repo, workspace = self._ready()
        limit = repo.limits.max_file_bytes
        code = []
        for path in paths:
            content = workspace.read(path, "base", limit=limit)
            if content is not None:
                code.append(
                    CodeFile(path=path, content=content, truncated=workspace.size(path) > limit)
                )
        return code

    def _test_examples(self, code: Sequence[CodeFile], tests: Sequence[str]) -> list[CodeFile]:
        """Existing tests of the same subject (or any test), so the model sees the conventions."""
        subjects = {regression_rules.stem(file.path) for file in code}
        related = [path for path in tests if regression_rules.stem(path) in subjects]
        return self._read((related or list(tests))[:2])

    def _ready(self) -> tuple[RepositoryPolicy, Workspace]:
        assert self.repo is not None and self.workspace is not None
        return self.repo, self.workspace

    # ---- Executions and manifests --------------------------------------------------------

    def _uri(self, name: str) -> str:
        return f"{self.settings.artifact_base_uri}{self.request.run_id}/{name}"

    def _tool_versions(self) -> dict[str, str]:
        if self._toolchain is None:
            repo, workspace = self._ready()
            assert self.runner is not None
            self._toolchain = {}
            if repo.toolchain:
                # Like every command: in an export, never in a tree the host runs git in.
                root = workspace.export(self.request.base_commit_sha, "toolchain", "base")
                for name, argv in sorted(repo.toolchain.items()):
                    log = self.artifacts / f"toolchain-{name}.log"
                    run = self.runner.run(list(argv), root, TOOLCHAIN_TIMEOUT_SECONDS, log)
                    text = log.read_text(encoding="utf-8", errors="replace").strip()
                    first = text.splitlines()[0][:120] if text else ""
                    self._toolchain[name] = first if run.exit_code == 0 and first else "unavailable"
                workspace.discard("toolchain")
        return self._toolchain

    def _hashes(self, paths: Sequence[str]) -> dict[str, str]:
        _, workspace = self._ready()
        return {path: workspace.sha256(path) or "missing" for path in paths}

    def _manifest(self, candidate_sha: str | None, test_ids: Sequence[str]) -> str:
        """8.6.3: written before the code it describes is run, and never changed."""
        repo, _ = self._ready()
        assert self.runner is not None and self.policy is not None
        runner = repo.runner
        manifest = ExecutionManifest(
            manifest_id=f"M{len(self.manifests) + 1}",
            ticket_id=self.request.ticket.id,
            repository_id=repo.repository_id,
            base_sha=self.request.base_commit_sha,
            candidate_sha=candidate_sha,
            runner=self.runner.describe(),
            toolchain=self._tool_versions(),
            lock_hashes=self._hashes(repo.lock_files),
            fixture_hashes=self._hashes(repo.fixture_paths),
            dependency_restore=repo.dependency_restore,
            environment_names=self.runner.environment_names(),
            timeouts_seconds={
                "test_file": runner.test_timeout_seconds,
                "suite": runner.suite_timeout_seconds,
                "benchmark": runner.benchmark_timeout_seconds,
            },
            commands={name: list(argv) for name, argv in sorted(repo.recipes.items())},
            expected_test_ids=list(test_ids),
            policy_revision=self.policy.revision,
            created_at=datetime.now(UTC),
        )
        text = manifest.model_dump_json(exclude={"sha256"}, indent=2)
        manifest = manifest.model_copy(update={"sha256": sha256_text(text)})
        self.artifacts.mkdir(parents=True, exist_ok=True)
        with (self.artifacts / f"{manifest.manifest_id}.json").open("x", encoding="utf-8") as out:
            out.write(text)
        self.manifests.append(manifest)
        _log().info(
            "fix_manifest_written",
            manifest_id=manifest.manifest_id,
            manifest_sha256=manifest.sha256,
            candidate_sha=candidate_sha,
            expected_test_ids=list(test_ids),
        )
        return manifest.manifest_id

    def _run(
        self,
        purpose: str,
        tree: Tree,
        recipe: str,
        manifest_id: str,
        test_path: str | None = None,
        test_id: str | None = None,
        expected: ExpectedFailure | None = None,
        overlay: Mapping[str, str] | None = None,
    ) -> tuple[Observation, Path]:
        """Run one allowlisted recipe on a fresh export of a tree's commit and record what
        happened. ``overlay`` are files that are not in the commit (the uncommitted reproduction
        test on the baseline)."""
        repo, workspace = self._ready()
        assert self.runner is not None
        execution_id = f"X{len(self.executions) + 1}"
        benchmark = recipe == "benchmark"
        relative = f"{OUTPUT_DIR}/{execution_id}.{'json' if benchmark else 'xml'}"
        commit = self.request.base_commit_sha if tree == "base" else self.candidate_sha
        assert commit is not None
        # Exactly this commit and nothing an earlier execution left behind. The previous export
        # is dropped only now, because its caller may still have read the report from it.
        if self._stage is not None:
            workspace.discard(self._stage)
        self._stage = execution_id
        root = workspace.export(commit, execution_id, tree, overlay)
        output = root / relative
        output.parent.mkdir(exist_ok=True)
        argv = render(
            repo.recipes[recipe],
            {
                "test_path": test_path or "",
                "test_name": PurePosixPath(test_path or "").name.split(".", 1)[0],
                "junit": relative,
                "output": relative,
            },
        )
        runner = repo.runner
        timeout = {
            "test_file": runner.test_timeout_seconds,
            "suite": runner.suite_timeout_seconds,
            "benchmark": runner.benchmark_timeout_seconds,
        }[recipe]
        log = self.artifacts / f"{execution_id}.log"
        remaining = self.settings.max_seconds - (self.clock() - self.started)
        if remaining < MIN_EXECUTION_SECONDS:
            # The run's time budget covers the runner too: nothing is started that cannot end
            # inside it, so a run never outlives the worker's lease.
            run = RunResult(
                datetime.now(UTC), 0, None, True, store_log(log, b"run_time_budget_exhausted")
            )
            self.limitations.append(
                "The run's time budget was used up before every check could be run."
            )
            _log().warning("fix_time_budget_exhausted", execution_id=execution_id)
        else:
            run = self.runner.run(argv, root, min(timeout, remaining), log)
        outcome: Outcome
        if not benchmark:
            outcome, signature = classify(run, parse_junit(output), test_id, expected)
        elif run.error is not None:
            outcome, signature = "ENVIRONMENT_ERROR", run.error
        elif run.timed_out:
            outcome, signature = "TIMEOUT", None
        else:
            outcome, signature = (
                ("PASS", None) if run.exit_code == 0 else ("ENVIRONMENT_ERROR", None)
            )
        # Test output is untrusted and may quote a credential.
        signature = redact(signature).text if signature else None
        self.executions.append(
            Execution(
                execution_id=execution_id,
                manifest_id=manifest_id,
                purpose=purpose,  # type: ignore[arg-type]
                code="baseline" if tree == "base" else "candidate",
                commit_sha=commit,
                command=argv,
                started_at=run.started_at,
                duration_ms=run.duration_ms,
                exit_code=run.exit_code,
                timed_out=run.timed_out,
                outcome=outcome,
                test_id=test_id,
                failure_signature=signature,
                log_uri=self._uri(f"{execution_id}.log"),
                log_sha256=run.log_sha256,
            )
        )
        # One line per runner execution. A run the environment broke is a warning; an ordinary
        # pass or fail is per-item detail (LOG_LEVEL=DEBUG). The failure text stays in the run's
        # log file: test output is untrusted and is never copied into the service log.
        broken = outcome in ("ENVIRONMENT_ERROR", "TIMEOUT")
        (_log().warning if broken else _log().debug)(
            "fix_execution",
            execution_id=execution_id,
            manifest_id=manifest_id,
            purpose=purpose,
            code="baseline" if tree == "base" else "candidate",
            commit_sha=commit,
            recipe=recipe,
            test_path=test_path,
            outcome=outcome,
            exit_code=run.exit_code,
            timed_out=run.timed_out,
            runner_error=run.error,
            duration_ms=run.duration_ms,
            log_sha256=run.log_sha256,
        )
        return Observation(execution_id, outcome, signature), output

    def _checked(self, execute: Callable[[], Observation], expectation: str) -> CheckVerdict:
        limits = self._ready()[0].limits
        return run_with_policy(
            execute,
            expectation,  # type: ignore[arg-type]
            limits.flaky_reruns,
            limits.red_confirmations,
            limits.environment_retries,
        )

    @staticmethod
    def _evidence(path: str | None, test_id: str | None, verdict: CheckVerdict) -> CheckEvidence:
        return CheckEvidence(
            test_path=path,
            test_id=test_id,
            outcome=verdict.outcome,
            first_outcome=verdict.first_outcome,
            execution_ids=verdict.execution_ids,
            failure_signature=verdict.signature,
        )

    # ---- DEF-01: reproduce ---------------------------------------------------------------

    def _reproduce(
        self, ticket: TicketText, code: Sequence[CodeFile], files: Sequence[str]
    ) -> TestProposal | None:
        """A test that is red on the baseline for the predicted reason, or None with the reason."""
        repo, _ = self._ready()
        tests = [p for p in files if is_safe_path(p) and matches(p, repo.test_file_patterns)]
        facts = (
            f"Add the new test file under: {', '.join(repo.new_test_paths)}.",
            f"Test files in this repository match: {', '.join(repo.test_file_patterns)}.",
            f"A test file is run with: {' '.join(repo.recipes['test_file'])}",
            "Existing test files: " + (", ".join(tests[:MAX_LISTED_TESTS]) or "none"),
        )
        context_code = [*code, *self._test_examples(code, tests)]
        feedback: list[str] = []
        last: CheckVerdict | None = None
        for attempt in range(1, repo.limits.max_test_attempts + 1):
            proposal = propose_test(
                self.gateway,
                self.ledger,
                context_for(ticket.title, ticket.text, facts, context_code, feedback),
                repo,
                set(files),
            )
            if isinstance(proposal, Declined):
                # The code only: an "unavailable" reason is model text and is not logged.
                _log().warning(
                    "fix_reproduction_test_declined",
                    attempt=attempt,
                    reason=proposal.code,
                    model_unavailable=proposal.llm,
                )
                if proposal.llm:
                    self.reasons.append(proposal.code)
                    if last is None:
                        self.reproduction = Reproduction(
                            reproducibility="INCONCLUSIVE", detail=proposal.detail
                        )
                    break
                if proposal.code == REPRODUCTION_UNAVAILABLE:
                    # 8.6.2: say so and escalate; never invent a red.
                    self.reproduction = Reproduction(
                        reproducibility="UNAVAILABLE", detail=proposal.detail
                    )
                    self.reasons.append(REPRODUCTION_UNAVAILABLE)
                    self.limitations.append(
                        "The defect cannot be reproduced in the sandbox and needs an engineer: "
                        + proposal.detail
                    )
                    return None
                self.attempts.append(
                    Attempt(
                        iteration=attempt,
                        stage="reproduction_test",
                        result="test_rejected",
                        detail=proposal.detail,
                    )
                )
                feedback.append(f"Attempt {attempt} was not run: {proposal.detail}")
                continue
            self.generation.update(
                test_provider=proposal.result.provider,
                test_prompt_version=proposal.result.prompt_template_version,
            )
            manifest = self._manifest(None, [proposal.test_id])
            # The test is not part of the baseline commit: it is laid over each export of it. The
            # baseline tree itself is never written to.
            last = self._checked(
                lambda p=proposal, m=manifest: self._run(
                    "reproduction",
                    "base",
                    "test_file",
                    m,
                    p.path,
                    p.test_id,
                    p.expected,
                    {p.path: p.content},
                )[0],
                "red",
            )
            outcome = last.outcome
            self.reproduction = Reproduction(
                reproducibility=_REPRODUCIBILITY.get(outcome, "INCONCLUSIVE"),
                test=GeneratedTest(
                    path=proposal.path,
                    test_id=proposal.test_id,
                    sha256=sha256_text(proposal.content),
                    content=proposal.content,
                    rationale=proposal.rationale,
                ),
                expected_failure=proposal.expected,
                evidence=self._evidence(proposal.path, proposal.test_id, last),
                detail=f"The test ended {outcome} on the unchanged baseline.",
            )
            self.attempts.append(
                Attempt(
                    iteration=attempt,
                    stage="reproduction_test",
                    result=outcome.lower(),
                    detail=last.signature or "",
                )
            )
            self.limitations.extend(proposal.limitations)
            reproduced = outcome == "FAIL_TARGET_SIGNATURE"
            (_log().info if reproduced else _log().warning)(
                "fix_reproduction_attempt",
                attempt=attempt,
                test_path=proposal.path,
                test_id=proposal.test_id,
                outcome=outcome,
                first_outcome=last.first_outcome,
                reproducibility=self.reproduction.reproducibility,
                execution_ids=last.execution_ids,
            )
            if outcome == "FAIL_TARGET_SIGNATURE":
                return proposal
            if outcome in ("FLAKY", "TIMEOUT"):
                break  # another test would not make this one trustworthy
            feedback.append(
                f"Attempt {attempt} ran on the current code and ended {outcome}"
                + (f": {last.signature}" if last.signature else "")
                + ". The test must fail with the failure you predict."
            )
        if last is not None:
            self.reasons.append(_REPRODUCTION_REASON.get(last.outcome, "reproduction_inconclusive"))
        elif not self.reasons:
            self.reasons.append(TEST_INVALID)
        _log().warning(
            "fix_not_reproduced",
            reproducibility=self.reproduction.reproducibility,
            reason_codes=self.reasons,
        )
        self.limitations.append(
            "The defect was not reproduced, so no fix was attempted. It needs an engineer."
        )
        return None

    # ---- DEF-02 to DEF-04: fix and verify ------------------------------------------------

    def _fix(
        self,
        ticket: TicketText,
        code: Sequence[CodeFile],
        files: Sequence[str],
        test: TestProposal,
    ) -> Verdict:
        repo, workspace = self._ready()
        limits = repo.limits
        baseline = set(files)
        shown = {file.path for file in code if not file.truncated}
        facts = (
            f"The failing test is {test.test_id} in {test.path}. It must pass unchanged.",
            f"Only files under these paths may be changed: {', '.join(repo.writable_paths)}.",
            "A file marked [file truncated] is shown for context only and may not be edited.",
        )
        context_code = [*code, CodeFile(path=test.path, content=test.content)]
        red = self.reproduction.evidence.failure_signature if self.reproduction.evidence else None
        feedback = [f"On the current code the test fails with: {red}"] if red else []
        tested = False

        def read_baseline(path: str) -> str | None:
            return workspace.read(path, "base")

        for iteration in range(1, limits.max_fix_iterations + 1):
            proposal = propose_patch(
                self.gateway,
                self.ledger,
                context_for(ticket.title, ticket.text, facts, context_code, feedback),
            )
            if isinstance(proposal, Declined):
                _log().warning("fix_patch_declined", iteration=iteration, reason=proposal.code)
                self.reasons.append(proposal.code)
                self.limitations.append(
                    "No model could be asked for a fix (or the model budget was used up), so "
                    "the run stopped. The result is not green."
                )
                return "FAIL" if tested else "INCONCLUSIVE"
            self.generation.update(
                fix_provider=proposal.result.provider,
                fix_model_id=proposal.result.model_version or proposal.result.model_id,
                fix_prompt_version=proposal.result.prompt_template_version,
            )
            checked = guardrails.check(
                proposal.changes, repo, read_baseline, test.path, shown, baseline
            )
            paths = [file.path for file in checked.files]
            self._clear_candidate()
            self.patch = Patch(
                iteration=iteration,
                files=list(checked.files),
                diff=_proposal_diff(proposal.changes, paths, read_baseline)[:MAX_DIFF_CHARS],
                cause=proposal.cause,
                summary=proposal.summary,
                policy_status=checked.status,
                findings=list(checked.findings),
            )
            # S11-06: which rules the proposal hit. Rule codes and paths; never the change itself.
            (_log().info if checked.status == "PASS" else _log().warning)(
                "fix_patch_checked",
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
                    f"Attempt {iteration} was rejected by the repository's policy and not run: "
                    f"{checked.feedback()}"
                )
                continue
            if checked.status == "REVIEW_REQUIRED":
                # 8.6.4: stopped for an engineer whatever the size of the change. Not run.
                self._attempt(iteration, "needs_engineer_review", checked.feedback())
                self.reasons.append(NEEDS_ENGINEER_REVIEW)
                self.limitations.append(
                    "The proposed change needs an engineer's review before it may be applied "
                    f"or run: {checked.feedback()}."
                )
                return "NEEDS_REVIEW"

            try:
                sha = self._commit(ticket, test, proposal.changes, proposal.summary)
            except (OSError, WorkspaceError) as error:
                if isinstance(error, WorkspaceError) and error.code == "git_unavailable":
                    raise  # infrastructure: the worker retries the run
                # The proposal itself cannot be applied (a path under a file, nothing to commit).
                # That is the proposal's fault, not the run's: the model may try again.
                self._attempt(iteration, "patch_not_applicable", "The change could not be applied.")
                feedback.append(
                    f"Attempt {iteration} could not be applied to the repository and was not run."
                )
                continue
            diff = workspace.diff(sha, paths)
            branch = self._branch()
            bundle = self.artifacts / "candidate.bundle"
            bundle.unlink(missing_ok=True)
            self.patch = self.patch.model_copy(
                update={
                    "candidate_sha": sha,
                    "tree_sha": workspace.tree_sha(sha),
                    "branch": branch,
                    "bundle_sha256": workspace.bundle(bundle, branch),
                    "bundle_uri": self._uri("candidate.bundle"),
                    "diff": diff[:MAX_DIFF_CHARS],
                    "diff_truncated": len(diff) > MAX_DIFF_CHARS,
                }
            )
            _log().info(
                "fix_candidate_committed",
                iteration=iteration,
                candidate_sha=sha,
                tree_sha=self.patch.tree_sha,
                branch=branch,
                bundle_sha256=self.patch.bundle_sha256,
            )
            manifest = self._manifest(sha, [test.test_id])
            verdict = self._checked(
                lambda m=manifest: self._run(
                    "candidate", "candidate", "test_file", m, test.path, test.test_id, test.expected
                )[0],
                "green",
            )
            self.candidate_test = self._evidence(test.path, test.test_id, verdict)
            (_log().info if verdict.outcome == "PASS" else _log().warning)(
                "fix_candidate_tested",
                iteration=iteration,
                candidate_sha=sha,
                test_id=test.test_id,
                outcome=verdict.outcome,
                first_outcome=verdict.first_outcome,
                execution_ids=verdict.execution_ids,
            )
            if verdict.outcome != "PASS":
                self._attempt(
                    iteration, f"candidate_test_{verdict.outcome.lower()}", verdict.signature or ""
                )
                if verdict.outcome in _FAILED:
                    tested = True
                    feedback.append(
                        f"Attempt {iteration} ({proposal.summary or 'no summary'}) did not make "
                        f"the test pass: {verdict.signature}"
                    )
                    continue
                self.reasons.append(CANDIDATE_TEST_INCONCLUSIVE)
                self.limitations.append(
                    f"The test ended {verdict.outcome} on the candidate, so the fix is unproven."
                )
                return "INCONCLUSIVE"

            # DEF-04: a fix that passes the new test but breaks an old one stops here.
            self.regression = self._replay(ticket, paths, baseline, test.path, manifest)
            replayed = self.regression.tests
            (_log().info if self.regression.status in ("PASS", "NOT_FOUND") else _log().warning)(
                "fix_regression_replayed",
                iteration=iteration,
                status=self.regression.status,
                historical_found=self.regression.historical_found,
                tests=len(replayed),
                unavailable=sum(1 for item in replayed if item.availability == "UNAVAILABLE"),
                not_passing=[item.test_path for item in replayed if item.outcome != "PASS"],
            )
            if self.regression.status == "FAIL":
                tested = True
                self._attempt(iteration, REGRESSION_FAILED, self.regression.detail)
                broken = [
                    f"{item.test_path}: {item.failure_signature}"
                    for item in self.regression.tests
                    if item.outcome in _FAILED and item.baseline_outcome == "PASS"
                ]
                feedback.append(
                    f"Attempt {iteration} made the test pass but broke tests that passed "
                    f"before: {'; '.join(broken[:3])}"
                )
                continue
            if self._suite(manifest) == "FAIL":
                tested = True
                signature = self.suite.failure_signature if self.suite else None
                self._attempt(iteration, SUITE_FAILED, signature or "")
                feedback.append(
                    f"Attempt {iteration} made the test pass but broke the project's suite: "
                    f"{signature}"
                )
                continue
            self.performance = self._measure(paths, manifest)
            measured = self.performance
            (
                _log().info
                if measured.status in ("NO_REGRESSION_DEMONSTRATED", "NOT_MEASURED")
                else _log().warning
            )(
                "fix_performance_measured",
                status=measured.status,
                pairs=measured.pairs,
                metric=measured.metric,
                change_percent=measured.change_percent,
                noise_percent=measured.noise_percent,
                threshold_percent=measured.threshold_percent,
            )
            self._attempt(iteration, "candidate_accepted", "")
            return self._settle()

        _log().warning("fix_budget_exhausted", iterations=limits.max_fix_iterations)
        self.reasons.append(BUDGET_EXHAUSTED)
        if self.regression.status == "FAIL":
            self.reasons.append(REGRESSION_FAILED)
        elif self.suite is not None and self.suite.outcome in _FAILED:
            self.reasons.append(SUITE_FAILED)
        elif self.candidate_test is not None and self.candidate_test.outcome in _FAILED:
            self.reasons.append(CANDIDATE_TEST_FAILED)
        self.limitations.append(
            f"No candidate passed every check within {limits.max_fix_iterations} iteration(s). "
            "The result is not green."
        )
        return "FAIL" if tested else "INCONCLUSIVE"

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
        accepted = result == "candidate_accepted"
        (_log().info if accepted else _log().warning)(
            "fix_iteration_ended",
            iteration=iteration,
            result=result,
            candidate_sha=self.candidate_sha,
        )

    def _branch(self) -> str:
        ticket = re.sub(r"[^A-Za-z0-9_-]", "-", self.request.ticket.id)[:40]
        return f"visioniq/fix/{ticket}-{self.request.run_id.hex[:8]}"

    def _commit(
        self,
        ticket: TicketText,
        test: TestProposal,
        changes: Sequence[guardrails.ProposedChange],
        summary: str,
    ) -> str:
        """Baseline + the reproduction test + the change, as one commit on the run's branch."""
        _, workspace = self._ready()
        workspace.reset_candidate()
        workspace.write("candidate", test.path, test.content)
        for change in changes:
            workspace.write("candidate", change.path, change.content)
        subject = f"Fix #{self.request.ticket.id}: {summary or ticket.title}"[:72]
        message = (
            f"{subject}\n\nProposed by VisionIQ, run {self.request.run_id}. Not reviewed.\n"
            f"Reproduction test: {test.path} ({test.test_id})\n"
            f"Baseline: {self.request.base_commit_sha}\n"
        )
        self.candidate_sha = workspace.commit_candidate(self._branch(), message)
        return self.candidate_sha

    def _replay(
        self,
        ticket: TicketText,
        changed: Sequence[str],
        baseline: Collection[str],
        reproduction_test: str,
        manifest: str,
    ) -> Regression:
        """S11-08 / S11-09: run the area's old tests on the candidate."""
        repo, _ = self._ready()
        # Full paths, not file names: a fix elsewhere that touched a file of the same name is
        # not history of this area.
        fixes = self.sources.historical_fixes(ticket, list(changed))
        found = regression_rules.discover(
            fixes, baseline, changed, repo, exclude={reproduction_test}, limit=False
        )
        candidates = found[: repo.limits.max_regression_tests]
        if len(found) > len(candidates):
            self.limitations.append(
                f"{len(found) - len(candidates)} more regression test(s) of this area were "
                f"found but not run (the limit is {repo.limits.max_regression_tests})."
            )
        tests: list[RegressionTest] = []
        for candidate in candidates:
            common = {
                "test_path": candidate.test_path,
                "tier": candidate.tier,
                "discovery_method": candidate.discovery_method,
                "ticket_id": candidate.ticket_id,
                "fix_reference": candidate.fix_reference,
            }
            if not candidate.available or not is_safe_path(candidate.test_path):
                tests.append(
                    RegressionTest(availability="UNAVAILABLE", outcome="UNAVAILABLE", **common)
                )
                continue
            verdict = self._checked(
                lambda c=candidate: self._run(
                    "regression", "candidate", "test_file", manifest, c.test_path
                )[0],
                "green",
            )
            ids = verdict.execution_ids
            baseline_outcome: Outcome | None = None
            if verdict.outcome in _FAILED:
                # Was it already failing before the change? Then the change did not break it.
                before, _ = self._run(
                    "regression", "base", "test_file", manifest, candidate.test_path
                )
                baseline_outcome = before.outcome
                ids.append(before.execution_id)
            tests.append(
                RegressionTest(
                    availability="AVAILABLE",
                    outcome=verdict.outcome,
                    baseline_outcome=baseline_outcome,
                    execution_ids=ids,
                    failure_signature=verdict.signature,
                    **common,
                )
            )
        return regression_rules.summarize(tests)

    def _suite(self, manifest: str) -> str | None:
        """S11-10 in the sandbox: the touched project's suite on the candidate."""
        repo, _ = self._ready()
        if "suite" not in repo.recipes:
            return None
        verdict = self._checked(
            lambda: self._run("suite", "candidate", "suite", manifest)[0], "green"
        )
        outcome = verdict.outcome
        ids = verdict.execution_ids
        if outcome in _FAILED:
            before, _ = self._run("suite", "base", "suite", manifest)
            ids.append(before.execution_id)
            if before.outcome != "PASS":
                outcome = "INCONCLUSIVE"  # the suite was not passing before the change either
        self.suite = CheckEvidence(
            test_path=None,
            test_id=None,
            outcome=outcome,
            first_outcome=verdict.first_outcome,
            execution_ids=ids,
            failure_signature=verdict.signature,
        )
        (_log().info if outcome == "PASS" else _log().warning)(
            "fix_suite_run",
            outcome=outcome,
            first_outcome=verdict.first_outcome,
            execution_ids=ids,
        )
        return "FAIL" if outcome in _FAILED else outcome

    def _measure(self, changed: Sequence[str], manifest: str) -> Performance:
        """S11-11: paired benchmark runs, when the change touches a path that has a workload."""
        repo, _ = self._ready()
        benchmark = repo.benchmark
        if benchmark is None:
            return Performance(
                status="NOT_MEASURED",
                detail="No benchmark is configured for this repository; performance was not "
                "measured.",
            )
        if not any(matches(path, benchmark.applies_to) for path in changed):
            return Performance(
                status="NOT_MEASURED",
                detail="The change does not touch a path that has a benchmark; performance was "
                "not measured.",
            )
        ids: list[str] = []
        described: dict[str, str] = {}

        def measure(tree: str) -> float | None:
            observation, output = self._run(
                "benchmark", "base" if tree == "baseline" else "candidate", "benchmark", manifest
            )
            ids.append(observation.execution_id)
            if observation.outcome != "PASS":
                return None
            try:
                data = json.loads(output.read_text(encoding="utf-8"))
                value = float(data["value"])
            except (OSError, ValueError, KeyError, TypeError):
                return None
            if not math.isfinite(value) or value < 0:
                return None
            described.setdefault("metric", str(data.get("metric", "value"))[:60])
            described.setdefault("unit", str(data.get("unit", ""))[:20])
            return value

        base, candidate = measure_pairs(measure, benchmark.pairs, benchmark.warmup_pairs)
        result = compare(
            base, candidate, benchmark.max_regression_percent, benchmark.max_noise_percent
        )
        return result.model_copy(
            update={
                "metric": described.get("metric"),
                "unit": described.get("unit"),
                "execution_ids": ids,
            }
        )

    def _settle(self) -> Verdict:
        """The verdict for a candidate that is green on the new test and broke nothing known."""
        unclear: list[str] = []
        if self.regression.status == "INCONCLUSIVE":
            unclear.append(REGRESSION_INCONCLUSIVE)
        if not self.regression.historical_found:
            self.limitations.append(
                f"{regression_rules.NOT_FOUND}: no runnable regression test linked to a past "
                "fix of this area was found, so old bugs were not re-checked."
            )
        if self.suite is not None and self.suite.outcome != "PASS":
            unclear.append(SUITE_INCONCLUSIVE)
        if self.performance.status == "REGRESSION":
            self.reasons.extend([*unclear, PERFORMANCE_REGRESSION])
            self.limitations.append(self.performance.detail)
            return "FAIL"
        if self.performance.status == "INCONCLUSIVE":
            unclear.append(PERFORMANCE_INCONCLUSIVE)
        if self.performance.status == "NOT_MEASURED":
            self.limitations.append(self.performance.detail)
        self.reasons.extend(unclear)
        return "INCONCLUSIVE" if unclear else "VERIFIED"

    # ---- Result --------------------------------------------------------------------------

    def _finish(self, state: FixState, verdict: Verdict) -> FixOutcome:
        request = self.request
        repo = self.repo
        head = self.candidate_sha
        gates = gate_rules.build(
            self.reproduction,
            self.patch,
            self.candidate_test,
            self.regression,
            self.suite,
            repo is not None and "suite" in repo.recipes,
            self.performance,
            head,
        )
        calls = self.ledger.calls
        result = FixResult(
            fix_id=uuid.uuid4(),
            run_id=request.run_id,
            ticket=request.ticket.model_copy(update={"title": self.title}),
            repository_id=request.repository_id,
            base_sha=request.base_commit_sha,
            candidate_sha=head,
            state=state,
            verdict=verdict,
            reason_codes=_unique(self.reasons),
            reproduction=self.reproduction,
            patch=self.patch,
            attempts=self.attempts,
            candidate_test=self.candidate_test,
            regression=self.regression,
            suite=self.suite,
            performance=self.performance,
            gates=gates,
            gate_summary=gate_rules.summarize(gates, head),
            manifests=self.manifests,
            executions=self.executions,
            limitations=_unique(self.limitations)[:MAX_LIMITATIONS],
            generation=Generation(
                routing_revision=ROUTING_REVISION,
                calls=len(calls),
                input_tokens=sum(call.result.input_tokens for call in calls),
                output_tokens=sum(call.result.output_tokens for call in calls),
                **self.generation,
            ),
            policy_revision=self.policy.revision if self.policy else None,
            timings=Timings(
                reproduction_ms=self.reproduction_ms,
                fix_ms=self.fix_ms,
                total_ms=int((self.clock() - self.started) * 1000),
            ),
        )
        draft = verdict == "VERIFIED" or (
            repo is not None
            and repo.draft_pr_on_incomplete
            and verdict in ("FAIL", "INCONCLUSIVE")
            and self.patch is not None
            and self.patch.policy_status == "PASS"
        )
        if draft and repo is not None:
            result = result.model_copy(
                update={"pull_request": report.pull_request(result, repo.target_branch)}
            )
        structlog.get_logger().info(
            "fix_built",
            state=state,
            verdict=verdict,
            reason_codes=result.reason_codes,
            reproducibility=self.reproduction.reproducibility,
            iterations=sum(1 for attempt in self.attempts if attempt.stage == "patch"),
            executions=len(self.executions),
            gates=result.gate_summary.overall,
            draft_pr=result.pull_request is not None,
            total_ms=result.timings.total_ms,
        )
        return FixOutcome(result, self.ledger)


def run_fix(
    request: FixRequest,
    sources: FixSources,
    gateway: Gateway,
    policy: DefectPolicy | None,
    settings: FixSettings,
    runner_for: Callable[[RunnerPolicy], Runner] | None = None,
    clock: Callable[[], float] = time.perf_counter,
) -> FixOutcome:
    """Run the pipeline for one accepted request. Raises only on infrastructure failures."""
    run = _FixRun(
        request,
        sources,
        gateway,
        policy,
        settings,
        runner_for or (lambda runner: build_runner(runner, settings.production)),
        clock,
    )
    # Every line of the run carries its id, also when it is started from the command line.
    with structlog.contextvars.bound_contextvars(run_id=str(request.run_id)):
        try:
            return run.execute()
        finally:
            run.close()
