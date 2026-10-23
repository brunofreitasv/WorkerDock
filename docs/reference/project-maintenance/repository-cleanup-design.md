# Repository cleanup design

Date: 2026-10-19 (America/Sao_Paulo)
Status: Proposed for review. If approved, this supersedes the legacy SQL transition decision recorded in the earlier database-free Control Center design, which remains recoverable from Git history.

## Intent and scope

WorkerDock is still before a SQL-backed Control Center deployment. The owner confirmed there are no installations or tenant links to import. The repository should contain only code, SQL assets, documentation, and development records that support the current file-backed Control Center and SQL-backed tenants. Git history remains the recovery path for removed historical sources.

The tenant fresh-install baseline, guarded rollback, SQL test fixtures, open-source notices, sample template, and active operational documentation remain. Existing uncommitted logo files and the modified `.superpowers` progress file are user work and must not be overwritten or deleted.

## Cleanup decision

Remove the obsolete central SQL schema and one-time importer as one change: `database/central`, `WorkerDock.CentralImport`, `CentralJournalBootstrap`, central migration mode, and tests whose only purpose was the retired path. Keep `MigrationRunner.ApplyAsync(string databaseKind, string targetMigration, CancellationToken ct)` and `RollbackAsync` signatures for existing tenant callers, but accept only `tenant`; reject `central` before opening SQL. The file journal, Control Center registry, tenant SQL schema, and their tests remain.

The tenant migrator and integration-test outputs should copy only `database/tenant` scripts. The tenant release package should contain that manifest and its up/down scripts; the center package should contain no database migrator or SQL scripts. The tenant schema is installed as a fresh baseline and there is no tenant upgrade converter. The upgrade script's center branch remains file-only; remove the dead SQL migration alternatives after its early center return.

Remove superseded planning documents and tracked `.superpowers/sdd` reports from the published repository. Use index-only removal for `.superpowers/sdd` so local files, including the modified progress report, remain on disk. Keep this cleanup design and plan; current operator docs become the sole source for setup and deployment. Update their legacy references and links to describe only current behavior.

## Verification and limits

Add a regression test that `MigrationRunner` rejects `central` before a connection attempt. Inspect actual built ZIP contents for tenant scripts, absent central/upstream SQL, and a SQL-free center package. Run the full non-SQL .NET suite, the solution build, deployment smoke checks, and locked dependency restore. SQL-backed integration and live Windows/IIS deployment remain separate release gates when their environments are available. Do not rewrite tenant migration bytes or delete ignored local build output as part of source cleanup.
