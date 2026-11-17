<#
.SYNOPSIS
  VisionIQ local end-to-end run for one defect ticket: LEARN -> BRIEF -> DEFECT -> draft PR.

.DESCRIPTION
  Run from the VisionIQ repository root (the folder that contains .env, src\ and config\).
  Every value comes from .env, the two policy files, docker-compose.yml or contracts\events\topology.json;
  nothing about your project, ticket, ports or repository is written into this script.

  One command does: checks -> local stack -> LEARN (sync, code scan, index) -> Intelligence and API ->
  moves the ticket into the pickup state in Azure DevOps -> BRIEF -> DEFECT -> draft PR, watched live.
  It stops at the first real problem and says how to fix it. It prints no secrets.
  Every consent is asked at the start, before anything happens (or given in advance with -Allow... switches):
  running AI-written tests on this machine (local runner), moving the ticket, stopping an Intelligence this
  script did not start. It writes to Azure DevOps only what you agreed to: the ticket's state change, the brief
  as a comment (BRIEF_PUBLISH_MODE=ado) and a DRAFT pull request (FIX_PR_MODE=ado). VisionIQ never approves,
  completes or merges a pull request: reviewing it is yours.

  Modes:  -PreflightOnly  every check, then stop: nothing is started, changed or written
          -WatchOnly      follow the ticket's latest run: no setup, no build, no migration, no restarts
  Exit codes: 0 success (incl. enhancement guidance), 10 blocked, 20 the run failed, 30 timeout (no outcome yet),
              40 the outcome could not be read, 50 stalled (stuck: a diagnosis is shown), 1 stopped before or during setup.
  While watching, a run that is stuck is detected and explained (see step 15), instead of waiting it out.

.EXAMPLE
  .\run-visioniq-e2e.ps1 -PreflightOnly                     # safe first step: checks only, nothing changed
  .\run-visioniq-e2e.ps1                                    # everything (incremental LEARN sync); asks consents first
  .\run-visioniq-e2e.ps1 -SkipSync -AllowLocalRunner -AllowTicketMove   # consents given in advance
  .\run-visioniq-e2e.ps1 -WatchOnly                         # keep watching the latest run
#>
[CmdletBinding()]
param(
    # Optional overrides. Leave empty: the values come from .env and the policy files.
    [string]$TicketId = '',      # default: the single id in PICKUP_ALLOWED_WORK_ITEM_IDS
    [string]$Project = '',       # default: the pickup policy project listed in ADO_PROJECTS
    [string]$ResetState = '',    # default: the work item type's first "Proposed" state, read from Azure DevOps
    [string]$TriggerPat = '',    # default: ADO_COMMENT_PAT
    [switch]$SkipSync,
    [switch]$FullSync,
    [switch]$SkipScan,
    [switch]$SkipIndex,
    [switch]$ReuseServices,      # keeps a running API if it is current; Intelligence: see step 12
    [switch]$NoTrigger,
    [switch]$NoWatch,
    [switch]$PreflightOnly,      # every check, then stop: nothing is started, changed or written
    [switch]$WatchOnly,          # follow the ticket's latest run: no setup, no build, no migration, no restarts
    [switch]$AllowLocalRunner,   # consent in advance: DEFECT may run AI-written tests on this machine (no sandbox)
    [switch]$AllowTicketMove,    # consent in advance: move the ticket into the pickup state (starts the run)
    [switch]$AllowStopIntelligence,  # consent in advance: stop a VisionIQ Intelligence this script did not start
                                     # (never one with runs in progress)
    [int]$IndexLimit = 500,
    [int]$WatchMinutes = 45,
    [int]$StallMinutes = 20      # pickup, delivery or the brief show no change for this long: STALLED (exit 50).
                                 # DEFECT is judged by its own time budget instead (DEFECT_MAX_SECONDS_PER_RUN).
)

$ErrorActionPreference = 'Continue'
$root = (Get-Location).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $root "artifacts\e2e-run\$stamp"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$env:GIT_TERMINAL_PROMPT = '0'
# Everything this window shows goes to run.log; at every stop and at the end, the API and emulator logs and the
# ticket's database rows are saved beside it (see Save-Diagnostics). The script prints no secrets.
Start-Transcript -Path (Join-Path $logDir 'run.log') -Append | Out-Null
$runStartIso = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$script:servicesStarted = $false
$script:dbReady = $false
$script:diagnosticsSaved = $false

function Step([string]$text) { Write-Host "`n=== $text ===  [$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')]" -ForegroundColor Cyan }
function Pass([string]$text) { Write-Host "  [PASS] $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }
function Info([string]$text) { Write-Host "  $text" }
function Query([string]$sql, [switch]$Tolerant) {
    # psql inside the container connects through its local socket, which needs no password (none is passed).
    # ON_ERROR_STOP makes a failed query exit non-zero; its error text must never be read as a result.
    $out = docker compose exec -T postgres psql -U $dbUser -d $dbName -v ON_ERROR_STOP=1 -At -F ' | ' -c $sql 2>&1
    $code = $LASTEXITCODE
    $text = ($out | Out-String).Trim()
    if ($code -ne 0) {
        # Tolerant (the watch and the outcome report): the caller decides; checks before the run stay strict.
        if ($Tolerant) { $script:lastQueryError = ($text -replace "`n", ' | '); return $null }
        $first = ($sql -split "`n")[0]; if ($first.Length -gt 90) { $first = $first.Substring(0, 90) + '...' }
        Stop-Run "A database query failed (exit $code): $($text -replace "`n", ' | ')" "Query: $first   Check: docker compose ps; docker compose logs --tail 50 postgres"
    }
    $script:lastQueryError = $null
    return $text
}
# One consent: given in advance by its own switch, or asked now.
function Confirm-Step([string]$question, [bool]$given, [string]$switchName) {
    if ($given) { Info "$question  -> yes (-$switchName)"; return $true }
    $answer = Read-Host "  $question [y/N]"
    return $answer -match '^(y|yes)$'
}
# Can BRIEF read the ticket? It needs it twice (history.py load_ticket): in the evidence store (filled by the
# sync), and its text in the SERVED search snapshot (tools.py fetch_authorized_source), both under the pickup
# policy's access scope. Read only.
function Test-TicketReadable {
    $scopeSql = $pickupProject.security_scope_id -replace "'", "''"
    $key = Query "SELECT i.item_key FROM evidence.source_items AS i JOIN evidence.source_revisions AS r ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id AND r.acl_verified AND r.acl_principals ? '$scopeSql' WHERE i.provider = 'azure_devops' AND i.kind = 'work_item_current' AND i.external_id = '$TicketId' AND i.deleted_at IS NULL LIMIT 1"
    if (-not $key) {
        if (Query "SELECT i.item_key FROM evidence.source_items AS i WHERE i.provider = 'azure_devops' AND i.kind = 'work_item_current' AND i.external_id = '$TicketId' AND i.deleted_at IS NULL LIMIT 1") {
            $labels = Query "SELECT array_to_string(r.acl_principals, ', ') FROM evidence.source_items i JOIN evidence.source_revisions r ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id WHERE i.kind = 'work_item_current' AND i.external_id = '$TicketId' LIMIT 1"
            return @{ Status = 'out-of-scope'; Labels = $labels }
        }
        return @{ Status = 'not-synced' }
    }
    $key = $key.Trim()
    $indexed = Query "SELECT 1 FROM knowledge.index_chunks AS c JOIN knowledge.snapshot_entries AS se ON se.entry_key = c.entry_key JOIN knowledge.snapshot_pointer AS p ON p.snapshot_id = se.snapshot_id WHERE c.item_key = '$key' AND c.acl_principals ? '$scopeSql' LIMIT 1"
    if (-not $indexed) { return @{ Status = 'not-indexed'; ItemKey = $key } }
    return @{ Status = 'ok'; ItemKey = $key }
}
function Save-Diagnostics {
    if ($script:diagnosticsSaved) { return }
    $script:diagnosticsSaved = $true
    try {
        if ($script:servicesStarted) {
            docker compose logs --no-color --timestamps --since $runStartIso api *> (Join-Path $logDir 'api.log')
            docker compose logs --no-color --timestamps --since $runStartIso pubsub *> (Join-Path $logDir 'pubsub.log')
            docker compose ps *> (Join-Path $logDir 'containers.txt')
        }
        if ($script:dbReady -and $TicketId -match '^\d+$') {
            $recent = "now() - interval '12 hours'"
            $sections = [ordered]@{
                'workflow.analysis_runs (API side: one row per brief / DEFECT run)' = "/* diagnostics */ SELECT run_id, analysis_mode, status, ticket_kind, trigger_source, requested_at, completed_at FROM workflow.analysis_runs WHERE ticket_id = '$TicketId' AND requested_at >= $recent ORDER BY requested_at"
                'knowledge.analysis_requests (Intelligence work queue: attempts, lease, result)' = "/* diagnostics */ SELECT run_id, analysis_mode, status, attempt_count, available_at, result_state, reason_codes, received_at FROM knowledge.analysis_requests WHERE ticket_id = '$TicketId' AND received_at >= $recent ORDER BY received_at"
                'knowledge.briefs (state, reasons, limitations)' = "/* diagnostics */ SELECT state, risk_band, reason_codes, document->'limitations', created_at FROM knowledge.briefs WHERE ticket_id = '$TicketId' AND created_at >= $recent ORDER BY created_at"
                'workflow.brief_publications (comment on the ticket)' = "/* diagnostics */ SELECT p.mode, p.status, p.reason, p.attempt_count, p.comment_id, p.published_at FROM workflow.brief_publications p JOIN workflow.analysis_runs r ON r.run_id = p.run_id WHERE r.ticket_id = '$TicketId' AND r.requested_at >= $recent"
                'knowledge.llm_calls (every model call: provider, model, status, error, tokens, time)' = "/* diagnostics */ SELECT c.created_at, c.task, c.role, c.provider, c.model_id, c.status, c.error_code, c.input_tokens, c.output_tokens, c.elapsed_ms FROM knowledge.llm_calls c JOIN workflow.analysis_runs r ON r.run_id = c.run_id WHERE r.ticket_id = '$TicketId' AND r.requested_at >= $recent ORDER BY c.created_at"
                'workflow.fix_pull_requests (draft PR)' = "/* diagnostics */ SELECT f.mode, f.status, f.result_state, f.pull_request_id, f.source_branch FROM workflow.fix_pull_requests f JOIN workflow.analysis_runs r ON r.run_id = f.run_id WHERE r.ticket_id = '$TicketId' AND r.requested_at >= $recent"
            }
            $report = foreach ($title in $sections.Keys) {
                "=== $title"
                ((docker compose exec -T postgres psql -U $dbUser -d $dbName -A -F ' | ' -c $sections[$title] 2>&1) | Out-String).Trim()
                ''
            }
            $report | Out-File -FilePath (Join-Path $logDir 'db-state.txt') -Encoding utf8
        }
    } catch { Write-Host "  (could not save every diagnostic: $($_.Exception.Message))" }
}
function Stop-Run([string]$text, [string]$fix) {
    Write-Host "  [STOP] $text" -ForegroundColor Red
    if ($fix) { Write-Host "  Fix:   $fix" -ForegroundColor Red }
    Save-Diagnostics
    Write-Host "`nLogs: $logDir  (run.log = this window; api.log, pubsub.log, intelligence.log, db-state.txt, git.log, ...)"
    Stop-Transcript | Out-Null
    exit 1
}
function Wait-Http([string]$url, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        try { Invoke-RestMethod -Uri $url -TimeoutSec 5 | Out-Null; return $true } catch { Start-Sleep -Seconds 3 }
    } while ((Get-Date) -lt $deadline)
    return $false
}
function Resolve-HostPath([string]$path, [string]$containerMount) {
    if ($containerMount -and $path.StartsWith($containerMount + '/')) { $path = 'config/' + $path.Substring($containerMount.Length + 1) }
    if ([IO.Path]::IsPathRooted($path)) { return $path }
    return (Join-Path $root $path)
}

# ---------------------------------------------------------------------------------------------
if ($WatchOnly -and ($PreflightOnly -or $NoWatch)) { Stop-Run '-WatchOnly cannot be combined with -PreflightOnly or -NoWatch.' 'Choose one mode.' }
Step '1. Location, code version and .env'
foreach ($p in '.env', 'docker-compose.yml', 'contracts\events\topology.json', 'src\VisionIQ.Api', 'src\VisionIQ.Intelligence', 'src\VisionIQ.Ingestor.Cli') {
    if (-not (Test-Path (Join-Path $root $p))) { Stop-Run "Not in the VisionIQ repository root ($root): $p missing." 'cd to the folder that contains .env and src\, then run again.' }
}
$required = @{
    'src\VisionIQ.Intelligence\app\defect\clone_check.py' = 'the private-repository clone (ADO_READ_PAT), TRX reports and model tiers'
    'src\VisionIQ.Intelligence\app\defect\complexity.py'  = 'model selection by complexity'
}
foreach ($f in $required.Keys) { if (-not (Test-Path (Join-Path $root $f))) { Stop-Run "This checkout is older than main: it does not contain $($required[$f])." 'Pull the latest main, then run again.' } }
if (-not (Test-Path (Join-Path $root 'src\VisionIQ.Intelligence\app\brief\guidance.py'))) { Warn 'Enhancement guidance is not in this checkout: enhancement tickets get a plain brief.' }
Info "VisionIQ code: $(git -C $root log -1 --format='%h %s' 2>$null)"

$envValues = @{}
foreach ($line in Get-Content (Join-Path $root '.env')) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $envValues[$Matches[1]] = $value
        [Environment]::SetEnvironmentVariable($Matches[1], $value, 'Process')
    }
}
Pass ".env loaded ($($envValues.Count) settings; values not shown)"
# A copy of the settings this run used, with every secret replaced by 'set' or 'EMPTY'.
# Only names known to hold no secret are printed; everything else (any new name too) shows only 'set' or 'EMPTY'.
$printable = @('APP_ENV', 'APP_LOG_LEVEL', 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'PYTHON_API_HOST', 'PYTHON_API_PORT',
    'PYTHON_SERVICE_URL', 'REQUEST_TIMEOUT_SECONDS', 'DB_TARGET', 'DB_HOST', 'DB_PORT', 'DB_NAME', 'DB_USER', 'DB_SSL_MODE',
    'DB_LOCAL_PORT', 'DB_LOCAL_NAME', 'DB_LOCAL_USER', 'DB_API_USER', 'DB_INTELLIGENCE_USER', 'DB_CLOUD_INSTANCE',
    'DB_CLOUD_PROXY_PORT', 'DB_CLOUD_NAME', 'DB_CLOUD_USER', 'GCP_PROJECT_ID', 'GCP_REGION', 'VERTEX_LOCATION',
    'EMBEDDING_PROVIDER', 'EMBEDDING_MODEL', 'EMBEDDING_DIMENSION', 'EMBEDDING_BATCH_SIZE', 'EMBEDDING_REQUEST_TIMEOUT',
    'EMBEDDING_MAX_RETRIES', 'INDEX_BATCH_LIMIT', 'BRIEF_PIPELINE_ENABLED', 'LLM_MODE', 'GEMINI_MODEL', 'CLAUDE_ROUTE',
    'CLAUDE_MODEL', 'LLM_CLAUDE_MAX_CLASSIFICATION', 'LLM_DATA_CLASSIFICATION', 'BRIEF_PUBLISH_MODE',
    'HOTSPOT_SWEEP_INTERVAL_HOURS', 'DEFECT_PIPELINE_ENABLED', 'DEFECT_POLICY_FILE', 'DEFECT_ALLOW_LOCAL_RUNNER',
    'DEFECT_REPO_CACHE_DIR', 'DEFECT_REPO_FETCH', 'DEFECT_MODEL_SELECTION', 'DEFECT_MODEL_SIMPLE', 'DEFECT_MODEL_STANDARD',
    'DEFECT_MODEL_COMPLEX', 'DEFECT_OFFLINE_SCRIPT', 'FIX_PR_MODE', 'FIX_AUTO_START', 'ADO_ORG_URL', 'ADO_PROJECTS',
    'ADO_REPOS', 'ADO_DEFAULT_BRANCH', 'ADO_PIPELINE_IDS', 'ADO_WEBHOOK_USERNAME', 'CONFLUENCE_BASE_URL',
    'CONFLUENCE_SPACE_KEYS', 'CONFLUENCE_DEPLOYMENT', 'TENANT_ID', 'PUBSUB_PROJECT_ID', 'PUBSUB_EMULATOR_HOST',
    'EVENTS_DISPATCH_ENABLED', 'EVENTS_DISPATCH_INTERVAL_SECONDS', 'EVENT_CONTRACTS_DIR', 'EVENTS_PUSH_BASE_URL_API',
    'EVENTS_PUSH_BASE_URL_INTELLIGENCE', 'PICKUP_POLICY_FILE', 'PICKUP_ALLOWED_WORK_ITEM_IDS', 'PICKUP_WORKER_INTERVAL_SECONDS',
    'PICKUP_SWEEP_INTERVAL_MINUTES', 'PICKUP_SWEEP_LOOKBACK_HOURS', 'INGESTION_SINCE', 'INGESTION_OUTPUT_DIR',
    'INGESTION_OUTPUT_MODE', 'CHECKPOINT_STORE', 'GCS_EVIDENCE_BUCKET', 'INGESTION_REQUEST_TIMEOUT_SECONDS',
    'INGESTION_EVIDENCE_CONCURRENCY', 'INGESTION_COMMIT_LOOKBACK_DAYS', 'ADO_ENVIRONMENT_RULES_FILE', 'LESSON_DEFECT_TYPES',
    'LOG_LEVEL', 'LOG_FORMAT', 'LEARN_E2E_COMMAND_TIMEOUT_SECONDS')
