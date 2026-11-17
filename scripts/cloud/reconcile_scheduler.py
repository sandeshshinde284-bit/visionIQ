#!/usr/bin/env python3
"""Cloud Scheduler job definitions for VisionIQ DEV.

Read-only plan (default) and --mode verify. --mode apply creates missing jobs
PAUSED, with an approved OAuth service account, without changing existing jobs.
No job is resumed here: enable schedules only after end-to-end validation.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys

PROJECT = "gbr-mts-wgh-hkt-dev"
REGION = "europe-west3"
JOBS = (
    ("visioniq-ingestion-hourly", "visioniq-ingestion-job", "0 * * * *"),
    ("visioniq-indexing-daily", "visioniq-indexing-job", "0 2 * * *"),
)


def command(*args: str, allow_missing: bool = False) -> dict | None:
    result = subprocess.run(
        ["gcloud", *args, f"--project={PROJECT}", "--format=json"],
        capture_output=True, text=True, check=False,
    )
    if result.returncode:
        error = (result.stderr + result.stdout).lower()
        if allow_missing and any(w in error for w in ("not found", "not_found", "404", "does not exist")):
            return None
        raise RuntimeError(f"gcloud {' '.join(args)}: {result.stderr.strip() or result.stdout.strip()}")
    return json.loads(result.stdout or "{}")


def apply(*args: str) -> None:
    result = subprocess.run(
        ["gcloud", *args, f"--project={PROJECT}", "--quiet"],
        text=True, capture_output=True, check=False,
    )
    if result.returncode:
        raise RuntimeError(f"gcloud {' '.join(args)}: {result.stderr.strip() or result.stdout.strip()}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("plan", "verify", "apply"), default="plan")
    parser.add_argument("--project", default=PROJECT)
    parser.add_argument("--region", default=REGION)
    parser.add_argument("--scheduler-service-account", default="visioniq-scheduler")
    args = parser.parse_args()
    if args.project != PROJECT or args.region != REGION:
        parser.error("Restricted to the approved VisionIQ DEV environment.")
    if not args.scheduler_service_account.replace("-", "").isalnum():
        parser.error("Invalid scheduler service account name")

    identity = f"{args.scheduler_service_account}@{PROJECT}.iam.gserviceaccount.com"
    missing = 0
    try:
        if not command("iam", "service-accounts", "describe", identity, allow_missing=True):
            print(f"[REQUIRED IAM] Scheduler service account missing: {identity}")
            if args.mode == "apply":
                raise RuntimeError("Provision the scheduler service account and approved jobs.run permissions first.")
            missing += 1

        for name, job, schedule in JOBS:
            target_job = command("run", "jobs", "describe", job, "--region", REGION, allow_missing=True)
            if not target_job:
                print(f"[MISSING] Cloud Run target {job}")
                missing += 1
                # Never create a scheduler that targets an absent job.
                continue

            uri = (f"https://run.googleapis.com/v2/projects/{PROJECT}/"
                   f"locations/{REGION}/jobs/{job}:run")
            scheduler = command("scheduler", "jobs", "describe", name,
                                "--location", REGION, allow_missing=True)
            if scheduler:
                http = scheduler.get("httpTarget") or {}
                oauth = http.get("oauthToken") or {}
                drift = []
                if scheduler.get("schedule") != schedule or scheduler.get("timeZone") != "Etc/UTC":
                    drift.append("schedule/timezone")
                if http.get("uri") != uri or http.get("httpMethod") != "POST":
                    drift.append("HTTP target")
                if oauth.get("serviceAccountEmail") != identity:
                    drift.append("OAuth identity")
                if scheduler.get("state") != "PAUSED":
                    drift.append("not paused (live schedules require separate operational review)")
                if drift:
                    print(f"[DRIFT] Scheduler {name}: {', '.join(drift)} (not automatically modified)")
                    missing += 1
                else:
                    print(f"[OK] Scheduler {name} is configured and paused")
                continue

            print(f"[MISSING] Scheduler {name} -> {job} ({schedule}, UTC)")
            if args.mode != "apply":
                missing += 1
                continue

            apply("scheduler", "jobs", "create", "http", name,
                  "--location", REGION,
                  "--schedule", schedule, "--time-zone", "Etc/UTC",
                  "--uri", uri, "--http-method", "POST",
                  "--oauth-service-account-email", identity,
                  "--oauth-token-scope", "https://www.googleapis.com/auth/cloud-platform",
                  "--attempt-deadline", "180s")
            # Pause immediately; on failure, exit nonzero and pause manually.
            apply("scheduler", "jobs", "pause", name, "--location", REGION)
            print(f"[CREATED PAUSED] Scheduler {name}")

        print("Check the scheduler identity's Cloud Run Jobs execute permissions before enabling.")
        print(f"Reconciliation differences: {missing}")
        return 1 if missing and args.mode != "plan" else 0
    except (RuntimeError, ValueError, KeyError) as exc:
        print(f"[ERROR] {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
