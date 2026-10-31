<#
.SYNOPSIS
 Manually deploy reviewed Artifact Registry images to private Cloud Run services.
.DESCRIPTION
 Plan-only without -Deploy. Requires numeric pinned secret versions, an
 explicit reviewed Git commit, approved runtime IAM and corporate GCP access.
 Does not invoke Cloud Build, make services public, migrate Cloud SQL, or
 create/delete secrets or IAM grants.
.EXAMPLE
 .\scripts\manual-deploy\02-deploy-services.ps1 -Commit 0123456789012345678901234567890123456789 -ApiSecretVersion 1 -IntelligenceSecretVersion 1
 .\scripts\manual-deploy\02-deploy-services.ps1 -Commit 0123456789012345678901234567890123456789 -ApiSecretVersion 1 -IntelligenceSecretVersion 1 -Deploy
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
    [string]$DbPrivateIp = '10.102.16.8',
    [switch]$Deploy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This deployment is intentionally restricted to the documented DEVELOPMENT project.
if ($Project -cne 'gbr-mts-wgh-hkt-dev' -or $Region -cne 'europe-west3') {
    throw 'Manual FND-04 is restricted to the approved development project/region.'
}
$runtime = "visioniq-runtime@$Project.iam.gserviceaccount.com"
$registry = "$($Region)-docker.pkg.dev/$Project/$Repository"
$services = @(
    @{
        Name = 'visioniq-intelligence'
        Image = "$registry/visioniq-intelligence:$Commit"
        Port = 8000
        Environment = 'APP_ENV=production,DB_HOST=' + $DbPrivateIp + ',DB_PORT=5432,DB_NAME=visioniq_dev,DB_INTELLIGENCE_USER=visioniq_intelligence,DB_SSL_MODE=require'
        Secret = "DB_INTELLIGENCE_PASSWORD=visioniq-db-intelligence-password:$IntelligenceSecretVersion"
    },
    @{
        Name = 'visioniq-api'
        Image = "$registry/visioniq-api:$Commit"
        Port = 5100
        Environment = 'ASPNETCORE_ENVIRONMENT=Production,DB_HOST=' + $DbPrivateIp + ',DB_PORT=5432,DB_NAME=visioniq_dev,DB_API_USER=visioniq_api,DB_SSL_MODE=require'
        Secret = "DB_API_PASSWORD=visioniq-db-api-password:$ApiSecretVersion"
    }
)

Write-Host "Development project: $Project / $Region"
Write-Host "Runtime identity: $runtime (shared-secret IAM approval still required)"
Write-Host "Network: private Cloud SQL $($DbPrivateIp):5432 via $VPCConnector"
foreach ($service in $services) {
    Write-Host "Proposed service $($service.Name): $($service.Image) (port $($service.Port), ingress internal, authenticated)"
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

foreach ($service in $services) {
    Write-Host "Deploying private Cloud Run service $($service.Name)"
    $arguments = @(
        'run', 'deploy', $service.Name, "--project=$Project", "--region=$Region",
        "--image=$($service.Image)", "--service-account=$runtime",
        "--port=$($service.Port)", '--execution-environment=gen2',
        "--vpc-connector=$VPCConnector", '--vpc-egress=private-ranges-only',
        '--ingress=internal', '--no-allow-unauthenticated',
        '--min-instances=0', '--max-instances=2',
        "--set-env-vars=$($service.Environment)",
        "--set-secrets=$($service.Secret)", '--quiet'
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
    Write-Host "$($service.Name) latest ready revision: $($state.status.latestReadyRevisionName)"
}

Write-Host 'Both private service revisions are READY. This does NOT establish health, Cloud SQL reachability or ACL acceptance.'
Write-Host 'Run 03-verify-deployment.ps1 and complete in-network authenticated + database validation before closing FND-04.'
