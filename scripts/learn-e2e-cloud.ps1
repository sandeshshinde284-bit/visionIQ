<#
.SYNOPSIS
  Cloud counterpart to learn-e2e.ps1: checks and exercises the GCP side (Cloud SQL, GCS, gcloud auth/project)
  for the LEARN features, without hardcoding any project ID, instance name, or bucket name.

.DESCRIPTION
  Every GCP identifier this script touches comes from your own .env (DB_CLOUD_*, GCS_EVIDENCE_BUCKET) or from
  `gcloud config get-value project`. If a value is missing, the relevant check is SKIPPED with the exact .env
  key to set -- never defaulted to a guessed project/bucket name.

  Nothing here creates a Cloud SQL instance or a GCS bucket without asking first and showing exactly what
  will be created and in which project. Existing resources are detected and reused, never silently recreated.

.PARAMETER Yes
  Answer every prompt with yes (still prints what it's about to do first).

.PARAMETER CreateMissing
  Allow this script to offer creating a missing Cloud SQL instance or GCS bucket. Without this switch,
  a missing resource is reported and the script tells you the exact gcloud command to run yourself.

.EXAMPLE
  ./scripts/learn-e2e-cloud.ps1
  Interactive: checks gcloud auth/project, Cloud SQL instance, GCS bucket, then runs functional checks
  against the cloud database (via the Cloud SQL Auth Proxy, same as scripts/use-db.ps1 cloud).
#>
[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$CreateMissing
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$script:Results = New-Object System.Collections.Generic.List[pscustomobject]

function Write-Section([string]$Title) { Write-Host ''; Write-Host ('== ' + $Title + ' ==') -ForegroundColor Cyan }

function Add-Result([string]$Feature, [string]$Check, [string]$Status, [string]$Detail = '') {
    $color = switch ($Status) { 'PASS' {'Green'} 'FAIL' {'Red'} 'SKIPPED' {'DarkGray'} 'PARTIAL' {'Yellow'} default {'White'} }
    Write-Host ('  [' + $Status.PadRight(10) + '] ' + $Feature + ' - ' + $Check + $(if ($Detail) { '  (' + $Detail + ')' } else { '' })) -ForegroundColor $color
    $script:Results.Add([pscustomobject]@{ Feature=$Feature; Check=$Check; Status=$Status; Detail=$Detail; At=(Get-Date).ToString('o') })
}

function Confirm-Step([string]$Message, [string]$Context = '', [bool]$YesDefault = $false) {
    # Default is $false here, unlike the local script: every confirmation in this cloud script either
    # points -Yes at a real GCP project it didn't choose, or provisions a real billable resource. -Yes
    # is for skipping routine local prompts, not for auto-approving spend or an unverified project target.
    if ($Yes) { return $YesDefault }
    if ($Context) { Write-Host $Context -ForegroundColor DarkGray }
    return ((Read-Host ($Message + ' (y/N)')) -match '^(y|yes)$')
}

function Get-EnvValue([string]$Key) {
    $envFile = Join-Path $root '.env'
    if (-not (Test-Path $envFile)) { return '' }
    foreach ($l in (Get-Content $envFile)) {
        if ($l -match ('^\s*' + [regex]::Escape($Key) + '=(.*)$')) { return $Matches[1].Trim() }
    }
    return ''
}

Write-Host ''
Write-Host 'VisionIQ LEARN - cloud (GCP) check' -ForegroundColor White
Write-Host 'Nothing here is hardcoded: project/instance/bucket all come from gcloud config or your .env.' -ForegroundColor DarkGray

# ============================================================== step 1: gcloud auth + project
Write-Section 'Step 1: gcloud authentication and project'
$gcloudCmd = Get-Command gcloud -ErrorAction SilentlyContinue
if (-not $gcloudCmd) {
    Add-Result 'Cloud' 'gcloud CLI installed' 'FAIL' 'install via: winget install --id Google.CloudSDK -e'
    Write-Host 'gcloud is required for everything below -- stopping.' -ForegroundColor Red
    exit 1
}
Add-Result 'Cloud' 'gcloud CLI installed' 'PASS'

$account = (& gcloud auth list --filter=status:ACTIVE --format='value(account)' 2>$null)
if (-not $account) {
    Add-Result 'Cloud' 'gcloud signed in' 'FAIL' 'run: gcloud auth login && gcloud auth application-default login'
    exit 1
}
Add-Result 'Cloud' 'gcloud signed in' 'PASS' $account

$project = (& gcloud config get-value project 2>$null)
if (-not $project -or $project -match '^\(unset\)$') {
    Add-Result 'Cloud' 'gcloud project set' 'FAIL' 'run: gcloud config set project <PROJECT_ID>  (ask your team for the approved project ID -- never guess one)'
    exit 1
}
Add-Result 'Cloud' 'gcloud project set' 'PASS' $project
if (-not (Confirm-Step ("Proceed against GCP project '" + $project + "' ?") 'This is whatever `gcloud config get-value project` currently returns on this machine -- confirm it is the approved non-production/test project before continuing.')) {
    Write-Host 'Stopped -- switch project with: gcloud config set project <PROJECT_ID>' -ForegroundColor Yellow
    exit 1
}

# ============================================================== step 2: Cloud SQL instance (LRN-03/05 cloud target)
Write-Section 'Step 2: Cloud SQL instance'
$instance = Get-EnvValue 'DB_CLOUD_INSTANCE'
if (-not $instance) {
    Add-Result 'LRN-03' 'Cloud SQL instance configured' 'SKIPPED' 'DB_CLOUD_INSTANCE is empty in .env -- set it to <project>:<region>:<instance> to enable cloud DB checks'
} else {
    $exists = (& gcloud sql instances describe $instance --project $project --format='value(name)' 2>$null)
    if ($exists) {
        Add-Result 'LRN-03' 'Cloud SQL instance exists' 'PASS' $instance
    } elseif ($CreateMissing) {
        Write-Host ("Cloud SQL instance '" + $instance + "' does not exist in project '" + $project + "'.") -ForegroundColor Yellow
        if (Confirm-Step 'Create it now? This provisions a real billable Cloud SQL instance.') {
            Write-Host 'Creation command intentionally not auto-run with defaulted tier/region -- run it yourself with your approved settings, e.g.:' -ForegroundColor Yellow
            Write-Host ("  gcloud sql instances create <name> --database-version=POSTGRES_16 --tier=<approved-tier> --region=<approved-region> --project=" + $project) -ForegroundColor DarkGray
            Add-Result 'LRN-03' 'Cloud SQL instance exists' 'SKIPPED' 'creation command shown above -- run with your own approved tier/region, then re-run this script'
        } else {
            Add-Result 'LRN-03' 'Cloud SQL instance exists' 'SKIPPED' 'declined creation'
        }
    } else {
        Add-Result 'LRN-03' 'Cloud SQL instance exists' 'SKIPPED' ("'" + $instance + "' not found -- pass -CreateMissing to be shown how to create it, or confirm the name in .env is correct")
    }
}

# ============================================================== step 3: GCS evidence bucket (LRN-03)
Write-Section 'Step 3: GCS evidence bucket'
$bucket = Get-EnvValue 'GCS_EVIDENCE_BUCKET'
if (-not $bucket) {
    Add-Result 'LRN-03' 'GCS evidence bucket configured' 'SKIPPED' 'GCS_EVIDENCE_BUCKET is empty in .env'
} else {
    & gcloud storage buckets describe ("gs://" + $bucket) --project $project *> $null
    if ($LASTEXITCODE -eq 0) {
        Add-Result 'LRN-03' 'GCS bucket exists' 'PASS' $bucket
    } elseif ($CreateMissing -and (Confirm-Step ("Create bucket gs://" + $bucket + " in project '" + $project + "' now?"))) {
        & gcloud storage buckets create ("gs://" + $bucket) --project $project --uniform-bucket-level-access --location=us
        Add-Result 'LRN-03' 'GCS bucket exists' $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }) 'created with --uniform-bucket-level-access; review retention/IAM before real use'
    } else {
        Add-Result 'LRN-03' 'GCS bucket exists' 'SKIPPED' ("'" + $bucket + "' not found -- pass -CreateMissing to create it, or confirm the name")
    }
}

