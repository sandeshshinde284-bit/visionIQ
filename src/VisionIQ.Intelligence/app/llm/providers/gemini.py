"""S08-02: Gemini on Vertex AI, with a JSON response schema.

Uses the same authenticated REST session as the embedding client (Application Default
Credentials), so no data leaves the Google Cloud project. The session is injectable for tests.
"""

from __future__ import annotations

import json
import re
import time
from collections.abc import Callable
from typing import Any

from app.llm.types import EvidenceContext, ModelPolicy, ModelResult, TaskSpec, failed

_RETRYABLE = (429, 500, 502, 503, 504)
# Vertex "responseSchema" is an OpenAPI subset: no additionalProperties, upper-case type names.
_UNSUPPORTED_KEYS = ("additionalProperties", "$schema")


def to_vertex_schema(schema: Any) -> Any:
    """The JSON Schema used for validation, reduced to what Vertex accepts."""
    if isinstance(schema, dict):
        converted = {
            key: to_vertex_schema(value)
            for key, value in schema.items()
            if key not in _UNSUPPORTED_KEYS
        }
        if isinstance(converted.get("type"), str):
            converted["type"] = converted["type"].upper()
        return converted
    if isinstance(schema, list):
        return [to_vertex_schema(item) for item in schema]
    return schema


class GeminiProvider:
    name = "gemini"

    def __init__(
        self,
        project: str,
        location: str,
        model: str,
        session: Any = None,
        max_retries: int = 1,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        if not re.fullmatch(r"[a-z][a-z0-9-]{4,61}[a-z0-9]", project) or not re.fullmatch(
            r"[a-z0-9-]+", location
        ):
            raise ValueError("A valid Google Cloud project ID and location are required")
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,100}", model):
            raise ValueError("GEMINI_MODEL is not a valid model name")
        self.model_id = model
        host = (
            "aiplatform.googleapis.com"
            if location == "global"
            else (f"{location}-aiplatform.googleapis.com")
        )
        self._url = (
            f"https://{host}/v1/projects/{project}/locations/{location}"
            f"/publishers/google/models/{model}:generateContent"
        )
        self._max_retries = max_retries
        self._sleep = sleep
        if session is None:
            # Imported lazily so tests and the offline provider need no Google credentials.
            import google.auth
            from google.auth.transport.requests import AuthorizedSession

            credentials, _ = google.auth.default(
                scopes=["https://www.googleapis.com/auth/cloud-platform"]
            )
            session = AuthorizedSession(credentials)
        self._session = session

    def generate(
        self, task: TaskSpec, context: EvidenceContext, policy: ModelPolicy
    ) -> ModelResult:
        body = {
            "systemInstruction": {"parts": [{"text": task.system_prompt}]},
            "contents": [{"role": "user", "parts": [{"text": task.user_prompt}]}],
            "generationConfig": {
                "responseMimeType": "application/json",
                "responseSchema": to_vertex_schema(task.output_schema),
                "maxOutputTokens": policy.max_output_tokens,
                "temperature": 0.2,
            },
        }
        started = time.perf_counter()
        deadline = started + policy.timeout_seconds

        def elapsed() -> int:
            return int((time.perf_counter() - started) * 1000)

        for attempt in range(self._max_retries + 1):
            remaining = deadline - time.perf_counter()
            if remaining <= 0:
                return failed(self, task, context, "TIMEOUT", "deadline_exceeded", elapsed())
            try:
                response = self._session.post(self._url, json=body, timeout=remaining)
            except Exception as error:
                timed_out = "timeout" in type(error).__name__.lower()
                return failed(
                    self,
                    task,
                    context,
                    "TIMEOUT" if timed_out else "ERROR",
                    type(error).__name__,
                    elapsed(),
                )
            if response.status_code in _RETRYABLE and attempt < self._max_retries:
                self._sleep(min(2.0**attempt, max(0.0, deadline - time.perf_counter())))
                continue
            if response.status_code != 200:
                return failed(
                    self, task, context, "ERROR", f"http_{response.status_code}", elapsed()
                )
            return self._parse(task, context, response, elapsed())
        return failed(self, task, context, "ERROR", "retries_exhausted", elapsed())

    def _parse(
        self, task: TaskSpec, context: EvidenceContext, response: Any, elapsed_ms: int
    ) -> ModelResult:
        try:
            payload = response.json()
        except ValueError:
            return failed(self, task, context, "ERROR", "response_not_json", elapsed_ms)
        usage = payload.get("usageMetadata") or {}
        result = ModelResult(
            provider=self.name,
            model_id=self.model_id,
            model_version=payload.get("modelVersion"),
            prompt_template_version=task.prompt_template_version,
            evidence_snapshot_id=context.snapshot_id,
            input_tokens=int(usage.get("promptTokenCount") or 0),
            # Thinking tokens are billed as output, so they count against the budget too.
            output_tokens=int(usage.get("candidatesTokenCount") or 0)
            + int(usage.get("thoughtsTokenCount") or 0),
            elapsed_ms=elapsed_ms,
        )
        if (payload.get("promptFeedback") or {}).get("blockReason"):
            return result.model_copy(update={"status": "REFUSED", "error_code": "prompt_blocked"})
        candidates = payload.get("candidates") or []
        if not candidates:
            return result.model_copy(update={"status": "ERROR", "error_code": "no_candidates"})
        finish = str(candidates[0].get("finishReason") or "")
        if finish in ("SAFETY", "RECITATION", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII"):
            return result.model_copy(update={"status": "REFUSED", "error_code": finish.lower()})
        if finish == "MAX_TOKENS":
            return result.model_copy(update={"status": "ERROR", "error_code": "output_truncated"})
        parts = (candidates[0].get("content") or {}).get("parts") or []
        text = "".join(part.get("text", "") for part in parts if not part.get("thought"))
        try:
            output = json.loads(text)
        except ValueError:
            return result.model_copy(update={"status": "ERROR", "error_code": "output_not_json"})
        if not isinstance(output, dict):
            return result.model_copy(update={"status": "ERROR", "error_code": "output_not_object"})
        return result.model_copy(update={"output": output})
