# Database migrations and verification

Use [Set up SQL Server](how-to/setup-sql-server.md) for current tenant and
central database provisioning, migration, runtime identity, rollback, and SQL
integration test procedures. The [database object reference](reference/database-objects.md)
lists the objects owned by each migration and the test-only SQL fixtures.

Tenant schema history is ordered by `database/tenant/manifest.json`. The current
chain ends at `0008_retention`; applying that migration on a fresh database
applies every preceding pair. When updating an existing installation, apply the
newest migration ID for the deployed release. Central registry schema is a
separate database and has its own manifest and instance ID.

The migrator verifies each up/down script checksum before use, requires SQL
Server 2022 or newer and compatibility level 160, records applied IDs in
`SchemaHistory`, and binds a database to its configured stable instance ID.
Runtime services use the restricted `WorkerDockRuntime` database role and do not
create or alter schema. Keep migration credentials separate from runtime
credentials. Applied migration files are immutable; make schema changes with a
new paired migration.

Rollback runs one migration at a time and its down script may reject populated
state. Stop all web, scheduler, worker, and publisher processes first, take and
verify a backup, and follow the prerequisites in the setup guide. For populated
databases, restore a backup or plan a forward data migration rather than
bypassing a guard.

SQL integration tests require an isolated disposable SQL Server instance. Their
fixtures create and drop generated `WorkerDock_Test_<32 hex digits>` databases;
Quartz process tests and target-template tests also require their distinct
restricted runtime logins. The setup guide lists the exact environment
variables and test command. A missing SQL configuration is reported as
`SQL_TEST_BLOCKED`; memory tests do not verify SQL persistence, migration
execution, runtime grants, or cluster behavior.

For the optional central registry and Control Center details, see
[Control Center](control-center.md). Quartz store topology and publication
behavior are described in [Quartz integration](quartz-integration.md).