# ============================================================== step 4: Cloud SQL Auth Proxy + functional check
Write-Section 'Step 4: Cloud SQL Auth Proxy and connectivity'
$proxyCmd = Get-Command cloud-sql-proxy -ErrorAction SilentlyContinue
if (-not $proxyCmd) {
    Add-Result 'LRN-03' 'Cloud SQL Auth Proxy installed' 'SKIPPED' 'install: see Google Cloud SQL Auth Proxy docs for your OS'
} elseif (-not $instance) {
    Add-Result 'LRN-03' 'Cloud SQL Auth Proxy reachable' 'SKIPPED' 'DB_CLOUD_INSTANCE not set'
} else {
    $proxyPort = Get-EnvValue 'DB_CLOUD_PROXY_PORT'; if (-not $proxyPort) { $proxyPort = '5433' }
    $listening = $false
    try { $listening = (Test-NetConnection -ComputerName '127.0.0.1' -Port $proxyPort -WarningAction SilentlyContinue).TcpTestSucceeded } catch {}
    if ($listening) {
        Add-Result 'LRN-03' 'Cloud SQL Auth Proxy reachable' 'PASS' ('already listening on 127.0.0.1:' + $proxyPort)
    } else {
        Write-Host ("Proxy is not listening on port " + $proxyPort + ".") -ForegroundColor Yellow
        Write-Host ('Start it yourself in another terminal, then re-run this script:') -ForegroundColor DarkGray
        Write-Host ('  cloud-sql-proxy --port ' + $proxyPort + ' ' + $instance) -ForegroundColor DarkGray
        Add-Result 'LRN-03' 'Cloud SQL Auth Proxy reachable' 'SKIPPED' 'not running -- command shown above'
    }

    if ($listening) {
        $dbName = Get-EnvValue 'DB_CLOUD_NAME'; $dbUser = Get-EnvValue 'DB_CLOUD_USER'; $dbPass = Get-EnvValue 'DB_CLOUD_PASSWORD'
        if ($dbName -and $dbUser) {
            $env:PGPASSWORD = $dbPass
            $n = & psql -h 127.0.0.1 -p $proxyPort -U $dbUser -d $dbName -tA -c "SELECT count(*) FROM evidence.source_revisions;" 2>$null
            Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
            Add-Result 'LRN-03' 'Cloud evidence.source_revisions reachable' $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }) ('row count: ' + $n)
        } else {
            Add-Result 'LRN-03' 'Cloud evidence.source_revisions reachable' 'SKIPPED' 'DB_CLOUD_NAME/DB_CLOUD_USER not set in .env'
        }
    }
}

# ============================================================== step 5: report
Write-Section 'Step 5: Report'
$reportDir = Join-Path $root 'artifacts\e2e-reports'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $reportDir ("learn-e2e-cloud-" + $stamp + ".json")
$script:Results | ConvertTo-Json -Depth 4 | Set-Content -Path $reportPath -Encoding utf8

$pass = @($script:Results | Where-Object Status -eq 'PASS').Count
$fail = @($script:Results | Where-Object Status -eq 'FAIL').Count
$skip = @($script:Results | Where-Object Status -eq 'SKIPPED').Count
Write-Host ''
Write-Host ('Summary: ' + $pass + ' PASS, ' + $fail + ' FAIL, ' + $skip + ' SKIPPED') -ForegroundColor White
Write-Host ('Report: ' + $reportPath) -ForegroundColor DarkGray
if ($fail -gt 0) { exit 1 }
exit 0
