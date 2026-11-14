"""The repository cache of a fix run: download once, then fetch only what is new."""

from pathlib import Path

import pytest
from defect_support import make_demo

from app.defect import workspace
from app.defect.workspace import WorkspaceError, prepare, run_git, update_cache

IDENTITY = ["-c", "user.name=t", "-c", "user.email=t@example.invalid"]


def git_calls(monkeypatch) -> list[list[str]]:
    """Every git command run from now on (its arguments after the fixed -c options)."""
    calls: list[list[str]] = []
    real = workspace.subprocess.run

    def spy(arguments, **options):
        calls.append(list(arguments[9:]))
        return real(arguments, **options)

    monkeypatch.setattr(workspace.subprocess, "run", spy)
    return calls


def new_commit(repo: Path) -> str:
    run_git(repo, [*IDENTITY, "commit", "--quiet", "--allow-empty", "-m", "next"])
    return run_git(repo, ["rev-parse", "HEAD"]).strip()


def names(calls: list[list[str]]) -> list[str]:
    return [call[0] for call in calls]


def test_the_first_run_mirrors_the_repository_and_later_runs_use_no_network(
    tmp_path: Path, monkeypatch
) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"

    mirror = update_cache(str(demo.repo), demo.base_sha, cache)
    assert run_git(mirror, ["rev-parse", "--is-bare-repository"]).strip() == "true"

    calls = git_calls(monkeypatch)
    again = update_cache(str(demo.repo), demo.base_sha, cache)

    assert again == mirror
    assert "clone" not in names(calls) and "fetch" not in names(calls)  # the commit is there


def test_a_new_commit_is_fetched_without_downloading_the_repository_again(
    tmp_path: Path, monkeypatch
) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"
    mirror = update_cache(str(demo.repo), demo.base_sha, cache)
    newer = new_commit(demo.repo)

    calls = git_calls(monkeypatch)
    update_cache(str(demo.repo), newer, cache)

    assert "fetch" in names(calls) and "clone" not in names(calls)
    assert run_git(mirror, ["cat-file", "-t", newer]).strip() == "commit"


def test_a_damaged_mirror_is_rebuilt(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"
    mirror = update_cache(str(demo.repo), demo.base_sha, cache)
    (mirror / "HEAD").unlink()  # no longer a repository

    rebuilt = update_cache(str(demo.repo), demo.base_sha, cache)

    assert rebuilt == mirror
    assert run_git(rebuilt, ["cat-file", "-t", demo.base_sha]).strip() == "commit"


def test_an_unreachable_repository_never_costs_the_mirror(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"
    mirror = update_cache(str(demo.repo), demo.base_sha, cache)
    unknown = "f" * 40
    hidden = demo.repo.rename(tmp_path / "offline")  # the source is gone

    try:
        with pytest.raises(WorkspaceError):
            update_cache(str(demo.repo), unknown, cache)  # needs a fetch, which fails
    finally:
        hidden.rename(demo.repo)

    # The mirror still serves the commits it has, and its lock was released.
    assert run_git(mirror, ["cat-file", "-t", demo.base_sha]).strip() == "commit"
    assert update_cache(str(demo.repo), demo.base_sha, cache) == mirror


def test_a_run_works_on_its_own_copy_and_cannot_change_the_mirror(
    tmp_path: Path, monkeypatch
) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"
    monkeypatch.setattr(workspace, "_is_remote", lambda source: True)  # treat it as a remote

    first = prepare(str(demo.repo), demo.base_sha, tmp_path / "run1", cache_dir=cache)
    mirror = next(cache.glob("*.git"))
    before = run_git(mirror, ["for-each-ref"]).strip()
    first.write("candidate", "src/added_by_a_run.py", "x = 1\n")
    first.commit_candidate("visioniq/fix/1-run", "a run's change")

    second = prepare(str(demo.repo), demo.base_sha, tmp_path / "run2", cache_dir=cache)

    assert run_git(mirror, ["for-each-ref"]).strip() == before  # nothing a run did reached it
    assert second.read("src/added_by_a_run.py") is None
    assert second.files() == first.files() or "src/added_by_a_run.py" not in second.files()


def test_a_local_repository_is_not_cached(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    cache = tmp_path / "cache"

    prepare(str(demo.repo), demo.base_sha, tmp_path / "run", cache_dir=cache)

    assert not cache.exists()  # a local path is already local
    assert workspace._is_remote("https://dev.azure.com/acme/Shop/_git/shop")
    assert not workspace._is_remote(str(demo.repo)) and not workspace._is_remote("file:///srv/x")


def test_two_updates_of_one_mirror_wait_for_each_other(tmp_path: Path) -> None:
    cache = tmp_path / "cache" / "abc.git"
    cache.parent.mkdir()

    with workspace._cache_lock(cache, 5):
        assert (cache.parent / "abc.git.lock").is_dir()
        with pytest.raises(WorkspaceError), workspace._cache_lock(cache, 0.3):
            pass  # a second update cannot start while the first holds the lock
    assert not (cache.parent / "abc.git.lock").exists()  # released afterwards


def test_an_abandoned_lock_is_taken_over_only_after_twice_the_update_time(tmp_path: Path) -> None:
    import os
    import time

    cache = tmp_path / "cache" / "abc.git"
    lock = cache.parent / "abc.git.lock"
    lock.mkdir(parents=True)  # left behind by a run that crashed
    old = time.time() - 1
    os.utime(lock, (old, old))

    # Waiting 2 s, the lock is at most 3 s old: under twice the update time, so it may still be
    # a live update and is left alone.
    with pytest.raises(WorkspaceError), workspace._cache_lock(cache, 2):
        pass
    assert lock.is_dir()
    # By now it is older than twice 0.4 s: abandoned, taken over and released again.
    with workspace._cache_lock(cache, 0.4):
        pass
    assert not lock.exists()
