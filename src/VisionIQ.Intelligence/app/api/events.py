"""Pub/Sub push endpoint (S03-09).

Pub/Sub treats 2xx as ack and anything else as nack. A nacked message is retried with backoff and,
after the subscription's max delivery attempts, moved to its dead-letter topic. So:

* processed or duplicate         -> 204 (ack; a duplicate changes nothing)
* malformed, contract-invalid or
  in conflict with stored state  -> 400 (retrying cannot fix it; it ends in the dead-letter topic)
* database or handler failure    -> 503 (transient; Pub/Sub retries)

On Cloud Run the service stays private: Pub/Sub authenticates with an OIDC token for a
push service account that holds roles/run.invoker, checked by Cloud Run before this code runs.
"""

import base64
import binascii
import json
from typing import Annotated, Any

import structlog
from fastapi import APIRouter, Depends, Request, Response
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ValidationError
from starlette.concurrency import run_in_threadpool

from app.events.contracts import EventContractError
from app.events.envelope import EventEnvelope
from app.events.processing import EventProcessor, EventRejectedError, Outcome

router = APIRouter(tags=["events"])


class PushMessage(BaseModel):
    data: str
    message_id: str | None = None
    attributes: dict[str, str] | None = None
    delivery_attempt: int | None = None


def get_event_processor(request: Request) -> EventProcessor:
    processor: EventProcessor | None = getattr(request.app.state, "event_processor", None)
    if processor is None:
        raise RuntimeError("event processor is not configured")
    return processor


def _rejected(reason: str, problems: list[str] | None = None) -> JSONResponse:
    structlog.get_logger().warning("event_rejected", reason=reason, problems=problems or [])
    return JSONResponse({"status": "rejected", "reason": reason}, status_code=400)


@router.post("/events/pubsub", status_code=204)
async def receive_push(
    request: Request,
    processor: Annotated[EventProcessor, Depends(get_event_processor)],
) -> Response:
    log = structlog.get_logger()
    try:
        body: Any = json.loads(await request.body())
        raw = body["message"]
        message = PushMessage(
            data=raw["data"],
            message_id=raw.get("messageId") or raw.get("message_id"),
            attributes=raw.get("attributes"),
            delivery_attempt=body.get("deliveryAttempt"),
        )
        event = json.loads(base64.b64decode(message.data, validate=True))
    except (ValueError, KeyError, TypeError, binascii.Error, ValidationError):
        return _rejected("malformed_push_message")

    contracts = request.app.state.event_context.contracts
    try:
        envelope = EventEnvelope.model_validate(contracts.validate(event))
    except EventContractError as error:
        return _rejected("contract_violation", error.problems or [str(error)])

    with envelope.logging_scope():
        log.info(
            "event_received",
            message_id=message.message_id,
            delivery_attempt=message.delivery_attempt,
        )
        try:
            outcome = await _run(processor, envelope)
        except EventContractError as error:
            return _rejected("not_consumed_here", [str(error)])
        except EventRejectedError as error:
            return _rejected(error.reason)
        except Exception as error:
            log.error("event_processing_failed", error_type=type(error).__name__)
            return JSONResponse({"status": "retry"}, status_code=503)
        log.info("event_processed", outcome=outcome.value)
    return Response(status_code=204)


async def _run(processor: EventProcessor, envelope: EventEnvelope) -> Outcome:
    # Processing is blocking database work; keep it off the event loop.
    return await run_in_threadpool(processor.process, envelope)
