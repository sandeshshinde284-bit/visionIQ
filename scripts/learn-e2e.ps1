<#
.SYNOPSIS
  One entry point for VisionIQ LEARN LRN-01 through LRN-14 acceptance checks.
.DESCRIPTION
  Runs scripts/learn_e2e.py. Scope 1-7 verifies source history, canonical
  evidence, Confluence, code scan and graph when approved inputs are present.
  Scope 8-13 additionally exercises index, retrieval, lesson, readiness and
  PR-event test paths; all stages report explicit coverage gaps.
  No ADO record is created unless fixture creation is selected and confirmed.
  Does NOT merge PRs, delete records/branches, or claim a Draft PR is a fix.
.EXAMPLE
  .\scripts\learn-e2e.ps1
.EXAMPLE
  .\scripts\learn-e2e.ps1 -Mode existing -OutputMode jsonl -TaskId 1002 -PullRequestId 88
.EXAMPLE
  .\scripts\learn-e2e.ps1 -Mode existing -OutputMode canonical -TaskId 1002 -BugId 1001 -PullRequestId 88
.EXAMPLE
  .\scripts\learn-e2e.ps1 -Mode previous -FixtureManifest .\artifacts\learn-e2e\runs\RUN\fixture.json -OutputMode canonical
#>
[CmdletBinding()]
param(
    [ValidateSet('create','existing','previous')][string]$Mode,
    [ValidateSet('1-7','8-13','1-13','14','LRN-01','LRN-02','LRN-03','LRN-04','LRN-05','LRN-06','LRN-07','LRN-08','LRN-09','LRN-10','LRN-11','LRN-12','LRN-13','LRN-14')][string]$Scope,
    [ValidateSet('jsonl','canonical')][string]$OutputMode,
    [string]$OrganizationUrl,
    [string]$Project,
    [string]$Repository,
    [int]$BugId,
    [int]$TaskId,
    [int]$PullRequestId,
    [string]$FixtureManifest,
    [switch]$NonInteractive,
    [switch]$ApproveFixtureWrites,
    [switch]$SkipDocker,
    [ValidateSet('reuse','start','rebuild')][string]$DockerMode,
    [ValidateRange(1,90)][int]$SinceDays = 2,
    [switch]$FullHistory,
    [string]$ConfluenceSpace,
    [string]$ConfluencePageId,
    [string]$ConfluenceAdrPageId,
    [string]$ConfluenceRestrictedPageId,
    [string]$ScanCheckout,
    [string]$ScanCommit,
    [string]$GraphAllowedTokenEnv,
    [string]$GraphDeniedTokenEnv,
    [switch]$CodeFixture,
    [switch]$ApproveKnowledgeWrites,
    [string]$HistoricalBugIds
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $PSScriptRoot 'learn_e2e.py'
if (-not (Test-Path -LiteralPath $helper)) { throw "Missing helper: $helper" }
$venvPython = Join-Path $root '.venv\Scripts\python.exe'
$pythonCmd = $null
if (Test-Path -LiteralPath $venvPython -PathType Leaf) {
    $pythonCmd = Get-Item -LiteralPath $venvPython
}
else {
    $pythonCmd = Get-Command python -ErrorAction SilentlyContinue
}
$pythonArgs = @()
if (-not $pythonCmd) {
    $pythonCmd = Get-Command py -ErrorAction SilentlyContinue
    if (-not $pythonCmd) { throw 'Python 3 is required. Run scripts/check-env.ps1 first.' }
    $pythonArgs += '-3'
}
$pythonArgs += $helper
if ($Mode) { $pythonArgs += @('--mode', $Mode) }
if ($Scope) { $pythonArgs += @('--scope', $Scope) }
if ($OutputMode) { $pythonArgs += @('--output-mode', $OutputMode) }
if ($OrganizationUrl) { $pythonArgs += @('--organization-url', $OrganizationUrl) }
if ($Project) { $pythonArgs += @('--project', $Project) }
if ($Repository) { $pythonArgs += @('--repository', $Repository) }
if ($BugId) { $pythonArgs += @('--bug-id', [string]$BugId) }
if ($TaskId) { $pythonArgs += @('--task-id', [string]$TaskId) }
if ($PullRequestId) { $pythonArgs += @('--pull-request-id', [string]$PullRequestId) }
if ($FixtureManifest) { $pythonArgs += @('--fixture-manifest', $FixtureManifest) }
if ($NonInteractive) { $pythonArgs += '--non-interactive' }
if ($ApproveFixtureWrites) { $pythonArgs += '--approve-fixture-writes' }
if ($SkipDocker) { $pythonArgs += '--skip-docker' }
if ($DockerMode) { $pythonArgs += @('--docker-mode', $DockerMode) }
$pythonArgs += @('--since-days', [string]$SinceDays)
if ($FullHistory) { $pythonArgs += '--full-history' }
if ($ConfluenceSpace) { $pythonArgs += @('--confluence-space', $ConfluenceSpace) }
if ($ConfluencePageId) { $pythonArgs += @('--confluence-page-id', $ConfluencePageId) }
if ($ConfluenceAdrPageId) { $pythonArgs += @('--confluence-adr-page-id', $ConfluenceAdrPageId) }
if ($ConfluenceRestrictedPageId) { $pythonArgs += @('--confluence-restricted-page-id', $ConfluenceRestrictedPageId) }
if ($ScanCheckout) { $pythonArgs += @('--scan-checkout', $ScanCheckout) }
if ($ScanCommit) { $pythonArgs += @('--scan-commit', $ScanCommit) }
if ($GraphAllowedTokenEnv) { $pythonArgs += @('--graph-allowed-token-env', $GraphAllowedTokenEnv) }
if ($GraphDeniedTokenEnv) { $pythonArgs += @('--graph-denied-token-env', $GraphDeniedTokenEnv) }
if ($CodeFixture) { $pythonArgs += '--code-fixture' }
if ($ApproveKnowledgeWrites) { $pythonArgs += '--approve-knowledge-writes' }
if ($HistoricalBugIds) { $pythonArgs += @('--historical-bug-ids', $HistoricalBugIds) }
Push-Location $root
try {
    $pythonExecutable = if ($pythonCmd -is [System.IO.FileInfo]) { $pythonCmd.FullName } else { $pythonCmd.Source }
    if (-not $pythonExecutable) { throw 'Unable to resolve Python executable path' }
    & $pythonExecutable @pythonArgs
    $exitCode = $LASTEXITCODE
}
finally { Pop-Location }
exit $exitCode
