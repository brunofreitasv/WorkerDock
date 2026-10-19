# Database objects

WorkerDock owns versioned runtime schema scripts in `database/tenant` and `database/central`. Each migration has an `.up.sql` and `.down.sql` file listed in its manifest with checksums. Apply them with `WorkerDock.DatabaseMigrator`; do not execute an individual runtime script by hand or edit a migration after it has been applied. New schema changes use a new migration pair.

All tenant-owned objects are under `[workerdock]`. The central database also uses `[workerdock]`, but is a separate database. The application connection uses the restricted runtime role created by the migrations. `deploy/permissions.sql` only adds existing database users or groups to these roles; it does not create roles, schemas, tables, procedures, or grants.

## Runtime tenant schema

| Migration | Main objects |
| --- | --- |
| `0001_bootstrap` | `TenantSettings`, `SchemaHistory`, append-only `AuditEvents`, `Outbox`, and `WorkerDockRuntime` role |
| `0002_quartz` | Quartz 4.5.1 scheduler and execution-publisher table sets, each with a distinct `WDS_` or `WDE_` prefix |
| `0003_execution` | `TemplateCatalog`, `TemplatePolicies`, `Jobs`, `JobRevisions`, `Workers`, `WorkerCapabilities`, `ServiceHealth`, `PolicyRevisions`, `Executions`, `Attempts`, `JobPermits`, `Commands`, `ManualRequestDedup` |
| `0003a_projection` | projection/review indexes and bounded outbox publication metadata |
| `0003b_attempts` | `AttemptStatusEvents` and attempt ownership/stop markers |
| `0003c_capacity` | capacity/admission metadata and supporting indexes |
| `0003d_dispatch` | `ProjectionMaintenance` and dispatch indexes |
| `0004_templates` | template definitions and template policy extensions |
| `0004a_profiles` | `ConnectionProfiles`, `ConnectionProfileRevisions` |
| `0004b_integration_settings` | `IntegrationSettings`, `TenantEgressPolicies`, `IntegrationSettingsRevisions` |
| `0005_scheduling` | `Schedules`, `ScheduleRecoveryEvents`, `ScheduleRuleScanState` |
| `0006_observability` | `ExecutionEvents`, `ExecutionMetricAggregates`, aggregation index |
| `0007_alerts` | alert sinks/rules and revisions, incidents, deliveries, delivery attempts, evaluation state |
| `0008_retention` | retention policy, archive batches/items, request tombstones, purge audit, guarded purge procedures |

Quartz schema is included in tenant migration `0002_quartz`; the source SQL Server script is preserved with its license and notices in `database/upstream/quartz-4.5.1/`. WorkerDock changes its table prefixes and schema to `[workerdock]`, and creates separate stores for scheduling and execution publishing. Do not install the upstream script independently. The paired down migration refuses to drop populated stores.

## Runtime central schema

`database/central/0001_registry.up.sql` creates `SchemaHistory`, `CentralSettings`, `TenantRegistrations`, `TenantHealthObservations`, append-only `AuditEvents`, supporting indexes, and `WorkerDockCentralRuntime`. The role's table grants are scoped to those central objects. The central database stores tenant endpoints, expected IDs and logical health credential references; it does not contain tenant job data.

## Test-only SQL objects

These SQL files exist only to support disposable integration fixtures and are not migrations or deployment scripts:

| Location | Purpose |
| --- | --- |
| `tests/sql/target-fixture.up.sql` and `.down.sql` | Creates/drops the `[workerdock].SqlTemplateTarget` table, test procedures, and `WorkerDockSqlTarget` role in the fixture database. |
| `tests/WorkerDock.IntegrationTests/Sql/quartz-probe.up.sql` and `.down.sql` | Creates/drops a Quartz process test probe's state/event tables and its restricted runtime user in a generated test database. |
| `database/upstream/quartz-4.5.1/sql-server.sql` | Attributed upstream source retained for reference; the runtime migration is the customized, versioned copy. |

`SqlDatabaseFixture` chooses a generated `WorkerDock_Test_<32 hex digits>` database, sets compatibility level 160, and drops that database at disposal. Target fixture scripts and Quartz probe scripts run only inside that generated database. Do not copy these scripts into a tenant or central installation.

For exact provisioning, least-privilege identity setup, test prerequisites, and rollback commands, see [Set up SQL Server](../how-to/setup-sql-server.md).
