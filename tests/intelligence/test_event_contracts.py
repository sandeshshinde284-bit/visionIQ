"""S03-01 to S03-03: golden fixtures are the cross-language contract (.NET tests share them)."""

import json
from pathlib import Path

import pytest

from app.events.contracts import ContractRegistry, EventContractError, find_contracts_dir
from app.events.envelope import AnalysisCompletedV1, AnalysisRequestedV1, EventEnvelope, follow_up
from app.events.topology import load_topology

CONTRACTS = find_contracts_dir()
VALID = sorted((CONTRACTS / "fixtures" / "valid").glob("*.json"))
INVALID = sorted((CONTRACTS / "fixtures" / "invalid").glob("*.json"))


@pytest.fixture(scope="module")
def registry() -> ContractRegistry:
    return ContractRegistry(CONTRACTS)


def _load(path: Path) -> dict[str, object]:
    return json.loads(path.read_text(encoding="utf-8"))


def test_every_contract_has_fixtures_and_a_topic(registry: ContractRegistry) -> None:
    topology = load_topology(CONTRACTS)
    for event_type, version in registry.known():
        assert (CONTRACTS / "fixtures" / "valid" / f"{event_type}.v{version}.json").is_file()
        assert topology.topic_for(event_type, version)


@pytest.mark.parametrize("path", VALID, ids=lambda p: p.stem)
def test_valid_fixtures_pass(registry: ContractRegistry, path: Path) -> None:
    envelope = EventEnvelope.model_validate(registry.validate(_load(path)))
    model = (
        AnalysisRequestedV1 if envelope.event_type == "analysis.requested" else AnalysisCompletedV1
    )
    model.model_validate(envelope.data)


@pytest.mark.parametrize("path", INVALID, ids=lambda p: p.stem)
def test_invalid_fixtures_fail(registry: ContractRegistry, path: Path) -> None:
    with pytest.raises(EventContractError):
        registry.validate(_load(path))


def test_problems_never_echo_values(registry: ContractRegistry) -> None:
    event = _load(CONTRACTS / "fixtures" / "valid" / "analysis.requested.v1.json")
    event["data"]["ticket_id"] = "secret ticket title with spaces"  # type: ignore[index]
    with pytest.raises(EventContractError) as raised:
        registry.validate(event)
    assert raised.value.problems == ["data/ticket_id: pattern"]
    assert "secret" not in str(raised.value)


def test_follow_up_keeps_correlation_and_is_valid(registry: ContractRegistry) -> None:
    cause = EventEnvelope.model_validate(
        _load(CONTRACTS / "fixtures" / "valid" / "analysis.requested.v1.json")
    )
    result = AnalysisCompletedV1.model_validate(
        _load(CONTRACTS / "fixtures" / "valid" / "analysis.completed.v1.json")["data"]
    )

    produced = follow_up(cause, "analysis.completed", result)

    registry.validate(produced.to_json_dict())
    assert produced.event_id != cause.event_id
    assert (produced.trace_id, produced.correlation_id, produced.run_id) == (
        cause.trace_id,
        cause.correlation_id,
        cause.run_id,
    )
