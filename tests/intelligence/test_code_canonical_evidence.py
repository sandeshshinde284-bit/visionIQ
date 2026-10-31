"""LRN-06 canonical evidence contract and last-good preservation tests."""

import json
from datetime import UTC, datetime

import pytest

from app.code_intelligence.canonical import (
    ScanEvidenceIdentity,
    build_scan_evidence,
    ingest_complete_scan,
)
from app.code_intelligence.scanner import FileAnalysis, GitFile, RepositoryScan


def example_scan(status: str = "COMPLETE", commit: str = "a" * 40) -> RepositoryScan:
    """Build a commit-pinned source inventory without checking out external data."""
    return RepositoryScan(
        repo_root="/approved/checkout",
        commit_sha=commit,
        status=status,
        files=(GitFile("src/main.py", "b" * 40, "python", False),),
        analyses=(
            FileAnalysis(
                "src/main.py",
                "b" * 40,
                "AVAILABLE" if status == "COMPLETE" else "FAILED",
                "python-ast",
                "1",
            ),
        ),
        errors=() if status == "COMPLETE" else ("Parser unavailable",),
    )


class FakeArchive:
    """Records that immutable metadata is archived before a database write."""

    def __init__(self) -> None:
        """Initialize upload log and optional failure injection."""
        self.uploaded: list[bytes] = []
        self.fail = False

    def store(self, evidence: object) -> str:
        """Upload a bounded metadata snapshot or simulate archive failure."""
        if self.fail:
            raise OSError("Archive unavailable")
        self.uploaded.append(evidence.raw_json)
        return "gs://private-test-bucket/" + evidence.content_hash


class FakeStore:
    """Simulates immutable revision conflicts and last-success pointers."""

    def __init__(self, archive: FakeArchive) -> None:
        """Keep test evidence in memory while checking archive-first ordering."""
        self._archive = archive
        self.revisions: dict[tuple[str, str], str] = {}
        self.latest: str | None = None

    def save(self, evidence: object, archive_uri: str) -> str:
        """Use a source revision/content hash and reject changes to existing keys."""
        assert self._archive.uploaded
        assert archive_uri.startswith("gs://")
        key = (evidence.item_key, evidence.revision)
        previous = self.revisions.get(key)
        if previous:
            return "UNCHANGED" if previous == evidence.content_hash else "CONFLICT"
        was_empty = not self.revisions
        self.revisions[key] = evidence.content_hash
        self.latest = evidence.revision
        return "CREATED" if was_empty else "NEW_REVISION"


def identity() -> ScanEvidenceIdentity:
    """Return a source-scoped, trusted synthetic Git identity."""
    return ScanEvidenceIdentity("https://github.com/acme/example", "project-1", "repo-1")


def when() -> datetime:
    """Return a stable Git commit timestamp distinct from observation time."""
    return datetime(2026, 10, 30, 12, 0, tzinfo=UTC)


def test_source_scope_and_repeatable_metadata() -> None:
    """Identical scans produce one identity/hash without embedding local paths."""
    a = build_scan_evidence(example_scan(), identity(), "https://github.com/acme/example", when())
    b = build_scan_evidence(example_scan(), identity(), "https://github.com/acme/example", when())
    assert a.item_key == b.item_key
    assert a.content_hash == b.content_hash
    assert a.acl_verified is False
    assert len(a.item_key) == 64
    assert (
        a.item_key
        != build_scan_evidence(
            example_scan(),
            ScanEvidenceIdentity("https://github.com/acme/example", "other", "repo-1"),
            "https://github.com/acme/example",
            when(),
        ).item_key
    )
    raw = json.loads(a.raw_json)
    assert raw["commit_sha"] == "a" * 40
    assert raw["files"][0]["path"] == "src/main.py"
    assert "/approved/checkout" not in a.raw_json.decode()
    assert "source_code" not in raw
    assert a.redacted_json == '{"kind":"code_scan"}'


def test_archive_first_idempotent_replay_and_changed_commits() -> None:
    """The next complete commit creates a revision; replay creates nothing new."""
    archive = FakeArchive()
    store = FakeStore(archive)
    args = (identity(), "https://github.com/acme/example", when(), archive, store)
    assert ingest_complete_scan(example_scan(), *args) == "CREATED"
    assert ingest_complete_scan(example_scan(), *args) == "UNCHANGED"
    assert ingest_complete_scan(example_scan(commit="c" * 40), *args) == "NEW_REVISION"
    assert len(store.revisions) == 2
    assert store.latest == "c" * 40


def test_partial_scan_does_not_archive_or_replace_durable_last_good() -> None:
    """Parser failure must preserve the last complete revision."""
    archive = FakeArchive()
    store = FakeStore(archive)
    args = (identity(), "https://github.com/acme/example", when(), archive, store)
    assert ingest_complete_scan(example_scan(), *args) == "CREATED"
    before = list(archive.uploaded)
    assert ingest_complete_scan(example_scan("PARTIAL"), *args) == "PARTIAL"
    assert archive.uploaded == before
    assert store.latest == "a" * 40


def test_archive_failure_prevents_database_write() -> None:
    """A failed immutable upload must not falsely mark evidence persisted."""
    archive = FakeArchive()
    archive.fail = True
    store = FakeStore(archive)
    with pytest.raises(OSError):
        ingest_complete_scan(
            example_scan(), identity(), "https://github.com/acme/example", when(), archive, store
        )
    assert not store.revisions


@pytest.mark.parametrize(
    "source",
    [
        "http://github.com/acme/example",
        "https://user:token@github.com/acme/example",
        "https://github.com/acme/example?token=secret",
        "file:///repo",
    ],
)
def test_untrusted_source_urls_are_rejected(source: str) -> None:
    """Untrusted URLs cannot appear in canonical evidence or archive paths."""
    with pytest.raises(ValueError):
        build_scan_evidence(example_scan(), identity(), source, when())


def test_mismatched_git_blob_or_failed_parse_is_rejected() -> None:
    """Structural code evidence must be attributable to the exact Git blob."""
    bad = example_scan()
    changed_analysis = bad.analyses[0].__class__("src/main.py", "c" * 40, "AVAILABLE")
    with pytest.raises(ValueError, match="committed Git blob"):
        build_scan_evidence(
            bad.__class__(
                bad.repo_root,
                bad.commit_sha,
                bad.status,
                bad.files,
                (changed_analysis,),
            ),
            identity(),
            "https://github.com/acme/example",
            when(),
        )
