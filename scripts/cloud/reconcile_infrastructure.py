#!/usr/bin/env python3
"""Idempotent VisionIQ DEV Pub/Sub reconciliation.

Default: read-only plan. --verify fails for absent/drifted resources.
--apply creates missing resources only; it never overwrites a subscription.
Run IAM/Cloud Run permissions reviews before --apply. No credentials are logged.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

PROJECT = "gbr-mts-wgh-hkt-dev"
REGION = "europe-west3"
ROOT = Path(__file__).resolve().parents[2]
TOPOLOGY = ROOT / "contracts" / "events" / "topology.json"


def gcloud(*args: str, allow_missing: bool = False) -> dict | None:
    command = ["gcloud", *args, "--project", PROJECT, "--format=json"]
    result = subprocess.run(command, text=True, capture_output=True, check=False)
    if result.returncode:
        output = (result.stderr + result.stdout).lower()
        if allow_missing and any(x in output for x in ("not found", "not_found", "404", "does not exist")):
            return None
        raise RuntimeError(f"gcloud {' '.join(args)} failed: {result.stderr.strip() or result.stdout.strip()}")
    return json.loads(result.stdout or "{}")


def execute(*args: str) -> None:
    command = ["gcloud", *args, "--project", PROJECT, "--quiet"]
    result = subprocess.run(command, text=True, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError(f"gcloud {' '.join(args)} failed: {result.stderr.strip() or result.stdout.strip()}")


def ensure(kind: str, name: str, command: list[str], *, mode: str, difference: str = "") -> bool:
    """Return False when present and correct; True when missing or drifted."""
    if difference:
        print(f"[DRIFT] {kind} {name}: {difference}")
        return True  # Never silently change an existing resource.
    print(f"[MISSING] {kind} {name}")
    if mode == "apply":
        execute(*command)
        print(f"[CREATED] {kind} {name}")
        return False
    return True


def check_push(data: dict, *, topic: str, endpoint: str, audience: str, identity: str,
               dead_letter: str, retry: dict) -> str:
    differences: list[str] = []
    if data.get("topic", "").split("/")[-1] != topic:
        differences.append("topic")
    push = data.get("pushConfig") or {}
    if push.get("pushEndpoint") != endpoint:
        differences.append("push endpoint")
    oidc = push.get("oidcToken") or {}
    if oidc.get("serviceAccountEmail") != identity or oidc.get("audience") != audience:
        differences.append("OIDC identity/audience")
    if data.get("deadLetterPolicy", {}).get("deadLetterTopic", "").split("/")[-1] != dead_letter:
        differences.append("dead-letter topic")
    if int(data.get("deadLetterPolicy", {}).get("maxDeliveryAttempts", 0)) != int(retry["max_delivery_attempts"]):
        differences.append("dead-letter attempts")
    if int(data.get("ackDeadlineSeconds", 0)) != int(retry["ack_deadline_seconds"]):
        differences.append("ack deadline")
    return ", ".join(differences)


def iam_bound(kind: str, name: str, role: str, member: str) -> bool:
    data = gcloud("pubsub", kind, "get-iam-policy", name)
    return any(item.get("role") == role and member in item.get("members", [])
               for item in data.get("bindings", []))


def reconcile(mode: str, push_account: str) -> int:
    topology = json.loads(TOPOLOGY.read_text(encoding="utf-8"))
    retry = topology["retry"]
    push_email = f"{push_account}@{PROJECT}.iam.gserviceaccount.com"
    missing = 0

    services = {}
    for consumer in ("api", "intelligence"):
        name = f"visioniq-{consumer}"
        data = gcloud("run", "services", "describe", name, "--region", REGION, allow_missing=True)
        if not data or not data.get("status", {}).get("url"):
            raise RuntimeError(f"Deploy Cloud Run service {name} before creating push subscriptions.")
        services[consumer] = data["status"]["url"].rstrip("/")

    sa = gcloud("iam", "service-accounts", "describe", push_email, allow_missing=True)
    if sa is None:
        print(f"[REQUIRED IAM] Create service account {push_email} and grant roles/run.invoker on both services.")
        if mode == "apply":
            raise RuntimeError("Push service account is missing; IAM must be approved separately.")
        missing += 1

    project_data = gcloud("projects", "describe", PROJECT)
    pubsub_agent = f"serviceAccount:service-{project_data['projectNumber']}@gcp-sa-pubsub.iam.gserviceaccount.com"

    for event in topology["events"]:
        topic = event["topic"]
        dlq = event["dead_letter_topic"]
        subscription = event["subscription"]
        hold = f"{dlq}-hold"
        destination = services[event["consumer"]]
        endpoint = destination + topology["push_path"]

        for name in (topic, dlq):
            existing = gcloud("pubsub", "topics", "describe", name, allow_missing=True)
            if existing:
                print(f"[OK] topic {name}")
            else:
                missing += int(ensure("topic", name, ["pubsub", "topics", "create", name], mode=mode))

        hold_data = gcloud("pubsub", "subscriptions", "describe", hold, allow_missing=True)
        if hold_data:
            diff = "" if (hold_data.get("topic", "").split("/")[-1] == dlq and
                          not (hold_data.get("pushConfig") or {}).get("pushEndpoint")) else "topic or pull mode"
            if diff:
                missing += int(ensure("hold subscription", hold, [], mode=mode, difference=diff))
            else:
                print(f"[OK] hold subscription {hold}")
        else:
            missing += int(ensure("hold subscription", hold, [
                "pubsub", "subscriptions", "create", hold, f"--topic={dlq}",
                f"--message-retention-duration={retry['message_retention_seconds']}s",
                "--expiration-period=never",
            ], mode=mode))

        push_data = gcloud("pubsub", "subscriptions", "describe", subscription, allow_missing=True)
        if push_data:
            diff = check_push(push_data, topic=topic, endpoint=endpoint, audience=destination,
                              identity=push_email, dead_letter=dlq, retry=retry)
            if diff:
                missing += int(ensure("push subscription", subscription, [], mode=mode, difference=diff))
            else:
                print(f"[OK] push subscription {subscription}")
        else:
            missing += int(ensure("push subscription", subscription, [
                "pubsub", "subscriptions", "create", subscription, f"--topic={topic}",
                f"--push-endpoint={endpoint}", f"--push-auth-service-account={push_email}",
                f"--push-auth-token-audience={destination}",
                f"--ack-deadline={retry['ack_deadline_seconds']}",
                f"--min-retry-delay={retry['minimum_backoff_seconds']}s",
                f"--max-retry-delay={retry['maximum_backoff_seconds']}s",
                f"--dead-letter-topic={dlq}",
                f"--max-delivery-attempts={retry['max_delivery_attempts']}",
                f"--message-retention-duration={retry['message_retention_seconds']}s",
                "--expiration-period=never",
            ], mode=mode))

        for kind, resource, role in (
            ("topics", dlq, "roles/pubsub.publisher"),
            ("subscriptions", subscription, "roles/pubsub.subscriber"),
        ):
            # IAM is checked only once a resource exists; permissions never change in plan/verify modes.
            resource_data = gcloud("pubsub", kind, "describe", resource, allow_missing=True)
            if resource_data is None:
                continue
            if iam_bound(kind, resource, role, pubsub_agent):
                print(f"[OK] {role} for Pub/Sub service agent on {resource}")
            else:
                print(f"[MISSING IAM] {role} for Pub/Sub service agent on {resource}")
                if mode == "apply":
                    execute("pubsub", kind, "add-iam-policy-binding", resource,
                            f"--member={pubsub_agent}", f"--role={role}")
                    print(f"[GRANTED] {role} on {resource}")
                else:
                    missing += 1

    print("Reminder: separately verify publisher roles for API/Intelligence and "
          "roles/run.invoker for the push account. No service IAM is changed here.")
    if missing:
        print(f"[INCOMPLETE] {missing} missing or drifted item(s)")
    else:
        print("[PASS] Existing Pub/Sub topology matches the declared contract")
    return 1 if mode == "verify" and missing else 1 if mode == "apply" and missing else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", default=PROJECT)
    parser.add_argument("--region", default=REGION)
    parser.add_argument("--mode", choices=("plan", "verify", "apply"), default="plan")
    parser.add_argument("--push-service-account", default="visioniq-pubsub-push",
                        help="Existing user-managed push identity (account name without domain)")
    args = parser.parse_args()
    if args.project != PROJECT or args.region != REGION:
        parser.error("Provisioning is restricted to the approved VisionIQ DEV project/region")
    if not args.push_service_account.replace("-", "").isalnum():
        parser.error("Invalid push service account name")
    try:
        return reconcile(args.mode, args.push_service_account)
    except (RuntimeError, KeyError, ValueError) as exc:
        print(f"[ERROR] {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
