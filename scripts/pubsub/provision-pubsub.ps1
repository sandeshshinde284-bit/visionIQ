<#
.SYNOPSIS
 Provision FND-05 Pub/Sub topics, dead-letter topics and authenticated push subscriptions (S02-07, S02-08).
.DESCRIPTION
 Plan-only without -Apply: prints every gcloud command and changes nothing. Reads
 contracts/events/topology.json, the same file the local emulator setup and both services use.
 Push subscriptions call the private Cloud Run services with an OIDC token for -PushServiceAccount,
 which must already hold roles/run.invoker on both services (granted separately, after approval).
 Dead-lettering needs the Pub/Sub service agent to publish to each dead-letter topic and subscribe to
 each source subscription; those bindings are part of the plan. Existing resources are not modified.
.EXAMPLE
 .\scripts\pubsub\provision-pubsub.ps1 -ApiUrl https://visioniq-api-xyz-ey.a.run.app -IntelligenceUrl https://visioniq-intelligence-xyz-ey.a.run.app
 .\scripts\pubsub\provision-pubsub.ps1 -ApiUrl ... -IntelligenceUrl ... -Apply
#>
[CmdletBinding()]
param(
    [string]$Project = 'gbr-mts-wgh-hkt-dev',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://[a-z0-9.-]+\.run\.app$')]
    [string]$ApiUrl,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://[a-z0-9.-]+\.run\.app$')]
    [string]$IntelligenceUrl,
    [string]$PushServiceAccount = 'visioniq-pubsub-push',
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Project -cne 'gbr-mts-wgh-hkt-dev') {
    throw 'FND-05 provisioning is restricted to the approved development project.'
}

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$topology = Get-Content -Raw -Encoding utf8 (Join-Path $root 'contracts\events\topology.json') | ConvertFrom-Json
$retry = $topology.retry
$pushAccount = "$PushServiceAccount@$Project.iam.gserviceaccount.com"
$baseUrls = @{ api = $ApiUrl; intelligence = $IntelligenceUrl }

$projectNumber = if ($Apply) {
    (gcloud projects describe $Project --format='value(projectNumber)').Trim()
} else {
    '<PROJECT_NUMBER>'
}
$serviceAgent = "serviceAccount:service-$projectNumber@gcp-sa-pubsub.iam.gserviceaccount.com"

function Invoke-Step {
    param([string]$Description, [string[]]$Arguments, [string]$ExistsCheck)
    Write-Host "# $Description"
    Write-Host ("gcloud " + ($Arguments -join ' '))
    if (-not $Apply) { return }
    if ($ExistsCheck) {
        # A missing resource makes gcloud write to stderr; with 'Stop', Windows PowerShell 5.1 would turn that
        # into a terminating error, so the probe runs under 'Continue' and is judged by its exit code only.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & gcloud @($ExistsCheck -split ' ') --project=$Project --format='value(name)' 2>$null | Out-Null
            $exists = $LASTEXITCODE -eq 0
        } finally {
            $ErrorActionPreference = $previous
        }
        if ($exists) { Write-Host '  exists, left unchanged'; return }
    }
    & gcloud @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gcloud failed: $Description" }
}

if (-not $Apply) { Write-Host 'PLAN ONLY: no resources are created. Re-run with -Apply after approval.' }

foreach ($event in $topology.events) {
    $endpoint = $baseUrls[$event.consumer] + $topology.push_path
    Invoke-Step "topic $($event.topic)" @('pubsub', 'topics', 'create', $event.topic, "--project=$Project") "pubsub topics describe $($event.topic)"
    Invoke-Step "dead-letter topic $($event.dead_letter_topic)" @('pubsub', 'topics', 'create', $event.dead_letter_topic, "--project=$Project") "pubsub topics describe $($event.dead_letter_topic)"
    Invoke-Step "dead-letter capture subscription for replay" @(
        'pubsub', 'subscriptions', 'create', "$($event.dead_letter_topic)-hold", "--topic=$($event.dead_letter_topic)",
        "--message-retention-duration=$($retry.message_retention_seconds)s", '--expiration-period=never', "--project=$Project"
    ) "pubsub subscriptions describe $($event.dead_letter_topic)-hold"
    Invoke-Step "push subscription $($event.subscription) -> $endpoint" @(
        'pubsub', 'subscriptions', 'create', $event.subscription, "--topic=$($event.topic)",
        "--push-endpoint=$endpoint", "--push-auth-service-account=$pushAccount", "--push-auth-token-audience=$($baseUrls[$event.consumer])",
        "--ack-deadline=$($retry.ack_deadline_seconds)",
        "--min-retry-delay=$($retry.minimum_backoff_seconds)s", "--max-retry-delay=$($retry.maximum_backoff_seconds)s",
        "--dead-letter-topic=$($event.dead_letter_topic)", "--max-delivery-attempts=$($retry.max_delivery_attempts)",
        "--message-retention-duration=$($retry.message_retention_seconds)s", '--expiration-period=never', "--project=$Project"
    ) "pubsub subscriptions describe $($event.subscription)"
    Invoke-Step "service agent may publish dead letters" @(
        'pubsub', 'topics', 'add-iam-policy-binding', $event.dead_letter_topic, "--member=$serviceAgent", '--role=roles/pubsub.publisher', "--project=$Project"
    )
    Invoke-Step "service agent may forward from the source subscription" @(
        'pubsub', 'subscriptions', 'add-iam-policy-binding', $event.subscription, "--member=$serviceAgent", '--role=roles/pubsub.subscriber', "--project=$Project"
    )
}

Write-Host "# Runtime identity publishes; it needs roles/pubsub.publisher on each source topic (grant after approval)."
