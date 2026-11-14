"""Cloning a private repository for a fix run: the read token goes only where it must."""

import base64
import subprocess
from pathlib import Path

from defect_support import make_demo

from app.defect import workspace
from app.defect.workspace import clone_credentials, prepare

TOKEN = "s3cr3t-read-token"
ADO = "https://dev.azure.com/acme/Shop/_git/shop"
ORG = "https://dev.azure.com/acme"  # ADO_ORG_URL


def header(source: str, organization: str | None = ORG) -> str | None:
    """The address the token is scoped to, or None when it is not sent at all."""
    environment = clone_credentials(source, TOKEN, organization)
    return environment.get("GIT_CONFIG_KEY_0")


def test_a_repository_of_the_organization_gets_the_token_as_a_scoped_header() -> None:
    environment = clone_credentials(ADO, TOKEN, ORG)

    expected = base64.b64encode(f":{TOKEN}".encode()).decode()
    assert environment == {
        "GIT_CONFIG_COUNT": "2",
        # Scoped to the organization, not to the whole host.
        "GIT_CONFIG_KEY_0": "http.https://dev.azure.com/acme/.extraHeader",
        "GIT_CONFIG_VALUE_0": f"Authorization: Basic {expected}",
        # A redirect is refused: git would otherwise keep sending the header to the new place.
        "GIT_CONFIG_KEY_1": "http.followRedirects",
        "GIT_CONFIG_VALUE_1": "false",
    }


def test_the_token_is_never_sent_outside_the_organization() -> None:
    assert header("https://dev.azure.com/someone-else/Shop/_git/shop") is None  # other org
    assert header("https://dev.azure.com/acmeX/Shop/_git/shop") is None  # look-alike name
    assert header("https://dev.azure.com/acme/../someone-else/_git/shop") is None  # path trick
    assert header("https://dev.azure.com/acme/%2e%2e/someone-else/_git/shop") is None
    assert header("https://evil.example/acme/Shop/_git/shop") is None  # other host
    assert header("https://dev.azure.com.evil.example/acme/x") is None
    assert header("https://dev.azure.com:8443/acme/Shop/_git/shop") is None  # other port
    assert header("http://dev.azure.com/acme/Shop/_git/shop") is None  # not HTTPS
    assert header("/srv/repos/shop") is None  # a local path needs no token
    assert header("file:///srv/repos/shop") is None


def test_no_token_or_no_organization_means_nothing_is_sent() -> None:
    assert clone_credentials(ADO, None, ORG) == {}
    assert clone_credentials(ADO, "", ORG) == {}
    assert header(ADO, None) is None  # ADO_ORG_URL not set: the token goes nowhere
    assert header(ADO, "") is None
    assert header(ADO, "http://dev.azure.com/acme") is None  # an organization that is not HTTPS


def test_the_header_is_scoped_as_the_address_is_written() -> None:
    # Azure DevOps ignores the case of the organization; git compares the path exactly. The
    # header therefore uses the source's own spelling, or git would not send it.
    assert header("https://dev.azure.com/ACME/Shop/_git/shop") == (
        "http.https://dev.azure.com/ACME/.extraHeader"
    )
    assert header("https://DEV.Azure.com/acme/Shop/_git/shop") is not None
    # A user name in the address, a trailing slash and spaces in a project name are all fine.
    assert header("https://acme@dev.azure.com/acme/Genoa%20Kiosk/_git/KIOSK_TVM") == (
        "http.https://dev.azure.com/acme/.extraHeader"
    )
    assert header(ADO, "https://dev.azure.com/acme/") is not None


def test_an_organization_named_by_its_host_covers_every_repository_on_it() -> None:
    legacy = "https://acme.visualstudio.com"

    assert header("https://acme.visualstudio.com/Shop/_git/shop", legacy) == (
        "http.https://acme.visualstudio.com/.extraHeader"
    )
    assert header("https://other.visualstudio.com/Shop/_git/shop", legacy) is None


def test_git_itself_sends_the_scoped_header_only_inside_the_organization(tmp_path: Path) -> None:
    """The scope is enforced by git, not only by this code: ask git what it would send."""
    environment = {**workspace._git_environment(), **clone_credentials(ADO, TOKEN, ORG)}

    def git_would_send(url: str) -> bool:
        answer = subprocess.run(
            ["git", "config", "--get-urlmatch", "http.extraHeader", url],
            cwd=tmp_path,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )
        return "Authorization" in answer.stdout

    assert git_would_send("https://dev.azure.com/acme/Shop/_git/shop")
    assert git_would_send("https://acme@dev.azure.com/acme/Other%20Project/_git/x")
    assert not git_would_send("https://dev.azure.com/someone-else/Shop/_git/shop")
    assert not git_would_send("https://dev.azure.com/acmeX/Shop/_git/shop")
    assert not git_would_send("https://dev.azure.com/acme/../someone-else/_git/shop")
    assert not git_would_send("https://evil.example/acme/Shop/_git/shop")


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

    prepared = prepare(
        ADO, demo.base_sha, tmp_path / "run", access_token=TOKEN, organization_url=ORG
    )

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
