# No framework required; run with Windows PowerShell 5.1+.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("visioniq-env-test-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work -Force
try {
    $template = Join-Path $work '.env.example'
    $envFile = Join-Path $work '.env'
    [System.IO.File]::WriteAllLines($template,@('# test','A=default','B='))
    [System.IO.File]::WriteAllLines($envFile,@('A=private-value','OLD=retain'))
    & (Join-Path $root 'scripts/sync-env.ps1') -TemplatePath $template -EnvPath $envFile -DryRun
    if ([System.IO.File]::ReadAllText($envFile) -notmatch 'OLD=retain') { throw 'Dry run modified file' }
    & (Join-Path $root 'scripts/sync-env.ps1') -TemplatePath $template -EnvPath $envFile
    $first = [System.IO.File]::ReadAllText($envFile)
    if ($first -notmatch 'A=private-value' -or $first -notmatch 'B=' -or $first -notmatch 'OLD=retain') { throw 'Value preservation failed' }
    if (@(Get-ChildItem -LiteralPath $work -Filter '.env.backup-*').Count -ne 1) { throw 'Backup missing' }
    & (Join-Path $root 'scripts/sync-env.ps1') -TemplatePath $template -EnvPath $envFile
    if ([System.IO.File]::ReadAllText($envFile) -cne $first) { throw 'Not idempotent' }
    if (@(Get-ChildItem -LiteralPath $work -Filter '.env.backup-*').Count -ne 1) { throw 'Unnecessary backup' }
    [System.IO.File]::WriteAllLines($template,@('A=x','A=y'))
    $failed = $false
    try { & (Join-Path $root 'scripts/sync-env.ps1') -TemplatePath $template -EnvPath $envFile -ErrorAction Stop } catch { $failed = $true }
    if (-not $failed) { throw 'Duplicate template not rejected' }
    [System.IO.File]::WriteAllLines($template,@('A=x','INVALID LINE'))
    $failed = $false
    try { & (Join-Path $root 'scripts/sync-env.ps1') -TemplatePath $template -EnvPath $envFile -ErrorAction Stop } catch { $failed = $true }
    if (-not $failed) { throw 'Malformed template not rejected' }
    if ([System.IO.File]::ReadAllText($envFile) -cne $first) { throw 'Invalid input modified private config' }
    Write-Host 'PASS: dry-run, preservation, missing/obsolete keys, backup, idempotency, invalid/duplicate rejection.'
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}