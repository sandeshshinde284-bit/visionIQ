<#
.SYNOPSIS
  VisionIQ DEFECT test dispatcher: one defect-policy recipe for a repository that has .NET tests and
  Node (e.g. Vue + vitest) tests.

.DESCRIPTION
  DEFECT allows one test recipe per repository. The policy's recipes call this script, which picks the
  test runner from the test file and, for the whole suite, runs both and writes one report.

  It runs in the commit DEFECT exported (the current directory is the repository root), started by
  the LOCAL runner. Nothing here names a project, and by default nothing in the policy has to either:
  the script finds what to run from the repository itself.

    one test file   *.cs  -> the nearest *.csproj above the file (else the nearest *.sln)
                    *.ts, *.vue, ... -> the nearest package.json above the file
    whole suite     every *.sln in the repository (a *.csproj is used only where no *.sln covers it),
                    and every folder whose package.json has the test script and a package-lock.json

  The policy's runner.env may still set these; a value that is set wins over what is found:

    VISIONIQ_DOTNET_TARGET  solution or project for `dotnet test`, relative to the repository root
    VISIONIQ_NODE_DIR       folder holding package.json, relative to the repository root
    VISIONIQ_NODE_SCRIPT    npm script that runs the tests (default: test:unit)
    VISIONIQ_NODE_CACHE     folder OUTSIDE any repository where the Node dependencies are installed once
                            per package-lock.json; each run links to them instead of reinstalling
                            (required as soon as Node tests run)

  Modes:
    -Mode test-file -TestPath <path> -Report <file>   one test file: *.cs -> dotnet test (TRX report),
                                                       *.ts/.tsx/.js/.jsx/.mjs/.cjs/.vue -> npm script (JUnit)
    -Mode test-files -TestPaths <a;b;c> -Report <file> several test files in one go: grouped by project /
                                                       package, one run each, one JUnit report with every case
    -Mode suite -Report <file>                         every suite; one JUnit report with every case
    -Plan                                              with either mode: print what would run, run nothing
  The exit code is the test runner's (non-zero when a test failed); 2 = the script could not run tests.
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('test-file', 'test-files', 'suite')][string]$Mode,
    [string]$TestPath = '',
    [string]$TestPaths = '',
    [string]$Report = '',
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$nodeScript = if ($env:VISIONIQ_NODE_SCRIPT) { $env:VISIONIQ_NODE_SCRIPT } else { 'test:unit' }
$nodeExtensions = '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.vue'
# Folders never searched: dependencies, build output and version control.
$skipFolders = 'node_modules', 'bin', 'obj', '.git', 'dist', '.vs', 'packages'

