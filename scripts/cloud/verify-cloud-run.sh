#!/usr/bin/env bash
# Read-only metadata validation. Does not invoke /health or any writes.
set -euo pipefail
: "${GCP_PROJECT:?Set GCP_PROJECT}"
: "${SERVICE_REGION:?Set SERVICE_REGION}"
: "${TEAM_GAR:?Set TEAM_GAR}"

for service in visioniq-api visioniq-intelligence visioniq-ingestor-webhooks; do
  echo "=== Verifying service ${service} ==="
  gcloud run services describe "${service}" \
    --project "${GCP_PROJECT}" --region "${SERVICE_REGION}" --format=json |
  python3 -c '
import json,sys
data=json.load(sys.stdin)
name=sys.argv[1]; prefix=sys.argv[2]+"/"+name+":"
status=data.get("status",{})
ready=next((c for c in status.get("conditions",[]) if c.get("type")=="Ready"),None)
image=data.get("spec",{}).get("template",{}).get("spec",{}).get("containers",[{}])[0].get("image","")
if not ready or ready.get("status")!="True" or not status.get("latestReadyRevisionName") or status.get("latestReadyRevisionName")!=status.get("latestCreatedRevisionName"):
    raise SystemExit(f"FAIL {name}: latest revision is not Ready")
if not (image.startswith(prefix) or image.startswith(prefix[:-1]+"@")):
    raise SystemExit(f"FAIL {name}: image is outside approved registry: {image}")
print("PASS %s: %s / %s" % (name, status["latestReadyRevisionName"], image))
' "${service}" "${TEAM_GAR}"
done

for job in visioniq-db-migrate visioniq-ingestion-job visioniq-indexing-job; do
  echo "=== Verifying job ${job} ==="
  gcloud run jobs describe "${job}" --project "${GCP_PROJECT}" \
    --region "${SERVICE_REGION}" --format='value(metadata.name)' >/dev/null
  echo "PASS job definition exists: ${job}"
done
echo "Cloud Run metadata validated. Runtime connectivity and E2E tests remain pending."
