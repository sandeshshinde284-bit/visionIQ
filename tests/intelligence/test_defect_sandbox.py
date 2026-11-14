"""S11-02: the pinned workspace and the runners, and the whole pipeline with real processes."""

import json
import sys
from pathlib import Path

import pytest
from defect_support import (
    DEMO,
    OLD_TEST,
    SOURCE,
    TEST_ID,
    FakeFixSources,
    make_demo,
    request,
    settings,
)

from app.defect import cli
from app.defect.pipeline import run_fix
from app.defect.policy import RunnerPolicy
from app.defect.runner import DockerRunner, LocalRunner, build_runner
from app.defect.workspace import WorkspaceError, is_safe_path, prepare
from app.llm.gateway import ModelGateway
from app.llm.providers.offline import OfflineProvider
from app.llm.routing import RoutingPolicy

DIGEST = "python@sha256:" + "a" * 64


# ---- Workspace --------------------------------------------------------------------------------


def test_both_trees_are_at_exactly_the_baseline_commit(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)

    workspace = prepare(str(demo.repo), demo.base_sha, tmp_path / "run")

    assert (
        workspace.read(SOURCE, "base") == workspace.read(SOURCE, "candidate") == DEMO.RETRY_POLICY
    )
    assert SOURCE in workspace.files() and OLD_TEST in workspace.files()
    assert workspace.sha256("pyproject.toml") and workspace.sha256("absent.toml") is None


@pytest.mark.parametrize(
    ("base_sha", "code"),
    [
        ("main", "baseline_not_pinned"),  # a branch moves
        ("HEAD", "baseline_not_pinned"),
        ("bd43865", "baseline_not_pinned"),  # a short SHA can become ambiguous
        ("f" * 40, "baseline_not_found"),
    ],
)
def test_only_an_existing_full_sha_pins_a_run(tmp_path: Path, base_sha: str, code: str) -> None:
    demo = make_demo(tmp_path)

    with pytest.raises(WorkspaceError) as error:
        prepare(str(demo.repo), base_sha, tmp_path / "run")

    assert error.value.code == code and not (tmp_path / "run").exists()


def test_a_repository_that_cannot_be_cloned_is_a_reason_code(tmp_path: Path) -> None:
    with pytest.raises(WorkspaceError) as error:
        prepare(str(tmp_path / "nowhere"), "a" * 40, tmp_path / "run")

    assert error.value.code == "repository_not_found"  # the precise cause, as a fixed code
    assert "nowhere" not in str(error.value)  # a code, never git's message


@pytest.mark.parametrize(
    "path",
    [
        "../secret.txt",
        "src/../../secret.txt",
        "/etc/passwd",
        "C:/Windows/system.ini",
        "src\\kiosk\\x.py",
        "src/./x.py",
        "src//x.py",
        "src/",
        "src/x.py; rm -rf /",
        "",
        ".git/config",
    ],
)
def test_a_path_from_a_model_cannot_leave_the_tree(tmp_path: Path, path: str) -> None:
    demo = make_demo(tmp_path)
    workspace = prepare(str(demo.repo), demo.base_sha, tmp_path / "run")

    assert workspace.read(path) is None
    with pytest.raises(WorkspaceError, match="unsafe_path"):
        workspace.write("candidate", path, "x")
    assert not is_safe_path(path) or path == ".git/config"


