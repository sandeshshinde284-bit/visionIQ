"""DEF-01 to DEF-04: the limits found in the review of PR #19, each with the case that broke it.

* A command never runs next to a Git directory, and never sees what an earlier run left behind.
* A report is not proof on its own: the exit code must agree with it.
* The predicted failure is matched on the failure's headline, not on the test's own source.
* A patch cannot replace a file it could not have read, and cannot swap one secret for another.
* A run ends inside its time budget and is not started again without end.
"""

import subprocess
import sys
from datetime import UTC, datetime
from pathlib import Path

import pytest
from defect_support import (
    RED,
    SAFE_FIX,
    SOURCE,
    TEST_ANSWER,
    TEST_PATH,
    Call,
    FakeFixSources,
    FakeRunner,
    gateway,
    honest,
    make_demo,
    request,
    settings,
)
from test_brief_worker import FakeConnection
from test_defect_guardrails import BASELINE, REPRODUCTION, repository
from test_defect_worker import claimed, context

from app.defect import sources, worker
from app.defect.generate import TEST_INVALID, Declined, propose_test
from app.defect.guardrails import ProposedChange, adds_secret, check
from app.defect.pipeline import run_fix
from app.defect.policy import RunnerPolicy
from app.defect.results import Case, classify
from app.defect.runner import MAX_LOG_BYTES, BoundedOutput, DockerRunner, LocalRunner, RunResult
from app.defect.schema import ExpectedFailure
from app.defect.worker import FixWorker, lease_seconds
from app.defect.workspace import WorkspaceError, prepare, run_git
from app.llm.gateway import Budget, RunLedger
from app.llm.types import DataClassification, EvidenceContext

DIGEST = "registry.example/runner@sha256:" + "a" * 64
EXPECTED = ExpectedFailure(
    exception="AssertionError", message_contains=["captured payment was retried"]
)


def ran(exit_code: int | None = 0) -> RunResult:
    return RunResult(datetime.now(UTC), 10, exit_code, False, "0" * 64, None)


# ---- The sandbox never holds a Git directory --------------------------------------------------


