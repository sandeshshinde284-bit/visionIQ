<#
.SYNOPSIS
 Validate committed .env.example and optionally check a private local .env.
.DESCRIPTION
 CI mode never reads .env. Static source scanning is advisory and covers direct literal references.
#>
[CmdletBinding()]
param([switch]$CheckLocalEnv,[string]$TemplatePath,[string]$EnvPath,[switch]$SkipSourceScan)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env-contract-common.ps1')
$root = Split-Path -Parent $PSScriptRoot
if (-not $TemplatePath) { $TemplatePath = Join-Path $root '.env.example' }
if (-not $EnvPath) { $EnvPath = Join-Path $root '.env' }
try {
    $template = Read-EnvContract $TemplatePath
    Assert-EnvValid $template '.env.example'
    Write-Host "Template valid: $($template.Keys.Count) unique keys."
    if ($CheckLocalEnv) {
        $local = Read-EnvContract $EnvPath
        Assert-EnvValid $local '.env'
        $missing = @($template.Keys | Where-Object { -not $local.Values.ContainsKey($_) })
        $extra = @($local.Keys | Where-Object { -not $template.Values.ContainsKey($_) })
        if ($missing.Count -or $extra.Count) {
            throw "Local key mismatch. Missing: $($missing -join ', '); extra: $($extra -join ', ')"
        }
        Write-Host 'Private .env has the same keys (values never displayed).'
    }
    if (-not $SkipSourceScan) {
        $unknown = New-Object 'System.Collections.Generic.HashSet[string]'
        $patterns = @(
          'Environment\.GetEnvironmentVariable\(\s*"([A-Z][A-Z0-9_]+)"',
          'os\.getenv\(\s*["'']([A-Z][A-Z0-9_]+)["'']',
          'os\.environ\.(?:get|__getitem__)\(\s*["'']([A-Z][A-Z0-9_]+)["'']',
          'configuration\[\s*"([A-Z][A-Z0-9_]+)"\s*\]',
          'Get\(\s*"([A-Z][A-Z0-9_]+)"\s*\)'
        )
        foreach ($dir in @('src','db','scripts')) {
            $source = Join-Path $root $dir
            if (-not (Test-Path -LiteralPath $source)) { continue }
            Get-ChildItem -LiteralPath $source -File -Recurse |
                Where-Object { $_.Extension -in @('.cs','.py','.ps1') -and $_.FullName -notmatch '[\\/](?:bin|obj|__pycache__)[\\/]' } |
                ForEach-Object {
                    $code = [System.IO.File]::ReadAllText($_.FullName)
                    foreach ($pattern in $patterns) {
                        foreach ($match in [regex]::Matches($code,$pattern)) {
                            $key = $match.Groups[1].Value
                            if (-not $template.Values.ContainsKey($key)) { $null = $unknown.Add($key) }
                        }
                    }
                }
        }
        if ($unknown.Count) { throw ('Source-referenced keys absent from template: ' + (($unknown | Sort-Object) -join ', ')) }
        Write-Host 'Direct literal source references match the template.'
    }
} catch {
    Write-Error $_.Exception.Message
    exit 1
}