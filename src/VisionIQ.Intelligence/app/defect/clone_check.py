"""Check that a repository of the fix policy can be cloned at an exact commit, read-only.

    python -m app.defect.clone_check --repository <repository_id> --base-sha <40-hex commit>

It does what a fix run does first, and nothing else: read the policy, clone the repository with
the configured read token, check out exactly that commit, and delete the copy again. Nothing is
written to the repository and no model or test is run. It prints no token and no address beyond
the host name, and exits 0 only when every check passed.

Use it to verify a new deployment (local Docker or Cloud Run) before the first fix run.
"""

from __future__ import annotations

import argparse
import contextlib
import shutil
import sys
import tempfile
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit

from app.defect.pipeline import _WORKSPACE_TEXT, FixSettings
from app.defect.policy import RepositoryPolicy, load_policy
from app.defect.workspace import WorkspaceError, clone_credentials, prepare, run_git


@dataclass(frozen=True)
class CloneCheck:
    ok: bool
    lines: tuple[str, ...]
    code: str | None = None


def check(repo: RepositoryPolicy, base_sha: str, settings: FixSettings, root: Path) -> CloneCheck:
    """Clone ``repo`` at ``base_sha`` under ``root``, verify the copy, and remove it."""
    parts = urlsplit(repo.source)
    where = parts.hostname or "a local path"
    token = "configured" if settings.repo_access_token else "not configured"
    sent = clone_credentials(
        repo.source, settings.repo_access_token, settings.repo_organization_url
    )
    if settings.repo_access_token and not sent and parts.hostname:
        token += " but NOT sent: the repository is not in the organization of ADO_ORG_URL"
    download = "the pinned commit only" if settings.repo_fetch == "commit" else "the repository"
    lines = [
        f"repository      {repo.repository_id} ({where})",
        f"read token      {token}",
        f"repository cache {'on' if settings.repo_cache_dir else 'off'}",
        f"download        {download}",
    ]
    target = root / f"clone-check-{uuid.uuid4().hex}"
    started = time.monotonic()
    try:
        try:
            workspace = prepare(
                repo.source,
                base_sha,
                target,
                access_token=settings.repo_access_token,
                organization_url=settings.repo_organization_url,
                cache_dir=settings.repo_cache_dir,
                fetch=settings.repo_fetch,
            )
        except WorkspaceError as error:
            text = _WORKSPACE_TEXT.get(error.code, "The workspace could not be prepared.")
            return CloneCheck(False, (*lines, f"FAILED          {error.code}: {text}"), error.code)
        head = run_git(workspace.base, ["rev-parse", "HEAD"]).strip()
        shallow = (
            run_git(workspace.candidate, ["rev-parse", "--is-shallow-repository"]).strip() == "true"
        )
        config = (workspace.candidate / ".git" / "config").read_text("utf-8", errors="replace")
        leaked = "extraheader" in config.lower() or "authorization" in config.lower()
        lines += [
            f"cloned in       {time.monotonic() - started:.1f} s",
            f"history         {'none (single commit)' if shallow else 'full'}",
            f"checked out     {head}",
            f"commit matches  {'yes' if head == base_sha else 'NO'}",
            f"files           {len(workspace.files())}",
            f"token in config {'FOUND' if leaked else 'none'}",
        ]
        ok = head == base_sha and not leaked
        return CloneCheck(ok, tuple(lines), None if ok else "clone_check_failed")
    finally:
        shutil.rmtree(target, ignore_errors=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repository", required=True, help="repository_id from the fix policy")
    parser.add_argument("--base-sha", required=True, help="the full commit SHA to check out")
    parser.add_argument("--policy", type=Path, help="policy file (default: DEFECT_POLICY_FILE)")
    arguments = parser.parse_args(argv)

    from app.config import get_settings

    app = get_settings()
    policy_file = arguments.policy or app.defect_policy_file
    if policy_file is None:
        sys.stdout.write("FAILED: no policy file (set DEFECT_POLICY_FILE or pass --policy)\n")
        return 2
    try:
        policy = load_policy(policy_file)
    except (OSError, ValueError) as error:
        sys.stdout.write(f"FAILED: the policy file could not be read ({type(error).__name__})\n")
        return 2
    repo = policy.repository(arguments.repository)
    if repo is None:
        sys.stdout.write(f"FAILED: {arguments.repository} is not in the fix policy\n")
        return 2
    root = app.defect_work_dir or Path(tempfile.gettempdir()) / "visioniq-fix"
    root.mkdir(parents=True, exist_ok=True)
    result = check(repo, arguments.base_sha, FixSettings.from_app(app, root), root)
    version = "not installed"
    with contextlib.suppress(WorkspaceError):
        version = run_git(root, ["--version"]).strip()
    sys.stdout.write("\n".join([f"git             {version}", *result.lines]) + "\n")
    sys.stdout.write("RESULT: OK\n" if result.ok else f"RESULT: FAILED ({result.code})\n")
    return 0 if result.ok else 2


if __name__ == "__main__":
    raise SystemExit(main())
