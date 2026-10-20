# Database migrations and verification

Use [Set up SQL Server](how-to/setup-sql-server.md) for current tenant database
provisioning, migration, runtime identity, rollback, and SQL integration test
procedures. A fresh Control Center uses protected files and has no central
database, migration, or runtime SQL identity. `database/central` is archived
source for recovery of a pre-file-center installation; it is never applied by
current setup, startup, upgrade, or the legacy importer. See [Control Center](control-center.md).
The [database object reference](reference/database-objects.md) lists current
tenant migration objects and test-only SQL fixtures.

Tenant schema history is ordered by `database/tenant/manifest.json`. The current
chain ends at `0008_retention`; applying that migration on a fresh database
applies every preceding pair. When updating an existing installation, apply
the newest migration ID for the deployed release. Tenant SQL Server and both
Quartz table sets remain required.

The migrator verifies each up/down script checksum before use, requires SQL
Server 2022 or newer and compatibility level 160, records applied IDs in
`SchemaHistory`, and binds a database to its configured stable instance ID.
Tenant runtime services use the restricted `WorkerDockRuntime` database role
and do not create or alter schema. Keep migration credentials separate from
runtime credentials. Applied migration files are immutable; make schema
changes with a new paired migration.

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
