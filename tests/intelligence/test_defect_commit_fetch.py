"""DEFECT_REPO_FETCH=commit: a fix run downloads only the commit it is pinned to.

Every test uses real git. A fix run never reads history, so a workspace made from one commit must
behave exactly like one made from a clone; and when a server does not hand out a single commit,
the run must fall back to a clone instead of failing.
"""

from pathlib import Path

import pytest
from defect_support import make_demo

from app.defect import workspace
from app.defect.workspace import WorkspaceError, prepare, run_git, update_commit_cache

IDENTITY = ["-c", "user.name=t", "-c", "user.email=t@example.invalid"]


def git_calls(monkeypatch) -> list[list[str]]:
    calls: list[list[str]] = []
    real = workspace.subprocess.run

    def spy(arguments, **options):
        calls.append(list(arguments[9:]))
        return real(arguments, **options)

    monkeypatch.setattr(workspace.subprocess, "run", spy)
    return calls


def new_commit(repo: Path, message: str = "next") -> str:
    run_git(repo, [*IDENTITY, "commit", "--quiet", "--allow-empty", "-m", message])
    return run_git(repo, ["rev-parse", "HEAD"]).strip()


def shallow(prepared) -> bool:
    return run_git(prepared.candidate, ["rev-parse", "--is-shallow-repository"]).strip() == "true"


def test_only_the_pinned_commit_is_downloaded_and_the_workspace_behaves_the_same(
    tmp_path: Path, monkeypatch
) -> None:
    demo = make_demo(tmp_path)
    full = prepare(str(demo.repo), demo.base_sha, tmp_path / "full")
    calls = git_calls(monkeypatch)

    one = prepare(str(demo.repo), demo.base_sha, tmp_path / "one", fetch="commit")

    assert "clone" not in [call[0] for call in calls]  # no clone of the repository
    assert shallow(one) and not shallow(full)  # no history came with it
    assert run_git(one.base, ["rev-parse", "HEAD"]).strip() == demo.base_sha
    assert one.files() == full.files()
    assert one.read("src/kiosk/retry.py") == full.read("src/kiosk/retry.py")


def test_a_fix_made_in_a_single_commit_workspace_is_a_normal_commit_diff_export_and_bundle(
    tmp_path: Path,
) -> None:
    demo = make_demo(tmp_path)
    one = prepare(str(demo.repo), demo.base_sha, tmp_path / "one", fetch="commit")

    one.write("candidate", "src/kiosk/retry.py", "FIXED = True\n")
    sha = one.commit_candidate("visioniq/fix/1-run", "the fix")
    exported = one.export(sha, "x1", "candidate")
    bundle = tmp_path / "fix.bundle"
    one.bundle(bundle, "visioniq/fix/1-run")

    assert "FIXED = True" in one.diff(sha, ["src/kiosk/retry.py"])
    assert (exported / "src" / "kiosk" / "retry.py").read_text("utf-8") == "FIXED = True\n"
    assert not (exported / ".git").exists()
    # The bundle is usable where the history is: the original repository takes it as a branch.
    run_git(demo.repo, ["bundle", "verify", str(bundle)])
    run_git(demo.repo, ["fetch", "--quiet", str(bundle), "visioniq/fix/1-run:applied"])
    assert run_git(demo.repo, ["rev-parse", "applied~1"]).strip() == demo.base_sha
    assert run_git(demo.repo, ["show", "applied:src/kiosk/retry.py"]) == "FIXED = True\n"


