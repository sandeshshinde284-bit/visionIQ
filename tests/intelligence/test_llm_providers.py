"""S08-01 to S08-03: Gemini, Claude and the offline stand-in behind one interface."""

import json
import uuid
from types import SimpleNamespace
from typing import Any

import anthropic
import pytest

try:  # the Anthropic SDK builds its errors on httpx2 (httpx on older releases)
    import httpx2 as httpx
except ImportError:  # pragma: no cover
    import httpx

from app.config import Settings
from app.llm.factory import build_gateway
from app.llm.gateway import Budget, RunLedger
from app.llm.prompts import BRIEF_SCHEMA, TEMPLATES, render_user_prompt
from app.llm.providers.claude import REFUSAL_FALLBACK_BETA, ClaudeProvider
from app.llm.providers.gemini import GeminiProvider, to_vertex_schema
from app.llm.providers.offline import OfflineProvider
from app.llm.types import (
    DataClassification,
    EvidenceContext,
    EvidenceItem,
    ModelPolicy,
    ModelProvider,
    TaskKind,
    TaskSpec,
)

CONTEXT = EvidenceContext(
    snapshot_id="snap-1",
    ticket_title="Bug 4711: Barcode not printed",
    ticket_text="Barcode print failed after retry.",
    evidence=(
        EvidenceItem(
            evidence_id="E1",
            kind="work_item_current",
            title="Bug 101: Barcode print fails after payment retry",
            text="Payment captured, retry printing: no barcode on the receipt.",
        ),
        EvidenceItem(
            evidence_id="E2",
            kind="work_item_current",
            title="Bug 102: Duplicate receipt printed on retry",
            text="Retrying a captured payment prints twice.",
            counterevidence=("A fix linked to this ticket was later reverted.",),
        ),
    ),
)
TEMPLATE = TEMPLATES[TaskKind.BRIEF_DRAFT]
TASK = TaskSpec(
    kind=TaskKind.BRIEF_DRAFT,
    run_id=uuid.uuid4(),
    classification=DataClassification.INTERNAL,
    system_prompt=TEMPLATE.system,
    user_prompt=render_user_prompt(TaskKind.BRIEF_DRAFT, CONTEXT),
    prompt_template_version=TEMPLATE.template_version,
    output_schema=BRIEF_SCHEMA,
)
POLICY = ModelPolicy(timeout_seconds=30, max_output_tokens=2000)
DRAFT = {"advice": [], "limitations": ["nothing to add"]}


# ---- Gemini on Vertex AI ---------------------------------------------------------------------


class FakeSession:
    def __init__(self, *responses: Any) -> None:
        self.responses = list(responses)
        self.requests: list[dict] = []

    def post(self, url: str, json: dict, timeout: float) -> Any:
        self.requests.append({"url": url, "json": json, "timeout": timeout})
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


def vertex(status: int = 200, body: dict | None = None) -> SimpleNamespace:
    return SimpleNamespace(status_code=status, json=lambda: body or {})


def answer(output: dict, finish: str = "STOP") -> dict:
    return {
        "candidates": [
            {"content": {"parts": [{"text": json.dumps(output)}]}, "finishReason": finish}
        ],
        "usageMetadata": {
            "promptTokenCount": 812,
            "candidatesTokenCount": 64,
            "thoughtsTokenCount": 10,
        },
        "modelVersion": "gemini-2.5-flash-001",
    }


def gemini(session: FakeSession, **options: Any) -> GeminiProvider:
    return GeminiProvider(
        "visioniq-dev-1", "europe-west1", "gemini-2.5-flash", session=session, **options
    )


def test_gemini_sends_a_json_schema_request_and_reports_tokens_and_version() -> None:
    session = FakeSession(vertex(body=answer(DRAFT)))

    result = gemini(session).generate(TASK, CONTEXT, POLICY)

    request = session.requests[0]
    assert request["url"] == (
        "https://europe-west1-aiplatform.googleapis.com/v1/projects/visioniq-dev-1/locations/"
        "europe-west1/publishers/google/models/gemini-2.5-flash:generateContent"
    )
    config = request["json"]["generationConfig"]
    assert config["responseMimeType"] == "application/json" and config["maxOutputTokens"] == 2000
    assert request["json"]["systemInstruction"]["parts"][0]["text"] == TASK.system_prompt
    assert request["timeout"] <= 30
    assert (result.status, result.output) == ("OK", DRAFT)
    assert (result.provider, result.model_id, result.model_version) == (
        "gemini",
        "gemini-2.5-flash",
        "gemini-2.5-flash-001",
    )
    assert (result.input_tokens, result.output_tokens) == (812, 74)
    assert result.prompt_template_version == "brief.defect@v1"
    assert result.evidence_snapshot_id == "snap-1"


def test_the_vertex_schema_drops_what_vertex_rejects() -> None:
    schema = to_vertex_schema(BRIEF_SCHEMA)

    assert "additionalProperties" not in json.dumps(schema)
    assert schema["type"] == "OBJECT" and schema["properties"]["advice"]["type"] == "ARRAY"
    assert schema["properties"]["advice"]["items"]["properties"]["kind"]["enum"] == [
        "RISK",
        "DO",
        "DONT",
        "APPROACH",
    ]


