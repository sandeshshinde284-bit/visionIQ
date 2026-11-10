# Environment key synchronization

The committed `.env.example` is the source of truth for configuration **keys, ordering and comments**. Private local values live in ignored `.env`.

Run these commands from the repository root, or invoke the scripts with absolute paths:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync-env.ps1 -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync-env.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-env-contract.ps1 -CheckLocalEnv
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-env-contract.ps1
```

- Sync does not overwrite existing values, including empty values deliberately set locally.
- Missing template keys are added with template defaults. Extra private keys are retained at the end, clearly identified for review.
- Sync creates a timestamped, Git-ignored backup before changing an existing `.env`.
- Dry-run mode reports only key names and counts, never values.
- Duplicate and malformed lines fail before any write; CI validation should use `check-env-contract.ps1` **without** `-CheckLocalEnv` so private `.env` is never read.
- Static source-key detection is best effort, not proof that every dynamic setting has a contract. Run `-SkipSourceScan` only when investigating false positives and document the exception.

These PowerShell scripts must be executed and validated on a Windows workstation before including them as a mandatory CI gate. Do not commit `.env`, backups, PATs or passwords.
