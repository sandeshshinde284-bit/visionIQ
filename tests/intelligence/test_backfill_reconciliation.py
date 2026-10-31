"""LRN-05 acceptance-oriented backfill, delta and quarantine tests."""

from dataclasses import dataclass, field

import pytest

from app.backfill.reconcile import (
    BackfillReconciler,
    ChangeRecord,
    SourceBatch,
    SyncState,
    WriteState,
)


@dataclass
class FakeSource:
    """An approved source adapter returning an ordered change batch."""

    source_id: str
    records: list[ChangeRecord] = field(default_factory=list)
    expected: int | None = None
    unavailable: bool = False
    received_cursor: str | None = None

    async def read(self, cursor: str | None) -> SourceBatch:
        """Return a deterministic batch or simulate an unavailable provider."""
        self.received_cursor = cursor
        if self.unavailable:
            raise ConnectionError("simulated source failure")
        return SourceBatch(self.records, self.expected)


class MemoryWriter:
    """A fake durable idempotent evidence store with controlled failures."""

    def __init__(self) -> None:
        """Initialize store and failure-injection state."""
        self.seen: set[tuple[str, str, str]] = set()
        self.fail_native_ids: set[str] = set()

    async def write(self, source_id: str, record: ChangeRecord) -> WriteState:
        """Store a unique revision or report an idempotent replay."""
        if record.native_id in self.fail_native_ids:
            raise OSError("controlled storage error")
        key = (source_id, record.native_id, record.revision_id)
        if key in self.seen:
            return WriteState.UNCHANGED
        self.seen.add(key)
        return WriteState.STORED


class MemoryCheckpoints:
    """A fake durable checkpoint store."""

    def __init__(self) -> None:
        """Initialize checkpoint state."""
        self.cursors: dict[str, str] = {}

    async def get(self, source_id: str) -> str | None:
        """Read the previously committed cursor."""
        return self.cursors.get(source_id)

    async def put(self, source_id: str, cursor: str) -> None:
        """Save a contiguous successful cursor."""
        self.cursors[source_id] = cursor


class MemoryQuarantine:
    """A fake quarantine store without raw data."""

    def __init__(self, available: bool = True) -> None:
        """Configure quarantine availability."""
        self.entries: list[tuple[str, str, str, str]] = []
        self.available = available

    async def put(self, source_id: str, native_id: str, revision_id: str, error_code: str) -> None:
        """Record a failure without the source payload."""
        if not self.available:
            raise OSError("unavailable")
        self.entries.append((source_id, native_id, revision_id, error_code))


def change(native_id: str, revision: str, cursor: str) -> ChangeRecord:
    """Build a synthetic source change with a sensitive test-only payload."""
    return ChangeRecord(native_id, revision, cursor, {"body": "SECRET"})


@pytest.mark.asyncio
async def test_initial_backfill_and_changed_ticket_appear_within_one_sync_cycle() -> None:
    """One approved source is initially loaded then updated in the next cycle."""
    writer, checkpoints, quarantine = MemoryWriter(), MemoryCheckpoints(), MemoryQuarantine()
    reconciler = BackfillReconciler(writer, checkpoints, quarantine)
    source = FakeSource("ado:project-a", [change("42", "1", "c1")], expected=1)

    first = await reconciler.run_one(source)
    assert first.state == SyncState.SYNCED
    assert first.stored == 1
    assert first.checkpoint == "c1"

    source.records = [change("42", "2", "c2")]
    second = await reconciler.run_one(source)
    assert second.state == SyncState.SYNCED
    assert second.stored == 1
    assert source.received_cursor == "c1"
    assert writer.seen == {("ado:project-a", "42", "1"), ("ado:project-a", "42", "2")}


