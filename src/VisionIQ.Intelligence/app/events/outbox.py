"""Transactional outbox (S03-08): an event is stored in the same transaction as the change that
caused it, then a dispatcher publishes it. A crash between the two only delays the event; a crash
after publishing but before marking it sent re-publishes it, which the consumer's inbox absorbs.
"""

import json
import re
from typing import LiteralString, Protocol

import psycopg
import structlog
from psycopg.types.json import Jsonb

from app.events.contracts import ContractRegistry
from app.events.envelope import EventEnvelope
from app.events.topology import Topology

MAX_BACKOFF_SECONDS = 600
# Failed publishes before a row is parked: about 2.3 hours with the backoff below, long enough to
# ride out a broker outage; a missing topic or permission is parked for a human, not retried
# forever.
MAX_PUBLISH_ATTEMPTS = 20
# Each publish waits at most this long for the broker; also sizes the claim lease.
PUBLISH_TIMEOUT_SECONDS = 10


class Publisher(Protocol):
    def publish(self, topic: str, data: bytes, attributes: dict[str, str]) -> None:
        """Publish and wait for the broker to accept the message; raise on failure."""


def enqueue(
    connection: psycopg.Connection,
    envelope: EventEnvelope,
    contracts: ContractRegistry,
    topology: Topology,
) -> None:
    """Validate against the contract (producer side) and store for publishing."""
    payload = contracts.validate(envelope.to_json_dict())
    connection.execute(
        "INSERT INTO knowledge.event_outbox "
        "(event_id, event_type, schema_version, topic, trace_id, payload) "
        "VALUES (%s, %s, %s, %s, %s, %s)",
        (
            envelope.event_id,
            envelope.event_type,
            envelope.schema_version,
            topology.topic_for(envelope.event_type, envelope.schema_version),
            envelope.trace_id,
            Jsonb(payload),
        ),
    )


def attributes_for(payload: dict[str, object]) -> dict[str, str]:
    """Pub/Sub attributes let subscriptions filter and logs correlate without parsing the body."""
    return {
        key: str(payload[key])
        for key in ("event_id", "event_type", "schema_version", "trace_id", "correlation_id")
    }


def error_code(error: BaseException) -> str:
    """The exception type name, reduced to what ``event_outbox.last_error_code`` accepts."""
    code = re.sub(r"[^A-Za-z0-9_.]", "", type(error).__name__)[:120]
    return code or "Unknown"


def retry_delay_seconds(previous_attempts: int) -> int | None:
    """Seconds to wait after a failed publish; None once the row should be parked."""
    if previous_attempts + 1 >= MAX_PUBLISH_ATTEMPTS:
        return None
    return int(min(MAX_BACKOFF_SECONDS, 5 * 2 ** min(previous_attempts, 10)))


def dispatch_pending(
    connection: psycopg.Connection, publisher: Publisher, batch_size: int = 20
) -> int:
    """Publish due outbox rows; return how many were published.

    Rows are claimed in one short statement that leases them (SKIP LOCKED, so replicas never claim
    the same row), then published with no transaction or lock held, and each outcome is committed on
    its own. A crash mid-batch only re-publishes rows whose outcome was not yet recorded, once their
    lease runs out; the consumer's inbox absorbs the repeat.
    """
    log = structlog.get_logger()
    sent = 0
    # The lease outlasts a whole batch of timed-out publishes, so a row is never claimed twice.
    lease_seconds = batch_size * PUBLISH_TIMEOUT_SECONDS + 60
    with connection.transaction():
        rows = connection.execute(
            "UPDATE knowledge.event_outbox o SET available_at = now() + make_interval(secs => %s) "
            "FROM (SELECT event_id FROM knowledge.event_outbox "
            "      WHERE published_at IS NULL AND parked_at IS NULL AND available_at <= now() "
            "      ORDER BY available_at LIMIT %s FOR UPDATE SKIP LOCKED) due "
            "WHERE o.event_id = due.event_id "
            "RETURNING o.event_id, o.topic, o.payload, o.attempt_count",
            (lease_seconds, batch_size),
        ).fetchall()
    for event_id, topic, payload, attempts in rows:
        try:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            attributes = attributes_for(payload)
        except (KeyError, TypeError, ValueError):
            # A row that cannot be turned into a message never will be: take it out of the queue
            # so it stops blocking the rows behind it and stays unpublished for a human.
            _record(connection, _PARK, ("UnpublishablePayload", event_id))
            log.error("event_outbox_row_unpublishable", event_id=str(event_id))
            continue
        with structlog.contextvars.bound_contextvars(
            trace_id=attributes["trace_id"],
            correlation_id=attributes["correlation_id"],
            event_id=attributes["event_id"],
            event_type=attributes["event_type"],
        ):
            try:
                publisher.publish(topic, data, attributes)
            except Exception as error:
                code = error_code(error)
                delay = retry_delay_seconds(attempts)
                if delay is None:
                    _record(connection, _PARK, (code, event_id))
                    log.error(
                        "event_publish_gave_up", topic=topic, error_type=code, attempt=attempts + 1
                    )
                    continue
                _record(connection, _DEFER, (code, delay, event_id))
                log.warning(
                    "event_publish_failed",
                    topic=topic,
                    error_type=code,
                    attempt=attempts + 1,
                    retry_in_seconds=delay,
                )
                continue
            _record(connection, _PUBLISHED, (event_id,))
            log.info("event_published", topic=topic)
            sent += 1
    return sent


_PUBLISHED = (
    "UPDATE knowledge.event_outbox SET attempt_count = attempt_count + 1, "
    "published_at = now(), last_error_code = NULL WHERE event_id = %s"
)
_DEFER = (
    "UPDATE knowledge.event_outbox SET attempt_count = attempt_count + 1, last_error_code = %s, "
    "available_at = now() + make_interval(secs => %s) WHERE event_id = %s"
)
_PARK = (
    "UPDATE knowledge.event_outbox SET attempt_count = attempt_count + 1, last_error_code = %s, "
    "parked_at = now() WHERE event_id = %s"
)


def _record(
    connection: psycopg.Connection, statement: LiteralString, params: tuple[object, ...]
) -> None:
    """Commit one row's outcome on its own, so later failures in the batch cannot undo it."""
    with connection.transaction():
        connection.execute(statement, params)
