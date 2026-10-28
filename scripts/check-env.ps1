<#
.SYNOPSIS
  Checks that this Windows machine is ready for VisionIQ development.

.DESCRIPTION
  Default mode only CHECKS. It prints what is missing and the exact fix. Nothing is installed or changed.
  With -Install it offers missing tool installations and copies .env.example to .env when needed.
  It never overwrites an existing .env, creates passwords, or shares local secrets.

  Keep this script inside the repository's scripts folder. It detects the repository from its own location,
  even when the terminal's current working directory is different.

  It never prints secrets and never changes the PowerShell execution policy.
  Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-env.ps1

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-env.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch]$Install
)

$ErrorActionPreference = 'Continue'
$script:Items = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------- helpers
function Invoke-Tool {
    param([string]$Exe, [string[]]$Arguments)
    $cmd = Get-Command $Exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cmd) { return [pscustomobject]@{ Found = $false; Code = -1; Text = '' } }
    $text = ''
    $code = 0
    try {
        $text = (& $cmd.Source @Arguments 2>&1 | Out-String)
        $code = $LASTEXITCODE
    }
    catch {
        $text = $_.Exception.Message
        $code = 1
    }
    return [pscustomobject]@{ Found = $true; Code = $code; Text = $text.Trim() }
}

function Add-Item {
    param(
        [string]$Status,       # OK | MISSING | FIX | WARN | INFO
        [string]$Name,
        [string]$Detail = '',
        [string]$Fix = '',
        [string]$WingetId = ''
    )
    $null = $script:Items.Add([pscustomobject]@{ Status = $Status; Name = $Name; Detail = $Detail; Fix = $Fix; WingetId = $WingetId })
    $tag = '[' + $Status + ']'
    $color = 'Gray'
    switch ($Status) {
        'OK'      { $color = 'Green' }
        'MISSING' { $color = 'Red' }
        'FIX'     { $color = 'Red' }
        'WARN'    { $color = 'Yellow' }
        'INFO'    { $color = 'Cyan' }
    }
    $line = ($tag.PadRight(10)) + $Name
    if ($Detail) { $line = $line + '  -  ' + $Detail }
    Write-Host $line -ForegroundColor $color
    if ($Fix) { Write-Host ('           Fix: ' + $Fix) -ForegroundColor DarkGray }
}

function Get-FirstLine([string]$Text) {
    if (-not $Text) { return '' }
    return ($Text -split "`r?`n")[0].Trim()
}

function Get-EnvFileValue([string]$Path, [string]$Key) {
    if (-not (Test-Path $Path)) { return '' }
    foreach ($l in (Get-Content $Path)) {
        if ($l -match ('^\s*' + [regex]::Escape($Key) + '=(.*)$')) { return $Matches[1].Trim() }
    }
    return ''
}

# ---------------------------------------------------------------- find repository from the script directory, independent of the caller's directory
$repoRoot = $null
$scriptRepo = Split-Path -Parent $PSScriptRoot
$g = Invoke-Tool 'git' @('-C', $scriptRepo, 'rev-parse', '--show-toplevel')
if ($g.Found -and $g.Code -eq 0 -and $g.Text) {
    if (Test-Path (Join-Path $g.Text 'docker-compose.yml')) { $repoRoot = $g.Text }
}

# versions pinned by the repository win over the defaults
$wantPython = '3.12'
$wantDotnet = '8'
if ($repoRoot) {
    foreach ($c in @((Join-Path $repoRoot '.python-version'))) {
        if (Test-Path $c) {
            $first = (Get-Content $c -TotalCount 1)
            if ($first -match '^\s*(\d+)\.(\d+)') { $wantPython = $Matches[1] + '.' + $Matches[2]; break }
        }
    }
    foreach ($c in @((Join-Path $repoRoot 'global.json'))) {
        if (Test-Path $c) {
            try {
                $v = (Get-Content $c -Raw | ConvertFrom-Json).sdk.version
                if ($v -match '^(\d+)\.') { $wantDotnet = $Matches[1]; break }
            }
            catch { }
        }
    }
}

Write-Host ''
Write-Host 'VisionIQ environment check' -ForegroundColor White
if ($repoRoot) { Write-Host ('Repository found: ' + $repoRoot) -ForegroundColor DarkGray }
else { Write-Host 'Not inside the VisionIQ repository: tool checks only.' -ForegroundColor DarkGray }
Write-Host ('Expecting .NET SDK ' + $wantDotnet + '.x and Python ' + $wantPython + '.x') -ForegroundColor DarkGray
Write-Host ''