@pytest.mark.asyncio
async def test_failure_never_advances_checkpoint_beyond_an_older_missing_record() -> None:
    """Later successes can be stored but the failed older item must be retried."""
    writer, checkpoints, quarantine = MemoryWriter(), MemoryCheckpoints(), MemoryQuarantine()
    writer.fail_native_ids.add("10")
    source = FakeSource("ado:project-b", [change("10", "1", "c1"), change("11", "1", "c2")])
    runner = BackfillReconciler(writer, checkpoints, quarantine)

    first = await runner.run_one(source)
    assert first.state == SyncState.PARTIAL
    assert first.checkpoint is None
    assert first.stored == 1
    assert first.quarantined == 1
    assert quarantine.entries == [("ado:project-b", "10", "1", "EVIDENCE_WRITE_FAILED")]

    writer.fail_native_ids.clear()
    second = await runner.run_one(source)
    assert second.state == SyncState.SYNCED
    assert second.stored == 1
    assert second.unchanged == 1
    assert second.checkpoint == "c2"
    assert len(writer.seen) == 2


@pytest.mark.asyncio
async def test_an_unavailable_source_does_not_hide_other_source_success() -> None:
    """Source failure is NOT_SYNCED, not a successful empty history."""
    writer, checkpoints, quarantine = MemoryWriter(), MemoryCheckpoints(), MemoryQuarantine()
    runner = BackfillReconciler(writer, checkpoints, quarantine)
    outcome = await runner.run_all(
        [
            FakeSource("ado:project-failed", unavailable=True),
            FakeSource("ado:project-ok", [change("1", "1", "c1")]),
        ]
    )
    assert not outcome.all_synced
    assert [item.state for item in outcome.sources] == [SyncState.NOT_SYNCED, SyncState.SYNCED]
    assert len(writer.seen) == 1


@pytest.mark.asyncio
async def test_mismatched_approved_source_count_is_partial() -> None:
    """A source-reported count must agree with the set of fetched changes."""
    runner = BackfillReconciler(MemoryWriter(), MemoryCheckpoints(), MemoryQuarantine())
    output = await runner.run_one(FakeSource("ado:a", [change("1", "1", "a")], 2))
    assert output.state is SyncState.PARTIAL
    assert output.reason_code == "SOURCE_COUNT_MISMATCH"
    assert output.checkpoint is None


@pytest.mark.asyncio
async def test_later_source_count_mismatch_keeps_existing_checkpoint_unchanged() -> None:
    """Missing unknown-order items cannot be skipped by a newer cursor."""
    writer, checkpoints, quarantine = MemoryWriter(), MemoryCheckpoints(), MemoryQuarantine()
    checkpoints.cursors["ado:project"] = "prior"
    runner = BackfillReconciler(writer, checkpoints, quarantine)
    output = await runner.run_one(
        FakeSource("ado:project", [change("17", "1", "current")], expected=3)
    )
    assert output.state == SyncState.PARTIAL
    assert output.checkpoint == "prior"
    assert checkpoints.cursors["ado:project"] == "prior"


@pytest.mark.asyncio
async def test_quarantine_unavailable_never_returns_success() -> None:
    """Failure to record a bad source item is an explicit NOT_SYNCED result."""
    writer = MemoryWriter()
    writer.fail_native_ids.add("10")
    runner = BackfillReconciler(writer, MemoryCheckpoints(), MemoryQuarantine(available=False))
    output = await runner.run_one(FakeSource("ado:p", [change("10", "1", "c1")]))
    assert output.state is SyncState.NOT_SYNCED
    assert output.reason_code == "QUARANTINE_UNAVAILABLE"


@pytest.mark.asyncio
async def test_duplicate_source_configuration_is_rejected() -> None:
    """Duplicate approved source identities must not race checkpoint state."""
    runner = BackfillReconciler(MemoryWriter(), MemoryCheckpoints(), MemoryQuarantine())
    with pytest.raises(ValueError, match="only once"):
        await runner.run_all([FakeSource("ado:a"), FakeSource("ado:a")])
