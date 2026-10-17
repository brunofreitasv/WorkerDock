# Quartz integration

Quartz.NET 4.5.1 provides two tenant scheduler stores. `WorkerDock.Schedule` uses
`WDS_QRTZ_` tables for schedule/control work; `WorkerDock.Execute` uses `WDE_QRTZ_`
tables for worker dispatch. The stores share a tenant database but never a table.
`QuartzTopology` verifies `TenantSettings.InstanceId` and the `0002_quartz`
schema-history entry before creating a SQL scheduler. Callers own scheduler
shutdown and dispose the topology. Started nodes in a store must use the same
scheduler name, compatible limits, and distinct boot IDs.

The public creation APIs are `CreateScheduleAsync`, `CreateExecutionAsync`, and
`CreateExecutionPublisherAsync`, each returning `IScheduler`. The SQL publisher
has its own handle with `ZeroSizeThreadPool` and is never started. Its facade
permits single-job `ScheduleJob` and `AddJob` writes with only string
`ExecutionId`, `DeliveryId`, and `AttemptOrdinal` job data. It denies starting,
rescheduling, deletion, and other scheduler administration. The execution
scheduler is the only handle that starts worker jobs. Both SQL stores use
clustering, string job data, `System.Text.Json`, locked trigger acquisition,
and cluster-scoped Quartz execution groups. WorkerDock commands and audits
remain outside Quartz tables; the application does not mutate those tables
directly.

`QuartzTopology.CreateMemory(instanceId, "Development")` is a disposable,
single-process testing adapter through the same three creation methods. Quartz
RAMJobStore instances cannot share stored jobs. In this mode the publisher
facade forwards validated writes to the in-process execution scheduler, while
still denying `Start` and administrative calls. One memory topology accepts a
single boot ID and fixed pool/group limits. A fresh topology loses its jobs.
This adapter does not establish SQL durability, clustering, restart behavior,
capacity across nodes, migration correctness, or SQL permissions.

The SQL schema comes from the attributed Quartz 4.5.1 SQL Server script in
`database/upstream/quartz-4.5.1/`. `0002_quartz.up.sql` creates two prefixed
copies of its creation section and grants `WorkerDockRuntime` table DML plus
schema-history read. `0002_quartz.down.sql` refuses populated stores and drops
both table sets in a migrator-owned serializable transaction. The upstream
script is retained for attribution and must not be run directly because its
default drops existing Quartz tables. See [database.md](database.md) for exact
local setup and [development.md](development.md) for memory commands.

The SQL integration suite is gated by `WORKERDOCK_TEST_SQL`. It runs the two
isolation settings, a separate non-started publisher process, process restart,
two or three worker processes, barrier-held jobs, group capacity, runtime DML,
and up/down/up schema checks. Until that suite passes on a disposable SQL
Server 2022+ instance, SQL clustering/capacity/restart and migration behavior
remain unverified.
