<#
.SYNOPSIS
  Switch the database target in .env between local Docker PostgreSQL and the Cloud SQL development database.
.EXAMPLE
  ./scripts/use-db.ps1 local
  ./scripts/use-db.ps1 cloud
  ./scripts/use-db.ps1 cloud -Yes      # skip the confirmation question
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('local', 'cloud')]
    [string]$Target,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$example = Join-Path $root '.env.example'

if (-not (Test-Path $envFile)) {
    Copy-Item $example $envFile
    Write-Host "Created .env from .env.example."
}

$lines = New-Object 'System.Collections.Generic.List[string]'
foreach ($l in [System.IO.File]::ReadAllLines($envFile)) { $lines.Add($l) }

function Get-EnvValue([string]$Key) {
    foreach ($l in $lines) {
        if ($l -match ('^\s*' + [regex]::Escape($Key) + '=(.*)$')) { return $Matches[1].Trim() }
    }
    return ''
}
function Set-EnvValue([string]$Key, [string]$Value) {
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ('^\s*' + [regex]::Escape($Key) + '=')) { $lines[$i] = "$Key=$Value"; return }
    }
    $lines.Add("$Key=$Value")
}

if ($Target -eq 'local') {
    $port = Get-EnvValue 'DB_LOCAL_PORT';  if (-not $port) { $port = '5432' }
    $name = Get-EnvValue 'DB_LOCAL_NAME';  if (-not $name) { $name = 'visioniq' }
    $user = Get-EnvValue 'DB_LOCAL_USER';  if (-not $user) { $user = 'visioniq' }
    $pass = Get-EnvValue 'DB_LOCAL_PASSWORD'
    if (-not $pass) { throw "DB_LOCAL_PASSWORD is empty in .env. Set any password for your own machine, then run this again." }
    $hostName = 'localhost'
    $next = "Start the database with: docker compose up -d postgres"
}
else {
    $instance = Get-EnvValue 'DB_CLOUD_INSTANCE'
    $port = Get-EnvValue 'DB_CLOUD_PROXY_PORT'; if (-not $port) { $port = '5433' }
    $name = Get-EnvValue 'DB_CLOUD_NAME'
    $user = Get-EnvValue 'DB_CLOUD_USER'
    $pass = Get-EnvValue 'DB_CLOUD_PASSWORD'
    $missing = @()
    if (-not $instance) { $missing += 'DB_CLOUD_INSTANCE' }
    if (-not $name)     { $missing += 'DB_CLOUD_NAME' }
    if (-not $user)     { $missing += 'DB_CLOUD_USER' }
    if (-not $pass)     { $missing += 'DB_CLOUD_PASSWORD' }
    if ($missing.Count -gt 0) { throw ("Missing in .env: " + ($missing -join ', ') + ". Set them first.") }
    if (-not $Yes) {
        Write-Warning "This points the app at the CLOUD development database ($instance)."
        Write-Warning "Never run destructive migrations or reset scripts against it."
        $answer = Read-Host "Continue? (y/N)"
        if ($answer -notmatch '^(y|yes)$') { Write-Host "Cancelled. .env unchanged."; return }
    }
    $hostName = '127.0.0.1'
    $next = "In another terminal run: cloud-sql-proxy --port $port $instance"
}

Set-EnvValue 'DB_TARGET'   $Target
Set-EnvValue 'DB_HOST'     $hostName
Set-EnvValue 'DB_PORT'     $port
Set-EnvValue 'DB_NAME'     $name
Set-EnvValue 'DB_USER'     $user
Set-EnvValue 'DB_PASSWORD' $pass
Set-EnvValue 'DB_SSL_MODE' 'disable'

[System.IO.File]::WriteAllLines($envFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Database target: $Target  ($hostName`:$port, database '$name', user '$user')"
Write-Host $next
