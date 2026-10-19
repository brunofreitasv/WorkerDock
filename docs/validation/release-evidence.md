# Release verification evidence

Record the exact checkout, tool versions, command, result and artifact location for each candidate. The source and portable checks below are not a live deployment certification.

## Last recorded verification (2026-10-19)

- Date: 2026-10-19.
- .NET non-SQL Release tests: `dotnet test WorkerDock.slnx --no-restore -c Release --filter "Store!=Sql"` — PASS; 384 tests (71 unit, 37 Windows process, 276 integration).
- Frontend unit tests: `npm.cmd --prefix frontend run test -- --run` — PASS; 35 tests.
- Tenant browser tests: `npm.cmd --prefix frontend run e2e -- --workers=1` — PASS; 20 tests.
- Control Center browser tests: `npm.cmd --prefix frontend run e2e:center -- --workers=1` — PASS; 2 tests.
- Focused accessibility browser tests: `npm.cmd --prefix frontend run e2e -- accessibility.spec.ts --workers=1` — PASS; 3 tests covering keyboard flow, visible focus, localized validation, non-color health text and mobile layout.
- Frontend production build: `npm.cmd --prefix frontend run build` — PASS; tenant and Control Center.
- Deployment script smoke: `pwsh -File tests/DeploymentSmoke.ps1` — PASS.
- OSS sample check: `dotnet test tests/WorkerDock.UnitTests/WorkerDock.UnitTests.csproj --no-restore -c Release --filter FullyQualifiedName~SampleTemplateConformanceTests` — PASS; 2 tests. `dotnet build examples/WorkerDock.SampleTemplate/WorkerDock.SampleTemplate.csproj --no-restore -c Release` — PASS.
- Package attribution: `pwsh -NoProfile -File scripts/Generate-DotNetNotices.ps1` — PASS; 94 locked NuGet packages. `node frontend/scripts/licenses.mjs` — PASS; emits tenant and center production notices. `pwsh -NoProfile -File deploy/Build-Packages.ps1 -Version 0.0.1 -OutputRoot artifacts/packages` — PASS; tenant and center ZIPs built, with attribution files verified in ZIP contents and checksummed manifests. These are disposable verification artifacts, not a release.
- Patch hygiene: `git diff --check` — PASS; Git reported only line-ending normalization warnings.
- Fault coverage and evidence boundaries: see [fault matrix](fault-matrix.md); portable test results are separated from SQL/IIS/Entra and host-suspension proofs that remain NOT RUN.
- SQL-backed tests, disposable IIS installation, Entra sign-in, upgrade interruption, restore, cross-tenant host boundary and measured workload — NOT RUN. A provisioned SQL Server 2022+ fixture and disposable Windows/IIS environment are still required.
- Production vault integration and secret rotation — NOT RUN; production adapters must first be implemented and installed for the relevant hosts.
- Hosted GitHub workflows — NOT RUN from this worktree; CI workflow outcomes must be attached to a reviewed revision before release.

## Candidate evidence to attach

For a release candidate, append the source commit and package checksums, SQL schema version/checksum, supported Windows/IIS/.NET/SQL versions, test runner identity, sanitized logs, backup/restore artifacts, identity-provider test summary, fault-matrix results and workload report. Keep secrets, customer data, and connection strings out of this file and its attachments.
