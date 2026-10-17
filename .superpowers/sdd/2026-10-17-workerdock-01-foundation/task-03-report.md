# Task 03 report — Quartz stores and disposable memory adapter

## Result

Implemented the Quartz.NET 4.5.1 topology and a Development-only,
single-process memory adapter through `CreateScheduleAsync`,
`CreateExecutionAsync`, and `CreateExecutionPublisherAsync`. SQL configuration
uses `WorkerDock.Schedule`/`WDS_QRTZ_` and `WorkerDock.Execute`/`WDE_QRTZ_`,
clustering, string job data, `System.Text.Json`, locked trigger acquisition,
and cluster-scoped execution groups. The SQL execution publisher owns a
separate `ZeroSizeThreadPool` handle and is never started. Its `IScheduler`
facade admits only validated single-job writes. RAMJobStore handles cannot
share data; the memory publisher therefore forwards writes to the in-process
execution scheduler, while retaining the non-startable facade. The two modes
are deliberately different and memory is not SQL proof.

The attributed upstream SQL Server schema snapshot is retained in
`database/upstream/quartz-4.5.1/`. `0002_quartz.up.sql` installs both prefixed
table sets and runtime DML/schema-history read grants. Its paired down script
refuses populated stores before dropping both sets. The manifest has SHA-256
checksums for both scripts. The SQL process fixture supplies credentials by
environment, creates/drops only its generated database, and controls probe
jobs through start/release barriers.

## Red and green evidence

- Red: `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~Memory_is_disposable_and_refuses_another_node_or_tenant' --no-restore` failed at the new group-limit assertion: `Assert.Throws() Failure: No exception was thrown` (1 failed). A memory scheduler previously accepted a second configuration with different template limits.
- Green: the same command passed (1 passed) after rejecting mismatched memory-node limits.
- `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzTopologyTests' --no-restore`: 6 passed, 0 failed. This covers memory delivery through a non-startable publisher, data validation, single-node/tenant isolation, disposal, and SQL-unavailable error handling.
- `dotnet build WorkerDock.slnx -c Release --no-restore`: succeeded, 0 warnings, 0 errors.
- Script audit: manifest SHA-256 values match both `0002_quartz` files; the up script contains 15 tables and 15 DML grants for each prefix.
- `dotnet test tests/WorkerDock.IntegrationTests -c Release --no-build --no-restore --logger 'console;verbosity=quiet'`: 51 passed, 14 failed. The 14 are the six Task03 SQL cases and eight pre-existing migration/tenant-settings SQL cases, all requiring the unavailable disposable SQL fixture.

## SQL gate and exact remaining proof

`dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzCapacityTests|FullyQualifiedName~QuartzSqlTopologyTests' --no-build --no-restore` returned 6 failures, all `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` No SQL case ran. The available real SQL login was rejected; no existing database or SQL permission was changed.

Configure `WORKERDOCK_TEST_SQL` for an authorized disposable SQL Server 2022+
instance and rerun that exact command. It must pass with
`READ_COMMITTED_SNAPSHOT` OFF and ON, including three-node locked capacity,
non-started publisher, persisted delivery across publisher restart, two-worker
single delivery, a populated-store rollback refusal, least-privilege DML,
and schema up/down/up. Those assertions and SQL compatibility remain
unresolved until the live run succeeds. Memory results do not establish
durability, cluster capacity, restart behavior, security isolation, or
migration correctness.

## Files

- `src/WorkerDock.Quartz/` — topology, node options, SQL identity checks,
  publisher facade, package lock.
- `database/upstream/quartz-4.5.1/`, `database/tenant/0002_quartz.*.sql`,
  `database/tenant/manifest.json` — attributed schema and migration pair.
- `tests/WorkerDock.IntegrationTests/QuartzTopologyTests.cs`,
  `QuartzCapacityTests.cs`, `QuartzProcessFixture.cs`, and
  `tests/WorkerDock.TestHost/` — memory and gated SQL process tests.
- `docs/quartz-integration.md`, `docs/development.md`, `docs/database.md` —
  exact memory and SQL setup, commands, and proof boundaries.
- Solution, central package versions, integration test project and locks.

## Concerns

The SQL publisher's never-started handle and cluster-scoped limits compile
against pinned public Quartz 4.5.1 APIs, but the actual SQL behavior and
least-privilege grants still require the gated live run. An empty-table
rollback guard cannot prevent a stopped-and-restarted external writer;
operators must stop all related processes before downgrade.
