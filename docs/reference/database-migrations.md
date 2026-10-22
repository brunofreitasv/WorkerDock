# Database migrations and verification

Use [Set up SQL Server](../how-to/setup-sql-server.md) for current tenant database
provisioning, migration, runtime identity, rollback, and SQL integration test
procedures. The Control Center uses protected files and has no SQL database,
migration, or runtime SQL identity. There is no Control Center SQL migration
or import command. See [Control Center](../how-to/configure-control-center.md).
The [database object reference](database-objects.md) lists current
tenant migration objects and test-only SQL fixtures.

Tenant schema history is ordered by `database/tenant/manifest.json`. The current
chain includes `0010_remove_builtin_v1`. Operators choose either the ordered,
checksummed migrations using `scripts/Invoke-TenantMigration.ps1` with an
explicit target, or `database/tenant/current-state.sql` for a fresh empty
database only. The snapshot cannot update an existing database. These paths are
design alternatives; live SQL parity has not been verified. Tenant SQL Server
and both Quartz table sets remain required.

The optional migration script verifies up/down script checksums, requires SQL Server 2022 or newer and compatibility level 160, records applied IDs in `SchemaHistory`, and binds a database to its configured stable instance ID. Supply an explicit target, separate migration credential, database script root, and reviewed Web assembly directory.
Tenant runtime services use the restricted `WorkerDockRuntime` database role
and do not create or alter schema. Keep migration credentials separate from
runtime credentials. Applied migration files are immutable; make schema
changes with a new paired migration.

Migration `0009_dynamic_values` adds nullable dynamic-plan data to job revisions
and execution value context, first-start time, resolved-value snapshot, and safe
resolution-error fields to executions. Existing fixed-value rows remain null;
the migration does not infer or backfill historical dates. New populated fields
are immutable after their initial write. The down migration refuses rollback
when dynamic plans or execution state has been populated, preserving pinned
configuration and retry snapshots. Deploy compatible Web, Worker, and Executor binaries together before admitting jobs that use the dynamic-value resolver;
the IPC envelope is version 2 and requires matching worker support. See
[upgrade and restore](../how-to/upgrade-and-restore.md) for the backup-first, explicit migration, deployment, and recovery steps.

Rollback runs one migration at a time and its down script may reject populated
state. Stop all tenant web, scheduler, worker, and publisher processes first,
take and verify a backup, and follow the prerequisites in the setup guide. For
populated databases, restore a backup or plan a forward data migration rather
than bypassing a guard.

SQL integration tests require an isolated disposable SQL Server instance.
Their fixtures create and drop generated `WorkerDock_Test_<32 hex digits>`
databases; Quartz process tests and target-template tests also require their
distinct restricted runtime logins. The setup guide lists the exact environment
variables and test command. A missing SQL configuration is reported as
`SQL_TEST_BLOCKED`; memory tests do not verify SQL persistence, migration
execution, runtime grants, or cluster behavior.

Quartz store topology and publication behavior are described in
[Quartz integration](quartz-integration.md).
