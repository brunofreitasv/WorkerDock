# Release verification evidence

Record the exact checkout, tool versions, command, result and artifact location for each candidate. The source and portable checks below are not a live deployment certification.

## Current worktree

- Date: 2026-10-19.
- .NET non-SQL Release tests: `dotnet test WorkerDock.slnx --no-restore -c Release --filter "Store!=Sql"` — PASS; 384 tests (71 unit, 37 Windows process, 276 integration).
- Frontend unit tests: `npm.cmd --prefix frontend run test -- --run` — PASS; 35 tests.
- Tenant browser tests: `npm.cmd --prefix frontend run e2e -- --workers=1` — PASS; 19 tests.
- Control Center browser tests: `npm.cmd --prefix frontend run e2e:center -- --workers=1` — PASS; 2 tests.
- Frontend production build: `npm.cmd --prefix frontend run build` — PASS; tenant and Control Center.
- Deployment script smoke: `pwsh -File tests/DeploymentSmoke.ps1` — PASS.
- Patch hygiene: `git diff --check` — PASS; Git reported only line-ending normalization warnings.
- SQL-backed tests, disposable IIS installation, Entra sign-in, upgrade interruption, restore, cross-tenant host boundary and measured workload — NOT RUN. A provisioned SQL Server 2022+ fixture and disposable Windows/IIS environment are still required.
- Hosted GitHub workflows — NOT RUN from this worktree; CI workflow outcomes must be attached to a reviewed revision before release.

## Candidate evidence to attach

For a release candidate, append the source commit and package checksums, SQL schema version/checksum, supported Windows/IIS/.NET/SQL versions, test runner identity, sanitized logs, backup/restore artifacts, identity-provider test summary, fault-matrix results and workload report. Keep secrets, customer data, and connection strings out of this file and its attachments.
