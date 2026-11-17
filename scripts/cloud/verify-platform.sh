#!/usr/bin/env bash
# Read-only preflight for the corporate VisionIQ DEV platform.
# No secret payload access, resource creation, or IAM mutation.
set -euo pipefail

: "${GCP_PROJECT:?The corporate GCP template must provide GCP_PROJECT}"
: "${GCS_EVIDENCE_BUCKET:?Set approved canonical evidence bucket in protected CI variables}"
: "${DB_CLOUD_USER:?Set approved Cloud SQL administrator login in protected CI variables}"

if [[ "${GCP_PROJECT}" != "gbr-mts-wgh-hkt-dev" ]]; then
  echo "FAIL: unexpected GCP project" >&2
  exit 1
fi

echo "Validating approved team infrastructure (metadata only)."
repo_format="$(gcloud artifacts repositories describe docker-ps04-t11 \
  --project="$GCP_PROJECT" --location=europe-west1 --format='value(format)')"
[[ "$repo_format" == "DOCKER" ]] || { echo "FAIL: missing team Docker registry" >&2; exit 1; }

connector_state="$(gcloud compute networks vpc-access connectors describe vpc-con \
  --project="$GCP_PROJECT" --region=europe-west3 --format='value(state)')"
[[ "$connector_state" == "READY" ]] || { echo "FAIL: VPC connector not READY" >&2; exit 1; }

db_version="$(gcloud sql instances describe visioniqdev \
  --project="$GCP_PROJECT" --format='value(databaseVersion)')"
[[ "$db_version" == POSTGRES_* ]] || { echo "FAIL: Cloud SQL version not verified" >&2; exit 1; }
echo "Cloud SQL engine: $db_version (cross-check PLATFORM.md, which says PostgreSQL 18)."

gcloud storage buckets describe "gs://${GCS_EVIDENCE_BUCKET}" \
  --format='value(name)' >/dev/null ||
  { echo "FAIL: evidence bucket is not accessible" >&2; exit 1; }
echo "Evidence bucket exists (persisted object URIs have NOT been checked)."

# Verify all 11 secrets listed by PLATFORM.md without reading payloads.
# Core runtime dependencies for the configured ingestion/indexing deployments.
# Other platform secrets are relevant only when the corresponding feature is enabled.
required_secrets=(
  visioniq-db-admin-password
  visioniq-db-api-password
  visioniq-db-intelligence-password
  visioniq-ado-read-pat
  visioniq-ado-webhook-secret
)
optional_secrets=(
  visioniq-ado-comment-pat
  visioniq-ado-pr-pat
  visioniq-pickup-policy
  visioniq-pickup-webhook-secret
  visioniq-anthropic-api-key
  visioniq-log-pseudonym-key
)
missing=0
for secret in "${required_secrets[@]}" "${optional_secrets[@]}"; do
  required=0
  for item in "${required_secrets[@]}"; do
    if [[ "$secret" == "$item" ]]; then required=1; break; fi
  done
  if ! gcloud secrets describe "$secret" --project="$GCP_PROJECT" \
    --format='value(name)' >/dev/null 2>&1; then
    echo "MISSING Secret Manager secret: $secret (required=$required)" >&2
    if (( required == 1 )); then missing=1; fi
    continue
  fi
  enabled="$(gcloud secrets versions list "$secret" --project="$GCP_PROJECT" \
    --filter='state=ENABLED' --limit=1 --format='value(name)')" ||
    { echo "FAIL: cannot inspect versions for $secret" >&2; exit 1; }
  if [[ -z "$enabled" ]]; then
    echo "MISSING enabled version: $secret (required=$required)" >&2
    if (( required == 1 )); then missing=1; fi
  else
    echo "OK enabled Secret Manager version: $secret"
  fi
done

if (( missing != 0 )); then
  echo "FAIL: secret metadata incomplete; contact token owners before deployment." >&2
  exit 1
fi

echo "Platform metadata preflight passed; optional secrets may still need setup."
echo "Not verified: secret contents, runtime IAM, SQL login, outbound NAT, Pub/Sub or E2E."
