# Task 4 report — Fresh install, package, upgrade, and restore

Implemented center file-state deployment support in the Task 4 scope:

- Center config now uses `Central:Storage:DataDirectory` and has no SQL connection or adapter setting.
- Center identity validation requires an absolute state path; install rejects state paths under the install/release/web tree, creates the state directory, and grants the IIS app-pool identity protected Modify access.
- Center packages contain only the web component and state format compatibility 1. The center migrator publish was removed; tenant schema compatibility and migrator packaging remain.
- Center package validation accepts state-format manifests and rejects incompatible state format or a non-web-only component list. Tenant schema checks remain.
- Center upgrade accepts a new `.zip` backup, requires IIS stopped and state format 1, verifies and archives journal, optional health snapshot, config, and prior release with SHA-256 hashes, switches the release, and leaves IIS stopped. Tenant SQL backup/migration flow is unchanged.
- Added identity-bound center backup verification and restore helpers. Restore verifies every listed hash and checks the archived config instance ID against the archive manifest.
- Updated production deployment and upgrade/restore instructions, including persistent state retention and same-instance-ID restore.

Validation:

- `pwsh -NoProfile -File tests/deploy/center-storage.Tests.ps1` — pass.
- Package smoke was attempted. Center publish failed because Task 2's `FileCentralRegistry` adapter was not present in the shared tree (`Program.cs(57): CS0103`).
- One `dotnet test WorkerDock.slnx` full-suite run was attempted before commit. Integration tests were blocked because `WORKERDOCK_TEST_SQL` is unset (88 failed, 278 passed); unit test compilation also failed against Task 2 in-progress `CentralHealthSnapshot` API (`IDisposable` and `Open` overload errors).
- Windows/IIS install, stopped-site upgrade, ACL, and restore procedure were not exercised on a disposable IIS host.

Final coordinated checks: PowerShell deployment tests passed; full solution build passed with zero warnings/errors; final center/tenant package smoke passed. The center ZIP contains only the web component, state format 1, and no SQL scripts/migrator. The tenant ZIP retains its migrator, SQL scripts, and schema compatibility 1–8. Windows/IIS cutover and restore remain release-gate checks.
