"""DEF-01 clone review: git in the image, one settings builder for worker and CLI, precise and
sanitised clone failures, and an opt-in clone of a real private repository."""

import dataclasses
import os
import subprocess
from pathlib import Path
from types import SimpleNamespace

import pytest
import yaml
from defect_support import make_demo
from pydantic import SecretStr

from app.defect import cli, clone_check, workspace
from app.defect.pipeline import _WORKSPACE_TEXT, FixSettings
from app.defect.workspace import WorkspaceError, classify_git_failure, prepare

ROOT = Path(__file__).resolve().parents[2]
SECRET = "s3cr3t-read-token"


# ---- Clone failures: precise, fixed codes; git's message never leaves ---------------------------


@pytest.mark.parametrize(
    ("message", "code"),
    [
        (
            "fatal: Authentication failed for 'https://dev.azure.com/acme/Shop/_git/shop/'",
            "repository_access_denied",
        ),
        (
            "fatal: unable to access 'https://dev.azure.com/a/b/_git/c/': The requested URL "
            "returned error: 403",
            "repository_access_denied",
        ),
        (
            "fatal: could not read Username for 'https://dev.azure.com': terminal prompts disabled",
            "repository_access_denied",
        ),
        (
            "remote: TF401019: The Git repository with name or identifier shop does not exist or "
            "you do not have permissions for the operation you are attempting.",
            "repository_not_found",
        ),
        (
            "fatal: repository 'https://dev.azure.com/a/b/_git/nope/' not found",
            "repository_not_found",
        ),
        (
            "fatal: unable to access 'https://dev.azure.com/': "
            "Could not resolve host: dev.azure.com",
            "repository_unreachable",
        ),
        (
            "fatal: unable to access 'https://x/': Failed to connect to x port 443",
            "repository_unreachable",
        ),
        (
            "fatal: unable to access 'https://x/': The requested URL returned error: 302",
            "repository_redirected",
        ),
        ("fatal: something nobody has seen before", None),
    ],
)
def test_git_messages_become_fixed_reason_codes(message: str, code: str | None) -> None:
    assert classify_git_failure(message) == code


def failing_git(error: BaseException):
    def run(*_arguments, **_options):
        raise error

    return run


