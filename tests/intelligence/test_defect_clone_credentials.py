"""Cloning a private repository for a fix run: the read token goes only where it must."""

import base64
import subprocess
from pathlib import Path

from defect_support import make_demo

from app.defect import workspace
from app.defect.workspace import clone_credentials, prepare

TOKEN = "s3cr3t-read-token"
ADO = "https://dev.azure.com/acme/Shop/_git/shop"
HOSTS = ("dev.azure.com",)


def test_an_approved_https_host_gets_the_token_as_a_scoped_header() -> None:
    environment = clone_credentials(ADO, TOKEN, HOSTS)

    expected = base64.b64encode(f":{TOKEN}".encode()).decode()
    assert environment == {
        "GIT_CONFIG_COUNT": "1",
        "GIT_CONFIG_KEY_0": "http.https://dev.azure.com/.extraHeader",  # only for this host
        "GIT_CONFIG_VALUE_0": f"Authorization: Basic {expected}",
    }


def test_the_token_is_never_sent_anywhere_else() -> None:
    assert clone_credentials(ADO, None, HOSTS) == {}  # no token configured
    assert clone_credentials("http://dev.azure.com/acme/Shop/_git/shop", TOKEN, HOSTS) == {}
    assert clone_credentials("https://evil.example/acme/_git/shop", TOKEN, HOSTS) == {}
    assert clone_credentials("https://dev.azure.com.evil.example/x", TOKEN, HOSTS) == {}
    assert clone_credentials("/srv/repos/shop", TOKEN, HOSTS) == {}  # local path
    assert clone_credentials("file:///srv/repos/shop", TOKEN, HOSTS) == {}


def test_other_approved_hosts_can_be_configured() -> None:
    environment = clone_credentials(
        "https://ACME.visualstudio.com/Shop/_git/shop",
        TOKEN,
        ("dev.azure.com", "acme.visualstudio.com"),
    )

    assert environment["GIT_CONFIG_KEY_0"] == "http.https://acme.visualstudio.com/.extraHeader"


def test_the_token_reaches_git_through_the_environment_never_the_command_line(
    tmp_path: Path, monkeypatch
) -> None:
    seen: dict = {}
    real_run = subprocess.run

    def spy(arguments, **options):
        if "clone" in arguments:
            seen["argv"] = list(arguments)
            seen["env"] = dict(options["env"])
            arguments = [a if a != ADO else str(demo.repo) for a in arguments]  # clone locally
        return real_run(arguments, **options)

    demo = make_demo(tmp_path)
    monkeypatch.setattr(workspace.subprocess, "run", spy)

    prepared = prepare(ADO, demo.base_sha, tmp_path / "run", access_token=TOKEN, token_hosts=HOSTS)

    assert TOKEN not in " ".join(seen["argv"])
    assert not any("Authorization" in argument for argument in seen["argv"])
    assert seen["env"]["GIT_CONFIG_VALUE_0"].startswith("Authorization: Basic ")
    # Nothing is left behind: later commands and the clone's own config never hold the token.
    config = (prepared.candidate / ".git" / "config").read_text("utf-8")
    assert "extraheader" not in config.lower() and "Authorization" not in config


def test_a_local_repository_is_cloned_as_before_even_when_a_token_is_configured(
    tmp_path: Path,
) -> None:
    demo = make_demo(tmp_path)

    prepared = prepare(str(demo.repo), demo.base_sha, tmp_path / "run", access_token=TOKEN)

    assert prepared.read("src/calculator.py") is not None or prepared.files()
