<#
.SYNOPSIS
 Manually deploy reviewed Artifact Registry images to private Cloud Run services, configured to do work.
.DESCRIPTION
 Plan-only without -Deploy: prints both services' full settings and changes nothing. Requires numeric pinned
 secret versions, an explicit reviewed Git commit, approved runtime IAM and corporate GCP access. Does not invoke
 Cloud Build, make services public, migrate Cloud SQL (see 04-migrate-cloud-sql.ps1), or create secrets or IAM
 grants.

 What gets turned on:
 - Both services: Pub/Sub event dispatch, CPU always allocated and -MinInstances kept warm, so the background
   workers (pickup, brief generation, outbox dispatch, brief delivery) keep running between
   requests. Cloud Run otherwise gives a service CPU only while it answers a request.
 - Intelligence: the brief pipeline with live Gemini on Vertex AI (Claude stays off; DEFECT stays off, as the
   image cannot run it yet).
 - API: brief delivery (-BriefPublishMode record keeps briefs in the database; ado posts them on the ticket).
 - API ticket pickup, only when -PickupPolicyVersion is given: the policy is mounted from Secret Manager as a
   file. Ingress stays internal, so Azure DevOps webhooks cannot reach the service; pickups come from the sweep
   (every 15 minutes), which needs -AdoOrgUrl and -AdoReadPatVersion.

 Every deploy replaces the full environment of each service (--set-env-vars), so settings added in the console
 are removed. Keep them here instead.
