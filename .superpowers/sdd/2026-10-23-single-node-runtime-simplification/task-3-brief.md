### Task 3: Child supervision, restart classification, and retry

**Files:** Create `src/WorkerDock.Runtime/Workers/ExecutionDispatchLoop.cs`; modify `src/WorkerDock.Runtime/Ownership/OwnershipMonitor.cs`, `tests/WorkerDock.ProcessTests/ExecutionControlTests.cs`, `tests/WorkerDock.IntegrationTests/AttemptSqlTests.cs`. Task 2 established SQL expiry and review behavior in `SqlAttemptStore`; Task 3 reuses `IAttemptStore.ReviewExpiredAsync`. Existing `AttemptLifecycleTests` remain policy regression coverage and are not edited by this task.

**Interfaces:** `ExecutionDispatchLoop(IExecutionQueue queue, IAttemptStore attempts, IAttemptRunner runner, TimeProvider clock, int maxChildren).RunOnceAsync(CancellationToken ct) : ValueTask<int>`. Reuse `ExecutionSupervisor` and `AttemptLease`; no child-facing protocol change.

- [ ] Add `RestartAfterPossibleHandlerEntry_RequiresReview` in `AttemptSqlTests`. Cover concurrent children within `maxChildren`, pre-entry descriptor failure, future retry delay, stop/drain, and explicitly permitted bounded replay in `ExecutionControlTests`.
- [ ] Run `dotnet test tests/WorkerDock.ProcessTests/WorkerDock.ProcessTests.csproj --filter FullyQualifiedName~ExecutionControlTests`; expect the new direct-dispatch tests to fail before implementation. Run the SQL regression with `WORKERDOCK_TEST_SQL` using `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~AttemptSqlTests`.
- [ ] Run existing lifecycle-policy coverage with `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~AttemptLifecycleTests`; it remains regression coverage and is not edited by this task.
- [ ] Implement bounded dispatch using Task ownership, existing renewal/containment, and `ReviewExpiredAsync(100, ct)` before admitting new work. Keep exact completion fencing and stable execution idempotency key.
- [ ] Run the process, SQL, and lifecycle filters above; expect pass when the SQL test connection is configured.
- [ ] Commit: `feat: supervise SQL-dispatched execution children`.