def test_gemini_retries_a_throttled_call_once_then_reports_the_status() -> None:
    waits: list[float] = []
    ok = FakeSession(vertex(429), vertex(body=answer(DRAFT)))
    still_failing = FakeSession(vertex(503), vertex(503))

    assert gemini(ok, sleep=waits.append).generate(TASK, CONTEXT, POLICY).status == "OK"
    failed = gemini(still_failing, sleep=waits.append).generate(TASK, CONTEXT, POLICY)

    assert (failed.status, failed.error_code) == ("ERROR", "http_503")
    assert len(waits) == 2 and len(ok.requests) == 2


@pytest.mark.parametrize(
    ("body", "status", "code"),
    [
        ({"promptFeedback": {"blockReason": "SAFETY"}}, "REFUSED", "prompt_blocked"),
        (answer(DRAFT, finish="SAFETY"), "REFUSED", "safety"),
        (answer(DRAFT, finish="MAX_TOKENS"), "ERROR", "output_truncated"),
        ({"candidates": []}, "ERROR", "no_candidates"),
        (
            {
                "candidates": [
                    {"content": {"parts": [{"text": "not json"}]}, "finishReason": "STOP"}
                ]
            },
            "ERROR",
            "output_not_json",
        ),
    ],
)
def test_gemini_failures_are_explicit_states(body, status, code) -> None:
    result = gemini(FakeSession(vertex(body=body))).generate(TASK, CONTEXT, POLICY)

    assert (result.status, result.error_code, result.output) == (status, code, {})


def test_gemini_timeouts_and_network_errors_never_raise() -> None:
    class ReadTimeout(Exception):
        pass

    timed_out = gemini(FakeSession(ReadTimeout())).generate(TASK, CONTEXT, POLICY)
    broken = gemini(FakeSession(ConnectionError("dns"))).generate(TASK, CONTEXT, POLICY)

    assert (timed_out.status, timed_out.error_code) == ("TIMEOUT", "ReadTimeout")
    assert (broken.status, broken.error_code) == ("ERROR", "ConnectionError")
    assert "dns" not in broken.model_dump_json()  # the type, never the message


def test_gemini_rejects_a_malformed_project_or_model_before_any_call() -> None:
    with pytest.raises(ValueError, match="project"):
        GeminiProvider("Bad Project", "europe-west1", "gemini-2.5-flash", session=FakeSession())
    with pytest.raises(ValueError, match="GEMINI_MODEL"):
        GeminiProvider("visioniq-dev-1", "europe-west1", "models/../x", session=FakeSession())


# ---- Claude through the Anthropic SDK --------------------------------------------------------


class FakeMessages:
    def __init__(self, owner: "FakeClaude") -> None:
        self.owner = owner

    def create(self, **request: Any) -> Any:
        self.owner.requests.append(request)
        if isinstance(self.owner.response, Exception):
            raise self.owner.response
        return self.owner.response


class FakeClaude:
    """The parts of an Anthropic client the provider uses."""

    def __init__(self, response: Any) -> None:
        self.response = response
        self.requests: list[dict] = []
        self.timeouts: list[float] = []
        self.messages = FakeMessages(self)
        self.beta = SimpleNamespace(messages=FakeMessages(self))

    def with_options(self, timeout: float) -> "FakeClaude":
        self.timeouts.append(timeout)
        return self


def message(output: Any, stop_reason: str = "end_turn", model: str = "claude-opus-5-5") -> Any:
    text = output if isinstance(output, str) else json.dumps(output)
    return SimpleNamespace(
        content=[
            SimpleNamespace(type="thinking", thinking=""),
            SimpleNamespace(type="text", text=text),
        ],
        stop_reason=stop_reason,
        model=model,
        usage=SimpleNamespace(
            input_tokens=700,
            output_tokens=90,
            cache_read_input_tokens=200,
            cache_creation_input_tokens=0,
        ),
    )


def test_claude_asks_for_structured_output_and_keeps_thinking_adaptive() -> None:
    client = FakeClaude(message(DRAFT))

    result = ClaudeProvider(client, effort="medium").generate(TASK, CONTEXT, POLICY)

    request = client.requests[0]
    assert request["model"] == "claude-opus-5-5" and request["max_tokens"] == 2000
    assert request["system"] == TASK.system_prompt
    assert request["messages"] == [{"role": "user", "content": TASK.user_prompt}]
    assert request["output_config"] == {
        "effort": "medium",
        "format": {"type": "json_schema", "schema": BRIEF_SCHEMA},
    }
    # Not accepted by current Claude models: fixed thinking budgets, sampling knobs, forced tools.
    assert not {"thinking", "temperature", "top_p", "tool_choice", "betas"} & set(request)
    assert client.timeouts == [30]
    assert (result.status, result.output, result.provider) == ("OK", DRAFT, "claude")
    assert (result.input_tokens, result.output_tokens) == (900, 90)
    assert result.model_version == "claude-opus-5-5"


