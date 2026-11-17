<#
.SYNOPSIS
 Run the Cloud SQL migrations and role grants as a one-off Cloud Run job (db/cloud_manage.py).
.DESCRIPTION
 Plan-only without -Run. Uses the visioniq-db-migrate image (db/Dockerfile), which contains db/; the Intelligence
 image deliberately does not. The job runs `python -m db.cloud_manage all`: Alembic migrations, runtime role
 grants, then the pgvector / full-text / role-isolation checks, against the private Cloud SQL address only.

 Needs three Secret Manager secrets the job identity can read: the Cloud SQL administrator password, and the
 same API and Intelligence passwords the services use (the runner sets those roles' passwords). It does not
 create secrets, IAM grants or Cloud SQL resources.
.EXAMPLE
 .\scripts\manual-deploy\04-migrate-cloud-sql.ps1 -Commit (git rev-parse HEAD) -AdminUser <cloud-sql-admin> `
   -AdminSecretVersion 1 -ApiSecretVersion 1 -IntelligenceSecretVersion 1
 .\scripts\manual-deploy\04-migrate-cloud-sql.ps1 ... -Run
#>
[CmdletBinding()]
param(
    [string]$Project = 'gbr-mts-wgh-hkt-dev',
    [string]$Region = 'europe-west3',
    [string]$Repository = 'team-visioniq-docker-dev',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string]$Commit,
    # The existing Cloud SQL administrative login (may be mixed-case).
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$AdminUser,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$AdminSecretVersion,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$ApiSecretVersion,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$IntelligenceSecretVersion,
    [string]$AdminSecret = 'visioniq-db-admin-password',
    [string]$Instance = 'visioniqdev',
    [string]$VPCConnector = 'vpc-con',
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')]
    [string]$DbPrivateIp = '10.102.16.8',
    [string]$JobName = 'visioniq-db-migrate',
    [switch]$Run
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Project -cne 'gbr-mts-wgh-hkt-dev' -or $Region -cne 'europe-west3') {
    throw 'Cloud SQL migration is restricted to the approved development project/region.'
}

$runtime = "visioniq-runtime@$Project.iam.gserviceaccount.com"
$image = "$($Region)-docker.pkg.dev/$Project/$Repository/visioniq-db-migrate:$Commit"
$connection = "$($Project):$($Region):$Instance"
$environment = @(
    'DB_TARGET=cloud', "DB_CLOUD_INSTANCE=$connection", "GCP_PROJECT_ID=$Project",
    "DB_HOST=$DbPrivateIp", 'DB_PORT=5432', 'DB_CLOUD_NAME=visioniq_dev', "DB_CLOUD_USER=$AdminUser",
    'DB_API_USER=visioniq_api', 'DB_INTELLIGENCE_USER=visioniq_intelligence',
    'VISIONIQ_CLOUD_RUN_MIGRATIONS=approved'
) -join ','
$secrets = @(
    "DB_CLOUD_PASSWORD=$($AdminSecret):$AdminSecretVersion",
    "DB_CLOUD_API_PASSWORD=visioniq-db-api-password:$ApiSecretVersion",
    "DB_CLOUD_INTELLIGENCE_PASSWORD=visioniq-db-intelligence-password:$IntelligenceSecretVersion"
) -join ','

Write-Host "Job $JobName in $Project / $Region, image $image"
Write-Host "Runs: python -m db.cloud_manage all --instance $connection"
Write-Host "Database: $($DbPrivateIp):5432 via $VPCConnector, admin login $AdminUser"
Write-Host "Secrets: $secrets"
if (-not $Run) {
    Write-Host 'PLAN ONLY. Use -Run to create or update the job and execute it once.'
    return
}

if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
    throw 'Google Cloud CLI (gcloud) is required.'
}

$arguments = @(
    'run', 'jobs', 'deploy', $JobName, "--project=$Project", "--region=$Region",
    "--image=$image", "--service-account=$runtime",
    "--vpc-connector=$VPCConnector", '--vpc-egress=private-ranges-only',
    '--command=python', "--args=-m,db.cloud_manage,all,--instance,$connection",
    '--max-retries=0', '--task-timeout=900s',
    '--labels=team=ps04-t11,wl-team=ps04-t11,pocid=ps04-t11,event=hackathon-2026,env=sandbox,app=visioniq',
    "--set-env-vars=$environment", "--set-secrets=$secrets", '--quiet'
)
& gcloud @arguments
if ($LASTEXITCODE -ne 0) { throw "Could not create or update the job $JobName." }

& gcloud run jobs execute $JobName --project=$Project --region=$Region --wait
if ($LASTEXITCODE -ne 0) {
    throw "Migration job failed. Read its logs: gcloud run jobs executions list --job=$JobName --region=$Region"
}
Write-Host 'Migrations, role grants and verification completed. Expected log lines: "Database roles and schema permissions configured." and "pgvector, full-text search and role isolation verified."'
