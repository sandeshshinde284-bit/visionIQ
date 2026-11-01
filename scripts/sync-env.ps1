<#
.SYNOPSIS
 Synchronize private .env keys against committed .env.example without replacing values.
.EXAMPLE
 powershell -ExecutionPolicy Bypass -File .\scripts\sync-env.ps1 -DryRun
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param([switch]$DryRun,[string]$TemplatePath,[string]$EnvPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env-contract-common.ps1')
$root = Split-Path -Parent $PSScriptRoot
if (-not $TemplatePath) { $TemplatePath = Join-Path $root '.env.example' }
if (-not $EnvPath) { $EnvPath = Join-Path $root '.env' }
$template = Read-EnvContract $TemplatePath
Assert-EnvValid $template '.env.example'
$exists = Test-Path -LiteralPath $EnvPath -PathType Leaf
if ($exists) {
    $current = Read-EnvContract $EnvPath
    Assert-EnvValid $current '.env'
} else {
    $current = [pscustomobject]@{ Keys=@(); Values=@{}; Lines=@() }
}
$missing = @($template.Keys | Where-Object { -not $current.Values.ContainsKey($_) })
$obsolete = @($current.Keys | Where-Object { -not $template.Values.ContainsKey($_) })
Write-Host ("Missing keys: {0}; extra local keys: {1}." -f $missing.Count,$obsolete.Count)
if ($missing.Count) { Write-Host ('Adding: ' + ($missing -join ', ')) }
if ($obsolete.Count) { Write-Warning ('Preserving extra local keys for review: ' + ($obsolete -join ', ')) }
$output = New-Object System.Collections.Generic.List[string]
foreach ($line in $template.Lines) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
        $key = $Matches[1]
        $value = if ($current.Values.ContainsKey($key)) { $current.Values[$key] } else { $template.Values[$key] }
        $output.Add("$key=$value")
    } else { $output.Add($line) }
}
while ($output.Count -gt 0 -and $output[$output.Count-1] -eq '') { $output.RemoveAt($output.Count-1) }
if ($obsolete.Count) {
    $output.Add('')
    $output.Add('# ========== Extra local keys (review; preserved) ==========')
    foreach ($key in $obsolete) { $output.Add("$key=$($current.Values[$key])") }
}
$rendered = ($output -join [Environment]::NewLine) + [Environment]::NewLine
if ($exists -and [System.IO.File]::ReadAllText($EnvPath) -ceq $rendered) {
    Write-Host 'Already synchronized.'
    return
}
if ($DryRun -or $WhatIfPreference) { Write-Host 'Dry run: no files changed.'; return }
if (-not $PSCmdlet.ShouldProcess($EnvPath,'Back up and synchronize local environment')) { return }
$directory = Split-Path -Parent $EnvPath
if (-not (Test-Path -LiteralPath $directory)) { $null = New-Item -ItemType Directory -Path $directory -Force }
if ($exists) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $backup = "$EnvPath.backup-$stamp"
    Copy-Item -LiteralPath $EnvPath -Destination $backup -ErrorAction Stop
    Write-Host 'Private backup created alongside .env (values not printed).'
}
$temp = "$EnvPath.tmp-$([guid]::NewGuid().ToString('N'))"
try {
    [System.IO.File]::WriteAllText($temp,$rendered,(New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $EnvPath -Force
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
}
Write-Host 'Synchronization complete; values retained.'