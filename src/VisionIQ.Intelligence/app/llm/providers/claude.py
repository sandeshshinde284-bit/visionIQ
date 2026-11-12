"""S08-03: Claude through the Anthropic SDK, directly or on Vertex AI (decision D04).

``CLAUDE_ROUTE`` chooses where the request goes:

* ``vertex``    - Claude on Vertex AI: the data stays inside the Google Cloud project.
* ``anthropic`` - the Anthropic API: an external transfer, so it needs D04 approval.
* ``off``       - no Claude provider at all (the default until D04 is decided).

The client is injectable for tests. Structured output uses ``output_config.format``; the model's
thinking stays on (adaptive) and its depth is set with ``output_config.effort``.
"""

from __future__ import annotations

import json
import time
from typing import Any

from app.llm.types import EvidenceContext, ModelPolicy, ModelResult, TaskSpec, failed

REFUSAL_FALLBACK_BETA = "server-side-fallback-2026-07-01"


class ClaudeProvider:
    name = "claude"

    def __init__(
        self,
        client: Any,
        model: str = "claude-opus-5-5",
        effort: str = "medium",
        refusal_fallback: bool = False,
    ) -> None:
        self._client = client
        self.model_id = model
        self._effort = effort
        # Server-side refusal fallback exists on the Anthropic API only, not on Vertex AI.
        self._refusal_fallback = refusal_fallback

    @classmethod
    def for_anthropic_api(
        cls, model: str, effort: str, refusal_fallback: bool = True
    ) -> ClaudeProvider:
        import anthropic

        # Credentials come from the environment (ANTHROPIC_API_KEY or an `ant auth login` profile).
        return cls(anthropic.Anthropic(max_retries=0), model, effort, refusal_fallback)

    @classmethod
    def for_vertex(cls, project: str, region: str, model: str, effort: str) -> ClaudeProvider:
        from anthropic import AnthropicVertex

        return cls(AnthropicVertex(project_id=project, region=region, max_retries=0), model, effort)

    def generate(
        self, task: TaskSpec, context: EvidenceContext, policy: ModelPolicy
    ) -> ModelResult:
        import anthropic

        request: dict[str, Any] = {
            "model": self.model_id,
            "max_tokens": policy.max_output_tokens,
            "system": task.system_prompt,
            "messages": [{"role": "user", "content": task.user_prompt}],
            "output_config": {
                "effort": self._effort,
                "format": {"type": "json_schema", "schema": task.output_schema},
            },
        }
        client = self._client.with_options(timeout=policy.timeout_seconds)
        started = time.perf_counter()

        def elapsed() -> int:
            return int((time.perf_counter() - started) * 1000)

        try:
            if self._refusal_fallback:
                response = client.beta.messages.create(
                    betas=[REFUSAL_FALLBACK_BETA], fallbacks="default", **request
                )
            else:
                response = client.messages.create(**request)
        except anthropic.APITimeoutError:
            return failed(self, task, context, "TIMEOUT", "request_timeout", elapsed())
        except anthropic.RateLimitError:
            return failed(self, task, context, "ERROR", "rate_limited", elapsed())
        except anthropic.APIStatusError as error:
            return failed(self, task, context, "ERROR", f"http_{error.status_code}", elapsed())
        except anthropic.APIConnectionError:
            return failed(self, task, context, "ERROR", "connection_failed", elapsed())

        usage = response.usage
        result = ModelResult(
            provider=self.name,
            model_id=self.model_id,
            # The model that actually answered (it differs after a refusal fallback).
            model_version=response.model,
            prompt_template_version=task.prompt_template_version,
            evidence_snapshot_id=context.snapshot_id,
            input_tokens=int(usage.input_tokens or 0)
            + int(getattr(usage, "cache_read_input_tokens", 0) or 0)
            + int(getattr(usage, "cache_creation_input_tokens", 0) or 0),
            output_tokens=int(usage.output_tokens or 0),
            elapsed_ms=elapsed(),
        )
        if response.stop_reason == "refusal":
            return result.model_copy(update={"status": "REFUSED", "error_code": "refusal"})
        if response.stop_reason == "max_tokens":
            return result.model_copy(update={"status": "ERROR", "error_code": "output_truncated"})
        text = next((block.text for block in response.content if block.type == "text"), "")
        try:
            output = json.loads(text)
        except ValueError:
            return result.model_copy(update={"status": "ERROR", "error_code": "output_not_json"})
        if not isinstance(output, dict):
            return result.model_copy(update={"status": "ERROR", "error_code": "output_not_object"})
        return result.model_copy(update={"output": output})