# ---------------------------------------------------------------- 1. winget
$r = Invoke-Tool 'winget' @('--version')
if ($r.Found -and $r.Code -eq 0) { Add-Item 'OK' 'winget' (Get-FirstLine $r.Text) }
else { Add-Item 'MISSING' 'winget (package manager)' 'not found' 'Install or repair "App Installer" through your company-approved channel. This script cannot install winget itself.' }

# ---------------------------------------------------------------- 2. Git
$r = Invoke-Tool 'git' @('--version')
if ($r.Found -and $r.Code -eq 0) {
    Add-Item 'OK' 'Git' (Get-FirstLine $r.Text)
    $n = Invoke-Tool 'git' @('config', '--global', 'user.name')
    $e = Invoke-Tool 'git' @('config', '--global', 'user.email')
    if ($n.Text -and $e.Text) { Add-Item 'OK' 'Git identity' 'name and email are set' }
    else { Add-Item 'WARN' 'Git identity' 'name or email not set' 'git config --global user.name "Your Name"   and   git config --global user.email "you@company.com"' }
}
else { Add-Item 'MISSING' 'Git' 'not found' 'winget install --id Git.Git -e' 'Git.Git' }

# ---------------------------------------------------------------- 3. .NET SDK
$r = Invoke-Tool 'dotnet' @('--list-sdks')
$dotnetId = 'Microsoft.DotNet.SDK.' + $wantDotnet
if (-not $r.Found) { Add-Item 'MISSING' ('.NET SDK ' + $wantDotnet) 'dotnet not found' ('winget install --id ' + $dotnetId + ' -e') $dotnetId }
elseif ($r.Text -match ('(?m)^' + [regex]::Escape($wantDotnet) + '\.')) {
    $line = ($r.Text -split "`r?`n" | Where-Object { $_ -match ('^' + [regex]::Escape($wantDotnet) + '\.') } | Select-Object -First 1)
    Add-Item 'OK' ('.NET SDK ' + $wantDotnet) (($line -split ' ')[0])
}
else { Add-Item 'MISSING' ('.NET SDK ' + $wantDotnet) 'installed SDKs do not include it' ('winget install --id ' + $dotnetId + ' -e') $dotnetId }

# ---------------------------------------------------------------- 4. Python
$pyId = 'Python.Python.' + $wantPython
$pyOk = $false
$pyText = ''
$r = Invoke-Tool 'py' @(('-' + $wantPython), '--version')
if ($r.Found -and $r.Code -eq 0 -and $r.Text -match ('^Python ' + [regex]::Escape($wantPython) + '(?:\.|\s|$)')) { $pyOk = $true; $pyText = Get-FirstLine $r.Text }
else {
    $r2 = Invoke-Tool 'python' @('--version')
    if ($r2.Found -and $r2.Code -eq 0 -and $r2.Text -match ('^Python ' + [regex]::Escape($wantPython) + '(?:\.|\s|$)')) { $pyOk = $true; $pyText = Get-FirstLine $r2.Text }
}
if ($pyOk) { Add-Item 'OK' ('Python ' + $wantPython) $pyText }
else { Add-Item 'MISSING' ('Python ' + $wantPython) 'not found' ('winget install --id ' + $pyId + ' -e') $pyId }

# ---------------------------------------------------------------- 5. Docker
$dockerRunning = $false
$r = Invoke-Tool 'docker' @('--version')
if (-not ($r.Found -and $r.Code -eq 0)) {
    Add-Item 'MISSING' 'Docker Desktop' 'not found' 'winget install --id Docker.DockerDesktop -e   (needs administrator rights and usually a restart) or use your IT channel' 'Docker.DockerDesktop'
}
else {
    Add-Item 'OK' 'Docker' (Get-FirstLine $r.Text)
    $info = Invoke-Tool 'docker' @('info')
    if ($info.Code -eq 0) {
        $dockerRunning = $true
        Add-Item 'OK' 'Docker engine' 'running'
    }
    else {
        Add-Item 'FIX' 'Docker engine' 'installed but not running' 'Start Docker Desktop, wait until it shows "running", then check again.'
        $w = Invoke-Tool 'wsl' @('--status')
        if ($w.Found -and $w.Code -ne 0) { Add-Item 'WARN' 'WSL 2' 'not set up' 'Docker Desktop needs WSL 2. Install it through IT: wsl --install (administrator rights and a restart).' }
    }
    $c = Invoke-Tool 'docker' @('compose', 'version')
    if ($c.Code -eq 0) { Add-Item 'OK' 'Docker Compose' (Get-FirstLine $c.Text) }
    elseif ($dockerRunning) { Add-Item 'MISSING' 'Docker Compose' 'plugin not found' 'Repair or update Docker Desktop.' }
}

