"""Validation against the shared JSON Schemas in ``contracts/events`` (S03-01 to S03-03).

Every event is validated when it is produced and again when it is consumed. A consumer rejects an
event type or schema version it does not have a schema for, rather than guessing at its meaning.
"""

import json
from functools import lru_cache
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

_MARKER = Path("contracts") / "events" / "envelope.v1.schema.json"


class EventContractError(ValueError):
    """The event does not match any known contract. Retrying will not help."""

    def __init__(self, message: str, problems: list[str] | None = None) -> None:
        super().__init__(message)
        self.problems = problems or []


def find_contracts_dir(start: Path | None = None) -> Path:
    """Locate ``contracts/events`` above this file (repository checkout or container /app)."""
    for directory in (start or Path(__file__).resolve()).parents:
        if (directory / _MARKER).is_file():
            return directory / "contracts" / "events"
    raise FileNotFoundError("contracts/events not found; set EVENT_CONTRACTS_DIR")


class ContractRegistry:
    """Loads every ``*.schema.json`` once and validates events by type and version."""

    def __init__(self, directory: Path) -> None:
        self.directory = directory
        resources: list[tuple[str, Resource[Any]]] = []
        self._validators: dict[tuple[str, int], Draft202012Validator] = {}
        schemas = {
            path.name: json.loads(path.read_text(encoding="utf-8"))
            for path in sorted(directory.glob("*.schema.json"))
        }
        for schema in schemas.values():
            resources.append((schema["$id"], Resource.from_contents(schema)))
        registry: Registry[Any] = Registry().with_resources(resources)
        for name, schema in schemas.items():
            if name.startswith("envelope."):
                continue
            event_type, version = name.removesuffix(".schema.json").rsplit(".v", 1)
            self._validators[(event_type, int(version))] = Draft202012Validator(
                schema, registry=registry
            )

    def known(self) -> list[tuple[str, int]]:
        return sorted(self._validators)

    def validate(self, event: object) -> dict[str, Any]:
        """Return the event unchanged if valid, else raise :class:`EventContractError`."""
        if not isinstance(event, dict):
            raise EventContractError("event is not a JSON object")
        event_type, version = event.get("event_type"), event.get("schema_version")
        if not isinstance(event_type, str) or not isinstance(version, int):
            raise EventContractError("event_type or schema_version missing")
        validator = self._validators.get((event_type, version))
        if validator is None:
            raise EventContractError(f"no contract for {event_type} v{version}")
        # Report schema paths only: values can carry ticket data and must not reach the logs.
        problems = sorted(
            f"{'/'.join(map(str, error.absolute_path)) or '<root>'}: {error.validator}"
            for error in validator.iter_errors(event)
        )
        if problems:
            raise EventContractError(f"{event_type} v{version} violates its contract", problems)
        return event


@lru_cache
def get_contracts(directory: Path | None = None) -> ContractRegistry:
    return ContractRegistry(directory or find_contracts_dir())
