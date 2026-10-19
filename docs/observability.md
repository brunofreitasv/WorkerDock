# Observability

WorkerDock persists bounded execution events in `workerdock.ExecutionEvents` and exposes them through `GET /api/monitoring/executions/{id}/events`. The Read policy applies. Pages default to 50 and cap at 100; the cursor is a stable timestamp/event-ID pair. Unknown executions return 404 and storage failures return 503. Lifecycle history is projected from `AttemptStatusEvents`; this remains the authoritative mandatory lifecycle stream.

Optional supervisor events use a bounded in-memory channel (128 batches by default). The worker drops on full queue, database failure, or shutdown timeout; none of these conditions changes an execution result. Event writes are idempotent by execution/attempt/source/sequence. Payloads allow only a small event-code and field allowlist, with a 2 KiB JSON cap. `SafeExecutionLog` caps accepted entries and emits a single bounded drop marker. Authentication event storage is not wired because the current host does not expose authoritative authentication outcomes to the application; no synthetic events are emitted.

The isolated executor IPC currently carries only its handshake, execution request/control, and terminal result. Handler log frames are therefore not relayed or persisted. Executor stdout/stderr are discarded by the supervisor. Supervisor result events and the mandatory SQL lifecycle projection remain available without changing IPC framing or execution semantics.

The `WorkerDock` `ActivitySource` and `Meter` use low-cardinality tags only: allowlisted template ID, outcome, and component. Execution and correlation identifiers are excluded from metric tags. Instrumentation uses .NET's built-in APIs; exporters are absent and disabled by default. Hosts may attach listeners/exporters. SQL aggregates are committed before those optional metrics are recorded, and aggregation failures are isolated from job execution.

Migration `0006_observability` adds the event and aggregate tables and adds only `SELECT` on `AuditEvents` to `WorkerDockRuntime`, preserving its existing `INSERT` permission. It grants no audit `UPDATE` or `DELETE`. Rollback refuses to remove populated diagnostics or aggregates. Runtime processes do not run migrations; deployment applies the manifest-verified migration.

SQL-backed integration checks require `WORKERDOCK_TEST_SQL`; local SQL verification is blocked when it is unset. The in-memory web adapter does not provide persisted event history.
