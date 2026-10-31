<#
.SYNOPSIS
 Build reviewed VisionIQ images locally, optionally push to the existing Artifact Registry.
.DESCRIPTION
 Does not call Cloud Build, create GCP resources, or deploy Cloud Run.
 Plan-only by default; -BuildOnly builds locally; -Push requires -ReviewedCommit
 and a clean tracked worktree. Never writes secret values to source control.
.EXAMPLE
 .\scripts\manual-deploy\01-build-push.ps1
 .\scripts\manual-deploy\01-build-push.ps1 -BuildOnly
 .\scripts\manual-deploy\01-build-push.ps1 -Push -ReviewedCommit (git rev-parse HEAD)
#>
[CmdletBinding()]
param(
    [string]$Project = 'gbr-mts-wgh-hkt-dev',
    [string]$Region = 'europe-west3',
    [string]$Repository = 'team-visioniq-docker-dev',
    [switch]$BuildOnly,
    [switch]$Push,
    [string]$ReviewedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($BuildOnly -and $Push) { throw 'Select either -BuildOnly or -Push.' }

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name is not installed or is not on PATH."
    }
}

Require-Command git
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$head = (& git -C $root rev-parse --verify HEAD 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'Run this script inside a valid Git checkout.' }
$head = $head.Trim()
if ($head -notmatch '^[a-f0-9]{40}$') { throw 'Source commit must be a complete Git SHA-1.' }
$registry = "$($Region)-docker.pkg.dev/$Project/$Repository"
$images = @(
    @{ Name = 'visioniq-api'; Dockerfile = 'src/VisionIQ.Api/Dockerfile' },
    @{ Name = 'visioniq-intelligence'; Dockerfile = 'src/VisionIQ.Intelligence/Dockerfile' },
    @{ Name = 'visioniq-db-migrate'; Dockerfile = 'db/Dockerfile' }
)

Write-Host "Reviewed source SHA: $head"
Write-Host "Target: $Project / $Region / $Repository"
foreach ($image in $images) {
    Write-Host ("Image: " + $registry + '/' + $image.Name + ':' + $head)
}

if (-not $BuildOnly -and -not $Push) {
    Write-Host 'PLAN ONLY. Use -BuildOnly or -Push -ReviewedCommit <full-sha> to execute.'
    return
}

Require-Command docker
& docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The Docker engine is not available.' }

if ($Push) {
    Require-Command gcloud
    if ($ReviewedCommit -notmatch '^[a-f0-9]{40}$' -or $ReviewedCommit -cne $head) {
        throw 'For pushing, -ReviewedCommit must exactly match the reviewed HEAD SHA.'
    }
    $dirty = @(& git -C $root status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) {
        throw 'Refusing to push images from a checkout with tracked local changes.'
    }
    $format = (& gcloud artifacts repositories describe $Repository --project=$Project --location=$Region '--format=value(format)')
    if ($LASTEXITCODE -ne 0 -or $format.Trim() -ne 'DOCKER') {
        throw 'Approved existing Artifact Registry DOCKER repository could not be verified.'
    }
    & gcloud auth configure-docker "$($Region)-docker.pkg.dev" --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Docker credential configuration failed.' }
}

foreach ($image in $images) {
    $tag = "$registry/$($image.Name):$head"
    Write-Host "Building $($image.Name) for linux/amd64"
    & docker build --platform linux/amd64 --pull --file (Join-Path $root $image.Dockerfile) --tag $tag $root
    if ($LASTEXITCODE -ne 0) { throw "Docker build failed for $($image.Name)." }
    if ($Push) {
        & docker push $tag
        if ($LASTEXITCODE -ne 0) { throw "Docker push failed for $($image.Name)." }
    }
}

if ($Push) {
    Write-Host 'All three image tags pushed. Capture their immutable digests from Artifact Registry.'
    foreach ($image in $images) {
        & gcloud artifacts docker images list "$registry/$($image.Name)" --project=$Project --include-tags --format='table(package,version,tags)'
        if ($LASTEXITCODE -ne 0) { throw 'Could not verify pushed image metadata.' }
    }
} else {
    Write-Host 'Local container images built; nothing was uploaded or deployed.'
}
