# Task 6 report: Persist binding plans and execution value state

## Result

Dynamic plans are persisted as nullable sidecars on immutable job revisions. Execution context, first-start time, resolved snapshots, and resolution errors are persisted with immutability checks. Legacy records remain null; migration 0009 does not backfill historical values. Archive output retains execution context and snapshots without secrets.

The Task 6 review follow-up was committed as `6ae7de9`. It fixed SQL trigger comparisons for populated empty-string and sentinel values, and validates job enablement using the active schedule state. An unscheduled job is validated as `Manual`; a scheduled job is validated as `Scheduled`.

The later review found that SQL text equality can ignore case and trailing spaces. The trigger now compares the UTF-16 bytes of populated context, snapshot, and error text. Regression cases cover case-only and trailing-space rewrites as well as attempts to clear populated values.

## Verification

- Initial implementation: the freeze regression first failed to compile because `JobRevision.DynamicValues` was missing; after implementation, the freeze and dynamic payload tests passed (2/2), and `JobCommandTests` passed (22/22).
- Current verification: `dotnet build WorkerDock.slnx -c Release --no-restore` passed with zero warnings and errors; `dotnet test tests/WorkerDock.IntegrationTests -c Release --no-build --filter 'Store!=Sql'` passed (351/351).
- The SQL trigger regression cases compile, but SQL-backed persistence, migration, rollback, archive, lifecycle, and trigger tests could not execute. `WORKERDOCK_TEST_SQL` is unset; the local SQL service could not be used because integrated authentication failed with `Failed to generate SSPI context`. No SQL fixture was weakened.
- The resolved snapshot cap and full execution-envelope checks were completed in Tasks 8 and 9 before dispatch, as recorded in their task reports. Their SQL restart/concurrency cases remain subject to the same SQL test prerequisite.

## Migration checksums

- `0009_dynamic_values.up.sql`: `A2B10404556787FAEF7E54E6F883E48D735DEA3FD4B4676A37B4E79F71FB940A`
- `0009_dynamic_values.down.sql`: `BC4F28E9212C2EDB56CA6F57627BC49F2E6F2929484C58D054381112F7D8B0F4`
- The manifest contains these hashes; migration files and hashes before 0009 are unchanged.

## Remaining prerequisite

Run the SQL-backed Task 6 and related Tasks 8/9 suites with `WORKERDOCK_TEST_SQL` configured for a disposable SQL Server 2022+ instance.
