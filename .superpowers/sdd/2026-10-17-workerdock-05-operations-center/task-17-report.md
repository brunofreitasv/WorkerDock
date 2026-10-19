# Task 17 report — Bounded observability and optional OpenTelemetry

Implemented an additive event query contract and Read-authorized `GET /api/monitoring/executions/{id}/events` endpoint, with stable cursor paging capped at 100 rows. The SQL event writer validates source, code, fields, payload size, timestamp, and event identity; unique execution/attempt/source/sequence keys make retries idempotent. The query combines persisted supervisor events with mandatory attempt lifecycle rows and excludes arbitrary logs/details.

Added a bounded best-effort event queue, isolated log sink failures and invalid/flooded diagnostics, aggregation into SQL hourly totals, and built-in `WorkerDock` ActivitySource/Meter instruments. Metric tags are allowlisted and exclude execution/correlation identifiers. The worker catches optional aggregation failures. Migration 0006 adds only AuditEvents SELECT to WorkerDockRuntime, preserving INSERT and adding no audit UPDATE/DELETE. The migration is manifest-hashed and rollback refuses to remove populated observability data.

The executor IPC currently carries no handler log frame; handler logs remain in local Trace output and are not persisted. The supervisor records its terminal outcome asynchronously into a bounded queue. Queue backpressure, sink failure, or event persistence failure cannot change the committed execution result. Authentication telemetry is not emitted because the host does not expose authoritative auth outcomes.

Verification:

- `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~ObservabilityTests`: PASS, 2/2.
- `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~ObservabilityTests|FullyQualifiedName~Every_paired_migration_targets_custom_schema_and_matches_exact_manifest_bytes"`: PASS, 3/3.
- Final focused telemetry unit suite: PASS, 5/5; `dotnet build WorkerDock.slnx --no-restore`: PASS, 0 warnings/errors.
- Build of WorkerDock.Web, Infrastructure, and test projects completed as part of the focused integration run.
- Full integration suite was attempted; SQL-backed cases fail at fixture startup with the exact `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` blocker. After adding migrations 0005/0006 to the static expected list, the manifest check passes.
- SQL-backed event idempotency and aggregate runtime checks: `SQL_TEST_BLOCKED`; `WORKERDOCK_TEST_SQL` is unset.
- Optional exporters are not installed/configured; instrumentation uses built-in .NET ActivitySource/Meter APIs and has no exporter by default.