function Fail([string]$message) { [Console]::Error.WriteLine("run-tests: $message"); exit 2 }
function Get-Relative([string]$path) { $path.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/' }

if (-not $Plan) {
    if (-not $Report) { Fail 'A report file is needed: -Report <file>.' }
    $out = [IO.Path]::GetFullPath((Join-Path $root $Report))
    New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null
}

# The nearest folder at or above a file (never above the repository root) that holds a file matching the filter.
function Find-Above([string]$relativeFile, [string]$filter) {
    $folder = Split-Path ([IO.Path]::GetFullPath((Join-Path $root $relativeFile))) -Parent
    while ($folder -and $folder.Length -ge $root.Length -and $folder.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        $hit = @(Get-ChildItem -LiteralPath $folder -Filter $filter -File -ErrorAction SilentlyContinue | Sort-Object Name)[0]
        if ($hit) { return $hit.FullName }
        if ($folder.Length -eq $root.Length) { break }
        $folder = Split-Path $folder -Parent
    }
    return $null
}

# Every file matching the filter in the repository. Links (the node_modules junction) and the folders
# above are not entered, so the search stays inside the source tree.
function Find-All([string]$filter) {
    $found = New-Object System.Collections.Generic.List[string]
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        $folder = $pending.Pop()
        foreach ($file in @(Get-ChildItem -LiteralPath $folder -Filter $filter -File -ErrorAction SilentlyContinue)) { $found.Add($file.FullName) }
        foreach ($sub in @(Get-ChildItem -LiteralPath $folder -Directory -ErrorAction SilentlyContinue)) {
            if ($skipFolders -contains $sub.Name) { continue }
            if ($sub.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $pending.Push($sub.FullName)
        }
    }
    return @($found | Sort-Object)
}

# What `dotnet test` runs for the whole suite: every solution, plus any test project no solution covers.
function Get-DotnetSuiteTargets {
    if ($env:VISIONIQ_DOTNET_TARGET) { return @($env:VISIONIQ_DOTNET_TARGET) }
    $solutions = @(Find-All '*.sln')
    $covered = @($solutions | ForEach-Object { (Split-Path $_ -Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar })
    $loose = @(Find-All '*.csproj' | Where-Object {
            $project = $_
            -not ($covered | Where-Object { $project.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) -and
            (Select-String -LiteralPath $project -Pattern 'Microsoft\.NET\.Test\.Sdk' -Quiet)
        })
    return @(@($solutions) + @($loose) | ForEach-Object { Get-Relative $_ })
}

# Folders whose Node tests can run: a package.json with the test script, and a lock file beside it.
function Get-NodeSuiteDirs {
    if ($env:VISIONIQ_NODE_DIR) { return @($env:VISIONIQ_NODE_DIR) }
    return @(Find-All 'package.json' | Where-Object {
            $folder = Split-Path $_ -Parent
            if (-not (Test-Path (Join-Path $folder 'package-lock.json'))) { return $false }
            try { $scripts = (Get-Content -Raw -LiteralPath $_ | ConvertFrom-Json).scripts } catch { return $false }
            $scripts -and ($scripts.PSObject.Properties.Name -contains $nodeScript)
        } | ForEach-Object { Get-Relative (Split-Path $_ -Parent) })
}

function Invoke-Dotnet([string]$target, [string]$filter, [string]$trxPath) {
    $arguments = @('test', $target, '--results-directory', (Split-Path $trxPath -Parent),
        '--logger', "trx;LogFileName=$(Split-Path $trxPath -Leaf)")
    if ($filter) { $arguments += '--filter', $filter }
    & dotnet @arguments | Out-Host
    return $LASTEXITCODE
}

# The export has no node_modules (it is never in git). Dependencies are installed once per lock file into
# VISIONIQ_NODE_CACHE (network is available to the local runner), then linked into this export.
function Use-NodeModules([string]$uiPath) {
    if (-not $env:VISIONIQ_NODE_CACHE) { Fail 'VISIONIQ_NODE_CACHE is not set in the policy runner.env.' }
    $lock = Join-Path $uiPath 'package-lock.json'
    if (-not (Test-Path $lock)) { Fail "No package-lock.json in $(Get-Relative $uiPath): dependencies cannot be installed reproducibly." }
    $key = (Get-FileHash -Algorithm SHA256 $lock).Hash.Substring(0, 16)
    $cache = Join-Path $env:VISIONIQ_NODE_CACHE $key
    if (-not (Test-Path (Join-Path $cache 'node_modules\.package-lock.json'))) {
        $staging = "$cache.installing-$PID"
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        Copy-Item (Join-Path $uiPath 'package.json'), $lock -Destination $staging
        Push-Location $staging
        try { & npm ci --no-audit --no-fund | Out-Host; $code = $LASTEXITCODE } finally { Pop-Location }
        if ($code -ne 0) { Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue; Fail "npm ci failed (exit $code)." }
        if (-not (Test-Path $cache)) { Move-Item $staging $cache } else { Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue }
    }
    $link = Join-Path $uiPath 'node_modules'
    if (-not (Test-Path $link)) {
        # A directory junction on Windows (no admin rights needed); removing the export removes only the link.
        # Windows is detected from the OS itself: DEFECT's local runner passes only an allowed list of
        # environment variables, and $env:OS is not among them.
        $kind = if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) { 'Junction' } else { 'SymbolicLink' }
        New-Item -ItemType $kind -Path $link -Target (Join-Path $cache 'node_modules') | Out-Null
    }
}

function Invoke-Node([string]$nodeDir, [string[]]$filter, [string]$junitPath) {
    $uiPath = if ($nodeDir) { Join-Path $root $nodeDir } else { $root }
    Use-NodeModules $uiPath
    $arguments = @('run', $nodeScript, '--', '--run', '--reporter=junit', "--outputFile=$junitPath")
    $arguments += @($filter | Where-Object { $_ })
    Push-Location $uiPath
    try { & npm @arguments | Out-Host; return $LASTEXITCODE } finally { Pop-Location }
}

# A .NET TRX report as JUnit <testcase> elements, named exactly as VisionIQ names TRX cases
# (results.py _trx_cases): class from the test definition, method without the class prefix,
# parameterised arguments kept on the name.
function ConvertFrom-Trx([string]$trxPath, [xml]$target) {
    $suite = $target.CreateElement('testsuite'); $suite.SetAttribute('name', 'dotnet')
    if (-not (Test-Path $trxPath)) { return $suite }
    [xml]$trx = Get-Content -Raw $trxPath
    $ns = New-Object Xml.XmlNamespaceManager $trx.NameTable
    $ns.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
    $methods = @{}
    foreach ($unit in $trx.SelectNodes('//t:UnitTest', $ns)) {
        $method = $unit.SelectSingleNode('t:TestMethod', $ns)
        if ($method) { $methods[$unit.GetAttribute('id')] = @($method.GetAttribute('className'), $method.GetAttribute('name')) }
    }
    $statusOf = @{ passed = 'passed'; failed = 'failed'; notexecuted = 'skipped'; inconclusive = 'skipped'; notrunnable = 'skipped'; pending = 'skipped' }
    foreach ($result in $trx.SelectNodes('//t:UnitTestResult', $ns)) {
        $testName = $result.GetAttribute('testName')
        $pair = $methods[$result.GetAttribute('testId')]; if (-not $pair) { $pair = @('', '') }
        $classname = $pair[0]; $methodName = $pair[1]
        $base = $(if ($methodName) { $methodName } else { $testName }).Split('(')[0]
        if ($classname -and $base.StartsWith("$classname.")) { $base = $base.Substring($classname.Length + 1) }
        elseif (-not $classname -and $base.Contains('.')) { $i = $base.LastIndexOf('.'); $classname = $base.Substring(0, $i); $base = $base.Substring($i + 1) }
        $arguments = if ($testName.Contains('(')) { $testName.Substring($testName.IndexOf('(')) } else { '' }
        $status = $statusOf[$result.GetAttribute('outcome').ToLowerInvariant()]; if (-not $status) { $status = 'error' }
        $case = $target.CreateElement('testcase'); $case.SetAttribute('name', $base + $arguments); $case.SetAttribute('classname', $classname)
        if ($status -ne 'passed') {
            $child = $target.CreateElement($(if ($status -eq 'failed') { 'failure' } else { $status }))
            $info = $result.SelectSingleNode('t:Output/t:ErrorInfo', $ns)
            if ($info) {
                $message = $info.SelectSingleNode('t:Message', $ns); $stack = $info.SelectSingleNode('t:StackTrace', $ns)
                if ($message) { $child.SetAttribute('message', $message.InnerText.Trim()) }
                if ($stack) { $child.InnerText = $stack.InnerText }
            }
            [void]$case.AppendChild($child)
        }
        [void]$suite.AppendChild($case)
    }
    return $suite
}

if ($Mode -eq 'test-file') {
    if (-not $TestPath) { Fail 'test-file needs -TestPath.' }
    $extension = [IO.Path]::GetExtension($TestPath).ToLowerInvariant()
    $relative = $TestPath -replace '\\', '/'
    if ($extension -eq '.cs') {
        # The test's own project is the smallest thing that builds and runs it; a solution is the fallback.
        $target = $env:VISIONIQ_DOTNET_TARGET
        if (-not $target) {
            $found = Find-Above $relative '*.csproj'
            if (-not $found) { $found = Find-Above $relative '*.sln' }
            if (-not $found) { Fail "No *.csproj or *.sln at or above '$TestPath': set VISIONIQ_DOTNET_TARGET in the policy runner.env." }
            $target = Get-Relative $found
        }
        $filter = [IO.Path]::GetFileNameWithoutExtension($TestPath)
        if ($Plan) { "dotnet test $target --filter FullyQualifiedName~$filter"; exit 0 }
        exit (Invoke-Dotnet $target "FullyQualifiedName~$filter" $out)                                               # TRX, read natively
    }
    if ($nodeExtensions -contains $extension) {
        $nodeDir = $env:VISIONIQ_NODE_DIR
        if (-not $nodeDir) {
            $found = Find-Above $relative 'package.json'
            if (-not $found) { Fail "No package.json at or above '$TestPath': set VISIONIQ_NODE_DIR in the policy runner.env." }
            $nodeDir = Get-Relative (Split-Path $found -Parent)
        }
        $prefix = ($nodeDir -replace '\\', '/').TrimEnd('/') + '/'
        if ($nodeDir -and $relative.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $relative = $relative.Substring($prefix.Length) }
        if ($Plan) { "npm run $nodeScript -- --run $relative   (in $(if ($nodeDir) { $nodeDir } else { '.' }))"; exit 0 }
        exit (Invoke-Node $nodeDir $relative $out)                                              # JUnit
    }
    Fail "No test runner for '$TestPath' (extension '$extension')."
}

# A TRX or JUnit report's cases, added to the merged JUnit report.
function Add-Report([xml]$merged, [string]$path, [bool]$trx) {
    if (-not (Test-Path $path)) { return }
    if ($trx) { [void]$merged.DocumentElement.AppendChild((ConvertFrom-Trx $path $merged)); return }
    [xml]$report = Get-Content -Raw $path
    $suites = if ($report.DocumentElement.LocalName -eq 'testsuite') { @($report.DocumentElement) } else { @($report.DocumentElement.SelectNodes('testsuite')) }
    foreach ($s in $suites) { [void]$merged.DocumentElement.AppendChild($merged.ImportNode($s, $true)) }
}

# test-files: several test files, grouped by the .NET project or Node package that runs them; one run per
# group, one JUnit report with every case. VisionIQ uses it to run related tests in one go, and reruns them
# one by one only when this run fails, to tell which test failed.
if ($Mode -eq 'test-files') {
    $paths = @($TestPaths -split ';' | Where-Object { $_ })
    if (-not $paths) { Fail 'test-files needs -TestPaths (paths separated by ;).' }
    $dotnetGroups = [ordered]@{}
    $nodeGroups = [ordered]@{}
    foreach ($path in $paths) {
        $relative = $path -replace '\\', '/'
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        if ($extension -eq '.cs') {
            $target = $env:VISIONIQ_DOTNET_TARGET
            if (-not $target) {
                $found = Find-Above $relative '*.csproj'
                if (-not $found) { $found = Find-Above $relative '*.sln' }
                if (-not $found) { Fail "No *.csproj or *.sln at or above '$path': set VISIONIQ_DOTNET_TARGET in the policy runner.env." }
                $target = Get-Relative $found
            }
            if (-not $dotnetGroups.Contains($target)) { $dotnetGroups[$target] = @() }
            $dotnetGroups[$target] += [IO.Path]::GetFileNameWithoutExtension($relative)
        } elseif ($nodeExtensions -contains $extension) {
            $nodeDir = $env:VISIONIQ_NODE_DIR
            if (-not $nodeDir) {
                $found = Find-Above $relative 'package.json'
                if (-not $found) { Fail "No package.json at or above '$path': set VISIONIQ_NODE_DIR in the policy runner.env." }
                $nodeDir = Get-Relative (Split-Path $found -Parent)
            }
            $prefix = ($nodeDir -replace '\\', '/').TrimEnd('/') + '/'
            if ($nodeDir -and $relative.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $relative = $relative.Substring($prefix.Length) }
            if (-not $nodeGroups.Contains($nodeDir)) { $nodeGroups[$nodeDir] = @() }
            $nodeGroups[$nodeDir] += $relative
        } else {
            Fail "No test runner for '$path' (extension '$extension')."
        }
    }
    if ($Plan) {
        foreach ($target in $dotnetGroups.Keys) {
            $classes = ($dotnetGroups[$target] | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
            "dotnet test $target --filter $classes"
        }
        foreach ($nodeDir in $nodeGroups.Keys) {
            "npm run $nodeScript -- --run $($nodeGroups[$nodeDir] -join ' ')   (in $(if ($nodeDir) { $nodeDir } else { '.' }))"
        }
        exit 0
    }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "visioniq-files-$PID"
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    try {
        $codes = @()
        [xml]$merged = '<testsuites/>'
        $index = 0
        foreach ($target in $dotnetGroups.Keys) {
            $index++
            $trx = Join-Path $scratch "dotnet-$index.trx"
            $classes = ($dotnetGroups[$target] | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
            $codes += Invoke-Dotnet $target $classes $trx
            Add-Report $merged $trx $true
        }
        foreach ($nodeDir in $nodeGroups.Keys) {
            $index++
            $junit = Join-Path $scratch "node-$index.xml"
            $codes += Invoke-Node $nodeDir $nodeGroups[$nodeDir] $junit
            Add-Report $merged $junit $false
        }
        $merged.Save($out)
        exit ([int](@($codes | Where-Object { $_ -ne 0 }) | Select-Object -First 1))
    } finally {
        Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
    }
}

# suite: everything found (or configured), one JUnit report (the reader takes every <testcase> in the file).
$dotnetTargets = @(Get-DotnetSuiteTargets)
$nodeDirs = @(Get-NodeSuiteDirs)
if (-not $dotnetTargets -and -not $nodeDirs) { Fail "No *.sln, test project or package.json with a '$nodeScript' script was found in the repository." }
if ($Plan) {
    $dotnetTargets | ForEach-Object { "dotnet test $_" }
    $nodeDirs | ForEach-Object { "npm run $nodeScript -- --run   (in $(if ($_) { $_ } else { '.' }))" }
    exit 0
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) "visioniq-suite-$PID"
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    $codes = @()
    [xml]$merged = '<testsuites/>'
    $index = 0
    foreach ($target in $dotnetTargets) {
        $index++
        $trx = Join-Path $scratch "dotnet-$index.trx"
        $codes += Invoke-Dotnet $target '' $trx
        [void]$merged.DocumentElement.AppendChild((ConvertFrom-Trx $trx $merged))
    }
    foreach ($nodeDir in $nodeDirs) {
        $index++
        $junit = Join-Path $scratch "node-$index.xml"
        $codes += Invoke-Node $nodeDir '' $junit
        if (Test-Path $junit) {
            [xml]$nodeReport = Get-Content -Raw $junit
            $suites = if ($nodeReport.DocumentElement.LocalName -eq 'testsuite') { @($nodeReport.DocumentElement) } else { @($nodeReport.DocumentElement.SelectNodes('testsuite')) }
            foreach ($s in $suites) { [void]$merged.DocumentElement.AppendChild($merged.ImportNode($s, $true)) }
        }
    }
    $merged.Save($out)
    exit ([int](@($codes | Where-Object { $_ -ne 0 }) | Select-Object -First 1))
} finally {
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}
