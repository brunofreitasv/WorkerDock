# Task 6 report: Persist binding plans and execution value state

## Result

Implemented nullable dynamic plan persistence on immutable job revisions, binding-plan validation, dynamic payload reservation, execution value-state migration constraints and immutability, archive inclusion, and SQL fixture upgrades to migration 0009. Legacy fixed revisions remain null. No historical rows are backfilled.

## RED / GREEN

- RED: the new freeze regression initially failed to compile because `JobRevision.DynamicValues` did not exist yet.
- GREEN: `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~Freeze_clones_dynamic_value_plan|FullyQualifiedName~Dynamic_payload_reserves_snapshot_space'` passed (2/2).
- GREEN: `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~JobCommandTests` passed (22/22).
- GREEN: `dotnet build WorkerDock.slnx` passed with 0 warnings and 0 errors.

## SQL verification blocker

The requested filters were run, but SQL fixture execution is unavailable in this environment:

- `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~DynamicValuePersistenceTests|FullyQualifiedName~JobCommandTests|FullyQualifiedName~JobLifecycleTests'` compiled; 22 passed and 20 SQL tests were blocked at fixture setup.
- `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~MigrationTests|FullyQualifiedName~JobSqlIntegrationTests'` compiled; 3 passed and 13 SQL tests were blocked at fixture setup.
- Blocker from `SqlDatabaseFixture`: `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` No fixture was weakened.

SQL test coverage now includes save/load/duplicate preservation, null legacy rows after upgrade, refusal to roll back populated plans/context/snapshots, and archive JSON context/snapshot retention without secrets. These assertions compiled but could not execute without the SQL fixture prerequisite.

Migration checksum verification:

- `0009_dynamic_values.up.sql`: `BA69116D9F74F839666A7C2A9FFA274E144D6F5CAF070406B2ED36EBE845662F`
- `0009_dynamic_values.down.sql`: `BC4F28E9212C2EDB56CA6F57627BC49F2E6F2929484C58D054381112F7D8B0F4`
- Both manifest hashes match `Get-FileHash -Algorithm SHA256`. No migration files or hashes before 0009 changed.

## Remaining prerequisite

Run the SQL-backed persistence, upgrade, rollback, retention archive, and lifecycle tests with `WORKERDOCK_TEST_SQL` configured before release.
