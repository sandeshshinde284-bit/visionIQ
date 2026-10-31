<#
.SYNOPSIS
 Read-only FND-04 deployment verification for the two Cloud Run services.
.DESCRIPTION
 Prints revision, image and ingress metadata. Does not reveal secret contents,
 grant IAM, send unauthenticated requests, or claim DB connectivity on /health.
.EXAMPLE
 .\scripts\manual-deploy\03-verify-deployment.ps1
#>
[CmdletBinding()]
param(
    [string]$Project = 'gbr-mts-wgh-hkt-dev',
    [string]$Region = 'europe-west3',
    [string]$Repository = 'team-visioniq-docker-dev',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string]$Commit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Project -cne 'gbr-mts-wgh-hkt-dev' -or $Region -cne 'europe-west3') {
    throw 'FND-04 verification is restricted to the development GCP environment.'
}
if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
    throw 'Google Cloud CLI is required.'
}
$registry = "$($Region)-docker.pkg.dev/$Project/$Repository"
$expected = @(
    @{ Name = 'visioniq-api'; Port = 5100; DatabaseIdentity = 'visioniq_api' },
    @{ Name = 'visioniq-intelligence'; Port = 8000; DatabaseIdentity = 'visioniq_intelligence' }
)

foreach ($service in $expected) {
    $json = & gcloud run services describe $service.Name --project=$Project --region=$Region --format=json
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect Cloud Run service $($service.Name)." }
    $state = $json | ConvertFrom-Json
    $ready = [string]$state.status.latestReadyRevisionName
    $created = [string]$state.status.latestCreatedRevisionName
    $image = [string]$state.spec.template.spec.containers[0].image
    $runtime = [string]$state.spec.template.spec.serviceAccountName
    $ingress = [string]$state.metadata.annotations.'run.googleapis.com/ingress'

    if (-not $ready -or $ready -cne $created) {
        throw "$($service.Name) latest created revision is not confirmed READY."
    }
    if ($image -cne "$registry/$($service.Name):$Commit") {
        throw "$($service.Name) running image does not match the requested reviewed commit tag."
    }
    if ($runtime -cne "visioniq-runtime@$Project.iam.gserviceaccount.com") {
        throw "$($service.Name) runtime identity differs from the approved FND-03 identity."
    }
    if ($ingress -cne 'internal') {
        throw "$($service.Name) ingress is not explicitly internal."
    }

    $policyJson = & gcloud run services get-iam-policy $service.Name --project=$Project --region=$Region --format=json
    if ($LASTEXITCODE -ne 0) { throw "Cannot confirm IAM policy for $($service.Name)." }
    $policy = $policyJson | ConvertFrom-Json
    $publicInvokers = @($policy.bindings | Where-Object {
        $_.role -eq 'roles/run.invoker' -and
        (@($_.members) -contains 'allUsers' -or @($_.members) -contains 'allAuthenticatedUsers')
    })
    if ($publicInvokers.Count -gt 0) {
        throw "$($service.Name) grants invoker to a broad/public principal."
    }

    Write-Host "$($service.Name) Ready: $ready"
    Write-Host "  Image: $image"
    Write-Host "  Port expected: $($service.Port); DB role expected: $($service.DatabaseIdentity)"
    Write-Host "  Ingress: internal; invoker: no public IAM binding found"
}

Write-Host 'Cloud Run metadata checks passed.'
Write-Host 'Still REQUIRED: authorized in-network HTTP /health tests and real Cloud SQL SELECT for both runtime roles.'
Write-Host 'Still REQUIRED: verify runtime/test identity cannot read unauthorized cross-schema tables or other service secrets.'
Write-Host 'Capture image digests, evidence screenshots and rollback details in VisionIQ_Cloud_Structure.'
