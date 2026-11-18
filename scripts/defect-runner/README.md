# DEFECT test dispatcher (`run-tests.ps1`)

DEFECT runs tests only through the commands in a repository's defect policy. This script is one such
command for the **local runner**: it works out how to run a test file of a .NET or JavaScript/Vue
repository and writes a report DEFECT reads (JUnit XML, or TRX for .NET). It contains no project names:
it finds projects by convention.

| Mode | What it runs |
| --- | --- |
| `-Mode test-file -TestPath <file> -Report <file>` | One test file: `*.cs` with `dotnet test` on the nearest project; `.ts`/`.vue`/`.js` with the package's npm test script |
| `-Mode test-files -TestPaths "<a>;<b>" -Report <file>` | Several test files in one go, grouped by project or package, one merged report |
| `-Mode suite -Report <file>` | Every solution and Node package found |
| `-Plan` (with any mode) | Prints what would run, runs nothing |

Node dependencies are installed once per `package-lock.json` into `VISIONIQ_NODE_CACHE` and linked into
each run (a directory junction on Windows, no administrator rights needed).

## Using it from a defect policy

The commands run inside a fresh copy of the repository, so the policy gives the script's **full path**:

```json
"recipes": {
  "test_file":  ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                 "C:/path/to/visionIQ/scripts/defect-runner/run-tests.ps1",
                 "-Mode", "test-file", "-TestPath", "{test_path}", "-Report", "{junit}"],
  "test_files": ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                 "C:/path/to/visionIQ/scripts/defect-runner/run-tests.ps1",
                 "-Mode", "test-files", "-TestPaths", "{test_paths}", "-Report", "{junit}"]
}
```

and in `runner.env`: `"VISIONIQ_NODE_CACHE": "C:/path/to/node-cache"` (required for Node tests), optionally
`"VISIONIQ_NODE_SCRIPT"` (default `test:unit`), `"ELECTRON_SKIP_BINARY_DOWNLOAD": "1"` (when a project's
dependencies download Electron) and `"NuGetAudit": "false"` (when a private NuGet feed cannot be reached
for its vulnerability data from the runner).

It also runs under PowerShell 7 on Linux (`pwsh`), for a container runner image.