# ---------------------------------------------------------------- 6. Editor (VS Code or Visual Studio)
$editor = ''
$r = Invoke-Tool 'code' @('--version')
if ($r.Found -and $r.Code -eq 0) { $editor = 'VS Code ' + (Get-FirstLine $r.Text) }
else {
    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) {
        $vsw = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vsw) {
            $vs = (& $vsw -latest -property displayName 2>$null | Out-String).Trim()
            if ($vs) { $editor = $vs }
        }
    }
}
if ($editor) { Add-Item 'OK' 'Editor' $editor }
else { Add-Item 'MISSING' 'Editor (VS Code or Visual Studio)' 'neither found' 'winget install --id Microsoft.VisualStudioCode -e   (or install Visual Studio 2022 with the ASP.NET and web development workload)' 'Microsoft.VisualStudioCode' }

# ---------------------------------------------------------------- 7. Google Cloud CLI
$r = Invoke-Tool 'gcloud' @('version')
if (-not ($r.Found -and $r.Code -eq 0)) {
    Add-Item 'WARN' 'Google Cloud CLI' 'not installed (optional for local FND-02)' 'Install when authorized Google Cloud integration begins: winget install --id Google.CloudSDK -e'
}
else {
    Add-Item 'OK' 'Google Cloud CLI' (Get-FirstLine $r.Text)
    $a = Invoke-Tool 'gcloud' @('auth', 'list', '--filter=status:ACTIVE', '--format=value(account)')
    if ($a.Code -eq 0 -and $a.Text) { Add-Item 'OK' 'Google Cloud sign-in' 'an account is signed in' }
    else { Add-Item 'WARN' 'Google Cloud sign-in' 'no signed-in account' 'gcloud auth login   and   gcloud auth application-default login   (your own company account)' }
    $p = Invoke-Tool 'gcloud' @('config', 'get-value', 'project')
    $proj = (Get-FirstLine $p.Text)
    if ($proj -and $proj -notmatch '^\(unset\)$' -and $p.Code -eq 0) { Add-Item 'OK' 'Google Cloud project' $proj }
    else { Add-Item 'WARN' 'Google Cloud project' 'not set' 'Ask the team for the project ID, then: gcloud config set project <PROJECT_ID>' }
}

# ---------------------------------------------------------------- 8. Cloud SQL Auth Proxy (optional)
$r = Invoke-Tool 'cloud-sql-proxy' @('--version')
if ($r.Found -and $r.Code -eq 0) { Add-Item 'OK' 'Cloud SQL Auth Proxy' (Get-FirstLine $r.Text) }
else { Add-Item 'INFO' 'Cloud SQL Auth Proxy' 'not installed (optional: only for the cloud database target)' }

