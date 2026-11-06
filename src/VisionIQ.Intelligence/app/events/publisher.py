"""Pub/Sub publisher and the background loop that drains the outbox."""

import asyncio

import structlog

from app.config import Settings
from app.db import connect
from app.events.outbox import PUBLISH_TIMEOUT_SECONDS, Publisher, dispatch_pending


class PubSubPublisher:
    """Uses Application Default Credentials, or the emulator when PUBSUB_EMULATOR_HOST is set."""

    def __init__(self, project_id: str) -> None:
        from google.cloud import pubsub_v1

        self._client = pubsub_v1.PublisherClient()
        self._project_id = project_id

    def publish(self, topic: str, data: bytes, attributes: dict[str, str]) -> None:
        path = self._client.topic_path(self._project_id, topic)
        self._client.publish(path, data, **attributes).result(timeout=PUBLISH_TIMEOUT_SECONDS)


async def run_outbox_dispatcher(settings: Settings, publisher: Publisher) -> None:
    """Poll the outbox until cancelled. Failures are logged and retried on the next tick."""
    log = structlog.get_logger()
    log.info(
        "outbox_dispatcher_started", interval_seconds=settings.events_dispatch_interval_seconds
    )

    def tick() -> int:
        with connect(settings) as connection:
            return dispatch_pending(connection, publisher)

    while True:
        try:
            sent = await asyncio.to_thread(tick)
        except Exception as error:
            # Type only: database exception text can include connection details.
            log.error("outbox_dispatch_failed", error_type=type(error).__name__)
            sent = 0
        if not sent:
            await asyncio.sleep(settings.events_dispatch_interval_seconds)