def test_an_export_is_exactly_one_commit_with_no_git_directory(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    workspace = prepare(str(demo.repo), demo.base_sha, tmp_path / "run")

    first = workspace.export(demo.base_sha, "X1", "base", {"tests/test_new.py": "def test(): ..."})
    (first / "conftest.py").write_text("raise SystemExit", "utf-8")  # what a run could leave
    second = workspace.export(demo.base_sha, "X2", "base")

    assert first.name == second.name == "base" and first != second
    for export in (first, second):
        assert not (export / ".git").exists()
        assert (export / SOURCE).read_text("utf-8") == (demo.repo / SOURCE).read_text("utf-8")
    assert (first / "tests/test_new.py").is_file()
    # Nothing crosses from one export to the next, and nothing reaches the trees git works in.
    assert not (second / "conftest.py").exists() and not (second / "tests/test_new.py").exists()
    assert not (workspace.base / "conftest.py").exists()
    workspace.discard("X1")
    assert not first.exists() and second.exists()


@pytest.mark.parametrize(
    ("commit", "name", "overlay"),
    [
        ("main", "X1", None),  # a name that moves, or a git option
        ("--output=/tmp/x", "X1", None),
        ("0" * 40, "../X1", None),
        (None, "X1", {"../escape.py": "x"}),
        (None, "X1", {".git/hooks/post-checkout": "x"}),
    ],
)
def test_an_export_refuses_what_could_leave_its_directory(
    tmp_path: Path, commit: str | None, name: str, overlay: dict | None
) -> None:
    demo = make_demo(tmp_path)
    workspace = prepare(str(demo.repo), demo.base_sha, tmp_path / "run")

    with pytest.raises(WorkspaceError):
        workspace.export(commit or demo.base_sha, name, "base", overlay)


def test_a_hook_planted_in_a_tree_is_not_run_by_the_hosts_git(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    workspace = prepare(str(demo.repo), demo.base_sha, tmp_path / "run")
    marker = tmp_path / "hook-ran"
    hooks = workspace.candidate / ".git" / "hooks"  # where git looks unless told otherwise
    hooks.mkdir(parents=True, exist_ok=True)
    for name in ("post-checkout", "pre-commit", "post-commit"):
        hook = hooks / name
        hook.write_text(f"#!/bin/sh\necho ran > '{marker.as_posix()}'\n", "utf-8", newline="\n")
        hook.chmod(0o755)

    workspace.reset_candidate()
    workspace.write("candidate", SOURCE, "changed = True\n")
    workspace.commit_candidate("visioniq/fix/hook", "Fix")

    assert not marker.exists()


def test_the_docker_runner_refuses_a_git_checkout_and_is_read_only(tmp_path: Path) -> None:
    runner = DockerRunner(RunnerPolicy(kind="docker", image=DIGEST))
    checkout = tmp_path / "checkout"
    (checkout / ".git").mkdir(parents=True)
    export = tmp_path / "export"
    export.mkdir()

    with pytest.raises(ValueError, match="Git checkout"):
        runner.command(["pytest"], checkout)
    refused = runner.run(["pytest"], checkout, 5, tmp_path / "X1.log")
    command = runner.command(["pytest"], export, "visioniq-fix-1")

    assert (refused.exit_code, refused.error) == (None, "runner_launch_failed")
    assert "--read-only" in command
    assert command[command.index("--name") + 1] == "visioniq-fix-1"  # so a timeout can kill it
    assert command[command.index("--tmpfs") + 1].startswith("/tmp:")


def test_every_execution_runs_in_its_own_export_of_the_commit(tmp_path: Path) -> None:
    roots: list[Path] = []
    overlaid: list[bool] = []

    def behaviour(call: Call):
        roots.append(call.root)
        overlaid.append((call.root / TEST_PATH).is_file())
        # Nothing a previous execution wrote is here, and there is no Git directory to poison.
        assert not (call.root / ".git").exists()
        assert not (call.root / "conftest.py").exists()
        assert list((call.root / ".visioniq-out").iterdir()) == []
        # What a hostile test would try: disable later tests and forge the next report.
        (call.root / "conftest.py").write_text("collect_ignore_glob = ['*']", "utf-8")
        forged = call.root / ".visioniq-out" / f"X{call.number + 1}.xml"
        forged.write_text('<testsuite><testcase name="test_all"/></testsuite>', "utf-8")
        return honest(call)

    demo = make_demo(tmp_path)
    result = run_fix(
        request(demo),
        FakeFixSources(),
        gateway(TEST_ANSWER, SAFE_FIX),
        demo.policy,
        settings(tmp_path),
        lambda _policy: FakeRunner(behaviour),
    ).result

    assert result.verdict == "VERIFIED"
    assert len(roots) == len(set(roots)) == len(result.executions)
    assert {root.name for root in roots} == {"base", "candidate"}
    # The reproduction test was laid over the export; the baseline commit does not have it.
    assert overlaid[0]
    assert TEST_PATH not in run_git(demo.repo, ["ls-tree", "-r", "--name-only", demo.base_sha])


# ---- A report is not proof on its own ---------------------------------------------------------


def test_a_passing_report_from_a_command_that_failed_is_not_a_pass() -> None:
    passed = [Case("test_red", "tests.test_pay", "passed")]
    other_failed = [*passed, Case("test_other", "tests.test_pay", "failed", "", "boom")]

    assert classify(ran(0), passed, "test_red") == ("PASS", None)
    assert classify(ran(2), passed, "test_red") == (
        "ENVIRONMENT_ERROR",
        "exit_code_contradicts_report",
    )
    assert classify(ran(2), passed, None)[0] == "ENVIRONMENT_ERROR"  # the whole file
    # Another test in the same file failed: that explains the exit code.
    assert classify(ran(1), other_failed, "test_red") == ("PASS", None)


def test_the_predicted_failure_must_be_in_the_headline_not_in_the_tests_own_source() -> None:
    traceback = (
        'def test_red():\n    """captured payment was retried"""\n'
        ">   total = 1 + None\nE   TypeError: unsupported operand\n"
        "tests/test_pay.py:7: TypeError"
    )
    quoted_only = [
        Case(
            "test_red", "tests.test_pay", "failed", "", "TypeError: unsupported operand", traceback
        )
    ]
    real = [Case("test_red", "tests.test_pay", "failed", "", f"{RED}\nassert False", traceback)]
    no_message = [Case("test_red", "tests.test_pay", "failed", "", "", f"trace\nE   {RED}")]

    assert classify(ran(1), quoted_only, "test_red", EXPECTED)[0] == "FAIL_DIFFERENT_SIGNATURE"
    assert classify(ran(1), real, "test_red", EXPECTED)[0] == "FAIL_TARGET_SIGNATURE"
    assert classify(ran(1), no_message, "test_red", EXPECTED)[0] == "FAIL_TARGET_SIGNATURE"


@pytest.mark.parametrize(
    ("status", "message", "outcome"),
    [
        # A bug in the patch is a result about the patch, whatever its message looks like.
        ("failed", "NameError: name 'captured' is not defined", "FAIL_DIFFERENT_SIGNATURE"),
        ("failed", "PermissionError: [Errno 13] Permission denied", "FAIL_DIFFERENT_SIGNATURE"),
        ("failed", "ImportError: cannot import name 'retry'", "FAIL_DIFFERENT_SIGNATURE"),
        # A setup or collection error, or a toolchain message, is the environment.
        ("error", "ImportError: cannot import name 'retry'", "ENVIRONMENT_ERROR"),
        ("error", "PermissionError: [Errno 13] Permission denied", "ENVIRONMENT_ERROR"),
        (
            "failed",
            "error CS0246: The type or namespace name could not be found",
            "ENVIRONMENT_ERROR",
        ),
    ],
)
def test_only_setup_and_toolchain_problems_count_as_the_environment(
    status: str, message: str, outcome: str
) -> None:
    cases = [Case("test_red", "tests.test_pay", status, "", message)]  # type: ignore[arg-type]

    assert classify(ran(1), cases, "test_red", EXPECTED)[0] == outcome


# ---- Output and time are bounded --------------------------------------------------------------


def test_only_the_head_and_the_tail_of_endless_output_are_kept() -> None:
    output = BoundedOutput(limit=100)
    output.feed(b"H" * 40)
    for _ in range(1000):
        output.feed(b"x" * 64)
    output.feed(b"TAIL")
    kept = output.value()

    assert kept.startswith(b"H" * 40) and kept.endswith(b"TAIL")
    assert b"[... log truncated ...]" in kept and len(kept) < 200
    short = BoundedOutput()
    short.feed(b"all of it")
    assert short.value() == b"all of it"


def test_a_command_that_prints_without_end_is_stopped_with_a_bounded_log(tmp_path: Path) -> None:
    log = tmp_path / "X1.log"
    script = "import sys\nwhile True: sys.stdout.write('x' * 65536)"

    result = LocalRunner(RunnerPolicy(kind="local")).run(
        [sys.executable, "-c", script], tmp_path, 2, log
    )

    assert (result.exit_code, result.timed_out) == (None, True)
    assert log.stat().st_size < MAX_LOG_BYTES + 1024


def test_a_timeout_stops_what_the_command_started_too(tmp_path: Path) -> None:
    marker = tmp_path / "grandchild-finished"
    child = f"import time, pathlib; time.sleep(4); pathlib.Path({str(marker)!r}).write_text('x')"
    script = (
        f"import subprocess, sys, time; subprocess.Popen([sys.executable, '-c', {child!r}]); "
        "time.sleep(30)"
    )

    result = LocalRunner(RunnerPolicy(kind="local")).run(
        [sys.executable, "-c", script], tmp_path, 1, tmp_path / "X1.log"
    )
    subprocess.run([sys.executable, "-c", "import time; time.sleep(5)"], check=True)

    assert result.timed_out and not marker.exists()


def test_a_run_starts_nothing_once_its_time_budget_is_used_up(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    runner = FakeRunner(honest)
    now = [0.0]

    def clock() -> float:
        now[0] += 400.0  # every look at the clock is 400 s later; the budget is 1800 s
        return now[0]

    result = run_fix(
        request(demo),
        FakeFixSources(),
        gateway(TEST_ANSWER, SAFE_FIX),
        demo.policy,
        settings(tmp_path),
        lambda _policy: runner,
        clock,
    ).result

    assert result.verdict != "VERIFIED" and result.state != "PUBLISHED"
    assert any(e.timed_out and e.exit_code is None for e in result.executions)
    assert len(runner.calls) < len(result.executions)  # the later ones were never started
    assert any("time budget" in line for line in result.limitations)


def test_the_lease_always_outlasts_the_runs_time_budget(tmp_path: Path) -> None:
    assert lease_seconds(settings(tmp_path)) == 3600
    long_run = settings(tmp_path, max_seconds=14_400.0)
    assert lease_seconds(long_run) > long_run.max_seconds


# ---- The worker does not start a run again without end ---------------------------------------


def test_a_run_whose_earlier_attempts_died_is_answered_and_not_run_again(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    connection = FakeConnection()
    runner = FakeRunner(honest)
    job = FixWorker(
        lambda: connection,
        lambda scope: FakeFixSources(),
        gateway(TEST_ANSWER, SAFE_FIX),
        demo.policy,
        settings(tmp_path),
        context(defect=True),
        lambda _policy: runner,
    )

    job._process(claimed(demo, attempt=worker.MAX_ATTEMPTS + 1))

    assert runner.calls == []  # nothing was cloned, generated or run
    completed = connection.statements[-1][1][-1].obj
    assert completed["data"]["result_state"] == "BLOCKED"
    assert completed["data"]["reason_codes"] == [worker.ANALYSIS_FAILED]


def test_with_nothing_stranded_the_start_up_check_answers_nothing() -> None:
    connection = FakeConnection()

    assert worker.answer_stranded(connection, context(defect=False)) == 0
    assert connection.tables() == []


# ---- What a patch may not do ------------------------------------------------------------------


def judge(*changes: tuple[str, str], baseline=BASELINE, shown=None, files=None):
    return check(
        [ProposedChange(path, content) for path, content in changes],
        repository(),
        baseline.get,
        REPRODUCTION,
        set(baseline) if shown is None else shown,
        set(baseline) if files is None else files,
    )


def test_an_existing_file_that_is_not_text_cannot_be_replaced() -> None:
    # The file is in the commit but could not be read as text, so the model never saw it.
    result = judge(("src/Kiosk/Logo.cs", "class Logo {}"), files={*BASELINE, "src/Kiosk/Logo.cs"})

    assert [(f.code, f.severity) for f in result.findings] == [("file_not_text", "block")]
    assert result.status == "BLOCKED" and result.files == ()


@pytest.mark.parametrize(
    ("path", "code"),
    [
        ("src/Kiosk", "path_not_a_file"),  # an existing directory
        ("src/kiosk", "path_not_a_file"),
        ("src/Kiosk/ReceiptPrinter.cs/extra.cs", "path_not_a_file"),  # under an existing file
        ("src/kiosk/receiptprinter.cs", "path_case_conflict"),  # the same file on Windows and macOS
    ],
)
def test_a_path_that_is_a_directory_or_differs_only_by_case_is_rejected(
    path: str, code: str
) -> None:
    assert "src/Kiosk/ReceiptPrinter.cs" in BASELINE

    result = judge((path, "class X {}"))

    assert (code, "block") in [(f.code, f.severity) for f in result.findings]


def test_replacing_one_secret_with_another_is_still_a_new_secret() -> None:
    old = 'var key = "password=hunter2secret";\nvar other = 1;\n'
    swapped = 'var key = "password=another9secret";\nvar other = 1;\n'
    untouched = 'var key = "password=hunter2secret";\nvar other = 2;\n'

    assert adds_secret(old, swapped)
    assert not adds_secret(old, untouched)  # an existing secret that was not changed
    assert adds_secret("", swapped) and not adds_secret(old, "var other = 1;\n")


@pytest.mark.parametrize(
    "path",
    [
        "src/setup.py",
        "src/setup.cfg",
        "src/Pipfile",
        "src/uv.lock",
        "src/pnpm-lock.yaml",
        "src/Cargo.toml",
        "src/requirements/base.txt",
        "src/requirements.in",
        "src/constraints.txt",
        "src/Kiosk/Kiosk.fsproj",
        "src/Directory.Build.props",
        "src/nuget.config",
        "src/global.json",
    ],
)
def test_more_dependency_files_stop_for_an_engineer(path: str) -> None:
    result = judge((path, "changed = true"))

    assert ("dependency_change", "review") in [(f.code, f.severity) for f in result.findings]
    assert result.status == "REVIEW_REQUIRED"


def test_a_generated_test_with_a_credential_or_over_an_existing_file_is_not_run(
    tmp_path: Path,
) -> None:
    demo = make_demo(tmp_path)
    repo = demo.policy.repositories[0]
    ledger = RunLedger(request(demo).run_id, DataClassification.INTERNAL, Budget(6, 400_000, 1800))
    context_ = EvidenceContext(snapshot_id=None, ticket_title="Bug", ticket_text="x", evidence=())
    with_secret = {
        **TEST_ANSWER,
        "test_content": TEST_ANSWER["test_content"] + '\nKEY = "password=hunter2secret"\n',
    }
    other_case = {**TEST_ANSWER, "test_path": "tests/Test_Retry_Policy.py"}

    for answer, existing in ((with_secret, set()), (other_case, {"tests/test_retry_policy.py"})):
        proposal = propose_test(gateway(answer), ledger, context_, repo, existing)
        assert isinstance(proposal, Declined) and proposal.code == TEST_INVALID


# ---- History of this area, not of a file name -------------------------------------------------


def test_past_fixes_are_matched_on_the_full_path() -> None:
    assert "regexp_replace(p.path, '^.*/', '')" not in sources.HISTORICAL_FIXES_SQL
    seen: list[dict] = []

    class Recording(FakeConnection):
        def execute(self, sql, params=()):
            seen.append(dict(params))
            return self

    fixes = sources.PostgresFixSources(Recording, None, ["project:Kiosk"])  # type: ignore[arg-type]
    fixes.historical_fixes(FakeFixSources().ticket, ["/src/Kiosk/Retry.cs", "src\\Kiosk\\A.cs"])

    assert seen[0]["files"] == ["src/kiosk/a.cs", "src/kiosk/retry.cs"]


def test_tests_that_were_found_but_not_run_are_named_in_the_result(tmp_path: Path) -> None:
    demo = make_demo(tmp_path, limits={"max_regression_tests": 1})
    fixes = (
        sources.HistoricalFix("101", "PR 201", (SOURCE, "tests/test_retry_policy.py")),
        sources.HistoricalFix("102", "PR 202", (SOURCE, "tests/test_other_area.py")),
    )

    result = run_fix(
        request(demo),
        FakeFixSources(fixes=fixes),
        gateway(TEST_ANSWER, SAFE_FIX),
        demo.policy,
        settings(tmp_path),
        lambda _policy: FakeRunner(honest),
    ).result

    assert len(result.regression.tests) == 1
    assert any("found but not run (the limit is 1)" in line for line in result.limitations)


# ---- The pull request text ---------------------------------------------------------------------


def test_ticket_and_model_text_cannot_become_markup_in_the_pull_request() -> None:
    from app.defect.report import MAX_DESCRIPTION_CHARS, _plain

    hostile = "See [docs](https://evil.example) ![x](https://evil.example/p.png) @admin #123 <b>"
    plain = _plain(hostile)

    for opener in ("[docs]", "![x]", "<b>"):
        assert opener not in plain
    assert r"\[docs\]" in plain and r"\@admin" in plain and r"\#123" in plain
    assert _plain("  spread\nover   lines ") == "spread over lines"
    assert MAX_DESCRIPTION_CHARS < 4000  # room for the line the API adds about the pushed commit
