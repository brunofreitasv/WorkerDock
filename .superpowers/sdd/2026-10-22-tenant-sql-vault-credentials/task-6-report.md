# Task 6 report: Examples and operator instructions

## Scope

Updated the tenant and worker examples to use `Tenant:Sql:{Host,Port,Database,User,PasswordReference}`, added a migrator example using the separate `Migrator:Sql` password reference, and added focused `DeploymentSmoke.ps1` assertions for all five keys, absence of `ConnectionString`, and reference separation. Updated SQL setup, tenant configuration, production deployment, upgrade/restore, configuration reference, secrets reference, and SQL Server tutorial instructions with Development vault mappings, certificate validation, restart guidance, separate migrator rights, and `-MigratorConfigPath`.

Preserved the pre-existing unrelated hunks in `docs/tutorials/run-with-sql-server.md` and `docs/how-to/upgrade-and-restore.md` as unstaged changes. Only Task 6 hunks from those files were staged.

## TDD evidence

- Red: `pwsh -NoProfile -File tests/DeploymentSmoke.ps1` failed with `Migrator example is missing.` before `deploy/config/migrator.example.json` existed.
- Green: the same command passed after the examples and assertions were added.
- Documentation scan: `rg -n "Tenant__ConnectionString|WORKERDOCK_SQL"` over the updated docs/examples returned no matches.
- Focused .NET check attempted: `dotnet test tests/WorkerDock.UnitTests -c Release --filter FullyQualifiedName~SecretBoundaryTests` stopped at the first blocker: NuGet could not write `src/WorkerDock.Contracts/obj/...tmp` (`Access is denied`).

## Commit

Task 6 paths and selected hunks were committed as `0b1ef52` after the Task 5 commit `a7c5764` completed with scoped paths.

## Fix round 1

Set `DOTNET_ENVIRONMENT=Development` in the SQL setup migrator block and each applicable SQL tutorial command block (migrator, shared runtime setup, Tenant UI, Worker, Scheduler). This ensures fresh PowerShell windows satisfy the Development-only environment vault restriction. The pre-existing unrelated catalog digest edit in the tutorial remains unstaged.

Verification: `pwsh -NoProfile -File tests/DeploymentSmoke.ps1` → `pass`.