def test_the_candidate_is_one_commit_on_a_branch_and_can_be_reset(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    workspace = prepare(
        str(demo.repo), demo.base_sha, tmp_path / "run", "Bot", "bot@example.invalid"
    )
    workspace.write("candidate", SOURCE, DEMO.SAFE_FIX)

    sha = workspace.commit_candidate("visioniq/fix/4711-abc", "Fix #4711")

    diff = workspace.diff(sha, [SOURCE])
    assert sha != demo.base_sha and "+        return not captured and" in diff
    assert workspace.read(SOURCE, "base") == DEMO.RETRY_POLICY  # the baseline tree is untouched
    assert len(workspace.tree_sha(sha)) == 40
    bundle = tmp_path / "out" / "candidate.bundle"
    assert len(workspace.bundle(bundle, "visioniq/fix/4711-abc")) == 64 and bundle.stat().st_size

    workspace.reset_candidate()
    assert workspace.read(SOURCE, "candidate") == DEMO.RETRY_POLICY

    workspace.cleanup()
    assert not (tmp_path / "run").exists()
    assert (demo.repo / SOURCE).read_text("utf-8") == DEMO.RETRY_POLICY  # the source is read-only


# ---- Runners ----------------------------------------------------------------------------------


def test_the_local_runner_gives_a_command_none_of_the_services_environment(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("DB_INTELLIGENCE_PASSWORD", "hunter2-do-not-leak")
    monkeypatch.setenv("ADO_READ_PAT", "pat-do-not-leak")
    monkeypatch.setenv("BUILD_FLAVOUR", "nightly")
    runner = LocalRunner(
        RunnerPolicy(kind="local", env_allow=("BUILD_FLAVOUR",), env={"SEED": "0"})
    )
    watched = ("DB_INTELLIGENCE_PASSWORD", "ADO_READ_PAT", "BUILD_FLAVOUR", "SEED")
    script = f"import os; print(sorted(k for k in os.environ if k in {watched!r}))"
    log = tmp_path / "X1.log"

    result = runner.run([sys.executable, "-c", script], tmp_path, 60, log)

    assert (result.exit_code, result.timed_out, result.error) == (0, False, None)
    assert log.read_text("utf-8").strip() == "['BUILD_FLAVOUR', 'SEED']"
    assert {"BUILD_FLAVOUR", "SEED"} <= set(runner.environment_names())
    assert "DB_INTELLIGENCE_PASSWORD" not in runner.environment_names()
    assert runner.describe().network == "unrestricted"  # it says what it cannot isolate


def test_a_command_that_overruns_its_limit_is_a_timeout(tmp_path: Path) -> None:
    result = LocalRunner(RunnerPolicy(kind="local")).run(
        [sys.executable, "-c", "import time; time.sleep(30)"], tmp_path, 0.5, tmp_path / "X1.log"
    )

    assert (result.exit_code, result.timed_out) == (None, True)


def test_a_command_that_cannot_start_is_a_runner_error_not_a_test_result(tmp_path: Path) -> None:
    result = LocalRunner(RunnerPolicy(kind="local")).run(
        ["no-such-tool-visioniq"], tmp_path, 5, tmp_path / "X1.log"
    )

    assert (result.exit_code, result.error) == (None, "runner_launch_failed")


def test_secret_looking_output_is_scrubbed_from_the_stored_log(tmp_path: Path) -> None:
    log = tmp_path / "X1.log"

    result = LocalRunner(RunnerPolicy(kind="local")).run(
        [sys.executable, "-c", "print('password=hunter2 exit')"], tmp_path, 60, log
    )

    text = log.read_text("utf-8")
    assert "hunter2" not in text and "[REDACTED:secret]" in text
    assert len(result.log_sha256) == 64


def test_the_docker_runner_has_no_network_no_privileges_and_a_pinned_image(tmp_path: Path) -> None:
    runner = DockerRunner(RunnerPolicy(kind="docker", image=DIGEST, env={"SEED": "0"}, memory="1g"))

    command = runner.command(["pytest", "tests/test_x.py"], tmp_path)

    assert command[:3] == ["docker", "run", "--rm"]
    for flags in (
        ["--network", "none"],
        ["--cap-drop", "ALL"],
        ["--security-opt", "no-new-privileges"],
        ["--memory", "1g"],
        ["--env", "SEED=0"],
        ["--workdir", "/workspace"],
    ):
        start = command.index(flags[0])
        assert command[start : start + 2] == flags
    assert command[-3:] == [DIGEST, "pytest", "tests/test_x.py"]
    assert f"{tmp_path.resolve()}:/workspace" in command  # only the tree is mounted
    assert runner.describe().network == "none" and runner.describe().image == DIGEST
    assert runner.environment_names() == ["SEED"]


def test_the_local_runner_is_refused_in_production() -> None:
    with pytest.raises(ValueError, match="network isolation"):
        build_runner(RunnerPolicy(kind="local"), production=True)
    assert isinstance(build_runner(RunnerPolicy(kind="local")), LocalRunner)
    assert isinstance(build_runner(RunnerPolicy(kind="docker", image=DIGEST), True), DockerRunner)


# ---- The whole pipeline with real git, real pytest and the real benchmark  -----------------------


def offline_gateway() -> ModelGateway:
    return ModelGateway(RoutingPolicy({}, {}, override=OfflineProvider(DEMO.OFFLINE_SCRIPT)))


def test_the_demo_defect_is_reproduced_fixed_and_verified_by_real_runs(tmp_path: Path) -> None:
    demo = make_demo(tmp_path, fake_runner=False)

    result = run_fix(
        request(demo), FakeFixSources(), offline_gateway(), demo.policy, settings(tmp_path)
    ).result

    # Red on the baseline for the predicted reason, by pytest itself.
    assert result.reproduction.reproducibility == "REPRODUCED"
    assert result.reproduction.evidence.failure_signature == (
        "AssertionError: captured payment was retried"
    )
    # The first prepared fix passes the new test and brings back bug 101: caught by the old test.
    first, second = (attempt for attempt in result.attempts if attempt.stage == "patch")
    assert (first.result, second.result) == ("regression_failed", "candidate_accepted")
    assert result.candidate_test.outcome == "PASS" and result.regression.status == "PASS"
    assert result.suite.outcome == "PASS"
    # Timing on a developer machine may be too noisy to conclude; it is never a regression here.
    assert result.performance.status in ("NO_REGRESSION_DEMONSTRATED", "INCONCLUSIVE")
    assert result.verdict in ("VERIFIED", "INCONCLUSIVE")
    if result.verdict == "VERIFIED":
        assert result.pull_request is not None and TEST_ID in result.pull_request.description
    manifest = result.manifests[-1]
    assert manifest.runner.kind == "local" and manifest.toolchain["python"].startswith("Python 3")
    assert any("no network isolation" in line for line in result.limitations)
    logs = tmp_path / "evidence" / str(result.run_id)
    assert all((logs / f"{e.execution_id}.log").is_file() for e in result.executions)


def test_the_command_line_runs_a_ticket_file_and_writes_the_evidence(
    tmp_path: Path, capsys
) -> None:
    created = DEMO.create(tmp_path / "demo", sys.executable)
    out = tmp_path / "out"

    code = cli.main(
        [
            "--policy", created["policy"],
            "--repository", "kiosk-demo",
            "--base-sha", created["base_sha"],
            "--ticket", created["ticket"],
            "--offline-script", created["offline_script"],
            "--out", str(out),
        ]
    )  # fmt: skip

    (evidence,) = out.iterdir()
    result = json.loads((evidence / "result.json").read_text("utf-8"))
    printed = capsys.readouterr().out
    assert f"verdict {result['verdict']}" in printed and "Reproduction: REPRODUCED" in printed
    assert code == (0 if result["verdict"] == "VERIFIED" else 3)
    assert result["reproduction"]["reproducibility"] == "REPRODUCED"
    assert result["candidate_test"]["outcome"] == "PASS"
    assert evidence.name == result["run_id"]
    assert "not captured and attempt" in (evidence / "candidate.patch").read_text("utf-8")
    assert (evidence / "M1.json").is_file() and (evidence / "candidate.bundle").is_file()