def test_a_missing_git_executable_is_reported_as_such(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(workspace.subprocess, "run", failing_git(FileNotFoundError("git")))

    with pytest.raises(WorkspaceError) as error:
        prepare("https://dev.azure.com/acme/Shop/_git/shop", "a" * 40, tmp_path / "run")

    assert error.value.code == "git_not_installed"


def test_a_clone_that_takes_too_long_is_a_timeout(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(
        workspace.subprocess, "run", failing_git(subprocess.TimeoutExpired("git", 1))
    )

    with pytest.raises(WorkspaceError) as error:
        prepare("https://dev.azure.com/acme/Shop/_git/shop", "a" * 40, tmp_path / "run")

    assert error.value.code == "repository_timeout"


def test_a_failed_clone_never_reveals_the_token_the_address_or_gits_message(
    tmp_path: Path, monkeypatch
) -> None:
    source = "https://dev.azure.com/acme/Shop/_git/shop"
    denied = f"fatal: Authentication failed for '{source}/' (token {SECRET})".encode()

    def run(arguments, **_options):
        return subprocess.CompletedProcess(arguments, 128, b"", denied)

    monkeypatch.setattr(workspace.subprocess, "run", run)

    with pytest.raises(WorkspaceError) as error:
        prepare(source, "a" * 40, tmp_path / "run", access_token=SECRET)

    assert error.value.code == "repository_access_denied"
    shown = f"{error.value} {error.value.args} {error.value.cause}"
    assert SECRET not in shown and "acme" not in shown and "Authentication failed" not in shown


def test_every_clone_failure_has_a_text_that_says_what_to_check() -> None:
    codes = {
        "git_not_installed",
        "repository_access_denied",
        "repository_not_found",
        "repository_unreachable",
        "repository_redirected",
        "repository_timeout",
        "repository_unavailable",
        "baseline_not_found",
        "baseline_not_pinned",
    }

    assert codes <= set(_WORKSPACE_TEXT)
    assert "DEFECT_REPO_READ_PAT" in _WORKSPACE_TEXT["repository_access_denied"]


# ---- One settings builder for the background worker and the command line ------------------------


def service_configuration(**overrides) -> SimpleNamespace:
    values = {
        "defect_repo_read_pat": SecretStr(SECRET),
        "defect_repo_token_hosts": "dev.azure.com, acme.visualstudio.com ,",
        "defect_repo_cache_dir": Path("/var/cache/visioniq"),
        "defect_artifact_dir": Path("/var/artifacts"),
        "defect_artifact_base_uri": "gs://bucket/fix-runs/",
        "defect_max_calls_per_run": 9,
        "defect_max_tokens_per_run": 123_456,
        "defect_max_seconds_per_run": 777.0,
        "defect_keep_workspaces": True,
        "defect_bot_name": "Some Bot",
        "defect_bot_email": "bot@example.invalid",
        "defect_model_selection": "by_complexity",
        "defect_simple_max_files": 3,
        "defect_complex_min_files": 9,
        "llm_data_classification": "RESTRICTED",
        "app_env": "development",
        "defect_allow_local_runner": True,
    }
    return SimpleNamespace(**{**values, **overrides})


def test_every_fix_setting_comes_from_the_service_configuration(tmp_path: Path) -> None:
    built = FixSettings.from_app(service_configuration(), tmp_path, production=True)

    assert built.repo_access_token == SECRET
    assert built.repo_token_hosts == ("dev.azure.com", "acme.visualstudio.com")
    assert built.repo_cache_dir == Path("/var/cache/visioniq")
    assert (built.model_selection, built.simple_max_files, built.complex_min_files) == (
        "by_complexity",
        3,
        9,
    )
    # A field added to FixSettings later must be wired here too: with distinctive values, none
    # may still hold its default (work_dir and artifact_dir have none).
    defaults = {f.name: f.default for f in dataclasses.fields(FixSettings)}
    unset = [
        name
        for name, default in defaults.items()
        if default is not dataclasses.MISSING and getattr(built, name) == default
    ]
    assert unset == []


def test_without_a_token_none_is_passed_on(tmp_path: Path) -> None:
    built = FixSettings.from_app(service_configuration(defect_repo_read_pat=None), tmp_path)

    assert built.repo_access_token is None


def test_the_command_line_uses_the_service_configuration_with_live_models(
    tmp_path: Path, monkeypatch
) -> None:
    monkeypatch.setattr(cli, "get_settings", service_configuration)

    live = cli._fix_settings(tmp_path, tmp_path / "out", keep_workspace=False, live=True)
    offline = cli._fix_settings(tmp_path, tmp_path / "out", keep_workspace=True, live=False)

    assert live.repo_access_token == SECRET and live.model_selection == "by_complexity"
    assert live.repo_cache_dir == Path("/var/cache/visioniq")
    assert (live.artifact_dir, live.keep_workspace) == (tmp_path / "out", False)
    # An offline demo needs no configuration and keeps the built-in defaults.
    assert offline.repo_access_token is None and offline.model_selection == "fixed"
    assert offline.keep_workspace is True


def test_the_worker_and_the_command_line_do_not_build_settings_by_hand() -> None:
    app = ROOT / "src" / "VisionIQ.Intelligence" / "app"
    worker = (app / "main.py").read_text("utf-8")
    command_line = (app / "defect" / "cli.py").read_text("utf-8")

    assert "FixSettings.from_app(settings, work_dir)" in worker
    assert "FixSettings(" not in worker
    assert "FixSettings.from_app(get_settings()" in command_line


@pytest.mark.parametrize(
    ("app_env", "allowed", "refused"),
    [
        ("development", True, False),  # a developer machine that asked for it: the only case
        ("development", False, True),  # the default
        ("production", True, True),  # never in production, whatever the flag says
        ("production", False, True),
    ],
)
def test_the_local_runner_is_refused_unless_a_developer_machine_allowed_it(
    tmp_path: Path, app_env: str, allowed: bool, refused: bool
) -> None:
    app = service_configuration(app_env=app_env, defect_allow_local_runner=allowed)

    assert FixSettings.local_runner_refused(app) is refused
    assert FixSettings.from_app(app, tmp_path).production is refused


def test_the_live_command_line_refuses_the_local_runner_like_the_worker_does(
    tmp_path: Path, monkeypatch
) -> None:
    from app.defect.policy import RunnerPolicy
    from app.defect.runner import build_runner

    monkeypatch.setattr(
        cli, "get_settings", lambda: service_configuration(defect_allow_local_runner=False)
    )

    live = cli._fix_settings(tmp_path, tmp_path / "out", keep_workspace=False, live=True)

    assert live.production is True
    with pytest.raises(ValueError, match="no network isolation"):
        build_runner(RunnerPolicy(kind="local"), live.production)
    # An offline demo runs prepared answers only and keeps the local runner available.
    offline = cli._fix_settings(tmp_path, tmp_path / "out", keep_workspace=False, live=False)
    assert offline.production is False


def test_a_ticket_file_can_state_its_risk_band_for_model_selection() -> None:
    assert cli.FileSources({"risk_band": "low"}).brief_risk_band("p", "1") == "LOW"
    assert cli.FileSources({}).brief_risk_band("p", "1") is None
    assert cli.FileSources({"risk_band": "whatever"}).brief_risk_band("p", "1") is None


# ---- The read-only clone check ------------------------------------------------------------------


def fix_settings(tmp_path: Path) -> FixSettings:
    return FixSettings(work_dir=tmp_path / "work", artifact_dir=tmp_path / "artifacts")


def test_the_clone_check_verifies_the_commit_and_leaves_nothing_behind(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    root = tmp_path / "check"
    root.mkdir()

    result = clone_check.check(
        demo.policy.repositories[0], demo.base_sha, fix_settings(tmp_path), root
    )

    assert result.ok and result.code is None
    text = "\n".join(result.lines)
    assert f"checked out     {demo.base_sha}" in text
    assert "commit matches  yes" in text and "token in config none" in text
    assert list(root.iterdir()) == []  # the copy was removed


def test_the_clone_check_reports_a_missing_commit_with_its_reason(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    root = tmp_path / "check"
    root.mkdir()

    result = clone_check.check(demo.policy.repositories[0], "f" * 40, fix_settings(tmp_path), root)

    assert not result.ok and result.code == "baseline_not_found"
    assert "FAILED          baseline_not_found" in result.lines[-1]
    assert list(root.iterdir()) == []


def test_the_clone_check_prints_neither_the_token_nor_the_full_address(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    repo = demo.policy.repositories[0].model_copy(
        update={"source": "https://dev.azure.com/acme/Shop/_git/shop"}
    )
    settings = dataclasses.replace(fix_settings(tmp_path), repo_access_token=SECRET)
    root = tmp_path / "check"
    root.mkdir()

    def run(arguments, **_options):  # the server refuses: no network is used in this test
        return subprocess.CompletedProcess(arguments, 128, b"", b"fatal: Authentication failed")

    workspace_run = workspace.subprocess.run
    workspace.subprocess.run = run
    try:
        result = clone_check.check(repo, "a" * 40, settings, root)
    finally:
        workspace.subprocess.run = workspace_run

    text = "\n".join(result.lines)
    assert result.code == "repository_access_denied"
    assert "(dev.azure.com)" in text and "read token      configured" in text
    assert SECRET not in text and "acme" not in text and "_git" not in text


# ---- The image and the local stack --------------------------------------------------------------


def test_the_intelligence_image_installs_git_and_still_runs_as_a_non_root_user() -> None:
    dockerfile = (ROOT / "src" / "VisionIQ.Intelligence" / "Dockerfile").read_text("utf-8")
    start = dockerfile.index("apt-get install")
    install = dockerfile[start : dockerfile.index("\n\n", start)]
    packages = install.split("--no-install-recommends", 1)[1].split("&&", 1)[0].replace("\\", "")

    # Exactly git and CA certificates: no compiler, no container runtime, no test runner.
    assert sorted(packages.split()) == ["ca-certificates", "git"]
    assert "rm -rf /var/lib/apt/lists/*" in install
    assert dockerfile.index("USER 10001") > start  # installed as root, run as the app user


def test_the_local_stack_passes_the_clone_settings_and_mounts_the_policy_read_only() -> None:
    compose = yaml.safe_load((ROOT / "docker-compose.yml").read_text("utf-8"))
    service = compose["services"]["intelligence"]
    environment = service["environment"]

    assert environment["DEFECT_REPO_READ_PAT"] == "${DEFECT_REPO_READ_PAT:-}"  # from .env only
    assert environment["DEFECT_REPO_TOKEN_HOSTS"] == "${DEFECT_REPO_TOKEN_HOSTS:-dev.azure.com}"
    assert "./config:/app/config:ro" in service["volumes"]


# ---- Opt-in: a REAL private repository ----------------------------------------------------------

REAL = os.environ.get("VISIONIQ_REAL_CLONE_TEST") == "1"
real_only = pytest.mark.skipif(
    not REAL,
    reason="Opt-in: set VISIONIQ_REAL_CLONE_TEST=1, DEFECT_REPO_READ_PAT, "
    "VISIONIQ_REAL_CLONE_URL and VISIONIQ_REAL_CLONE_SHA (read-only, no fakes)",
)


@real_only
def test_real_private_repository_is_cloned_at_the_exact_commit(tmp_path: Path) -> None:
    source = os.environ["VISIONIQ_REAL_CLONE_URL"]
    sha = os.environ["VISIONIQ_REAL_CLONE_SHA"]
    token = os.environ["DEFECT_REPO_READ_PAT"]
    hosts = tuple(os.environ.get("DEFECT_REPO_TOKEN_HOSTS", "dev.azure.com").split(","))

    prepared = prepare(source, sha, tmp_path / "run", access_token=token, token_hosts=hosts)

    assert workspace.run_git(prepared.base, ["rev-parse", "HEAD"]).strip() == sha
    assert prepared.files()
    config = (prepared.candidate / ".git" / "config").read_text("utf-8")
    assert token not in config and "extraheader" not in config.lower()


@real_only
def test_real_private_repository_refuses_a_wrong_token(tmp_path: Path) -> None:
    source = os.environ["VISIONIQ_REAL_CLONE_URL"]
    hosts = tuple(os.environ.get("DEFECT_REPO_TOKEN_HOSTS", "dev.azure.com").split(","))

    with pytest.raises(WorkspaceError) as error:
        prepare(
            source,
            os.environ["VISIONIQ_REAL_CLONE_SHA"],
            tmp_path / "run",
            access_token="not-a-valid-token",
            token_hosts=hosts,
        )

    assert error.value.code in {"repository_access_denied", "repository_not_found"}
