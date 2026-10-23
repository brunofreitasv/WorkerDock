# Database objects

WorkerDock's only runtime SQL schema is the tenant schema in `database/tenant`. The manifest contains one checksummed fresh-install baseline, `0001_initial`, with a guarded rollback. Apply it to a new tenant database with `scripts/Invoke-TenantMigration.ps1`; do not execute an individual runtime script by hand or edit the baseline. There are no deployed tenant databases and no upgrade converter.

All tenant-owned objects are under `[workerdock]`. Tenant application connections use the restricted runtime role created by the fresh-install baseline. Current Control Center state is held in protected files, not SQL objects, and it has no SQL runtime principal. `database/tenant/permissions.sql` only adds an existing tenant database user or group to `WorkerDockRuntime`; it does not create roles, schemas, tables, procedures, or grants.

## Runtime tenant schema

| Area | Main objects |
| --- | --- |
| Tenant and security | `TenantSettings`, `SchemaHistory`, append-only `AuditEvents`, `WorkerDockRuntime` role |
| Jobs and executions | `TemplateCatalog`, `Jobs`, `JobRevisions`, `Executions`, `Attempts`, `Commands`, manual request identity and active-work state on `Executions` |
| Scheduling | `Schedules`, occurrence cursors, recovery history in `OperationalEvents` and uniqueness constraints |
| Runtime and capacity | singleton `RuntimeStatus`, local process ceiling, per-template/job limits stored on their owner rows, and execution holds |
| Connections and egress | connection profiles and revisions, integration settings, outbound policies |
| Monitoring and alerts | typed `OperationalEvents`, metric aggregates, alert rules/revisions, incidents, durable deliveries and delivery attempts |
| Retention | retention settings, archive batches/items with durable purge progress, request tombstones, audit events and guarded purge procedures |

The baseline creates no third-party scheduler schema or distributed worker registry. The single runtime service claims queued executions directly from `Executions`; attempt leases and `HoldsJobSlot` own durable execution state, while a local semaphore enforces the process ceiling. Template and job concurrency limits live on `TemplateCatalog` and `Jobs`. Schedule cursors, per-rule alert cursors, and alert deliveries live on their owning rows and are claimed by the runtime service. Manual request identity and its 24-hour response window live on `Executions`; tombstones preserve idempotency after execution retention. Schedule recovery history shares the typed `OperationalEvents` timeline. `database/tenant/current-state.sql` provisions the same schema for a fresh, empty tenant database only; it cannot update an existing database. The `database/tenant/permissions.sql` script assigns an existing principal to the restricted runtime role; `permissions.rollback.sql` removes that membership.

Archive retention progress is counted on each `ArchiveBatches` row in `PurgedRowCount`. Purge decisions and outcomes remain in append-only `AuditEvents`; there is no separate purge-summary table.

## Test-only SQL objects

These SQL files exist only to support disposable integration fixtures and are not migrations or deployment scripts:

| Location | Purpose |
| --- | --- |
| `tests/sql/target-fixture.up.sql` and `.down.sql` | Creates/drops the `[workerdock].SqlTemplateTarget` table, test procedures, and `WorkerDockSqlTarget` role in the fixture database. |
| `tests/WorkerDock.IntegrationTests/Sql` | Supporting objects used only by disposable integration fixtures. |

`SqlDatabaseFixture` chooses a generated `WorkerDock_Test_<32 hex digits>` database, sets compatibility level 160, and drops that database at disposal. Fixture scripts run only inside that generated database. Do not copy them into a tenant installation.

For exact provisioning, least-privilege identity setup, test prerequisites, and rollback commands, see [Set up SQL Server](../how-to/setup-sql-server.md).
