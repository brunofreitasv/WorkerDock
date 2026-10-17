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

## FIX round 1 — independent review

Addressed the three Important review findings:

1. `TestHost_output_preserves_native_SNI_assets` failed red with `Assert.True() Failure` because the copied TestHost omitted `runtimes/win-x64/native/Microsoft.Data.SqlClient.SNI.dll`. The integration test project now recursively copies the complete TestHost output with relative paths. The same filtered command passed green (1 passed), and the check also covers the paired probe scripts.
2. `Quartz_runtime_connection_requires_a_distinct_sql_login` failed red with `NotImplementedException` after a test helper stub was introduced, then passed green (1 passed). `WORKERDOCK_TEST_SQL_RUNTIME` must identify a separate SQL-authentication login on the same disposable server. The fixture maps it only in the generated database, adds it to `WorkerDockRuntime`, verifies that it lacks database-owner/CREATE TABLE rights, and supplies that connection to every spawned publisher/worker. The fixture-owned `Sql/quartz-probe.up.sql` and `.down.sql` pair creates/removes the restricted database user and probe objects. The test operator must provision the separate server login; no existing SQL permission was changed here.
3. `Locked_acquisition_does_not_multiply_capacity` now arms all three worker processes and waits for each cluster registration before publishing. While the first job is held, it waits for another database check-in from each node. Probe jobs update a single SQL state row transactionally on start/end and write events with their boot IDs; assertions use the SQL maximum active count, start/end rows, and participation by three distinct nodes. Two workers enter standby while their held jobs run to force the remaining nodes to take later work. The real behavior is still SQL-gated, so no pass is claimed.

Verification commands and outcomes after these edits:

- Red: `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~TestHost_output_preserves_native_SNI_assets' --no-restore` → 1 failed, missing native asset. Green: same command → 1 passed.
- Red: `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~Quartz_runtime_connection_requires_a_distinct_sql_login' --no-restore` → 1 failed, expected `InvalidOperationException`, received `NotImplementedException`. Green: same command → 1 passed.
- `dotnet build WorkerDock.slnx -c Release --no-restore` → succeeded, 0 warnings, 0 errors.
- `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzTopologyTests' --no-build --no-restore` → 8 passed, 0 failed.
- `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzCapacityTests|FullyQualifiedName~QuartzSqlTopologyTests' --no-build --no-restore --logger 'console;verbosity=quiet'` → 6 failed, all blocked before SQL setup by `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` With that variable supplied, the separate runtime login variable is also required.
- `dotnet test tests/WorkerDock.IntegrationTests -c Release --no-build --no-restore --logger 'console;verbosity=quiet'` → 53 passed, 14 SQL fixture cases blocked, 0 skipped.

The SQL registration barrier, restricted principal, publisher write, execution,
cluster overlap, restart, and paired fixture scripts still require a live run
against an authorized disposable SQL Server. The minor mutable caller-owned
`TemplateLimits` dictionary concern is deferred to final-branch triage.