.EXAMPLE
 # Plan only: brief generation and recording, no ticket pickup yet
 .\scripts\manual-deploy\02-deploy-services.ps1 -Commit (git rev-parse HEAD) -ApiSecretVersion 1 -IntelligenceSecretVersion 1

 # Full pilot on three work items, briefs posted to Azure DevOps
 .\scripts\manual-deploy\02-deploy-services.ps1 -Commit (git rev-parse HEAD) -ApiSecretVersion 1 -IntelligenceSecretVersion 1 `
   -AdoOrgUrl https://dev.azure.com/contoso `
   -PickupPolicyVersion 1 -PickupWebhookSecretVersion 1 -AdoReadPatVersion 1 -PickupAllowedWorkItemIds 101,102,103 `
   -BriefPublishMode ado -AdoCommentPatVersion 1 -Deploy
#>
[CmdletBinding()]
param(
    [string]$Project = 'gbr-mts-wgh-hkt-dev',
    [string]$Region = 'europe-west3',
    [string]$Repository = 'team-visioniq-docker-dev',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string]$Commit,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ApiSecretVersion,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$IntelligenceSecretVersion,
    [string]$VPCConnector = 'vpc-con',
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')]
    [string]$DbPrivateIp = '10.102.16.8',

    # Instances kept warm per service. 1 keeps the workers running; 0 lets the services stop when idle (no briefs).
    [ValidateRange(0, 2)]
    [int]$MinInstances = 1,
    [ValidatePattern('^[A-Za-z0-9_.:-]{1,128}$')]
    [string]$TenantId = 'visioniq-dev',

    # Intelligence: Vertex AI region and Gemini model.
    [ValidatePattern('^[a-z]+(-[a-z0-9]+)+$|^global$')]
    [string]$VertexLocation = 'europe-west3',
    [ValidatePattern('^[a-z0-9.-]+$')]
    [string]$GeminiModel = 'gemini-2.5-flash',

    # API: brief delivery. off | record (kept in the database) | ado (posted as a work-item comment).
    [ValidateSet('off', 'record', 'ado')]
    [string]$BriefPublishMode = 'record',
    [ValidatePattern('^$|^https://dev\.azure\.com/[A-Za-z0-9._-]+/?$')]
    [string]$AdoOrgUrl = '',

    # API: Secret Manager versions; 0 leaves that feature off. Secret names can be overridden.
    [ValidateRange(0, 2147483647)][int]$PickupPolicyVersion = 0,
    [ValidateRange(0, 2147483647)][int]$PickupWebhookSecretVersion = 0,
    [ValidateRange(0, 2147483647)][int]$AdoReadPatVersion = 0,
    [ValidateRange(0, 2147483647)][int]$AdoCommentPatVersion = 0,
    [string]$PickupPolicySecret = 'visioniq-pickup-policy',
    [string]$PickupWebhookSecretName = 'visioniq-pickup-webhook-secret',
    [string]$AdoReadPatSecret = 'visioniq-ado-read-pat',
    [string]$AdoCommentPatSecret = 'visioniq-ado-comment-pat',
    # Pilot switch: only these work items are ever picked up.
    [ValidateRange(1, 2147483647)]
    [int[]]$PickupAllowedWorkItemIds = @(),

    [switch]$Deploy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This deployment is intentionally restricted to the documented DEVELOPMENT project.
if ($Project -cne 'gbr-mts-wgh-hkt-dev' -or $Region -cne 'europe-west3') {
    throw 'Manual FND-04 is restricted to the approved development project/region.'
}

# Settings that cannot work together fail here, before anything is deployed.
$pickup = $PickupPolicyVersion -gt 0
if ($pickup -and $PickupWebhookSecretVersion -eq 0) {
    throw 'Ticket pickup needs -PickupWebhookSecretVersion: the API refuses a pickup policy without a webhook secret.'
}
if (-not $pickup -and ($PickupWebhookSecretVersion -gt 0 -or $PickupAllowedWorkItemIds.Count -gt 0)) {
    throw '-PickupWebhookSecretVersion and -PickupAllowedWorkItemIds only apply with -PickupPolicyVersion.'
}
if ($BriefPublishMode -eq 'ado' -and (-not $AdoOrgUrl -or $AdoCommentPatVersion -eq 0)) {
    throw '-BriefPublishMode ado needs -AdoOrgUrl and -AdoCommentPatVersion.'
}
if ($AdoReadPatVersion -gt 0 -and -not $AdoOrgUrl) {
    throw '-AdoReadPatVersion needs -AdoOrgUrl.'
}
$sweep = $pickup -and $AdoOrgUrl -and $AdoReadPatVersion -gt 0

$runtime = "visioniq-runtime@$Project.iam.gserviceaccount.com"
$registry = "$($Region)-docker.pkg.dev/$Project/$Repository"
$policyPath = '/secrets/pickup/policy.json'
$labels = 'team=ps04-t11,wl-team=ps04-t11,pocid=ps04-t11,event=hackathon-2026,env=sandbox,app=visioniq'

$common = [ordered]@{
    DB_HOST                 = $DbPrivateIp
    DB_PORT                 = '5432'
    DB_NAME                 = 'visioniq_dev'
    DB_SSL_MODE             = 'require'
    TENANT_ID               = $TenantId
    PUBSUB_PROJECT_ID       = $Project
    EVENTS_DISPATCH_ENABLED = 'true'
}

$intelligenceEnv = [ordered]@{ APP_ENV = 'production'; DB_INTELLIGENCE_USER = 'visioniq_intelligence' }
$common.GetEnumerator() | ForEach-Object { $intelligenceEnv[$_.Key] = $_.Value }
$intelligenceEnv['BRIEF_PIPELINE_ENABLED'] = 'true'
$intelligenceEnv['LLM_MODE'] = 'live'
$intelligenceEnv['EMBEDDING_PROVIDER'] = 'vertex'
$intelligenceEnv['GCP_PROJECT_ID'] = $Project
$intelligenceEnv['VERTEX_LOCATION'] = $VertexLocation
$intelligenceEnv['GEMINI_MODEL'] = $GeminiModel
$intelligenceEnv['CLAUDE_ROUTE'] = 'off'
$intelligenceEnv['DEFECT_PIPELINE_ENABLED'] = 'false'
$intelligenceSecrets = [ordered]@{ DB_INTELLIGENCE_PASSWORD = "visioniq-db-intelligence-password:$IntelligenceSecretVersion" }

$apiEnv = [ordered]@{ ASPNETCORE_ENVIRONMENT = 'Production'; DB_API_USER = 'visioniq_api' }
$common.GetEnumerator() | ForEach-Object { $apiEnv[$_.Key] = $_.Value }
$apiEnv['BRIEF_PUBLISH_MODE'] = $BriefPublishMode
$apiEnv['FIX_PR_MODE'] = 'off'
if ($AdoOrgUrl) { $apiEnv['ADO_ORG_URL'] = $AdoOrgUrl.TrimEnd('/') }
if ($pickup) {
    $apiEnv['PICKUP_POLICY_FILE'] = $policyPath
    if ($PickupAllowedWorkItemIds.Count -gt 0) { $apiEnv['PICKUP_ALLOWED_WORK_ITEM_IDS'] = $PickupAllowedWorkItemIds -join ',' }
    if (-not $sweep) { $apiEnv['PICKUP_SWEEP_INTERVAL_MINUTES'] = '0' }
}
$apiSecrets = [ordered]@{ DB_API_PASSWORD = "visioniq-db-api-password:$ApiSecretVersion" }
if ($pickup) {
    $apiSecrets[$policyPath] = "$($PickupPolicySecret):$PickupPolicyVersion"
    $apiSecrets['PICKUP_WEBHOOK_SECRET'] = "$($PickupWebhookSecretName):$PickupWebhookSecretVersion"
}
if ($AdoReadPatVersion -gt 0) { $apiSecrets['ADO_READ_PAT'] = "$($AdoReadPatSecret):$AdoReadPatVersion" }
if ($AdoCommentPatVersion -gt 0) { $apiSecrets['ADO_COMMENT_PAT'] = "$($AdoCommentPatSecret):$AdoCommentPatVersion" }

# gcloud splits --set-env-vars on commas unless another delimiter is declared (^;^), and work-item lists contain commas.
function Join-Settings([System.Collections.IDictionary]$Values) {
    '^;^' + (($Values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';')
}

$services = @(
    @{ Name = 'visioniq-intelligence'; Image = "$registry/visioniq-intelligence:$Commit"; Port = 8000; Memory = '2Gi'
        Environment = $intelligenceEnv; Secrets = $intelligenceSecrets },
    @{ Name = 'visioniq-api'; Image = "$registry/visioniq-api:$Commit"; Port = 5100; Memory = '1Gi'
        Environment = $apiEnv; Secrets = $apiSecrets }
)

Write-Host "Development project: $Project / $Region"
Write-Host "Runtime identity: $runtime (needs Vertex AI user, Pub/Sub publisher and access to every secret below)"
Write-Host "Network: private Cloud SQL $($DbPrivateIp):5432 via $VPCConnector; ingress internal, authenticated"
Write-Host "Instances: min $MinInstances, max 2 per service, CPU always allocated"
Write-Host ("Ticket pickup: " + $(if (-not $pickup) { 'OFF (no -PickupPolicyVersion)' } elseif ($sweep) { 'ON, sweep every 15 min (webhooks cannot reach internal ingress)' } else { 'ON, but NO SWEEP: give -AdoOrgUrl and -AdoReadPatVersion, or nothing is picked up' }))
Write-Host "Brief delivery: $BriefPublishMode"
foreach ($service in $services) {
    Write-Host ""
    Write-Host "Service $($service.Name): $($service.Image) (port $($service.Port), memory $($service.Memory))"
    $service.Environment.GetEnumerator() | ForEach-Object { Write-Host "  env    $($_.Key)=$($_.Value)" }
    # Secret references only (name:version); values never leave Secret Manager.
    $service.Secrets.GetEnumerator() | ForEach-Object { Write-Host "  secret $($_.Key) <- $($_.Value)" }
}
Write-Host ""
if ($MinInstances -gt 0) {
    Write-Host "COST: $MinInstances warm instance(s) per service with CPU always allocated run around the clock. Use -MinInstances 0 outside demo windows (no briefs are generated then)."
}
if (-not $Deploy) {
    Write-Host 'PLAN ONLY. Use -Deploy after confirming IAM, secrets, reviewed image tags and network security.'
    return
}

if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
    throw 'Google Cloud CLI (gcloud) is required for manual Cloud Run deployment.'
}

# Read-only preflight: refuse unexpected project/Cloud SQL identity or VPC state.
$instance = & gcloud sql instances describe visioniqdev --project=$Project --format=json
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the pre-existing development Cloud SQL instance.' }
$instanceInfo = $instance | ConvertFrom-Json
if ($instanceInfo.connectionName -cne "$($Project):$($Region):visioniqdev") {
    throw 'Cloud SQL instance identity does not match the approved FND-03 resource.'
}
$privateAddresses = @($instanceInfo.ipAddresses | Where-Object { $_.type -eq 'PRIVATE' } | ForEach-Object { $_.ipAddress })
if ($privateAddresses -notcontains $DbPrivateIp) {
    throw 'Cloud SQL private IP has changed; review connectivity before proceeding.'
}

$connector = & gcloud compute networks vpc-access connectors describe $VPCConnector --project=$Project --region=$Region --format='value(state)'
if ($LASTEXITCODE -ne 0 -or $connector.Trim() -ne 'READY') {
    throw 'Expected regional VPC connector is not READY or cannot be inspected.'
}

# Every referenced secret version must exist before a revision is created that cannot start without it.
foreach ($service in $services) {
    foreach ($reference in $service.Secrets.Values) {
        $name, $version = $reference -split ':'
        & gcloud secrets versions describe $version --secret=$name --project=$Project --format='value(state)' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Secret $name version $version is missing or not readable." }
    }
}

foreach ($service in $services) {
    Write-Host "Deploying private Cloud Run service $($service.Name)"
    $arguments = @(
        'run', 'deploy', $service.Name, "--project=$Project", "--region=$Region",
        "--image=$($service.Image)", "--service-account=$runtime",
        "--port=$($service.Port)", '--execution-environment=gen2',
        "--vpc-connector=$VPCConnector", '--vpc-egress=private-ranges-only',
        '--ingress=internal', '--no-allow-unauthenticated',
        # Background workers need CPU between requests, and at least one running instance.
        '--no-cpu-throttling', '--cpu=1', "--memory=$($service.Memory)",
        "--min-instances=$MinInstances", '--max-instances=2',
        "--labels=$labels",
        "--set-env-vars=$(Join-Settings $service.Environment)",
        "--set-secrets=$(($service.Secrets.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ',')",
        '--quiet'
    )
    & gcloud @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Cloud Run deployment of $($service.Name) failed. Check the existing revisions before retrying."
    }

    $json = & gcloud run services describe $service.Name --project=$Project --region=$Region --format=json
    if ($LASTEXITCODE -ne 0) { throw "Could not verify $($service.Name) readiness." }
    $state = $json | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($state.status.latestReadyRevisionName) -or
        $state.status.latestReadyRevisionName -cne $state.status.latestCreatedRevisionName) {
        throw "$($service.Name) has no confirmed latest ready revision."
    }
    Write-Host "$($service.Name) latest ready revision: $($state.status.latestReadyRevisionName) ($($state.status.url))"
}

Write-Host 'Both private service revisions are READY. This does NOT establish health, Cloud SQL reachability or ACL acceptance.'
Write-Host 'Then: scripts\pubsub\provision-pubsub.ps1 (subscriptions) and 03-verify-deployment.ps1.'
