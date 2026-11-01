# Shared parser used by sync-env.ps1 and check-env-contract.ps1.
Set-StrictMode -Version Latest
function Read-EnvContract {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Environment file not found: $Path" }
    $lines = [System.IO.File]::ReadAllLines($Path)
    $values = @{}
    $keys = New-Object System.Collections.Generic.List[string]
    $duplicates = New-Object System.Collections.Generic.List[string]
    $invalid = New-Object System.Collections.Generic.List[int]
    for ($i=0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*(?:#.*)?$') { continue }
        if ($line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
            $invalid.Add($i+1)
            continue
        }
        $key = $Matches[1]
        if ($values.ContainsKey($key)) { $duplicates.Add($key); continue }
        $values[$key] = $Matches[2]
        $keys.Add($key)
    }
    return [pscustomobject]@{
        Lines = $lines
        Keys = @($keys.ToArray())
        Values = $values
        Duplicates = @($duplicates.ToArray())
        InvalidLines = @($invalid.ToArray())
    }
}
function Assert-EnvValid {
    param($Document,[string]$Label)
    if ($Document.InvalidLines.Count -gt 0) { throw "$Label contains malformed entries on line(s): $($Document.InvalidLines -join ', ')" }
    if ($Document.Duplicates.Count -gt 0) { throw "$Label contains duplicate keys: $($Document.Duplicates -join ', ')" }
}