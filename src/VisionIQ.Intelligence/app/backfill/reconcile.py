"""LRN-05 backfill and reconciliation orchestration.

The runner is provider-agnostic and operates only on explicitly approved source
adapters. It never logs raw source payloads or confuses a failed read with an
empty source. Checkpoints advance only through a contiguous successful prefix.
"""

from collections.abc import Sequence
from dataclasses import dataclass, field
from enum import StrEnum
from typing import Any, Protocol


class SyncState(StrEnum):
    """Stable machine-readable result states for each approved source."""

    SYNCED = "SYNCED"
    PARTIAL = "PARTIAL"
    NOT_SYNCED = "NOT_SYNCED"


class WriteState(StrEnum):
    """Evidence-writer outcomes; replays are successful idempotent writes."""

    STORED = "STORED"
    UNCHANGED = "UNCHANGED"


@dataclass(frozen=True, slots=True)
class ChangeRecord:
    """One ordered provider change. The payload must not be added to diagnostics."""

    native_id: str
    revision_id: str
    cursor: str
    payload: Any


@dataclass(frozen=True, slots=True)
class SourceBatch:
    """A source response with an optional independently obtained expected count."""

    records: Sequence[ChangeRecord]
    expected_count: int | None = None


@dataclass(frozen=True, slots=True)
class SourceReport:
    """A sanitized per-source synchronization outcome and checkpoint."""

    source_id: str
    state: SyncState
    received: int = 0
    stored: int = 0
    unchanged: int = 0
    quarantined: int = 0
    checkpoint: str | None = None
    reason_code: str | None = None


@dataclass(frozen=True, slots=True)
class ReconciliationReport:
    """Combines independent source outcomes without concealing failures."""

    sources: tuple[SourceReport, ...] = field(default_factory=tuple)

    @property
    def all_synced(self) -> bool:
        """Return true only when every approved source completed successfully."""
        return bool(self.sources) and all(
            source.state is SyncState.SYNCED for source in self.sources
        )


class ChangeSource(Protocol):
    """Supplies changes ordered by the provider's safe opaque checkpoint."""

    @property
    def source_id(self) -> str:
        """Return the canonical allowlisted source identity."""

    async def read(self, cursor: str | None) -> SourceBatch:
        """Read ordered changes after the checkpoint without mutating the source."""


class EvidenceWriter(Protocol):
    """Stores canonical evidence idempotently before a checkpoint is advanced."""

    async def write(self, source_id: str, record: ChangeRecord) -> WriteState:
        """Persist an immutable source revision and return its replay outcome."""


class CheckpointStore(Protocol):
    """Stores durable committed progress per source."""

    async def get(self, source_id: str) -> str | None:
        """Read a source's last durable cursor."""

    async def put(self, source_id: str, cursor: str) -> None:
        """Persist a new cursor after the corresponding evidence writes succeed."""


class QuarantineStore(Protocol):
    """Records failed items without source payloads for investigation and retry."""

    async def put(self, source_id: str, native_id: str, revision_id: str, error_code: str) -> None:
        """Record a structured failure for a particular source revision."""


class BackfillReconciler:
    """Runs an approved source batch with fail-safe checkpoints and quarantine."""

    def __init__(
        self,
        writer: EvidenceWriter,
        checkpoints: CheckpointStore,
        quarantine: QuarantineStore,
    ) -> None:
        """Inject persistence dependencies without constructing cloud clients."""
        self._writer = writer
        self._checkpoints = checkpoints
        self._quarantine = quarantine

    async def run_one(self, source: ChangeSource) -> SourceReport:
        """Process ordered source changes without skipping a failed older item."""
        source_id = source.source_id
        if not source_id or len(source_id) > 256:
            raise ValueError("Source identity must be nonempty and bounded")

        try:
            saved = await self._checkpoints.get(source_id)
            batch = await source.read(saved)
        except Exception:
            return SourceReport(
                source_id, SyncState.NOT_SYNCED, reason_code="SOURCE_OR_CHECKPOINT_UNAVAILABLE"
            )

        if batch.expected_count is not None and batch.expected_count < 0:
            return SourceReport(source_id, SyncState.NOT_SYNCED, reason_code="INVALID_SOURCE_COUNT")

        accepted = unchanged = failed = 0
        contiguous_cursor = saved
        had_gap = False
        for record in batch.records:
            if not record.native_id or not record.revision_id or not record.cursor:
                had_gap = True
                failed += 1
                try:
                    await self._quarantine.put(
                        source_id,
                        record.native_id or "UNKNOWN",
                        record.revision_id or "UNKNOWN",
                        "INVALID_SOURCE_RECORD",
                    )
                except Exception:
                    return SourceReport(
                        source_id,
                        SyncState.NOT_SYNCED,
                        received=len(batch.records),
                        stored=accepted,
                        unchanged=unchanged,
                        quarantined=failed,
                        checkpoint=saved,
                        reason_code="QUARANTINE_UNAVAILABLE",
                    )
                continue

            try:
                result = await self._writer.write(source_id, record)
                if result is WriteState.STORED:
                    accepted += 1
                elif result is WriteState.UNCHANGED:
                    unchanged += 1
                else:
                    raise ValueError("Unknown evidence write result")
                if not had_gap:
                    contiguous_cursor = record.cursor
            except Exception:
                had_gap = True
                failed += 1
                try:
                    await self._quarantine.put(
                        source_id,
                        record.native_id,
                        record.revision_id,
                        "EVIDENCE_WRITE_FAILED",
                    )
                except Exception:
                    return SourceReport(
                        source_id,
                        SyncState.NOT_SYNCED,
                        received=len(batch.records),
                        stored=accepted,
                        unchanged=unchanged,
                        quarantined=failed,
                        checkpoint=saved,
                        reason_code="QUARANTINE_UNAVAILABLE",
                    )

        try:
            if contiguous_cursor is not None and contiguous_cursor != saved:
                await self._checkpoints.put(source_id, contiguous_cursor)
        except Exception:
            return SourceReport(
                source_id,
                SyncState.PARTIAL,
                received=len(batch.records),
                stored=accepted,
                unchanged=unchanged,
                quarantined=failed,
                checkpoint=saved,
                reason_code="CHECKPOINT_WRITE_FAILED",
            )

        incomplete_count = batch.expected_count is not None and batch.expected_count != len(
            batch.records
        )
        state = SyncState.PARTIAL if had_gap or incomplete_count else SyncState.SYNCED
        return SourceReport(
            source_id,
            state,
            received=len(batch.records),
            stored=accepted,
            unchanged=unchanged,
            quarantined=failed,
            checkpoint=contiguous_cursor,
            reason_code=(
                "SOURCE_COUNT_MISMATCH"
                if incomplete_count
                else "QUARANTINED_RECORDS"
                if had_gap
                else None
            ),
        )

    async def run_all(self, sources: Sequence[ChangeSource]) -> ReconciliationReport:
        """Reconcile every approved source independently despite per-source errors."""
        reports: list[SourceReport] = []
        ids: set[str] = set()
        for source in sources:
            if source.source_id in ids:
                raise ValueError("Each source may be configured only once")
            ids.add(source.source_id)
            reports.append(await self.run_one(source))
        return ReconciliationReport(tuple(reports))