$envValues.Keys | Sort-Object | ForEach-Object {
    $v = $envValues[$_]
    if ($printable -notcontains $_) { "$_=" + $(if ($v -and -not $v.StartsWith('#')) { 'set' } else { 'EMPTY' }) } else { "$_=$v" }
} | Out-File -FilePath (Join-Path $logDir 'settings-masked.txt') -Encoding utf8

function Get-Setting([string]$name) {
    $v = $envValues[$name]
    if ($null -eq $v -or $v -eq '' -or $v.StartsWith('#')) { return $null }  # '#<...>' is a template placeholder
    return $v
}
# The leading comma keeps a one-item list a list (PowerShell would otherwise hand back the bare string).
$dbUser = $null; $dbName = $null
function Split-List([string]$value) { return , @(($value -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

# ---------------------------------------------------------------------------------------------
Step '2. Configuration: real models, no offline paths, tokens present'
$problems = @()
foreach ($key in 'ADO_ORG_URL', 'ADO_PROJECTS', 'ADO_REPOS', 'ADO_READ_PAT', 'GCP_PROJECT_ID', 'GEMINI_MODEL',
                 'DB_LOCAL_USER', 'DB_LOCAL_NAME', 'DB_LOCAL_PASSWORD', 'DB_API_PASSWORD', 'DB_INTELLIGENCE_PASSWORD',
                 'PYTHON_API_PORT', 'PICKUP_POLICY_FILE', 'DEFECT_POLICY_FILE',
                 'PICKUP_ALLOWED_WORK_ITEM_IDS', 'GCS_EVIDENCE_BUCKET') {
    if (-not (Get-Setting $key)) { $problems += "$key is empty (or still a '#<...>' placeholder)" }
}
if ((Get-Setting 'LLM_MODE') -ne 'live') { $problems += 'LLM_MODE must be live (offline = no real model is called)' }
if ((Get-Setting 'EMBEDDING_PROVIDER') -ne 'vertex') { $problems += 'EMBEDDING_PROVIDER must be vertex (anything else is not a real embedding)' }
if (Get-Setting 'DEFECT_OFFLINE_SCRIPT') { $problems += 'DEFECT_OFFLINE_SCRIPT must be empty (it replaces the model with scripted answers)' }
if ((Get-Setting 'CHECKPOINT_STORE') -ne 'postgres') { $problems += 'CHECKPOINT_STORE must be postgres (canonical ingestion refuses to start otherwise)' }
if ((Get-Setting 'INGESTION_OUTPUT_MODE') -ne 'canonical') { $problems += 'INGESTION_OUTPUT_MODE must be canonical' }
if ((Get-Setting 'BRIEF_PIPELINE_ENABLED') -ne 'true') { $problems += 'BRIEF_PIPELINE_ENABLED must be true' }
if ((Get-Setting 'CONFLUENCE_SPACE_KEYS') -and -not (Get-Setting 'CONFLUENCE_API_TOKEN')) { $problems += 'CONFLUENCE_SPACE_KEYS is set but CONFLUENCE_API_TOKEN is empty (the sync would fail on Confluence)' }
if ((Get-Setting 'BRIEF_PUBLISH_MODE') -eq 'ado' -and -not (Get-Setting 'ADO_COMMENT_PAT')) { $problems += 'BRIEF_PUBLISH_MODE=ado needs ADO_COMMENT_PAT (Work Items: Read & Write)' }
if ((Get-Setting 'FIX_PR_MODE') -eq 'ado' -and -not (Get-Setting 'ADO_PR_PAT')) { $problems += 'FIX_PR_MODE=ado needs ADO_PR_PAT (Code: Read & Write)' }
$claude = Get-Setting 'CLAUDE_ROUTE'
if ($claude -eq 'anthropic') {
    if (-not (Get-Setting 'ANTHROPIC_API_KEY')) { $problems += 'CLAUDE_ROUTE=anthropic but ANTHROPIC_API_KEY is empty' }
    if (-not (Get-Setting 'LLM_CLAUDE_MAX_CLASSIFICATION')) { $problems += 'LLM_CLAUDE_MAX_CLASSIFICATION is empty: Claude would receive nothing' }
    if (-not (Get-Setting 'CLAUDE_MODEL')) { $problems += 'CLAUDE_MODEL is empty: the code then falls back to its built-in default (an Opus model)' }
}
if ((Get-Setting 'DEFECT_PIPELINE_ENABLED') -eq 'true' -and $claude -notin 'anthropic', 'vertex') { $problems += 'DEFECT needs Claude: set CLAUDE_ROUTE=anthropic (or vertex)' }
$allowed = Split-List (Get-Setting 'PICKUP_ALLOWED_WORK_ITEM_IDS')
if (-not $TicketId) {
    if ($allowed.Count -eq 1) { $TicketId = $allowed[0] }
    elseif ($allowed.Count -gt 1) { $problems += "PICKUP_ALLOWED_WORK_ITEM_IDS has $($allowed.Count) ids; pass -TicketId <id> to choose the one to run" }
}
if ($TicketId -and $TicketId -notmatch '^\d+$') { $problems += "Ticket id must be numeric ($TicketId)" }
elseif ($TicketId -and $allowed -notcontains $TicketId) { $problems += "PICKUP_ALLOWED_WORK_ITEM_IDS does not contain $TicketId" }
if ($problems) { $problems | ForEach-Object { Write-Host "  [STOP] $_" -ForegroundColor Red }; Stop-Run 'Fix the settings above in .env.' '' }
Pass "Live models, Vertex embeddings, no offline script, required tokens present; ticket $TicketId"
$dbUser = Get-Setting 'DB_LOCAL_USER'; $dbName = Get-Setting 'DB_LOCAL_NAME'

$sweep = 0; [int]::TryParse((Get-Setting 'PICKUP_SWEEP_INTERVAL_MINUTES'), [ref]$sweep) | Out-Null
if ($sweep -le 0) { Stop-Run 'PICKUP_SWEEP_INTERVAL_MINUTES is 0: nothing would pick the ticket up (no webhook reaches this machine).' 'Set PICKUP_SWEEP_INTERVAL_MINUTES (e.g. 3) in .env.' }
$intelPort = 0; if (-not [int]::TryParse((Get-Setting 'PYTHON_API_PORT'), [ref]$intelPort) -or $intelPort -le 0) { Stop-Run 'PYTHON_API_PORT is not a port number.' 'Set PYTHON_API_PORT in .env.' }
Pass "Pickup sweep every $sweep min, limited to: $($allowed -join ', ')"
if ((Get-Setting 'DEFECT_PIPELINE_ENABLED') -ne 'true') { Warn 'DEFECT_PIPELINE_ENABLED is not true: the run stops after BRIEF.' }
if ((Get-Setting 'FIX_AUTO_START') -ne 'true') { Warn 'FIX_AUTO_START is not true: DEFECT will not start after the brief.' }
Info "BRIEF_PUBLISH_MODE=$(Get-Setting 'BRIEF_PUBLISH_MODE')  FIX_PR_MODE=$(Get-Setting 'FIX_PR_MODE')   (record = database only, ado = real comment / real draft PR)"
Info "Models: Gemini=$(Get-Setting 'GEMINI_MODEL')  Claude default=$(Get-Setting 'CLAUDE_MODEL')  selection=$(Get-Setting 'DEFECT_MODEL_SELECTION')  simple=$(Get-Setting 'DEFECT_MODEL_SIMPLE')  standard=$(Get-Setting 'DEFECT_MODEL_STANDARD')  complex=$(Get-Setting 'DEFECT_MODEL_COMPLEX')"
foreach ($k in 'CLAUDE_MODEL', 'DEFECT_MODEL_SIMPLE', 'DEFECT_MODEL_STANDARD', 'DEFECT_MODEL_COMPLEX') {
    if ((Get-Setting $k) -match 'opus') { Warn "$k uses an Opus model; you decided to keep Opus out of this test (cost)." }
}

# ---------------------------------------------------------------------------------------------
Step '3. docker-compose.yml and the event topology'
foreach ($tool in 'docker', 'dotnet', 'git', 'gcloud') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-Run "$tool is not on PATH." "Install $tool or open a shell where it is available." }
}
docker info *> $null
if ($LASTEXITCODE -ne 0) { Stop-Run 'Docker is not running.' 'Start Docker Desktop and wait until it is ready.' }
& { "PowerShell $($PSVersionTable.PSVersion)"; docker compose version; "dotnet $(dotnet --version)"; git --version; gcloud --version } 2>&1 |
    Out-File -FilePath (Join-Path $logDir 'environment.txt') -Encoding utf8
try { $compose = (docker compose config --format json 2>$null | Out-String) | ConvertFrom-Json } catch { $compose = $null }
if (-not $compose) { Stop-Run 'Could not read docker-compose.yml (docker compose config).' 'docker compose config' }
function Get-Port($service) {
    $port = @($compose.services.$service.ports)[0]
    if (-not $port) { Stop-Run "docker-compose.yml publishes no port for '$service'." '' }
    return @{ Target = [int]$port.target; Published = [int]$port.published }
}
$apiPort = Get-Port 'api'
$pubsubPort = Get-Port 'pubsub'
$dbPort = Get-Port 'postgres'   # published from DB_LOCAL_PORT; host processes read DB_PORT, so it is set from this
$configDir = (Join-Path $root 'config').TrimEnd('\').ToLowerInvariant()
$mount = @($compose.services.api.volumes) | Where-Object { $_.type -eq 'bind' -and ([string]$_.source).TrimEnd('\', '/').ToLowerInvariant() -eq $configDir } | Select-Object -First 1
if (-not $mount) { Stop-Run 'docker-compose.yml does not mount .\config into the api service; the API could not read the pickup policy.' '' }
$apiConfigMount = ([string]$mount.target).TrimEnd('/')
$topology = Get-Content (Join-Path $root 'contracts\events\topology.json') -Raw | ConvertFrom-Json
# All three parties must use one Pub/Sub project: the API container (publishes requests, receives completions),
# pubsub-init (creates the topics) and Intelligence on this machine (receives requests, publishes completions).
# The containers take it from docker-compose.yml, so the host process is given the same value.
$pubsubProject = $compose.services.'pubsub-init'.environment.PUBSUB_PROJECT_ID
$apiPubsubProject = $compose.services.api.environment.PUBSUB_PROJECT_ID
if (-not $pubsubProject -or $pubsubProject -ne $apiPubsubProject) { Stop-Run "docker-compose.yml gives pubsub-init '$pubsubProject' but the API '$apiPubsubProject' as PUBSUB_PROJECT_ID." 'Use the same value for both.' }
if ((Get-Setting 'PUBSUB_PROJECT_ID') -and (Get-Setting 'PUBSUB_PROJECT_ID') -ne $pubsubProject) { Warn ".env PUBSUB_PROJECT_ID is '$(Get-Setting 'PUBSUB_PROJECT_ID')'; the local containers use '$pubsubProject', so Intelligence on this machine is given '$pubsubProject' too." }
$env:PUBSUB_PROJECT_ID = $pubsubProject
$py = Join-Path $root '.venv\Scripts\python.exe'
if (-not (Test-Path $py)) { $py = (Get-Command python -ErrorAction SilentlyContinue).Source }
if (-not $py) { Stop-Run 'No Python found (.venv\Scripts\python.exe or python).' 'Create the venv and install requirements.' }
Pass "API on port $($apiPort.Published), PostgreSQL on $($dbPort.Published), emulator on $($pubsubPort.Published) (project '$pubsubProject'), Intelligence on $intelPort; policies mounted at $apiConfigMount; $(@($topology.events).Count) event subscriptions"

# ---------------------------------------------------------------------------------------------
Step '4. Policy files'
$pickupFile = Resolve-HostPath (Get-Setting 'PICKUP_POLICY_FILE') $apiConfigMount
$defectFile = Resolve-HostPath (Get-Setting 'DEFECT_POLICY_FILE') $apiConfigMount
foreach ($f in $pickupFile, $defectFile) { if (-not (Test-Path $f)) { Stop-Run "Policy file not found: $f" 'Check PICKUP_POLICY_FILE / DEFECT_POLICY_FILE in .env.' } }
try { $pickup = Get-Content $pickupFile -Raw | ConvertFrom-Json; $defect = Get-Content $defectFile -Raw | ConvertFrom-Json }
catch { Stop-Run 'A policy file is not valid JSON.' $_.Exception.Message }
if ((Split-Path $pickupFile -Parent).TrimEnd('\').ToLowerInvariant() -ne $configDir) { Stop-Run 'The pickup policy must be in .\config (the only folder the API container sees).' 'Move it to .\config and point PICKUP_POLICY_FILE at it.' }

$adoProjects = Split-List (Get-Setting 'ADO_PROJECTS')
$candidates = @($pickup.projects) | Where-Object { if ($Project) { $_.project -eq $Project } else { $adoProjects -contains $_.project } }
if (@($candidates).Count -ne 1) { Stop-Run "The pickup policy has $(@($candidates).Count) projects matching $(if ($Project) { "'$Project'" } else { "ADO_PROJECTS ($($adoProjects -join ', '))" })." 'Pass -Project <ADO project name>.' }
$pickupProject = @($candidates)[0]
$repo = @($defect.repositories) | Where-Object { $_.repository_id -eq $pickupProject.repository_id } | Select-Object -First 1
if (-not $repo) { Stop-Run "The defect policy has no repository '$($pickupProject.repository_id)' (the pickup policy's repository_id)." 'Add it to the defect policy, or fix repository_id.' }
$org = (Get-Setting 'ADO_ORG_URL').TrimEnd('/')
$policyProblems = @()
if (($pickup.bot_identities | Out-String) -match '<') { $policyProblems += 'pickup policy: bot_identities still contains a <placeholder>' }
if ($pickup.policy_revision -notmatch '^[A-Za-z0-9_.:-]{1,64}$') { $policyProblems += "pickup policy: policy_revision '$($pickup.policy_revision)' may only use letters, digits and _ . : -" }
if ($pickupProject.project_id -notmatch '^[A-Za-z0-9_.:/-]{1,256}$') { $policyProblems += "pickup policy: project_id '$($pickupProject.project_id)' must be the ADO project GUID (no spaces)" }
if (-not @($pickupProject.pickup_states)) { $policyProblems += 'pickup policy: pickup_states is empty' }
if ($pickupProject.branch -cne $repo.target_branch) { $policyProblems += "branch differs: pickup '$($pickupProject.branch)', defect '$($repo.target_branch)' (case matters)" }
if ((Split-List (Get-Setting 'ADO_REPOS')) -notcontains $repo.repository_id) { $policyProblems += "ADO_REPOS does not include '$($repo.repository_id)' (LEARN would not sync its PRs and commits)" }
if (-not $repo.source.StartsWith($org + '/', [StringComparison]::OrdinalIgnoreCase)) { $policyProblems += "defect policy: source is not in ADO_ORG_URL ($org): the read token would not be sent and a private clone fails" }
if ($repo.runner.kind -eq 'local') {
    if ((Get-Setting 'DEFECT_ALLOW_LOCAL_RUNNER') -ne 'true') { $policyProblems += 'defect policy uses the local runner: set DEFECT_ALLOW_LOCAL_RUNNER=true in .env' }
    if ((Get-Setting 'APP_ENV') -eq 'production') { $policyProblems += 'the local runner is always refused with APP_ENV=production' }
    # The local runner passes dotnet only PATH, SYSTEMROOT, TEMP and similar; on Windows, dotnet and NuGet also need the profile.
    $missing = @('USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'PROGRAMFILES', 'PROGRAMDATA' | Where-Object { @($repo.runner.env_allow) -notcontains $_ })
    if ($missing) { $policyProblems += "defect policy runner.env_allow is missing $($missing -join ', '): NuGet restore fails inside DEFECT on Windows. Add: `"env_allow`": [`"USERPROFILE`", `"APPDATA`", `"LOCALAPPDATA`", `"PROGRAMFILES`", `"PROGRAMFILES(X86)`", `"PROGRAMDATA`", `"HOMEDRIVE`", `"HOMEPATH`"]" }
}
if ($repo.runner.kind -eq 'docker' -and $repo.runner.image -notmatch '@sha256:[0-9a-f]{64}$') { $policyProblems += 'defect policy: the docker runner image must be pinned by a real @sha256 digest' }
if ($policyProblems) { $policyProblems | ForEach-Object { Write-Host "  [STOP] $_" -ForegroundColor Red }; Stop-Run 'Fix the policy files above.' '' }
$branch = $repo.target_branch
Copy-Item $pickupFile, $defectFile -Destination $logDir -ErrorAction SilentlyContinue   # policies hold no secrets
Pass "Project '$($pickupProject.project)', repository '$($repo.repository_id)', branch '$branch', runner '$($repo.runner.kind)'"
if ($repo.runner.kind -eq 'local') { Warn 'Local runner: model-written tests and fixes run on this machine without a sandbox. Watch the Intelligence window.' }

# A sync in another window would write the same records at the same time.
$running = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match 'VisionIQ\.Ingestor\.Cli' -and $_.CommandLine -match '\ssync(\s|$)' }
if ($running) { Stop-Run 'A LEARN sync is still running in another window.' 'Wait for it to finish, then run this script (with -SkipSync if it was the same sync).' }

# ---------------------------------------------------------------------------------------------
# From here on: whatever ends the script (a stop, an error, Ctrl+C), the logs and the ticket's database rows are
# saved (Save-Diagnostics runs once; later calls do nothing).
try {
if ($WatchOnly) {
    Step '5. Watch only: no setup, no build, no migration, no restarts, nothing changed'
    $up = @(docker compose ps --status running --services 2>$null)
    if ($up -notcontains 'postgres') { Stop-Run 'PostgreSQL is not running, so there is no run to watch.' 'Start a run first (without -WatchOnly).' }
    $ready = $false
    for ($i = 0; $i -lt 10; $i++) { docker compose exec -T postgres pg_isready -U $dbUser *> $null; if ($LASTEXITCODE -eq 0) { $ready = $true; break }; Start-Sleep 2 }
    if (-not $ready) { Stop-Run 'PostgreSQL is not ready.' 'docker compose logs postgres' }
    $script:dbReady = $true; $script:servicesStarted = $true
    Pass "Following the latest run of ticket $TicketId; nothing is started or changed"
} else {
if (-not $PreflightOnly) {
    Step '4b. Your consent for this run (asked now, so nothing waits for you later)'
    $defectExpected = (Get-Setting 'DEFECT_PIPELINE_ENABLED') -eq 'true' -and (Get-Setting 'FIX_AUTO_START') -eq 'true'
    if ($repo.runner.kind -eq 'local' -and $defectExpected) {
        if (-not (Confirm-Step 'DEFECT will run AI-written tests and code changes on THIS machine, without a sandbox (local runner). Allow?' $AllowLocalRunner 'AllowLocalRunner')) {
            Stop-Run 'Local runner not allowed: nothing was started or changed.' 'Answer y, or pass -AllowLocalRunner; or use a docker runner in the defect policy.'
        }
    }
    if (-not $NoTrigger) {
        if (-not (Confirm-Step "Move ticket $TicketId in Azure DevOps into '$(@($pickupProject.pickup_states)[0])' (out and back if it is already there)? This starts the run" $AllowTicketMove 'AllowTicketMove')) {
            Stop-Run 'Ticket move not allowed: nothing was started or changed.' 'Answer y, or pass -AllowTicketMove; or use -NoTrigger and move the ticket yourself.'
        }
    }
    # An Intelligence on the port that this script did not start (no record of it): decided now, used at step 12,
    # where one with runs in progress is never stopped whatever the answer.
    $intelRecord = Join-Path $root 'artifacts\e2e-run\intelligence-process.json'
    $recorded = $null; try { $recorded = Get-Content $intelRecord -Raw -ErrorAction Stop | ConvertFrom-Json } catch { }
    $script:mayStopForeign = $false
    foreach ($holder in @(Get-NetTCPConnection -LocalPort $intelPort -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)) {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $holder" -ErrorAction SilentlyContinue
        $holderStart = (Get-Process -Id $holder -ErrorAction SilentlyContinue).StartTime
        $ours = $recorded -and [int]$recorded.pid -eq [int]$holder -and $holderStart -and $recorded.started -eq $holderStart.ToString('o')
        if ($proc -and $proc.CommandLine -match 'uvicorn' -and $proc.CommandLine -match 'app\.main:app' -and -not $ours) {
            $script:mayStopForeign = Confirm-Step "Port $intelPort is held by a VisionIQ Intelligence this script did not start (process $holder). Stop it at step 12 if it has no run in progress?" $AllowStopIntelligence 'AllowStopIntelligence'
            if (-not $script:mayStopForeign) { Stop-Run "Not allowed to stop the running Intelligence (process $holder): nothing was started or changed." 'Close it yourself, answer y, or pass -AllowStopIntelligence.' }
        }
    }
    Pass 'All consents for this run are given'
}
Step '5. Cloud access: Google login and Claude models'
gcloud auth application-default print-access-token *> $null
if ($LASTEXITCODE -ne 0) { Stop-Run 'Google application-default login is missing or expired (this caused the earlier invalid_grant).' 'gcloud auth application-default login' }
Pass 'Google application-default credentials valid (Vertex AI, evidence bucket)'
if ($claude -eq 'anthropic') {
    $models = @(Get-Setting 'CLAUDE_MODEL')
    foreach ($k in 'DEFECT_MODEL_SIMPLE', 'DEFECT_MODEL_STANDARD', 'DEFECT_MODEL_COMPLEX') {
        $v = Get-Setting $k
        if ($v -and $v.StartsWith('claude:')) { $models += $v.Substring(7) }
    }
    foreach ($m in ($models | Where-Object { $_ } | Select-Object -Unique)) {
        try {
            Invoke-RestMethod -Uri "https://api.anthropic.com/v1/models/$m" -TimeoutSec 20 `
                -Headers @{ 'x-api-key' = (Get-Setting 'ANTHROPIC_API_KEY'); 'anthropic-version' = '2023-06-01' } | Out-Null
            Pass "Claude model '$m' exists for this API key (checked without spending credits)"
        } catch {
            $code = 0; try { $code = [int]$_.Exception.Response.StatusCode } catch { }
            if ($code -eq 404) { Stop-Run "Claude model id '$m' does not exist." 'Copy the exact model id from the Anthropic console.' }
            if ($code -eq 401) { Stop-Run 'ANTHROPIC_API_KEY was rejected.' 'Check the key in the Anthropic console.' }
            Warn "Could not check Claude model '$m' (HTTP $code). Continuing."
        }
    }
}

# ---------------------------------------------------------------------------------------------
Step "6. Repository '$($repo.repository_id)' at the server's latest '$branch' (from the defect policy source)"
# The script keeps its own checkout, so your own clones are never touched. The read token goes through git's
# environment for this call only (never on the command line or into the checkout's config), scoped to ADO_ORG_URL.
$checkout = Join-Path $root "artifacts\repos\$($repo.repository_id)"
function Invoke-AdoGit([string[]]$gitArgs) {
    $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(':' + (Get-Setting 'ADO_READ_PAT')))
    $scope = $repo.source.Substring(0, $org.Length)   # same organisation, spelled as in the source (git compares exactly)
    $env:GIT_CONFIG_COUNT = '1'; $env:GIT_CONFIG_KEY_0 = "http.$scope/.extraHeader"; $env:GIT_CONFIG_VALUE_0 = "Authorization: Basic $basic"
    try { & git @gitArgs 2>&1 } finally { Remove-Item Env:\GIT_CONFIG_COUNT, Env:\GIT_CONFIG_KEY_0, Env:\GIT_CONFIG_VALUE_0 -ErrorAction SilentlyContinue }
}
# git's own messages go to git.log (they never contain the token: it travels only in git's environment).
$gitLog = Join-Path $logDir 'git.log'
# The reason shown is the last lines of the call that failed, not of earlier calls in the same log.
function Get-GitReason($output) { (@($output) | ForEach-Object { "$_".Trim() } | Where-Object { $_ } | Select-Object -Last 2) -join ' | ' }
$heads = Invoke-AdoGit @('ls-remote', '--heads', $repo.source)
$heads | Out-File -FilePath $gitLog -Append -Encoding utf8
if ($LASTEXITCODE -ne 0) { Stop-Run "git ls-remote of $($repo.source) failed: $(Get-GitReason $heads)" "Check ADO_READ_PAT (Code: Read) and the source address in the defect policy. Full output: $gitLog" }
if (-not ($heads | Where-Object { "$_" -cmatch "refs/heads/$([regex]::Escape($branch))$" })) {
    $similar = $heads | Where-Object { "$_" -match "refs/heads/$([regex]::Escape($branch))$" }
    if ($similar) { Stop-Run "Branch exists only with different case: $(($similar -replace '.*refs/heads/', '') -join ', ')" 'Use that exact spelling in both policy files.' }
    Stop-Run "Branch '$branch' does not exist on the server." 'Create it in Azure DevOps (from main), then run again.'
}
Pass "refs/heads/$branch exists on the server"
if (-not (Test-Path (Join-Path $checkout '.git'))) {
    Info "First run: cloning $($repo.source) into $checkout (once; later runs only fetch)..."
    $gitOut = Invoke-AdoGit @('clone', '--quiet', '--no-checkout', $repo.source, $checkout); $gitCode = $LASTEXITCODE
    $gitOut | Out-File -FilePath $gitLog -Append -Encoding utf8
    if ($gitCode -ne 0) { Stop-Run "Clone failed: $(Get-GitReason $gitOut)" "Full output: $gitLog" }
}
$gitOut = Invoke-AdoGit @('-C', $checkout, 'fetch', '--quiet', '--prune', 'origin', "+refs/heads/$($branch):refs/remotes/origin/$branch"); $gitCode = $LASTEXITCODE
$gitOut | Out-File -FilePath $gitLog -Append -Encoding utf8
if ($gitCode -ne 0) { Stop-Run "Fetch of '$branch' failed: $(Get-GitReason $gitOut)" "Check ADO_READ_PAT and the network. Full output: $gitLog" }
$env:GIT_LFS_SKIP_SMUDGE = '1'
$gitOut = git -C $checkout checkout --quiet --force -B $branch "origin/$branch" 2>&1
$checkoutCode = $LASTEXITCODE
$gitOut | Out-File -FilePath $gitLog -Append -Encoding utf8
Remove-Item Env:\GIT_LFS_SKIP_SMUDGE -ErrorAction SilentlyContinue
if ($checkoutCode -ne 0) { Stop-Run "Could not check out '$branch' in ${checkout}: $(Get-GitReason $gitOut)" "Full output: $gitLog" }
$sha = (git -C $checkout rev-parse HEAD | Out-String).Trim()
if ($sha -notmatch '^[0-9a-f]{40}$') { Stop-Run "Could not read the commit of '$branch' in $checkout." "git -C `"$checkout`" log -1" }
Pass "Checkout at $($sha.Substring(0, 12)) (the commit DEFECT will clone)"
foreach ($arg in @($repo.recipes.test_file) + @($repo.recipes.suite)) {
    if ($arg -match '\.(sln|csproj)$' -and -not (Test-Path (Join-Path $checkout $arg))) { Stop-Run "The test recipe points at '$arg', which is not in the repository." 'Fix the path in the defect policy recipes (relative to the repository root).' }
}
Pass 'Test recipe paths exist in the repository'
if ($repo.runner.kind -eq 'docker') {
    # DEFECT starts its test container with no network, so the image must already be on this machine.
    docker image inspect $repo.runner.image *> $null
    if ($LASTEXITCODE -ne 0) { Stop-Run "The defect policy's runner image is not on this machine: $($repo.runner.image)" 'Build or pull that image (by exactly this name and digest), or use the local runner policy.' }
    Pass "Runner image present: $($repo.runner.image)"
}

# A recipe may call a test dispatcher script (e.g. run-tests.ps1, for a repository with .NET and Node tests).
# DEFECT runs it later, many times: check now that it and everything it needs are in place.
$dispatchers = @(@($repo.recipes.test_file) + @($repo.recipes.suite)) | ForEach-Object -Begin { $next = $false } -Process {
    if ($next) { $_; $next = $false } elseif ($_ -eq '-File') { $next = $true }
} | Select-Object -Unique
foreach ($d in $dispatchers) {
    $where = if ([IO.Path]::IsPathRooted($d)) { $d } else { Join-Path $checkout $d }   # DEFECT runs recipes inside the target repository
    if (-not (Test-Path $where)) { Stop-Run "The defect policy's recipes call $d, which does not exist ($where)." "Copy run-tests.ps1 there, or change the path in both recipes of $(Split-Path $defectFile -Leaf)." }
    Pass "Test dispatcher present: $d"
}
$runnerEnv = $repo.runner.env
if ($runnerEnv.VISIONIQ_DOTNET_TARGET) {
    if (-not (Test-Path (Join-Path $checkout $runnerEnv.VISIONIQ_DOTNET_TARGET))) { Stop-Run "VISIONIQ_DOTNET_TARGET '$($runnerEnv.VISIONIQ_DOTNET_TARGET)' is not in the repository." 'Fix runner.env.VISIONIQ_DOTNET_TARGET in the defect policy (relative to the repository root).' }
    Pass ".NET tests: $($runnerEnv.VISIONIQ_DOTNET_TARGET)"
}
if ($runnerEnv.VISIONIQ_NODE_DIR) {
    foreach ($tool in 'node', 'npm') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-Run "The Node tests need $tool, which is not on PATH." 'Install Node.js (it includes npm), then open a new PowerShell window.' } }
    $nodeDir = Join-Path $checkout $runnerEnv.VISIONIQ_NODE_DIR
    foreach ($f in 'package.json', 'package-lock.json') {
        if (-not (Test-Path (Join-Path $nodeDir $f))) { Stop-Run "$($runnerEnv.VISIONIQ_NODE_DIR)/$f is not in the repository." $(if ($f -eq 'package-lock.json') { 'The dispatcher installs dependencies from the lock file (npm ci); commit one, or remove the Node side from the policy.' } else { 'Fix runner.env.VISIONIQ_NODE_DIR in the defect policy.' }) }
    }
    $npmScript = if ($runnerEnv.VISIONIQ_NODE_SCRIPT) { $runnerEnv.VISIONIQ_NODE_SCRIPT } else { 'test:unit' }
    $scripts = (Get-Content (Join-Path $nodeDir 'package.json') -Raw | ConvertFrom-Json).scripts
    if (-not ($scripts -and $scripts.PSObject.Properties.Name -contains $npmScript)) { Stop-Run "package.json in $($runnerEnv.VISIONIQ_NODE_DIR) has no '$npmScript' script." "Set VISIONIQ_NODE_SCRIPT in the defect policy to its test script (it has: $(if ($scripts) { $scripts.PSObject.Properties.Name -join ', ' } else { 'none' }))." }
    if (-not $runnerEnv.VISIONIQ_NODE_CACHE) { Stop-Run 'VISIONIQ_NODE_CACHE is not set in the defect policy runner.env.' 'Set it to a folder outside any repository, e.g. C:/visioniq-tools/node-cache.' }
    Pass "Node tests: $($runnerEnv.VISIONIQ_NODE_DIR), npm script '$npmScript' ($(node --version)); dependencies cached in $($runnerEnv.VISIONIQ_NODE_CACHE)"
}
if ($repo.runner.kind -eq 'local') {
    Push-Location $checkout; $sdk = (dotnet --version 2>&1 | Out-String).Trim(); $sdkOk = $LASTEXITCODE -eq 0; Pop-Location
    if (-not $sdkOk) { Stop-Run "The .NET SDK required by the repository's global.json is not installed: $sdk" 'Install the SDK version named in its global.json.' }
    Pass "The repository's .NET SDK resolves on this machine ($sdk)"
}

$pickupState = @($pickupProject.pickup_states)[0]
$projectPath = [uri]::EscapeDataString($pickupProject.project)
if ($PreflightOnly -or -not $NoTrigger) {
    Step "6b. Ticket $TicketId in Azure DevOps (read only)"
    $wiPat = if ($TriggerPat) { $TriggerPat } else { Get-Setting 'ADO_COMMENT_PAT' }
    if (-not $wiPat) { Stop-Run 'No token that may change work items.' 'Set ADO_COMMENT_PAT (Work Items: Read & Write), or pass -TriggerPat <a person''s token>.' }
    $auth = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$wiPat")) }
    try { $me = (Invoke-RestMethod "$org/_apis/connectionData" -Headers $auth -TimeoutSec 30).authenticatedUser }
    catch { Stop-Run 'Azure DevOps rejected the token used to start the run.' 'Check ADO_COMMENT_PAT (or -TriggerPat).' }
    $meName = $me.properties.Account.'$value'
    # Pickup ignores changes made by VisionIQ's own bot identities: the move must come from a person.
    if (@($pickup.bot_identities) | Where-Object { $_ -and ($_ -ieq $me.id -or $_ -ieq $meName) }) {
        Stop-Run "The token belongs to a bot identity ($meName); pickup ignores changes made by bots." 'Pass -TriggerPat with a person''s token (Work Items: Read & Write), or move the ticket by hand.'
    }
    $wiUrl = "$org/$projectPath/_apis/wit/workitems/$TicketId"
    try { $wi = Invoke-RestMethod "$($wiUrl)?api-version=7.1" -Headers $auth -TimeoutSec 30 }
    catch { Stop-Run "Work item $TicketId could not be read." "Check that it exists in project '$($pickupProject.project)'." }
    $type = $wi.fields.'System.WorkItemType'; $state = $wi.fields.'System.State'
    $tags = @(($wi.fields.'System.Tags' -split ';') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if (@($pickupProject.defect_types) -notcontains $type) { Stop-Run "Work item $TicketId is a '$type', not one of the pickup policy's defect_types ($(@($pickupProject.defect_types) -join ', '))." 'Add the type to defect_types, or use a ticket of a listed type.' }
    if ($pickupProject.require_assignee -and -not $wi.fields.'System.AssignedTo') { Stop-Run "Work item $TicketId is not assigned; the pickup policy requires an assignee." 'Assign it in Azure DevOps, then run again.' }
    $blocked = @($tags | Where-Object { @($pickupProject.excluded_tags) -contains $_ })
    if ($blocked) { Stop-Run "Work item $TicketId has an excluded tag: $($blocked -join ', ')." 'Remove that tag, then run again.' }
    Pass "Ticket $TicketId is a '$type' in '$state', assigned, with no excluded tag; a move would be made by $meName"
}

if ($PreflightOnly) {
    Step '7. Preflight: knowledge base (read only, only if PostgreSQL is already running)'
    $up = @(docker compose ps --status running --services 2>$null)
    if ($up -contains 'postgres') {
        docker compose exec -T postgres pg_isready -U $dbUser *> $null
        if ($LASTEXITCODE -eq 0) {
            $script:dbReady = $true
            $t = Test-TicketReadable
            switch ($t.Status) {
                'ok' { Pass "Ticket $TicketId is synced and in the served search snapshot" }
                'not-indexed' { if ($SkipIndex) { Stop-Run "Ticket $TicketId is synced but not in the served search snapshot, and -SkipIndex was given." 'Run without -SkipIndex (it then indexes just this ticket).' } else { Warn "Ticket $TicketId is synced but not yet indexed: the run will index just this ticket at step 11b." } }
                'not-synced' { if ($SkipSync) { Stop-Run "Ticket $TicketId is not synced, and -SkipSync was given." 'Run without -SkipSync.' } else { Warn "Ticket $TicketId is not synced yet: the run's sync (step 9) should bring it in." } }
                'out-of-scope' { Stop-Run "Ticket $TicketId is synced but not readable under the scope '$($pickupProject.security_scope_id)'. Its access labels: $($t.Labels)" 'Set security_scope_id in the pickup policy to one of those labels.' }
            }
        } else { Info 'PostgreSQL is not ready: knowledge-base checks skipped.' }
    } else { Info 'PostgreSQL is not running: knowledge-base checks skipped (a real run starts it).' }
    Step 'PREFLIGHT PASSED'
    Write-Host '  Nothing was started, changed or written: no ticket move, nothing in Azure DevOps, no services, no migration.' -ForegroundColor Green
    Write-Host '  Only local files were written: this logs folder and the repository checkout under artifacts\repos\.' -ForegroundColor Green
    Write-Host "`nLogs: $logDir"
    Stop-Transcript | Out-Null
    exit 0
}

# ---------------------------------------------------------------------------------------------
Step '7. Local infrastructure (PostgreSQL, Pub/Sub emulator, migrations)'
docker compose stop intelligence *> $null   # the Docker Intelligence must not compete with the host one
docker compose up -d postgres pubsub 2>&1 | ForEach-Object { "  $_" }
if ($LASTEXITCODE -ne 0) { Stop-Run 'docker compose could not start postgres/pubsub.' 'docker compose logs postgres pubsub' }
$dbUser = Get-Setting 'DB_LOCAL_USER'; $dbName = Get-Setting 'DB_LOCAL_NAME'
$ready = $false
for ($i = 0; $i -lt 30; $i++) { docker compose exec -T postgres pg_isready -U $dbUser *> $null; if ($LASTEXITCODE -eq 0) { $ready = $true; break }; Start-Sleep 2 }
if (-not $ready) { Stop-Run 'PostgreSQL did not become ready.' 'docker compose logs postgres' }
Pass 'PostgreSQL ready'
$script:dbReady = $true
# Nothing is rebuilt, migrated or restarted under a run in progress. (A fresh database has no queue table yet:
# that read fails and counts as no run.)
# A run counts as in progress only while an Intelligence is listening, the only process that can be working on it
# here. With none, its runs are orphaned: setup goes ahead, and the restarted Intelligence picks them up once their
# lease expires (brief: up to 5 min, DEFECT: up to 1 hour). Only a missing queue table (a fresh database) counts as
# "no run": any other failure to read it stops, never assumed to be "no run".
$queueExists = Query "SELECT to_regclass('knowledge.analysis_requests') IS NOT NULL"
$busy = if ($queueExists -eq 't') { [int](Query "SELECT count(*) FROM knowledge.analysis_requests WHERE status = 'pending' AND available_at > now()") } else { 0 }
$listening = @(Get-NetTCPConnection -LocalPort $intelPort -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique |
    Where-Object { $p = Get-CimInstance Win32_Process -Filter "ProcessId = $_" -ErrorAction SilentlyContinue; $p -and $p.CommandLine -match 'uvicorn' -and $p.CommandLine -match 'app\.main:app' }).Count -gt 0
if ($busy -gt 0 -and $listening) {
    Stop-Run "$busy run(s) are in progress in the running Intelligence: this script does not rebuild, migrate or restart anything under a running run." 'Follow them with: .\run-visioniq-e2e.ps1 -WatchOnly   Run the full script again once they have finished.'
}
if ($busy -gt 0) { Warn "$busy run(s) are marked in progress, but no Intelligence is running: they are orphaned and resume after their lease once Intelligence starts (step 12)." }
if (-not (Test-NetConnection 127.0.0.1 -Port $pubsubPort.Published -InformationLevel Quiet -WarningAction SilentlyContinue)) { Stop-Run "Pub/Sub emulator is not listening on $($pubsubPort.Published)." 'docker compose logs pubsub' }
Pass "Pub/Sub emulator listening on $($pubsubPort.Published)"
Info 'Building images from this checkout (so they match the code)...'
docker compose build api db-migrate *> (Join-Path $logDir 'build.log')
if ($LASTEXITCODE -ne 0) { Stop-Run 'Docker build failed.' "See $logDir\build.log" }
# Intelligence itself runs on this machine; its image is only used by pubsub-init to create the topics.
$initImage = $compose.services.'pubsub-init'.image
docker image inspect $initImage *> $null
if ($LASTEXITCODE -ne 0) {
    docker compose build intelligence *>> (Join-Path $logDir 'build.log')
    if ($LASTEXITCODE -ne 0) { Stop-Run "Building $initImage failed." "See $logDir\build.log" }
}
docker compose run --rm db-migrate *> (Join-Path $logDir 'db-migrate.log')
if ($LASTEXITCODE -ne 0) { Stop-Run 'Database migration failed.' "See $logDir\db-migrate.log" }
Pass 'Images built; database migrations current'
$script:servicesStarted = $true

# ---------------------------------------------------------------------------------------------
Step '8. Pub/Sub topics and subscriptions (from contracts\events\topology.json)'
# Each subscription pushes to its consumer: the API in Docker (by its compose service name), Intelligence on this
# machine (Docker reaches the host as host.docker.internal).
$pushBase = @{ 'api' = "http://api:$($apiPort.Target)"; 'intelligence' = "http://host.docker.internal:$intelPort" }
$env:EVENTS_PUSH_BASE_URL_API = $pushBase['api']
$env:EVENTS_PUSH_BASE_URL_INTELLIGENCE = $pushBase['intelligence']
docker compose run --rm --no-deps pubsub-init *> (Join-Path $logDir 'pubsub-init.log')
if ($LASTEXITCODE -ne 0) { Stop-Run 'pubsub-init failed.' "See $logDir\pubsub-init.log" }
$expected = @{}
foreach ($event in $topology.events) {
    if (-not $pushBase.ContainsKey($event.consumer)) { Stop-Run "topology.json names an unknown consumer '$($event.consumer)'." '' }
    $expected[$event.subscription] = $pushBase[$event.consumer] + $topology.push_path
}
$env:VIQ_EXPECTED_SUBSCRIPTIONS = ($expected | ConvertTo-Json -Compress)
$env:PUBSUB_EMULATOR_HOST = "127.0.0.1:$($pubsubPort.Published)"
# pubsub-init never changes an existing subscription, so a stale push target is corrected here.
$fixSubs = @'
import json, os
from google.cloud import pubsub_v1
project = "projects/" + os.environ["PUBSUB_PROJECT_ID"]
want = json.loads(os.environ["VIQ_EXPECTED_SUBSCRIPTIONS"])
client = pubsub_v1.SubscriberClient()
found = {s.name.rsplit("/", 1)[-1]: s for s in client.list_subscriptions(request={"project": project})}
bad = 0
for name, endpoint in want.items():
    sub = found.get(name)
    if sub is None:
        print(f"  MISSING {name}"); bad += 1; continue
    if sub.push_config.push_endpoint != endpoint:
        client.modify_push_config(request={"subscription": sub.name, "push_config": {"push_endpoint": endpoint}})
        print(f"  FIXED   {name}: {sub.push_config.push_endpoint} -> {endpoint}")
    else:
        print(f"  OK      {name} -> {endpoint}")
raise SystemExit(1 if bad else 0)
'@
$fixSubs | & $py - 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { Stop-Run 'A required subscription is missing or the check failed.' "See $logDir\pubsub-init.log" }
Pass 'Every subscription pushes to the right place'

# ---------------------------------------------------------------------------------------------
$env:PYTHONPATH = Join-Path $root 'src\VisionIQ.Intelligence'
$env:DB_HOST = 'localhost'                 # host processes reach PostgreSQL through its published port
$env:DB_PORT = [string]$dbPort.Published
$env:EVENTS_DISPATCH_ENABLED = 'true'      # Compose sets this inside containers; the host Intelligence needs it too

if ($SkipSync) { Step '9. LEARN sync: skipped (-SkipSync)' } else {
    $syncArgs = @('run', '--project', 'src/VisionIQ.Ingestor.Cli', '--', 'sync', '--source', 'all')
    if ($FullSync) { $syncArgs += '--full' }
    Step "9. LEARN sync ($(if ($FullSync) { 'FULL backfill - can take hours' } else { 'incremental' }))"
    $syncLog = Join-Path $logDir 'sync.log'
    & dotnet @syncArgs 2>&1 | Tee-Object -FilePath $syncLog
    $code = $LASTEXITCODE
    $failed = Select-String -Path $syncLog -Pattern '\[(NotSynced|Partial)\]'
    if ($code -ne 0 -or $failed) {
        $failed | ForEach-Object { Write-Host "  $($_.Line.Trim())" -ForegroundColor Red }
        if (Select-String -Path $syncLog -Pattern 'invalid_grant|invalid_rapt' -Quiet) { Stop-Run 'Google login expired during the sync.' 'gcloud auth application-default login, then run again.' }
        Stop-Run 'LEARN sync did not complete for every stream.' "See $syncLog"
    }
    Pass 'All streams synced'
}

if ($SkipScan) { Step '10. Code scan: skipped (-SkipScan)' } else {
    Step "10. Code and dependency scan (LRN-06/07) of '$branch' at $($sha.Substring(0, 12))"
    & $py -m app.code_intelligence.ingest_cli --repo $checkout --cache (Join-Path $root "artifacts\repos\$($repo.repository_id).scan-cache.json") `
        --project-id $pickupProject.project --repository-id $repo.repository_id --source-url $repo.source `
        --ado-source-instance $org 2>&1 | Tee-Object -FilePath (Join-Path $logDir 'scan.log')
    if ($LASTEXITCODE -ne 0) { Stop-Run 'Code scan failed.' "See $logDir\scan.log" }
    Pass 'Code scan finished'
}

if ($SkipIndex) { Step '11. Indexing: skipped (-SkipIndex)' } else {
    Step "11. Search index (partial backfill, up to $IndexLimit items, real Vertex embeddings)"
    $indexLog = Join-Path $logDir 'index.log'
    & $py -m app.knowledge.index_cli --allow-partial --limit $IndexLimit 2>&1 | Tee-Object -FilePath $indexLog
    if ($LASTEXITCODE -ne 0) {
        # index_cli exits 1 when ANY item failed, even if the new snapshot was published. Only "no snapshot
        # published" is a failure; a few failed items are reported and retried on a later pass.
        $finished = Select-String -Path $indexLog -Pattern 'index\.finished' -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($finished -and $finished.Line -match 'snapshot_status=published') {
            $failedCount = if ($finished.Line -match '\bfailed=(\d+)') { $Matches[1] } else { 'some' }
            Warn "Indexing published a new snapshot, but $failedCount item(s) failed (see index.log); they are retried on a later pass."
        } else {
            Stop-Run 'Indexing failed: no new snapshot was published.' "See $indexLog"
        }
    }
}
$status = (& $py -m app.knowledge.index_cli status 2>&1 | Out-String).Trim()
$status -split "`n" | ForEach-Object { Info $_ }
if ($status -notmatch '(?m)^served: \S') { Stop-Run 'Could not read the index status.' 'Run: python -m app.knowledge.index_cli status' }
if ($status -match '(?m)^served: none') { Stop-Run 'No search snapshot is served: BRIEF would have nothing to cite.' 'Run again without -SkipIndex.' }
Pass 'A search snapshot is served (partial backfill: not a claim of full coverage)'

# ---------------------------------------------------------------------------------------------
# BRIEF needs the ticket twice (history.py load_ticket): first in the evidence store (filled by the sync), then
# its text in the SERVED search snapshot (tools.py fetch_authorized_source), both under the pickup policy's access
# scope. If either is missing, the brief ends BLOCKED ("not in the knowledge base").
Step "11b. Ticket $TicketId readable by BRIEF (synced and indexed, under scope '$($pickupProject.security_scope_id)')"
$t = Test-TicketReadable
if ($t.Status -eq 'not-synced') {
    if ($SkipSync) { Stop-Run "Ticket $TicketId is not in the evidence store, and -SkipSync was given: nothing was synced." 'Run again without -SkipSync.' }
    Stop-Run "Ticket $TicketId is not in the evidence store, so its brief would be BLOCKED (not in the knowledge base)." "Check it is in project '$($pickupProject.project)' (ADO_PROJECTS)$(if (Get-Setting 'INGESTION_SINCE') { " and changed after INGESTION_SINCE ($(Get-Setting 'INGESTION_SINCE'))" }); then run again."
}
if ($t.Status -eq 'out-of-scope') { Stop-Run "Ticket $TicketId is synced but not readable under the scope '$($pickupProject.security_scope_id)'. Its access labels: $($t.Labels)" 'Set security_scope_id in the pickup policy to one of those labels.' }
Pass "Ticket $TicketId is in the evidence store and readable under the policy's scope"
if ($t.Status -eq 'not-indexed') {
    if ($SkipIndex) { Stop-Run "Ticket $TicketId is not in the served search snapshot, and -SkipIndex was given: nothing was indexed." 'Run again without -SkipIndex (it then indexes just this ticket).' }
    Info 'Not in the served search snapshot yet: indexing this ticket only (a targeted pass publishes without the coverage threshold)...'
    & $py -m app.knowledge.index_cli --only $t.ItemKey 2>&1 | Tee-Object -FilePath (Join-Path $logDir 'index-ticket.log')
    if ($LASTEXITCODE -ne 0) { Stop-Run "Indexing ticket $TicketId failed." "See $logDir\index-ticket.log" }
    if ((Test-TicketReadable).Status -ne 'ok') { Stop-Run "Ticket $TicketId is still not in the served search snapshot, so its brief would be BLOCKED." "See $logDir\index-ticket.log, and: python -m app.knowledge.index_cli status" }
}
Pass "Ticket $TicketId is in the served search snapshot"

# ---------------------------------------------------------------------------------------------
Step "12. Intelligence on this machine (port $intelPort)"
# Intelligence loads its Google credential once, at start-up (google.auth.default in the embedder and the Gemini
# provider). A restart picks up the current login, but it also stops any run in progress: that run is picked up
# again only after its lease (brief: up to 5 min; DEFECT: up to 1 hour) and uses one of its attempts. So:
#   no run in progress (Intelligence's queue)   -> restart (a fresh login costs nothing)
#   run in progress, login older than process   -> keep it (its credential is current)
#   run in progress, login renewed after start  -> STOP and say so: a process with runs in progress is never stopped
$intelReady = "http://127.0.0.1:$intelPort/ready"
# The login file Google's libraries use: an explicit key file, else the one 'gcloud auth application-default login' writes.
$adcFile = if ($env:GOOGLE_APPLICATION_CREDENTIALS) { $env:GOOGLE_APPLICATION_CREDENTIALS } elseif ($env:APPDATA) { Join-Path $env:APPDATA 'gcloud\application_default_credentials.json' } else { $null }
$keepIntelligence = $false
$holders = @(Get-NetTCPConnection -LocalPort $intelPort -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
# The process this script started last time is recorded here (process id and start time, so a reused id is not
# mistaken for it). Any other Intelligence is stopped only after you agree.
$intelRecord = Join-Path $root 'artifacts\e2e-run\intelligence-process.json'
$recorded = $null; try { $recorded = Get-Content $intelRecord -Raw -ErrorAction Stop | ConvertFrom-Json } catch { }
foreach ($holder in $holders) {
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $holder" -ErrorAction SilentlyContinue
    if (-not ($proc -and $proc.CommandLine -match 'uvicorn' -and $proc.CommandLine -match 'app\.main:app')) {
        Stop-Run "Port $intelPort is used by another program ($(if ($proc) { $proc.Name } else { "process $holder" }))." 'Close that program, or set a free PYTHON_API_PORT in .env.'
    }
    $holderStart = (Get-Process -Id $holder -ErrorAction SilentlyContinue).StartTime
    $ours = $recorded -and [int]$recorded.pid -eq [int]$holder -and $holderStart -and $recorded.started -eq $holderStart.ToString('o')
    $inProgress = 0
    # Claimed runs have available_at pushed into the future by their lease (runs waiting to retry likewise);
    # a run that never reached Intelligence is not counted, so it cannot keep a stale process alive.
    [int]::TryParse((Query "SELECT count(*) FROM knowledge.analysis_requests WHERE status = 'pending' AND available_at > now()"), [ref]$inProgress) | Out-Null
    $started = (Get-Process -Id $holder -ErrorAction SilentlyContinue).StartTime
    $login = if ($adcFile) { (Get-Item $adcFile -ErrorAction SilentlyContinue).LastWriteTime } else { $null }
    $stale = $started -and $login -and $login -gt $started
    if ($inProgress -gt 0) {
        if ($stale) {
            Stop-Run "Intelligence (process $holder) has $inProgress run(s) in progress but started before the last Google login, so they may fail. A process with runs in progress is never stopped." 'Let them finish (follow them with -WatchOnly), then run again; or close its window yourself.'
        }
        # Normally the guard in step 7 has already stopped the script when runs are in progress; this remains for a
        # run that started between step 7 and here.
        $keepIntelligence = $true
        Pass "Kept the running Intelligence (process $holder): $inProgress run(s) in progress, and its Google login is current. Its output goes to the log folder of the run that started it."
        if (-not $started -or -not $login) { Warn 'Could not compare its start time with the Google login file; if model calls fail with RefreshError, close it after its runs finish and run again.' }
        # It keeps the settings it started with: say so when .env or the defect policy changed since.
        $newer = @((Join-Path $root '.env'), $defectFile | Where-Object { $started -and (Get-Item $_ -ErrorAction SilentlyContinue).LastWriteTime -gt $started } | ForEach-Object { Split-Path $_ -Leaf })
        if ($newer) { Warn "It still uses the settings it started with: $($newer -join ' and ') changed since. Once the run in progress has finished, close its window and run again to apply them." }
    } else {
        if (-not $ours -and -not $script:mayStopForeign) { Stop-Run "Port $intelPort is held by a VisionIQ Intelligence this script did not start (process $holder)." 'Close it yourself, or run again with -AllowStopIntelligence.' }
        Info "Restarting the earlier Intelligence (process $holder) with the current Google login (no run in progress)."
        Stop-Process -Id $holder -Force -ErrorAction SilentlyContinue
    }
}
if ($holders -and -not $keepIntelligence) {
    $freeBy = (Get-Date).AddSeconds(30)
    while ((Get-NetTCPConnection -LocalPort $intelPort -State Listen -ErrorAction SilentlyContinue) -and (Get-Date) -lt $freeBy) { Start-Sleep -Seconds 1 }
    if (Get-NetTCPConnection -LocalPort $intelPort -State Listen -ErrorAction SilentlyContinue) { Stop-Run "Port $intelPort did not become free." 'Close the old VisionIQ Intelligence window, then run again.' }
    Info "Its old window stays open with an exit message; you can close it."
}
if (-not $keepIntelligence) {
# Intelligence runs on this machine, so it gets the policy's path here, not a container path (/app/config/...).
$env:DEFECT_POLICY_FILE = $defectFile
# DEFECT writes each test execution's log under <dir>\<run id>\ while it runs (the database gets everything only at
# the end): the watch uses these files as DEFECT's sign of life.
$env:DEFECT_ARTIFACT_DIR = Join-Path $root 'artifacts\defect-runs'
# 0.0.0.0, not PYTHON_API_HOST: Docker can only push events to a host process that listens beyond loopback.
# Its output also goes to intelligence.log in this run's folder (model calls, DEFECT test runs, errors).
$intelLog = Join-Path $logDir 'intelligence.log'
$env:PYTHONUNBUFFERED = '1'
$rootQ = $root -replace "'", "''"; $pyQ = $py -replace "'", "''"; $intelLogQ = $intelLog -replace "'", "''"   # single-quoted below
$launch = "Set-Location '$rootQ'; `$host.UI.RawUI.WindowTitle = 'VisionIQ Intelligence'; & '$pyQ' -m uvicorn app.main:app --host 0.0.0.0 --port $intelPort 2>&1 | ForEach-Object { `$line = `"`$_`"; `$line; Add-Content -Path '$intelLogQ' -Value `$line -Encoding UTF8 }"
Start-Process powershell -ArgumentList '-NoExit', '-Command', $launch -WorkingDirectory $root | Out-Null
if (-not (Wait-Http $intelReady 120)) { Stop-Run 'Intelligence did not become ready within 2 minutes.' "Read the error in the 'VisionIQ Intelligence' window." }
# Record the process this script started, so a later run may restart it without asking.
$newPid = @(Get-NetTCPConnection -LocalPort $intelPort -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)[0]
$newStart = if ($newPid) { (Get-Process -Id $newPid -ErrorAction SilentlyContinue).StartTime } else { $null }
if ($newPid -and $newStart) { @{ pid = [int]$newPid; started = $newStart.ToString('o'); artifactDir = $env:DEFECT_ARTIFACT_DIR } | ConvertTo-Json | Out-File -FilePath $intelRecord -Encoding utf8 }
Pass "Intelligence ready with the current Google login. Keep the 'VisionIQ Intelligence' window open: model calls and DEFECT test runs show there."
}
Warn "Intelligence listens on all network interfaces (Docker needs that) and has no login locally. On a shared network, allow port $intelPort only from Docker: New-NetFirewallRule -DisplayName 'VisionIQ Intelligence (local Pub/Sub push)' -Direction Inbound -Protocol TCP -LocalPort $intelPort -Action Allow -Profile Any -RemoteAddress 172.16.0.0/12,192.168.65.0/24 (as Administrator); do not allow it for all networks when Windows asks."

# The emulator pushes requests from inside Docker to this machine. Prove that exact network path now,
# instead of discovering at the end that every push was silently retried (the Windows firewall can block it).
$probeUrl = "http://host.docker.internal:$intelPort/ready"
# The URL goes in as a separate argument, so no nested quoting is needed. python3 ships with gcloud in the
# emulator image; curl is the fallback.
$probe = (docker compose exec -T pubsub python3 -c "import sys, urllib.request; print(urllib.request.urlopen(sys.argv[1], timeout=5).status)" $probeUrl 2>&1 | Out-String).Trim()
if ($probe -notmatch '^200$') { $probe = (docker compose exec -T pubsub curl -sS -o /dev/null -w "%{http_code}" --max-time 5 $probeUrl 2>&1 | Out-String).Trim() }
if ($probe -notmatch '(^|\s)200$') {
    Stop-Run "The Pub/Sub emulator cannot reach Intelligence at $probeUrl, so no request would ever arrive." "As Administrator: New-NetFirewallRule -DisplayName 'VisionIQ Intelligence (local Pub/Sub push)' -Direction Inbound -Protocol TCP -LocalPort $intelPort -Action Allow -Profile Any -RemoteAddress 172.16.0.0/12,192.168.65.0/24 ; then run again with -ReuseServices."
}
Pass "The emulator reaches Intelligence on this machine ($probeUrl)"

Step "13. API in Docker (port $($apiPort.Published))"
$env:PICKUP_POLICY_FILE = "$apiConfigMount/" + (Split-Path $pickupFile -Leaf)   # path inside the API container
$apiReady = "http://127.0.0.1:$($apiPort.Published)/ready"
$apiCurrent = $false
if ($ReuseServices -and (Wait-Http $apiReady 1)) {
    $apiImage = $compose.services.api.image
    $builtId = (docker image inspect --format '{{.Id}}' $apiImage 2>$null | Out-String).Trim()
    $containerId = (docker compose ps -q api 2>$null | Out-String).Trim()
    $runningId = if ($containerId) { (docker inspect --format '{{.Image}}' $containerId 2>$null | Out-String).Trim() } else { '' }
    $startedText = if ($containerId) { (docker inspect --format '{{.State.StartedAt}}' $containerId 2>$null | Out-String).Trim() } else { '' }
    $apiStarted = [datetime]::MinValue; [datetime]::TryParse($startedText, [ref]$apiStarted) | Out-Null
    $changed = @((Join-Path $root '.env'), $pickupFile | Where-Object { (Get-Item $_ -ErrorAction SilentlyContinue).LastWriteTime.ToUniversalTime() -gt $apiStarted.ToUniversalTime() } | ForEach-Object { Split-Path $_ -Leaf })
    if (-not $builtId -or $runningId -ne $builtId) { Info 'The running API is not the image just built from this checkout: recreating it.' }
    elseif ($changed) { Info "The running API started before $($changed -join ' and ') changed: recreating it." }
    else { $apiCurrent = $true; Pass 'API already running the current image and settings (-ReuseServices): kept as is' }
}
if (-not $apiCurrent) {
    docker compose up -d --no-deps --force-recreate api 2>&1 | ForEach-Object { "  $_" }
    if ($LASTEXITCODE -ne 0) { Stop-Run 'The API container did not start.' 'docker compose logs api' }
    if (-not (Wait-Http $apiReady 120)) { Stop-Run 'API did not become ready within 2 minutes.' 'docker compose logs --tail 100 api' }
    Pass 'API ready'
}

# ---------------------------------------------------------------------------------------------
$startedAt = (Get-Date).ToUniversalTime().AddMinutes(-2).ToString('yyyy-MM-ddTHH:mm:ssZ')   # 2 min margin for clock drift
Step "14. Start the run: move ticket $TicketId to '$pickupState'"
if ($NoTrigger) { Info 'Skipped (-NoTrigger). Watching the latest run.' } else {
    # Returns the updated work item, or $null (the HTTP status is kept in $script:stateError).
    function Set-State([string]$value) {
        $body = ConvertTo-Json -InputObject @(@{ op = 'add'; path = '/fields/System.State'; value = $value })
        try { return Invoke-RestMethod -Method Patch -Uri "$($wiUrl)?api-version=7.1" -Headers $auth -ContentType 'application/json-patch+json' -Body $body -TimeoutSec 30 }
        catch {
            $script:stateError = 0; try { $script:stateError = [int]$_.Exception.Response.StatusCode } catch { }
            return $null
        }
    }
    # The state now (step 6b read it, but minutes may have passed).
    try { $state = (Invoke-RestMethod "$($wiUrl)?api-version=7.1" -Headers $auth -TimeoutSec 30).fields.'System.State' }
    catch { Stop-Run "Work item $TicketId could not be read just before moving it. The ticket was not changed." 'Check the network and ADO_COMMENT_PAT, then run again.' }
    # A run starts only on a move INTO the pickup state, so a ticket already there is moved out first,
    # to the type's own "Proposed" state as defined in Azure DevOps (unless -ResetState names one).
    $fromState = $state
    if (@($pickupProject.pickup_states) -contains $state) {
        $outState = $ResetState
        if (-not $outState) {
            try {
                $states = (Invoke-RestMethod "$org/$projectPath/_apis/wit/workitemtypes/$([uri]::EscapeDataString($type))/states?api-version=7.1" -Headers $auth -TimeoutSec 30).value
                $outState = (@($states) | Where-Object { $_.category -eq 'Proposed' -and @($pickupProject.pickup_states) -notcontains $_.name } | Select-Object -First 1).name
            } catch { }
        }
        if (-not $outState) { Stop-Run "Ticket $TicketId is already '$state' and no 'Proposed' state was found for a $type. The ticket was not changed." 'Pass -ResetState <a state outside pickup_states>.' }
        Info "Ticket is already '$state': moving it to '$outState' first, so the move into '$pickupState' is a new event."
        if (-not (Set-State $outState)) { Stop-Run "Azure DevOps refused to set '$outState' (HTTP $script:stateError). The ticket was not changed." "The token needs Work Items: Read & Write, and '$outState' must be a valid state for a $type." }
        $fromState = $outState
        Start-Sleep -Seconds 3
    }
    $moveAt = (Get-Date).ToUniversalTime().AddSeconds(-5)   # a few seconds' margin for clock differences
    $after = Set-State $pickupState
    $triggeredAt = Get-Date
    if (-not $after) { Start-Sleep -Seconds 5; $after = Set-State $pickupState }   # one retry: usually a network blip
    if (-not $after) {
        if ($fromState -ne $state) {
            # The ticket was moved out of the pickup state above and could not be moved back in.
            Stop-Run "The ticket is now in '$fromState' (it was '$state'): moving it back to '$pickupState' failed twice (HTTP $script:stateError)." "In Azure DevOps, move ticket $TicketId to '$pickupState' (that also starts the run), then follow it with: .\run-visioniq-e2e.ps1 -WatchOnly"
        }
        Stop-Run "Azure DevOps refused to set '$pickupState' (HTTP $script:stateError). The ticket was not changed." "The token needs Work Items: Read & Write, and '$pickupState' must be a valid state for a $type."
    }
    Pass "Ticket $TicketId moved '$fromState' -> '$pickupState' (revision $($after.rev)) by $meName"
    Info "Pickup sweep: first run about 30 s after the API started, then every $sweep min. Nothing else to do: watch below."
}
Info "Ticket: $org/$projectPath/_workitems/edit/$TicketId"
if ($NoWatch) { Save-Diagnostics; Write-Host "`nLogs: $logDir"; Stop-Transcript | Out-Null; exit 0 }
}   # end of the setup (not -WatchOnly)

# ---------------------------------------------------------------------------------------------
# Which run to follow: the first brief run after the ticket moved, or (-NoTrigger, -WatchOnly) the ticket's latest
# brief run. Once it is known, everything is followed by its trace id, which the DEFECT run started from that brief
# shares (FixRunStarter reuses it): another run of the same ticket can never mix in.
$since = if ($moveAt) { "timestamptz '$($moveAt.ToString('yyyy-MM-ddTHH:mm:ssZ'))'" } elseif ($NoTrigger -or $WatchOnly) {
    "(SELECT coalesce(max(requested_at), now()) FROM workflow.analysis_runs WHERE ticket_id = '$TicketId' AND analysis_mode = 'brief')"
} else { "timestamptz '$startedAt'" }
$traceId = $null
if ($WatchOnly) { $runStartIso = (Get-Date).ToUniversalTime().AddHours(-12).ToString('yyyy-MM-ddTHH:mm:ssZ') }   # api.log covers the run's earlier lines too
function Get-RunScope { if ($traceId) { "r.trace_id = '$traceId'" } else { "r.ticket_id = '$TicketId' AND r.requested_at >= $since" } }
function Get-Checks {
    $scope = Get-RunScope
    [ordered]@{
        'Runs (brief = BRIEF, deep = DEFECT)' = "SELECT r.analysis_mode, r.status, r.ticket_kind, r.trigger_source, r.requested_at, r.completed_at, r.run_id FROM workflow.analysis_runs r WHERE $scope ORDER BY r.requested_at"
        'Brief'                               = "SELECT b.state, b.risk_band, b.reason_codes, b.snapshot_id FROM knowledge.briefs b JOIN workflow.analysis_runs r ON r.run_id = b.run_id WHERE $scope ORDER BY b.created_at"
        'Brief on the ticket'                 = "SELECT p.mode, p.status, p.comment_id, p.published_at FROM workflow.brief_publications p JOIN workflow.analysis_runs r ON r.run_id = p.run_id WHERE $scope ORDER BY p.created_at"
        'Model calls (real Gemini / Claude)'  = "SELECT c.task, c.provider, c.model_id, c.status, count(*), sum(c.input_tokens), sum(c.output_tokens) FROM knowledge.llm_calls c JOIN workflow.analysis_runs r ON r.run_id = c.run_id WHERE $scope GROUP BY 1, 2, 3, 4 ORDER BY 1"
        'Draft PR'                            = "SELECT f.mode, f.status, f.result_state, f.pull_request_id, f.source_branch FROM workflow.fix_pull_requests f JOIN workflow.analysis_runs r ON r.run_id = f.run_id WHERE $scope"
    }
}
Step "15. Watching ticket $TicketId (every 20 s, up to $WatchMinutes min; Ctrl+C to stop)"
$last = @{}
$deadline = (Get-Date).AddMinutes($WatchMinutes)
$defectExpected = (Get-Setting 'DEFECT_PIPELINE_ENABLED') -eq 'true' -and (Get-Setting 'FIX_AUTO_START') -eq 'true'
$deepDoneWithoutPr = 0
$searchWarned = $false
$briefDoneAt = $null
$noRunLimit = 2 * $sweep + 1   # minutes: two sweeps plus the first one's start-up delay
$queryFailures = 0
$result = $null                # @{ Code = 0 | 10 | 20 | 30 | 40 | 50 (see the help); Text = ... }
# ---- stuck-run detection: signs of life, and what to say when there are none ----
# VisionIQ's default is 1800 s. TryParse sets its output to 0 when it fails (e.g. the setting is not in .env), so
# the value is parsed into a separate variable and used only when it is a real, positive number.
$defectBudget = 1800.0; $parsed = 0.0
if ([double]::TryParse([string](Get-Setting 'DEFECT_MAX_SECONDS_PER_RUN'), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -and $parsed -gt 0) { $defectBudget = $parsed }
# As in VisionIQ's code (defect/worker.py): a claimed attempt holds the run for lease_seconds() =
# max(LEASE_SECONDS 3600, budget + LEASE_MARGIN_SECONDS 900); a failed one is retried after at most 600 s
# (retry_delay_seconds). So more than 600 s left on available_at means an attempt is running, and it started one
# lease before available_at. Update these if the worker's constants change.
$deepLease = [math]::Max(3600, [int]$defectBudget + 900); $maxRetryDelay = 600
# Where the Intelligence being watched writes DEFECT's test logs: known if this script started it (now, or in an
# earlier run that recorded it); otherwise unknown, and said so rather than shown as "no test logs".
$artifactRoot = $null
if ($env:DEFECT_ARTIFACT_DIR -and -not $WatchOnly -and -not $keepIntelligence) { $artifactRoot = $env:DEFECT_ARTIFACT_DIR }
else {
    try {
        $rec = Get-Content (Join-Path $root 'artifacts\e2e-run\intelligence-process.json') -Raw -ErrorAction Stop | ConvertFrom-Json
        $holder = @(Get-NetTCPConnection -LocalPort $intelPort -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)[0]
        if ($rec.artifactDir -and $holder -and [int]$rec.pid -eq [int]$holder) { $artifactRoot = $rec.artifactDir }
    } catch { }
}
if (-not $artifactRoot) { Info "DEFECT's test logs are not visible for this Intelligence (not started by this script version): DEFECT is judged by its time budget only." }
$intelLogNow = if ($intelLog -and (Test-Path $intelLog)) { $intelLog } else {
    (Get-ChildItem (Join-Path $root 'artifacts\e2e-run') -Recurse -Filter intelligence.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
$lastChangeAt = Get-Date; $lastHeartbeat = Get-Date; $lastSignature = $null; $seenAttempt = @{}; $intelDown = 0
function Get-Life {
    $scope = Get-RunScope
    $queue = Query -Tolerant "SELECT q.analysis_mode || ' ' || q.status || ' attempt ' || q.attempt_count || ' next ' || to_char(q.available_at AT TIME ZONE 'UTC', 'HH24:MI:SS') FROM knowledge.analysis_requests q JOIN workflow.analysis_runs r ON r.run_id = q.run_id WHERE $scope ORDER BY q.received_at"
    $unreceived = Query -Tolerant "SELECT r.analysis_mode || ' ' || floor(extract(epoch FROM now() - r.requested_at))::int FROM workflow.analysis_runs r WHERE $scope AND r.status = 'requested' AND NOT EXISTS (SELECT 1 FROM knowledge.analysis_requests q WHERE q.run_id = r.run_id)"
    # The pending DEFECT run: its id, its attempt number, and the seconds left until its available_at.
    $deep = Query -Tolerant "SELECT r.run_id || ' ' || q.attempt_count || ' ' || floor(extract(epoch FROM q.available_at - now()))::int FROM workflow.analysis_runs r JOIN knowledge.analysis_requests q ON q.run_id = r.run_id WHERE $scope AND r.analysis_mode = 'deep' AND r.status = 'requested' AND q.status = 'pending' ORDER BY r.requested_at DESC LIMIT 1"
    $deepId = $null; $attempt = 0; $left = 0; $running = $false; $attemptAge = 0
    if ($deep) {
        $deepId, $attempt, $left = $deep -split ' '; $attempt = [int]$attempt; $left = [int]$left
        $running = $left -gt $maxRetryDelay                       # claimed; otherwise waiting to be retried
        if ($running) { $attemptAge = $deepLease - $left }        # seconds since this attempt was claimed
    }
    $files = @(); $newest = $null
    if ($deepId -and $artifactRoot) { $files = @(Get-ChildItem (Join-Path $artifactRoot $deepId) -File -Recurse -ErrorAction SilentlyContinue); $newest = $files | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
    $logLength = if ($intelLogNow) { (Get-Item $intelLogNow -ErrorAction SilentlyContinue).Length } else { 0 }
    [pscustomobject]@{ Queue = $queue; Unreceived = $unreceived; DeepId = $deepId; Attempt = $attempt; Running = $running; AttemptAge = $attemptAge
        RetryIn = $(if ($deepId -and -not $running) { [math]::Max(0, $left) } else { 0 }); Files = $(if ($artifactRoot) { $files.Count } else { -1 })
        Bytes = ($files | Measure-Object Length -Sum).Sum; Newest = $newest; LogLength = $logLength }
}
function Get-Stage($life, [string]$runs) {
    if (-not $runs) { return 'waiting for pickup to create the run' }
    if ($life.Unreceived) { return "request sent, not yet received by Intelligence ($($life.Unreceived) s)" }
    if ($life.DeepId -and -not $life.Running) { return "DEFECT attempt $($life.Attempt) failed; retrying in $([int]($life.RetryIn / 60)) min" }
    if ($life.DeepId) {
        $logs = if ($life.Files -lt 0) { 'test logs not visible for this Intelligence' } else { "$($life.Files) test execution(s) finished$(if ($life.Newest) { ", latest $([int]((Get-Date) - $life.Newest.LastWriteTime).TotalMinutes) min ago" })" }
        return "DEFECT attempt $($life.Attempt) running for $([int]($life.AttemptAge / 60)) of its $([int]($defectBudget / 60)) min; $logs"
    }
    if ($life.Queue -match 'brief pending') { return "BRIEF in Intelligence ($(($life.Queue -split "`n" | Where-Object { $_ -match '^brief' }) -join '; '))" }
    if ($runs -match '(?m)^brief \| completed' -and $runs -notmatch '(?m)^deep ') { return 'BRIEF done; waiting for the DEFECT run to be requested' }
    return 'waiting for the next step'
}
function Show-Diagnosis($life, [string]$why) {
    Write-Host "`n  [DIAGNOSIS] $why" -ForegroundColor Red
    Write-Host "    Stage:         $(Get-Stage $life $last['Runs (brief = BRIEF, deep = DEFECT)'])"
    Write-Host "    Intelligence:  $(if (Wait-Http "http://127.0.0.1:$intelPort/ready" 2) { 'answers' } else { 'NOT answering' })    API: $(if (Wait-Http "http://127.0.0.1:$($apiPort.Published)/ready" 2) { 'answers' } else { 'NOT answering' })"
    Write-Host "    Work queue:    $(if ($life.Queue) { $life.Queue -replace "`n", '; ' } else { '(no rows: the run never reached Intelligence)' })"
    if ($life.Newest) {
        Write-Host "    Latest DEFECT test log: $($life.Newest.FullName)"
        Get-Content $life.Newest.FullName -Tail 8 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
    }
    if ($intelLogNow) {
        Write-Host "    Recent errors in $intelLogNow"
        Select-String -Path $intelLogNow -Pattern 'ERROR|Traceback|Exception|RefreshError|timed out|refused' -ErrorAction SilentlyContinue | Select-Object -Last 10 | ForEach-Object { Write-Host "      $($_.Line)" }
    }
    Write-Host '    Recent API warnings and errors:'
    docker compose logs --no-color --since $runStartIso api 2>&1 | Select-String -Pattern 'fail|error|warn' | Select-Object -Last 6 | ForEach-Object { Write-Host "      $($_.Line)" }
}
try {
while ((Get-Date) -lt $deadline) {
    if (-not $traceId) {
        $found = Query -Tolerant "SELECT r.trace_id || ' | ' || r.run_id FROM workflow.analysis_runs r WHERE r.ticket_id = '$TicketId' AND r.analysis_mode = 'brief' AND r.requested_at >= $since ORDER BY r.requested_at LIMIT 1"
        $parts = if ($found) { $found -split ' \| ' } else { @() }
        if ($parts.Count -eq 2 -and $parts[0] -match '^[A-Za-z0-9._:-]+$') { $traceId = $parts[0]; Info "Following brief run $($parts[1]) and the DEFECT run it starts (trace $traceId)." }
    }
    # A failed query here does not end the watch at once: a one-off hiccup is retried; 3 in a row stop it.
    $checks = Get-Checks
    $failed = $false; $failReason = $null
    foreach ($name in $checks.Keys) {
        $now = Query -Tolerant $checks[$name]
        if ($null -eq $now -and $script:lastQueryError) { $failed = $true; if (-not $failReason) { $failReason = $script:lastQueryError }; continue }
        if ($now -ne $last[$name]) {
            $last[$name] = $now
            Write-Host "`n  [$(Get-Date -Format HH:mm:ss)] $name" -ForegroundColor Cyan
            if (-not $now) { Write-Host '    (nothing yet)' } else { $now -split "`n" | ForEach-Object { Write-Host "    $_" } }
        }
    }
    if ($failed) {
        $queryFailures++
        Warn "A database query failed ($queryFailures of 3 in a row): $failReason"
        if ($queryFailures -ge 3) { $result = @{ Code = 40; Text = 'The database could not be read 3 times in a row; the run may still be going (follow it with -WatchOnly).' }; break }
        Start-Sleep -Seconds 20; continue
    }
    $queryFailures = 0
    $runs = $last['Runs (brief = BRIEF, deep = DEFECT)']
    # A brief written while a search was down is degraded: say so at once (e.g. RefreshError from an expired login).
    if ($last['Brief'] -and -not $searchWarned) {
        $down = Query -Tolerant "SELECT l FROM knowledge.briefs b JOIN workflow.analysis_runs r ON r.run_id = b.run_id CROSS JOIN LATERAL jsonb_array_elements_text(b.document->'limitations') AS l WHERE $(Get-RunScope) AND l ILIKE '%search unavailable%'"
        if ($down) {
            $searchWarned = $true
            Write-Host "`n  [DEGRADED BRIEF] Part of the search failed while the brief was written: $($down -replace "`n", '; ')" -ForegroundColor Red
            if ($down -match 'RefreshError') { Write-Host '  RefreshError = expired Google login in Intelligence. Run gcloud auth application-default login, then run this script again.' -ForegroundColor Red }
        }
    }
    # No run at all after the ticket moved: pickup ignored the change, or no sweep ran.
    if ($triggeredAt -and -not $runs -and (Get-Date) -gt $triggeredAt.AddMinutes($noRunLimit)) {
        Write-Host "`n  No run was created $noRunLimit min after the ticket moved. Usual causes: pickup ignored the change (bot identity, state, tag, allow-list) or the sweep is not running. API log lines about pickup:" -ForegroundColor Red
        docker compose logs --no-color --since $runStartIso api 2>&1 | Select-String -Pattern 'ickup' | Select-Object -Last 10 | ForEach-Object { Write-Host "    $($_.Line)" }
        $result = @{ Code = 20; Text = 'No run was created after the ticket moved.' }; break
    }
    # DEFECT expected after a completed brief, but no deep run appeared.
    if ($runs -match '(?m)^brief \| completed' -and -not $briefDoneAt) { $briefDoneAt = Get-Date }
    if ($defectExpected -and $briefDoneAt -and $runs -notmatch '(?m)^deep ' -and $last['Brief'] -notmatch '(?m)^BLOCKED' -and (Get-Date) -gt $briefDoneAt.AddMinutes(3)) {
        Write-Host "`n  The brief finished 3 min ago but no DEFECT run started. Check FIX_AUTO_START in the API, and that the ticket is classed as a defect (ticket_kind above). API log lines about fixes:" -ForegroundColor Red
        docker compose logs --no-color --since $runStartIso api 2>&1 | Select-String -Pattern 'fix|Fix' | Select-Object -Last 10 | ForEach-Object { Write-Host "    $($_.Line)" }
        $result = @{ Code = 20; Text = 'The brief finished but no DEFECT run started.' }; break
    }
    if ($last['Brief'] -match '(?m)^BLOCKED') { $result = @{ Code = 10; Text = 'The brief was BLOCKED (reason codes above): DEFECT does not start.' }; break }
    if ($runs -match '(?m)^brief \| completed \| enhancement') { $result = @{ Code = 0; Text = 'The ticket was classed as an enhancement: guidance is the outcome, DEFECT does not apply.' }; break }
    if (-not $defectExpected -and $runs -match '(?m)^brief \| completed') { $result = @{ Code = 0; Text = 'BRIEF finished; DEFECT is switched off (DEFECT_PIPELINE_ENABLED / FIX_AUTO_START).' }; break }
    # Column 2 of the Draft PR row is its status (pending, published, skipped, failed); case-sensitive on purpose,
    # because column 3 holds the result state in capitals (PUBLISHED) and must not be mistaken for it.
    if ($last['Draft PR'] -cmatch '(?m)^[a-z]* \| published \|') { $result = @{ Code = 0; Text = 'DEFECT finished with a draft pull request (or, with FIX_PR_MODE=record, a recorded one).' }; break }
    if ($last['Draft PR'] -cmatch '(?m)^[a-z]* \| skipped \|') { $result = @{ Code = 10; Text = 'DEFECT finished without a fix worth proposing: no pull request.' }; break }
    if ($last['Draft PR'] -cmatch '(?m)^[a-z]* \| failed \|') { $result = @{ Code = 20; Text = 'DEFECT finished, but creating the draft pull request failed.' }; break }
    if ($runs -match '(?m)^deep \| completed' -and -not $last['Draft PR']) { $deepDoneWithoutPr++ } else { $deepDoneWithoutPr = 0 }
    $life = Get-Life
    $signature = "$(($checks.Keys | ForEach-Object { $last[$_] }) -join '|')#$($life.Queue)#$($life.Files)/$($life.Bytes)#$($life.LogLength)"
    if ($signature -ne $lastSignature) { $lastSignature = $signature; $lastChangeAt = Get-Date }
    # Warn once per new attempt: only when a run's attempt number goes up.
    $rose = $false
    foreach ($line in @($life.Queue -split "`n" | Where-Object { $_ -match '^(\S+) \S+ attempt (\d+)' })) {
        $null = $line -match '^(\S+) \S+ attempt (\d+)'; $mode = $Matches[1]; $n = [int]$Matches[2]
        # A new attempt, or one already past the first when the watch first sees it (e.g. after a -WatchOnly restart).
        if (($seenAttempt.ContainsKey($mode) -and $n -gt $seenAttempt[$mode]) -or (-not $seenAttempt.ContainsKey($mode) -and $n -ge 2)) { $rose = $true }
        $seenAttempt[$mode] = $n
    }
    if ($rose) {
        Warn "A run failed and is being retried: $($life.Queue -replace "`n", '; ')"
        if ($intelLogNow) { Select-String -Path $intelLogNow -Pattern 'ERROR|Traceback|Exception|RefreshError' -ErrorAction SilentlyContinue | Select-Object -Last 5 | ForEach-Object { Write-Host "      $($_.Line)" -ForegroundColor Yellow } }
    }
    $pending = $life.Unreceived -or ($life.Queue -match 'pending')
    if ($pending -and -not (Wait-Http "http://127.0.0.1:$intelPort/ready" 2)) { $intelDown++ } else { $intelDown = 0 }
    if ($intelDown -ge 2) {
        Show-Diagnosis $life 'Intelligence is not running, but a run is waiting for it.'
        $result = @{ Code = 50; Text = 'STALLED: Intelligence is not running. Start it again (run the script without -WatchOnly); the run resumes after its lease (brief: up to 5 min, DEFECT: up to 1 hour).' }; break
    }
    $oldest = (($life.Unreceived -split "`n") | ForEach-Object { [int](($_ -split ' ')[-1]) } | Measure-Object -Maximum).Maximum
    if ($life.Unreceived -and $oldest -ge 180) {
        Show-Diagnosis $life 'A run was requested 3+ minutes ago but never reached Intelligence.'
        Write-Host '    Emulator log (pushes):'; docker compose logs --no-color --tail 15 pubsub 2>&1 | ForEach-Object { Write-Host "      $_" }
        $result = @{ Code = 50; Text = 'STALLED: the request never reached Intelligence (Pub/Sub push). Check the firewall rule for its port and the subscriptions (step 8).' }; break
    }
    # DEFECT can be quiet for a long time (a test execution's log is written only when it ends), so a running
    # attempt is judged by its own time budget, counted from its claim; a no-change limit would call it stuck.
    if ($life.DeepId -and $life.Running -and $life.AttemptAge -gt $defectBudget + 300) {
        Show-Diagnosis $life "DEFECT attempt $($life.Attempt) has run for $([int]($life.AttemptAge / 60)) min, past its $([int]($defectBudget / 60))-min budget."
        $result = @{ Code = 50; Text = 'STALLED: DEFECT is past its time budget. See the latest test log above and intelligence.log.' }; break
    }
    # The no-change limit is for the short stages (pickup, delivery, the brief), which are never quiet for long.
    if (((Get-Date) - $lastChangeAt).TotalMinutes -ge $StallMinutes -and ($pending -or -not $runs) -and -not $life.DeepId) {
        Show-Diagnosis $life "Nothing has changed for $StallMinutes min (database, work queue, Intelligence log)."
        $result = @{ Code = 50; Text = "STALLED: no sign of life for $StallMinutes min. The diagnosis above shows where it stopped." }; break
    }
    if (((Get-Date) - $lastChangeAt).TotalMinutes -ge 5 -and ((Get-Date) - $lastHeartbeat).TotalMinutes -ge 5) {
        $lastHeartbeat = Get-Date
        Info "[$(Get-Date -Format HH:mm:ss)] Still working: $(Get-Stage $life $runs). No visible change for $([int]((Get-Date) - $lastChangeAt).TotalMinutes) min ($(if ($life.DeepId) { 'DEFECT is judged by its time budget' } else { "stalled after $StallMinutes" }))."
    }
    if ($deepDoneWithoutPr -ge 6) { $result = @{ Code = 20; Text = "The DEFECT run completed but no PR record appeared (2 min). Check the 'VisionIQ Intelligence' window and api.log." }; break }
    Start-Sleep -Seconds 20
}
if (-not $result) { $result = @{ Code = 30; Text = "No outcome after $WatchMinutes min; the run may still be going. Keep following it with: .\run-visioniq-e2e.ps1 -WatchOnly" } }
} finally {
    # Also on Ctrl+C: keep the API and emulator logs and the ticket's database rows with this run.
    Save-Diagnostics
}

# One outcome report, each part separately: published, skipped and failed are different results. A value that
# cannot be read is shown as such; it never turns a finished run into a stop.
Step "16. Outcome for ticket $TicketId"
function Show([string]$label, [string]$value, [string]$color = 'Gray') { Write-Host ("  {0,-26} {1}" -f $label, $(if ($value) { $value } else { '(none)' })) -ForegroundColor $color }
function Read-Value([string]$sql) { $v = Query -Tolerant $sql; if ($null -eq $v -and $script:lastQueryError) { "(could not read: $($script:lastQueryError))" } else { $v } }
$scope = Get-RunScope
$briefRow = Read-Value "SELECT b.state || ' | risk ' || coalesce(b.risk_band, '-') || ' | reasons ' || coalesce(array_to_string(b.reason_codes, ', '), '') FROM knowledge.briefs b JOIN workflow.analysis_runs r ON r.run_id = b.run_id WHERE $scope ORDER BY b.created_at DESC LIMIT 1"
Show 'Brief' $briefRow $(if ($briefRow -match '^PUBLISHED') { 'Green' } elseif ($briefRow -match '^PARTIAL') { 'Yellow' } elseif ($briefRow) { 'Red' } else { 'Gray' })
$comment = Read-Value "SELECT p.mode || ' | ' || p.status || coalesce(' | comment ' || p.comment_id, '') || coalesce(' | ' || p.reason, '') FROM workflow.brief_publications p JOIN workflow.analysis_runs r ON r.run_id = p.run_id WHERE $scope ORDER BY p.created_at DESC LIMIT 1"
Show 'Comment on the ticket' $comment $(if ($comment -match '^ado \| published') { 'Green' } elseif ($comment -match '^record') { 'Yellow' } elseif ($comment) { 'Red' } else { 'Gray' })
$fixRow = Read-Value "SELECT f.state || ' | verdict ' || coalesce(f.verdict, '-') || ' | reproduced ' || coalesce(f.reproducibility, '-') || ' | gates ' || coalesce(f.gates_overall, '-') || ' | reasons ' || coalesce(array_to_string(f.reason_codes, ', '), '') FROM knowledge.fix_runs f JOIN workflow.analysis_runs r ON r.run_id = f.run_id WHERE $scope ORDER BY f.created_at DESC LIMIT 1"
Show 'DEFECT result' $fixRow $(if ($fixRow -match '^PUBLISHED') { 'Green' } elseif ($fixRow -match '^PARTIAL') { 'Yellow' } elseif ($fixRow) { 'Red' } else { 'Gray' })
$tests = Read-Value "SELECT string_agg(purpose || ' ' || outcome || ' x' || n, ', ' ORDER BY purpose) FROM (SELECT e.purpose, e.outcome, count(*) AS n FROM knowledge.fix_executions e JOIN knowledge.fix_runs f ON f.fix_id = e.fix_id JOIN workflow.analysis_runs r ON r.run_id = f.run_id WHERE $scope GROUP BY 1, 2) AS t"
Show 'Test runs' $tests
$pr = Read-Value "SELECT f.mode || ' | ' || f.status || ' | ' || f.result_state || coalesce(' | PR ' || f.pull_request_id, '') || coalesce(' | branch ' || f.source_branch, '') FROM workflow.fix_pull_requests f JOIN workflow.analysis_runs r ON r.run_id = f.run_id WHERE $scope ORDER BY f.created_at DESC LIMIT 1"
Show 'Draft pull request' $pr $(if ($pr -cmatch '^ado \| published') { 'Green' } elseif ($pr -cmatch '^record') { 'Yellow' } elseif ($pr) { 'Red' } else { 'Gray' })
Write-Host ''
if ($pr -cmatch '^ado \| published') { Write-Host "  A DRAFT pull request was created against '$branch'. Review it in Azure DevOps and leave it as a draft: VisionIQ never approves, completes or merges it." -ForegroundColor Green }
elseif ($pr -cmatch '^record') { Write-Host '  FIX_PR_MODE=record: the pull request was only recorded, nothing was created in Azure DevOps.' -ForegroundColor Yellow }
elseif ($pr -cmatch '\| skipped \|') { Write-Host '  No pull request: DEFECT did not reach a fix worth proposing. Its reasons are in the DEFECT result above.' -ForegroundColor Yellow }
elseif ($pr -cmatch '\| failed \|') { Write-Host '  Creating the draft pull request failed: see api.log in the logs folder.' -ForegroundColor Red }
if ($comment -match '^ado \| published') { Write-Host '  The brief is posted as a comment on the ticket.' -ForegroundColor Green }
$label = @{ 0 = 'SUCCESS'; 10 = 'BLOCKED'; 20 = 'FAILED'; 30 = 'TIMEOUT'; 40 = 'UNREADABLE'; 50 = 'STALLED' }[[int]$result.Code]
Write-Host "`n  Result: $label (exit $($result.Code)): $($result.Text)" -ForegroundColor $(if ($result.Code -eq 0) { 'Green' } elseif ($result.Code -eq 20) { 'Red' } else { 'Yellow' })
Write-Host "`nLogs: $logDir  (run.log, api.log, pubsub.log, intelligence.log, db-state.txt, settings-masked.txt, ...)"
Stop-Transcript | Out-Null
exit [int]$result.Code
} finally {
    Save-Diagnostics
    try { Stop-Transcript | Out-Null } catch { }   # already closed after a normal end or a stop
}
