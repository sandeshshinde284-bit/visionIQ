#!/usr/bin/env python3
"""Offline contract checks for the VisionIQ cloud deployment (Python standard library)."""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def check(ok: bool, message: str) -> None:
    if not ok:
        raise SystemExit(f"[FAIL] {message}")
    print(f"[PASS] {message}")


def main() -> None:
    topology = json.loads((ROOT / "contracts/events/topology.json").read_text(encoding="utf-8"))
    events = topology["events"]
    check(len(events) == 2, "Exactly two event flows are defined")
    check(len({e["topic"] for e in events}) == 2, "Unique Pub/Sub topics")
    check(len({e["subscription"] for e in events}) == 2, "Unique Pub/Sub subscriptions")
    check(all(e["dead_letter_topic"].endswith("-dlq") for e in events), "Dead-letter topics declared")
    check(topology["push_path"] == "/events/pubsub", "Pub/Sub event endpoint")

    api = (ROOT / "src/VisionIQ.Api/Features/Events/EventTopology.cs").read_text(encoding="utf-8")
    for event in events:
        check(event["topic"] in api, f"API supports topic: {event['topic']}")

    docker = (ROOT / "src/VisionIQ.Ingestor.Cli/Dockerfile").read_text(encoding="utf-8")
    check('"serve-webhooks"' in docker, "Webhook container starts HTTP service")
    check('"VisionIQ.Ingestor.Cli.dll"' in docker, "Webhook image contains ingestor CLI")
    check("COPY db ./db" in (ROOT / "db/Dockerfile").read_text(encoding="utf-8"),
          "Migration image contains db code")
    cli = (ROOT / "src/VisionIQ.Ingestor/Configuration/CommandLine.cs").read_text(encoding="utf-8")
    check('case "--source"' in cli and 'case "all"' in cli, "Ingestor accepts sync --source all")
    indexer = (ROOT / "src/VisionIQ.Intelligence/app/knowledge/index_cli.py").read_text(encoding="utf-8")
    check('if __name__ == "__main__":' in indexer, "Indexer CLI available")

    yaml = (ROOT / ".gitlab-ci.yml").read_text(encoding="utf-8")
    check("docker-" + "$" + "{TEAM_SLUG}" in yaml, "Uses approved team Artifact Registry")
    check("db.cloud_manage" in yaml, "Cloud migration uses cloud-specific entrypoint")
    check('GCS_EVIDENCE_BUCKET=' + "$" + '{GCS_EVIDENCE_BUCKET}' in yaml,
          "Evidence bucket supplied to cloud workloads")
    check('BRIEF_PUBLISH_MODE=off,FIX_PR_MODE=off' in yaml,
          "External writes are disabled for initial rollout")
    check('--no-allow-unauthenticated' in yaml, "Webhook is not anonymous")
    print("[PASS] Static cloud contracts validated")


if __name__ == "__main__":
    main()