# ---------------------------------------------------------------- 9. project checks (inside the repository only)
if (-not $repoRoot) {
    Add-Item 'INFO' 'Project checks' 'skipped: run this script again from inside the cloned repository'
}
else {
    $envFile = Join-Path $repoRoot '.env'
    if (-not (Test-Path $envFile)) {
        if (Test-Path (Join-Path $repoRoot '.env.txt')) {
            Add-Item 'FIX' '.env file' 'saved as ".env.txt" (Windows added .txt)' 'Inside the project folder run: Rename-Item .env.txt .env'
        }
        else {
            $template = Join-Path $repoRoot '.env.example'
            if (-not (Test-Path $template)) {
                Add-Item 'FIX' '.env template' 'not found' 'Restore the tracked .env.example file before continuing.'
            }
            elseif ($Install) {
                try {
                    Copy-Item -LiteralPath $template -Destination $envFile -ErrorAction Stop
                    Add-Item 'OK' '.env file' 'created from .env.example; edit only your own local settings'
                    Add-Item 'FIX' 'DB_LOCAL_PASSWORD' 'not set' 'Edit your private .env and enter your own DB_LOCAL_PASSWORD. Never share .env.'
                }
                catch {
                    Add-Item 'FIX' '.env file' 'could not copy .env.example' 'Check local file permissions; do not overwrite any existing .env.'
                }
            }
            else {
                Add-Item 'FIX' '.env file' 'missing' 'Run this checker with -Install, or run: Copy-Item .env.example .env'
            }
        }
    }
    else {
        Add-Item 'OK' '.env file' 'exists'
        if (Get-EnvFileValue $envFile 'DB_LOCAL_PASSWORD') { Add-Item 'OK' 'DB_LOCAL_PASSWORD' 'is set' }
        else { Add-Item 'FIX' 'DB_LOCAL_PASSWORD' 'empty in .env' 'Open .env and set any password (it only protects your own machine).' }
        $tgt = Get-EnvFileValue $envFile 'DB_TARGET'
        if ($tgt) {
            $hostName = Get-EnvFileValue $envFile 'DB_HOST'
            $port = Get-EnvFileValue $envFile 'DB_PORT'
            Add-Item 'INFO' 'Database target' ($tgt + ' (' + $hostName + ':' + $port + ')')
        }
    }
    $ign = Invoke-Tool 'git' @('-C', $repoRoot, 'check-ignore', '-q', '.env')
    if ($ign.Code -eq 0) { Add-Item 'OK' '.env is ignored by Git' '' }
    else { Add-Item 'FIX' '.env is NOT ignored by Git' '' 'Do not commit. Check that .gitignore excludes .env on this branch.' }
    $trk = Invoke-Tool 'git' @('-C', $repoRoot, 'ls-files', '.env')
    if ($trk.Text) { Add-Item 'FIX' '.env is tracked by Git' '' 'Tell the team lead now. Adding it to .gitignore is not enough. Do not print its contents.' }

    if ($dockerRunning) {
        $h = Invoke-Tool 'docker' @('inspect', '-f', '{{.State.Health.Status}}', 'visioniq-postgres')
        if ($h.Code -ne 0) { Add-Item 'INFO' 'Local database' 'container not started' 'docker compose up -d postgres' }
        elseif ($h.Text -eq 'healthy') { Add-Item 'OK' 'Local database' 'postgres container is healthy' }
        else { Add-Item 'WARN' 'Local database' ('container status: ' + (Get-FirstLine $h.Text)) 'Wait a minute and run this check again. If it stays unhealthy: docker compose logs postgres' }
    }
}

# ---------------------------------------------------------------- summary
$missing = @($script:Items | Where-Object { $_.Status -eq 'MISSING' })
$fixes = @($script:Items | Where-Object { $_.Status -eq 'FIX' })
$warns = @($script:Items | Where-Object { $_.Status -eq 'WARN' })
$oks = @($script:Items | Where-Object { $_.Status -eq 'OK' })
Write-Host ''
Write-Host ('Summary: ' + $oks.Count + ' OK, ' + $missing.Count + ' missing, ' + $fixes.Count + ' to fix, ' + $warns.Count + ' warnings') -ForegroundColor White

if ($Install) {
    $installable = @($missing | Where-Object { $_.WingetId })
    $wingetOk = [bool]($script:Items | Where-Object { $_.Name -eq 'winget' -and $_.Status -eq 'OK' })
    if ($installable.Count -eq 0) { Write-Host 'Nothing to install.' -ForegroundColor DarkGray }
    elseif (-not $wingetOk) { Write-Host 'winget is not available, so nothing can be installed from here. Use the Fix lines above.' -ForegroundColor Yellow }
    else {
        $did = 0
        foreach ($m in $installable) {
            $ans = Read-Host ('Install ' + $m.Name + ' with winget now? (y/N)')
            if ($ans -match '^(y|yes)$') {
                if ($m.WingetId -eq 'Docker.DockerDesktop') { Write-Host 'Docker Desktop needs administrator rights and usually a restart.' -ForegroundColor Yellow }
                & winget install --id $m.WingetId -e --accept-source-agreements --accept-package-agreements
                $did++
            }
            else { Write-Host ('Skipped ' + $m.Name) -ForegroundColor DarkGray }
        }
        if ($did -gt 0) { Write-Host 'Open a NEW PowerShell window and run this check again so the new tools are found.' -ForegroundColor Cyan }
    }
}
elseif ($missing.Count -gt 0) {
    Write-Host 'Run again with -Install to be offered each missing install (it asks before every one), or use the Fix lines above.' -ForegroundColor DarkGray
}

if (($missing.Count + $fixes.Count) -eq 0) {
    if ($warns.Count -gt 0) { Write-Host 'Ready. The warnings are things to finish before Part B or cloud work.' -ForegroundColor Green }
    else { Write-Host 'Ready. Nothing else to do.' -ForegroundColor Green }
    exit 0
}
Write-Host 'Not ready yet. Work through the [MISSING] and [FIX] lines, then run this check again.' -ForegroundColor Red
exit 1