def test_the_direct_api_route_opts_into_server_side_refusal_fallback() -> None:
    client = FakeClaude(message(DRAFT, model="claude-opus-4-8"))

    result = ClaudeProvider(client, refusal_fallback=True).generate(TASK, CONTEXT, POLICY)

    request = client.requests[0]
    assert request["betas"] == [REFUSAL_FALLBACK_BETA] and request["fallbacks"] == "default"
    # The model that actually answered is what the call log records.
    assert (result.model_id, result.model_version) == ("claude-opus-5-5", "claude-opus-4-8")


def http_error(kind: type, status: int) -> Exception:
    request = httpx.Request("POST", "https://api.anthropic.com/v1/messages")
    return kind("failed", response=httpx.Response(status, request=request), body=None)


def sdk_errors() -> list[tuple[Exception, str, str]]:
    request = httpx.Request("POST", "https://api.anthropic.com/v1/messages")
    return [
        (anthropic.APITimeoutError(request=request), "TIMEOUT", "request_timeout"),
        (anthropic.APIConnectionError(request=request), "ERROR", "connection_failed"),
        (http_error(anthropic.RateLimitError, 429), "ERROR", "rate_limited"),
        (http_error(anthropic.InternalServerError, 500), "ERROR", "http_500"),
        (http_error(anthropic.BadRequestError, 400), "ERROR", "http_400"),
    ]


@pytest.mark.parametrize(("error", "status", "code"), sdk_errors())
def test_claude_sdk_errors_become_explicit_states(error, status, code) -> None:
    result = ClaudeProvider(FakeClaude(error)).generate(TASK, CONTEXT, POLICY)

    assert (result.status, result.error_code) == (status, code)


@pytest.mark.parametrize(
    ("response", "status", "code"),
    [
        (message(DRAFT, stop_reason="refusal"), "REFUSED", "refusal"),
        (message(DRAFT, stop_reason="max_tokens"), "ERROR", "output_truncated"),
        (message("I think the answer is"), "ERROR", "output_not_json"),
        (message([1, 2]), "ERROR", "output_not_object"),
    ],
)
def test_claude_checks_the_stop_reason_before_reading_content(response, status, code) -> None:
    result = ClaudeProvider(FakeClaude(response)).generate(TASK, CONTEXT, POLICY)

    assert (result.status, result.error_code, result.output) == (status, code, {})


# ---- Offline stand-in and the factory --------------------------------------------------------


def test_the_offline_provider_only_restates_the_evidence() -> None:
    result = OfflineProvider().generate(TASK, CONTEXT, POLICY)

    kinds = [item["kind"] for item in result.output["advice"]]
    assert kinds == ["APPROACH", "DONT"]  # the closest case first; the reverted fix as a don't
    for item, source in zip(result.output["advice"], CONTEXT.evidence, strict=True):
        (support,) = item["support"]
        assert support["evidence_id"] == source.evidence_id
        assert support["quote"] in source.text
        assert source.title in item["statement"]
    assert result.cited_evidence_ids == ["E1", "E2"]
    assert result.provider == "offline" and "offline" in result.output["limitations"][0]


def providers_of(settings: Settings) -> dict[str, ModelProvider | None]:
    gateway = build_gateway(settings)
    run = RunLedger(None, DataClassification.INTERNAL, Budget(1, 1, 1))
    routing = gateway._routing  # the test inspects what was wired, not a public behaviour
    return {
        "brief": routing.primary(TaskKind.BRIEF_DRAFT, run.classification).provider,
        "code": routing.primary(TaskKind.CODE_FIX, run.classification).provider,
    }


def settings(**values: Any) -> Settings:
    return Settings(_env_file=None, **values)


def test_with_no_provider_configured_nothing_is_routed(monkeypatch: pytest.MonkeyPatch) -> None:
    for name in ("GCP_PROJECT_ID", "LLM_MODE", "CLAUDE_ROUTE", "LLM_CLAUDE_MAX_CLASSIFICATION"):
        monkeypatch.delenv(name, raising=False)

    assert providers_of(settings()) == {"brief": None, "code": None}


def test_offline_mode_routes_everything_to_the_stand_in(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("APP_ENV", raising=False)
    wired = providers_of(settings(llm_mode="offline"))

    assert isinstance(wired["brief"], OfflineProvider) and isinstance(
        wired["code"], OfflineProvider
    )


def test_offline_mode_is_refused_in_production() -> None:
    with pytest.raises(ValueError, match="offline"):
        settings(llm_mode="offline", app_env="production")


def test_a_claude_route_without_a_d04_limit_wires_no_claude(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", "test-only-not-a-real-key")
    monkeypatch.delenv("LLM_CLAUDE_MAX_CLASSIFICATION", raising=False)
    monkeypatch.delenv("GCP_PROJECT_ID", raising=False)

    without_limit = providers_of(settings(claude_route="anthropic"))
    with_limit = providers_of(
        settings(claude_route="anthropic", llm_claude_max_classification="internal")
    )

    assert without_limit["code"] is None
    assert isinstance(with_limit["code"], ClaudeProvider)
    assert isinstance(with_limit["brief"], ClaudeProvider)  # no Gemini configured: allowed fallback
