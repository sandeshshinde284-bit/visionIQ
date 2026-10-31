# FND-04: explicit manual image build/push and private Cloud Run deployment.
# Run from any directory. Does not create GCP infrastructure or execute migrations.
# Requires authenticated gcloud, Docker daemon, and authorized Cloud IAM.
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][ValidateSet('BuildPush','Deploy','Inspect')][string]$Action,
  [string]$Project = 'gbr-mts-wgh-hkt-dev',
  [string]$Region = 'europe-west3',
  [string]$Registry = 'team-visioniq-docker-dev',
  [string]$RuntimeServiceAccount = 'visioniq-runtime@gbr-mts-wgh-hkt-dev.iam.gserviceaccount.com',
  [string]$VpcConnector = 'vpc-con',
  [string]$DbHost = '10.102.16.8',
  [string]$DbName = 'visioniq_dev',
  [Parameter(Mandatory=$true)][string]$ImageTag,
  [int]$ApiSecretVersion = 0,
  [int]$IntelligenceSecretVersion = 0,
  [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($ImageTag -notmatch '^[0-9a-f]{40}$') { throw 'ImageTag must be the reviewed 40-character source commit SHA.' }
if ($Project -ne 'gbr-mts-wgh-hkt-dev' -or $Region -ne 'europe-west3' -or $Registry -ne 'team-visioniq-docker-dev') {
  throw 'FND-04 is restricted to the approved project, region and registry.'
}
$prefix = "$Region-docker.pkg.dev/$Project/$Registry"
$images = @{
  api = "$prefix/visioniq-api:$ImageTag"
  intelligence = "$prefix/visioniq-intelligence:$ImageTag"
}
function Invoke-External([string]$Exe,[string[]]$Arguments) {
  & $Exe @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$Exe failed (exit $LASTEXITCODE)." }
}
function Print-Args([string]$Exe,[string[]]$Arguments) {
  Write-Host "$Exe $($Arguments -join ' ')"
}
if ($Action -eq 'Inspect') {
  Invoke-External 'gcloud' @('run','services','list',"--project=$Project","--region=$Region")
  Invoke-External 'gcloud' @('run','jobs','describe','visioniq-db-migrate',"--project=$Project","--region=$Region","--format=yaml(metadata.name,status)")
  exit
}
if ($Action -eq 'BuildPush') {
  Push-Location $repoRoot
  try {
    $actualSha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSha -ne $ImageTag) { throw 'Checkout must match ImageTag.' }
    if ((& git status --porcelain).Length -gt 0) { throw 'Working tree must be clean.' }
    $commands = @(
      @{ exe='gcloud'; args=@('auth','configure-docker',"$Region-docker.pkg.dev",'--quiet') },
      @{ exe='docker'; args=@('build','--platform','linux/amd64','-f','src/VisionIQ.Api/Dockerfile','-t',$images.api,'.') },
      @{ exe='docker'; args=@('build','--platform','linux/amd64','-f','src/VisionIQ.Intelligence/Dockerfile','-t',$images.intelligence,'.') },
      @{ exe='docker'; args=@('push',$images.api) },
      @{ exe='docker'; args=@('push',$images.intelligence) }
    )
    foreach($cmd in $commands) {
      Print-Args $cmd.exe $cmd.args
      if($Apply) { Invoke-External $cmd.exe $cmd.args }
    }
  } finally { Pop-Location }
}
if ($Action -eq 'Deploy') {
  if ($ApiSecretVersion -lt 1 -or $IntelligenceSecretVersion -lt 1) {
    throw 'Pass approved numeric Secret Manager versions for both services (never use latest).'
  }
  $common = @(
    "--project=$Project","--region=$Region","--platform=managed",
    "--service-account=$RuntimeServiceAccount","--vpc-connector=$VpcConnector",
    '--vpc-egress=private-ranges-only','--ingress=internal','--no-allow-unauthenticated',
    "--set-env-vars=DB_TARGET=cloud,DB_HOST=$DbHost,DB_PORT=5432,DB_NAME=$DbName,DB_SSL_MODE=require"
  )
  $apiArgs = @('run','deploy','visioniq-api',"--image=$($images.api)",'--port=5100') + $common + @(
    '--update-env-vars=DB_API_USER=visioniq_api',
    "--set-secrets=DB_API_PASSWORD=visioniq-db-api-password:$ApiSecretVersion"
  )
  $intArgs = @('run','deploy','visioniq-intelligence',"--image=$($images.intelligence)",'--port=8000') + $common + @(
    '--update-env-vars=DB_INTELLIGENCE_USER=visioniq_intelligence',
    "--set-secrets=DB_INTELLIGENCE_PASSWORD=visioniq-db-intelligence-password:$IntelligenceSecretVersion"
  )
  foreach($args in @($intArgs,$apiArgs)) {
    Print-Args 'gcloud' $args
    if($Apply) { Invoke-External 'gcloud' $args }
  }
}
if (-not $Apply) { Write-Host 'DRY RUN: add -Apply to execute. No cloud changes were made.' }
else { Write-Host 'Commands completed. Verify ready revisions, approved caller /health, and actual DB permission tests before closing FND-04.' }