def test_a_commit_that_is_not_the_newest_can_be_pinned(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    newer = new_commit(demo.repo)

    one = prepare(str(demo.repo), demo.base_sha, tmp_path / "one", fetch="commit")

    assert newer != demo.base_sha
    assert run_git(one.base, ["rev-parse", "HEAD"]).strip() == demo.base_sha


def test_when_the_server_does_not_hand_out_a_single_commit_the_repository_is_cloned(
    tmp_path: Path, monkeypatch
) -> None:
    demo = make_demo(tmp_path)
    real = workspace.subprocess.run
    seen: list[str] = []

    def refusing(arguments, **options):
        command = list(arguments[9:])
        seen.append(command[0])
        if command[:1] == ["fetch"] and "--depth" in command:
            message = b"error: Server does not allow request for unadvertised object"
            return workspace.subprocess.CompletedProcess(arguments, 1, b"", message)
        return real(arguments, **options)

    monkeypatch.setattr(workspace.subprocess, "run", refusing)

    prepared = prepare(str(demo.repo), demo.base_sha, tmp_path / "run", fetch="commit")

    assert seen.index("fetch") < seen.index("clone")  # tried the commit, then cloned
    assert not shallow(prepared)
    assert run_git(prepared.base, ["rev-parse", "HEAD"]).strip() == demo.base_sha
    assert prepared.files()


def test_a_missing_commit_is_still_reported_as_not_found(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)

    with pytest.raises(WorkspaceError) as error:
        prepare(str(demo.repo), "f" * 40, tmp_path / "run", fetch="commit")

    assert error.value.code == "baseline_not_found"
    assert not (tmp_path / "run").exists()


def test_without_git_the_run_stops_at_once_instead_of_trying_a_clone(
    tmp_path: Path, monkeypatch
) -> None:
    attempts: list[str] = []

    def missing(arguments, **_options):
        attempts.append(arguments[9])
        raise FileNotFoundError("git")

    monkeypatch.setattr(workspace.subprocess, "run", missing)

    with pytest.raises(WorkspaceError) as error:
        prepare("https://dev.azure.com/a/b/_git/c", "a" * 40, tmp_path / "run", fetch="commit")

    assert error.value.code == "git_not_installed"
    assert "clone" not in attempts


def test_the_commit_store_adds_one_commit_at_a_time_and_needs_no_network_for_a_stored_one(
    tmp_path: Path, monkeypatch
) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"
    source = f"file://{demo.repo.as_posix()}"

    store = update_commit_cache(source, demo.base_sha, cache)
    calls = git_calls(monkeypatch)
    again = update_commit_cache(source, demo.base_sha, cache)

    assert again == store and "fetch" not in [call[0] for call in calls]  # already stored
    newer = new_commit(demo.repo)
    update_commit_cache(source, newer, cache)
    refs = run_git(store, ["for-each-ref", "--format=%(refname)"]).split()
    assert sorted(refs) == sorted(
        [f"refs/visioniq/commits/{demo.base_sha}", f"refs/visioniq/commits/{newer}"]
    )
    assert store.name.endswith(".commits.git")  # apart from the full mirror


def test_with_a_cache_the_workspace_is_made_from_the_local_store(
    tmp_path: Path, monkeypatch
) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"
    source = f"file://{demo.repo.as_posix()}"
    monkeypatch.setattr(workspace, "_is_remote", lambda _source: True)

    first = prepare(source, demo.base_sha, tmp_path / "run1", cache_dir=cache, fetch="commit")
    store = next(cache.glob("*.commits.git"))
    before = run_git(store, ["for-each-ref"]).strip()
    first.write("candidate", "src/added_by_a_run.py", "x = 1\n")
    first.commit_candidate("visioniq/fix/1-run", "a run's change")
    hidden = demo.repo.rename(tmp_path / "offline")  # the server is gone: only the store is left
    try:
        second = prepare(source, demo.base_sha, tmp_path / "run2", cache_dir=cache, fetch="commit")
    finally:
        hidden.rename(demo.repo)

    assert shallow(second) and second.read("src/added_by_a_run.py") is None
    assert run_git(store, ["for-each-ref"]).strip() == before  # a run cannot change the store


def test_the_default_still_clones_the_repository(tmp_path: Path, monkeypatch) -> None:
    demo = make_demo(tmp_path)
    calls = git_calls(monkeypatch)

    prepared = prepare(str(demo.repo), demo.base_sha, tmp_path / "run")

    assert calls[0][0] == "clone" and not shallow(prepared)


def test_a_whole_fix_run_gives_the_same_result_from_one_commit_as_from_a_clone(
    tmp_path: Path,
) -> None:
    from defect_support import SAFE_FIX, TEST_ANSWER, honest
    from test_defect_pipeline import run

    answers = [TEST_ANSWER, SAFE_FIX]
    cloned = run(tmp_path / "a", answers, honest)
    single = run(tmp_path / "b", answers, honest, repo_fetch="commit")

    assert cloned.verdict == "VERIFIED"
    assert (single.state, single.verdict) == (cloned.state, cloned.verdict)
    assert single.reason_codes == cloned.reason_codes == []
    assert [gate.status for gate in single.gates] == [gate.status for gate in cloned.gates]
    assert single.patch.files == cloned.patch.files  # the same change, file for file
    # The Git tree is the identity of the candidate's content: identical in both modes.
    assert single.patch.tree_sha == cloned.patch.tree_sha
    assert single.patch.tree_sha is not None
    # Committed, exported and bundled without the repository's history.
    assert single.patch.bundle_uri is not None
