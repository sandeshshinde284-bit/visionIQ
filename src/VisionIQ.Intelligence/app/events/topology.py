"""Topic and subscription names, read from the shared ``contracts/events/topology.json``."""

import json
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class EventRoute:
    event_type: str
    schema_version: int
    topic: str
    dead_letter_topic: str
    subscription: str
    consumer: str


@dataclass(frozen=True)
class Topology:
    routes: tuple[EventRoute, ...]
    retry: dict[str, int]
    push_path: str

    def topic_for(self, event_type: str, schema_version: int) -> str:
        for route in self.routes:
            if (route.event_type, route.schema_version) == (event_type, schema_version):
                return route.topic
        raise KeyError(f"no topic for {event_type} v{schema_version}")


@lru_cache
def load_topology(contracts_dir: Path) -> Topology:
    raw: dict[str, Any] = json.loads((contracts_dir / "topology.json").read_text(encoding="utf-8"))
    return Topology(
        routes=tuple(EventRoute(**item) for item in raw["events"]),
        retry=raw["retry"],
        push_path=raw["push_path"],
    )
