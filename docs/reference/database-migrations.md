# Database migrations and verification

Use [Set up SQL Server](../how-to/setup-sql-server.md) for current tenant database
provisioning, migration, runtime identity, rollback, and SQL integration test
procedures. The Control Center uses protected files and has no SQL database,
migration, or runtime SQL identity. There is no Control Center SQL migration
or import command. See [Control Center](../how-to/configure-control-center.md).
The [database object reference](database-objects.md) lists current
tenant migration objects and test-only SQL fixtures.

Tenant schema history is defined by `database/tenant/manifest.json`, which
contains only the `0001_initial` baseline. Apply it to a new tenant database
with `scripts/Invoke-TenantMigration.ps1`, or run
`database/tenant/current-state.sql` against a fresh empty database. Both paths
create the same schema; neither upgrades an existing database. In SSMS, run
the complete rendered snapshot with SQLCMD mode enabled and preserve its
`:ON ERROR EXIT` directive so any preflight error stops all later batches.
No tenant
databases are deployed, so the project provides no upgrade converter.

The migration script verifies checksums, requires SQL Server 2022 or newer and
compatibility level 160, records the baseline in `SchemaHistory`, and binds a
database to its configured stable instance ID. Supply the target
`0001_initial`, existing `-RuntimePrincipal`, separate migration credential,
database script root, and reviewed Web assembly directory. Use `-InitialSetup`
to generate a GUID for a fresh database, then save it for `Tenant.InstanceId`;
for reuse, supply that same GUID with `-InstanceId`. The selected runtime
database user must already exist in the target database. Setup validates and
grants it directly; it does not create users, roles, or logins. Keep migration
credentials separate from runtime credentials. Any future upgrade path must be
designed and reviewed separately.

The baseline includes dynamic plans and execution snapshots as part of the
fresh schema. Dynamic values are pinned to job revisions and accepted execution
context; see [execution guarantees](execution-guarantees.md).

Rollback is available only for an empty fresh-install database and may refuse
when user data exists. Stop tenant web and runtime processes, take and verify a
backup, and follow the setup guide. Never bypass the guard.

SQL integration tests require an isolated disposable SQL Server instance.
Their fixtures create and drop generated `WorkerDock_Test_<32 hex digits>`
databases; target-template tests also require a distinct restricted runtime
login. The setup guide lists the exact environment variables and test command.
A missing SQL configuration is reported as
`SQL_TEST_BLOCKED`; memory tests do not verify SQL persistence, migration
execution, runtime grants, or cluster behavior.

The runtime service, direct SQL work model, and operator boundaries are
described in [architecture](architecture.md) and
[execution guarantees](execution-guarantees.md).
