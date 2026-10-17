# WorkerDock Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run durable manual jobs through Quartz with isolated children, safe recovery and configurable tenant-wide capacity.

**Architecture:** Business commands commit execution intent and audit; an outbox projects it into Quartz. A guarded wrapper owns each child's supervised lifetime, while the application records retry/stop/outcome policy and Quartz owns dispatch capacity.

**Tech Stack:** Stage 01 stack, Windows Job Objects, ACL-restricted named pipes, real SQL/process integration tests.

**Spec:** [Approved design](../specs/2026-10-17-workerdock-design.md), sections 4–6, 8–9 and 13. Depends on [Stage 01](2026-10-17-workerdock-01-foundation.md).

## Global Constraints

- All [index](2026-10-17-workerdock-implementation.md) constraints apply.
- "Workers do not poll a WorkerDock work table and perform their own queue-claim algorithm."
- "WorkerDock does not claim exactly-once effects."
- "Infinite execution timeout does not disable ownership deadlines."
- "Never call Quartz, a vault, a network endpoint or a child inside that transaction, and never acquire application locks while directly manipulating Quartz rows."

## Review Focus

Project focus 1 is covered by Task 05 (stale browser edits/deduplicated POST), and focus 2 by Task 09 (offline node/policy revision). Tasks 06–08 additionally pin lost acknowledgements and stop/completion races.

## File structure

Domain records and command transactions belong to Task 05; publication belongs to 06. Task 07 owns process control independent of SQL. Task 08 connects that control to durable attempt state and Quartz. Task 09 adds coordinated capacity and per-job admission, without duplicating Quartz reservations.

### Task 05: Immutable jobs, manual intent and request deduplication

**Files:** Create `src/WorkerDock.Domain/Jobs/JobRevision.cs`, `ExecutionPolicy.cs`, `src/WorkerDock.Domain/Executions/ExecutionState.cs`, `src/WorkerDock.Application/Common/CommandReceipt.cs`, `src/WorkerDock.Application/Jobs/IJobCommands.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobCommands.cs`, `src/WorkerDock.Web/Endpoints/JobCommandsEndpoints.cs`; `database/tenant/0003_execution.up.sql`, `0003_execution.down.sql`; modify manifest/Web registration; create `tests/WorkerDock.IntegrationTests/JobCommandTests.cs`, `docs/execution-guarantees.md`.

**Interfaces:**
- Consumes Tasks 01–02 ActorContext, transactions and access policies.
- Defines `JobRevision(Guid JobId, long Revision, string Name, string TemplateId, string TemplateVersion, JsonElement Configuration, Guid? ProfileId, ExecutionPolicy Policy)` and `ExecutionPolicy(TimeSpan? Timeout, int MaxAttempts, TimeSpan RetryBaseDelay, TimeSpan RetryMaxDelay, double JitterRatio, IReadOnlyList<string> RetryableCodes, bool RetryUncertain, string Overlap, int BacklogLimit, int Priority)`; null timeout means infinite, zero/negative is invalid. Overlap is Queue/Skip/Allow; live job concurrency is stored outside immutable revisions. Job revisions reference a profile identity; each new execution resolves and pins its current nonsecret profile revision, preserving that revision across retries. Task 11 adds actual profile storage/validation.
- Defines execution states exactly as spec section 4; outcome certainty is separate Known/Unknown. `CommandReceipt(Guid CommandId, Guid TargetId, string State)` is the index contract.
- `IJobCommands.SaveAsync(JobRevision proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; null version is create only. `RunNowAsync(Guid jobId, string requestKey, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; `GetCommandAsync(Guid commandId, CancellationToken ct) -> ValueTask<CommandReceipt>`.
- Exposes management `POST /api/jobs`, `PUT /api/jobs/{id}`, `POST /api/jobs/{id}/runs` and safe `GET /api/commands/{id}`; server supplies actor and enforces CSRF/If-Match.

- [ ] **Step 1 — Write tests.** `JobCommandTests.Stale_edit_returns_412_without_revision_or_audit` uses simultaneous editors; `Repeated_manual_key_returns_original_for_24_hours` uses a controllable TimeProvider and two simultaneous requests. After 24h the key may create a new run; different input under the same scope/key fails. Also verify disabled/archived rejection, actor/anonymous-session scoping, immutable revision pinning and no mutation without audit/outbox.

```csharp
Assert.Equal(412, staleEdit.StatusCode);
Assert.Equal(first.TargetId, concurrentRepeat.TargetId);
Assert.NotEqual(first.TargetId, afterWindow.TargetId);
Assert.Equal(1, executionCountBeforeExpiry);
Assert.Equal(originalRevision, pinnedExecutionRevision);
Assert.Equal("RequestKeyConflict", changedInput.Code);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~JobCommandTests`; expect command/deduplication failures.
- [ ] **Step 3 — Implement.** Create jobs/revisions, catalog metadata identity, executions, attempts, permits, workers, service health, policy revisions and outbox tables with unique constraints from spec section 4. Use Task 01's transaction and 24h compact dedup records. Structural/template-reference validation uses seeded trusted catalog metadata until Task 10 provides registration; do not accept an arbitrary unknown template. POST returns 202 plus command/execution ID. Keep query text/configuration out of safe response/audit projections. Add documented idempotency scope/delivery limitations.
- [ ] **Step 4 — Verify green.** Repeat tests plus AccessPolicyTests; expected PASS and no partial revision/audit/outbox changes after injected SQL failures.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: persist immutable jobs and deduplicated execution intent"`.

### Task 06: Idempotent Quartz publication and reconciliation

**Files:** Create `src/WorkerDock.Application/Dispatch/DispatchIntent.cs`, `IProjectionStore.cs`, `src/WorkerDock.Infrastructure/Dispatch/SqlProjectionStore.cs`, `src/WorkerDock.Quartz/Dispatch/QuartzPublisher.cs`, `ProjectionJob.cs`, `DispatchReconciler.cs`; create Scheduler project and `src/WorkerDock.Scheduler/Program.cs`; `tests/WorkerDock.IntegrationTests/PublicationRecoveryTests.cs`; update `docs/quartz-integration.md`.

**Interfaces:**
- Consumes Task 03 topology and Task 05 execution/outbox records.
- `DispatchIntent(Guid DeliveryId, Guid ExecutionId, int AttemptOrdinal, string TemplateId, DateTimeOffset DueUtc, int Priority, long EntityRevision, string PayloadDigest)`; all Quartz data stays opaque/string-only.
- `IProjectionStore.ReadPendingAsync(int limit, CancellationToken ct) -> ValueTask<IReadOnlyList<DispatchIntent>>`; `AcknowledgeAsync(Guid deliveryId, string payloadDigest, CancellationToken ct) -> ValueTask`; `RecordFailureAsync(Guid deliveryId, string safeCode, DateTimeOffset retryAtUtc, CancellationToken ct) -> ValueTask`.
- `QuartzPublisher.PublishAsync(DispatchIntent intent, CancellationToken ct) -> ValueTask`; `DispatchReconciler.ReconcileAsync(int batchSize, CancellationToken ct) -> ValueTask<int>` returns repaired/retired count. Production wrapper type is registered in Task 08; test probes from Task 03 prove publication independently here.

- [ ] **Step 1 — Write fault tests.** `PublicationRecoveryTests.Crash_after_store_write_before_ack_does_not_duplicate_authorized_run` injects each side of publication/ack; `Consumed_trigger_is_not_proof_of_missing_execution` consumes/deletes the trigger before ack and seeds completed business state. Test mismatched digest, concurrent publishers, stale entity revision, failed Quartz API, permanently errored trigger and delayed retry deadline.

```csharp
Assert.Equal(1, persistedDeliveryIdentities);
Assert.Equal(0, republishedCompletedExecutions);
Assert.Equal("ProjectionIntegrityError", mismatchedPayload.Code);
Assert.True(retryTriggerUtc >= approvedRetryDueUtc);
Assert.True(unavailableStoreIntentRemainsPending);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~PublicationRecoveryTests`; expect recovery/publication failures.
- [ ] **Step 3 — Implement.** Deterministic key names encode execution/delivery/ordinal, not display name. Use public Quartz APIs outside business transactions; verify immutable digest on replay. Explicitly suppress Quartz automatic retries, keep dispatch misfires, request guarded recovery, and persist application ack independently. Control-job bounded batches reconcile intent versus terminal/active state; they do not claim jobs or retry effects. Scheduler starts only its control scheduler and retains the non-started execution publisher. Document fault boundaries and visible pending/error states.
- [ ] **Step 4 — Verify green.** Repeat tests with restarted Quartz nodes and concurrent publishers. Expected PASS; no retry/repair resurrects terminal work or relies only on trigger absence.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: publish and reconcile Quartz dispatch intents safely"`.

### Task 07: Trusted template contract and contained execution process

**Files:** Create Contracts, Runtime, Executor and TestTemplates projects; `src/WorkerDock.Contracts/Templates/IWorkerTemplate.cs`, `TemplateDescriptor.cs`, `TemplateExecutionContext.cs`, `TemplateResult.cs`, `Secrets/ISecretVault.cs`, `Logging/ISafeExecutionLog.cs`; `src/WorkerDock.Runtime/Processes/ContainedProcess.cs`, `ExecutionSupervisor.cs`, `Ipc/ExecutionProtocol.cs`; `src/WorkerDock.Executor/Program.cs`; `tests/WorkerDock.ProcessTests/ContainmentTests.cs` and project; `tests/WorkerDock.TestTemplates/BlockingTemplate.cs`, `SpawningTemplate.cs`; `docs/custom-templates.md`.

**Interfaces:**
- `TemplateDescriptor(string Id, string Version, string DisplayName, string Description, JsonElement ConfigurationSchema, JsonElement UiHints, string MetadataDigest)`; immutable exact-version registration. Safe schema/reference declarations are schema annotations specified in Task 10.
- `IWorkerTemplate.Descriptor { get; }`, `Validate(JsonElement configuration) -> IReadOnlyList<ValidationIssue>`, `ExecuteAsync(TemplateExecutionContext context, CancellationToken ct) -> ValueTask<TemplateResult>`; `ValidationIssue(string Path, string Code)`.
- `TemplateExecutionContext(Guid ExecutionId, int AttemptOrdinal, string IdempotencyKey, JsonElement Configuration, Guid? ProfileRevisionId, JsonElement? ProfileConfiguration, ISecretVault Secrets, ISafeExecutionLog Log, DateTimeOffset? DeadlineUtc)`; no access to scheduler internals. ProfileRevisionId is the execution's pinned identity; the supervisor supplies only that revision's nonsecret fields/reference metadata in ProfileConfiguration. The child needs no WorkerDock database login. `TemplateResult(string Outcome, string SafeCode, bool EffectsMayHaveOccurred, JsonElement SafeSummary)`; outcome Success/TransientFailure/PermanentFailure.
- `SecretReference(string Name)`; `ISecretVault.ResolveAsync(SecretReference reference, CancellationToken ct) -> ValueTask<SecretValue>`, with a scoped `SecretValue` whose value is explicitly accessed and whose `ToString()` returns `[REDACTED]`. `ISafeExecutionLog.WriteAsync(string eventCode, IReadOnlyDictionary<string,string> safeFields, CancellationToken ct) -> ValueTask`.
- `ExecutionSupervisor.StartAsync(Guid attemptId, long generation, TemplateExecutionContext context, CancellationToken ct) -> ValueTask<ChildHandle>`; serialize only nonsecret configuration/references, not injected facade objects. `ChildHandle.WaitAsync(CancellationToken ct) -> ValueTask<TemplateResult>`, `CancelAsync(CancellationToken ct) -> ValueTask`, `TerminateAsync(CancellationToken ct) -> ValueTask`; handle owns local process identity and containment.

- [ ] **Step 1 — Write Windows tests.** `ContainmentTests.Supervisor_exit_kills_child_and_grandchild` kills the supervisor; `Force_stop_of_one_noncooperative_child_preserves_sibling` runs two children. Verify pre-handshake containment failure never executes the handler; wrong pipe nonce/identity is rejected; oversized messages are bounded and secret sentinel is absent from argv/stdout/persisted diagnostics.

```csharp
Assert.False(childStillAlive);
Assert.False(grandchildStillAlive);
Assert.True(siblingStillAlive);
Assert.Equal(0, handlerEntriesAfterContainmentFailure);
Assert.DoesNotContain(secretSentinel, capturedArguments);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.ProcessTests --filter FullyQualifiedName~ContainmentTests`; expect containment/IPC behavior failures on Windows.
- [ ] **Step 3 — Implement.** Use a Windows Job Object with kill-on-close/no breakaway, contain the process before the allowed-execute handshake, restrictive named-pipe ACLs and nonce/generation checks. Bound protocol messages/startup time; discard raw child stdout/stderr. Child resolves injected vault/logging adapters inside its composition root; parent passes references, never resolved secrets. Only installed allowlisted packages can register; test handlers live exclusively in test outputs. Document trusted-code assumptions and the contract with a nonexecuted authoring example.
- [ ] **Step 4 — Verify green.** Repeat tests and inspect remaining test processes; expected PASS, no surviving contained descendants, sibling remains alive until fixture cleanup. No admin elevation should be needed for ordinary child containment.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: supervise trusted handlers in contained child processes"`.

### Task 08: Durable ownership, retry, stop and uncertain-outcome recovery

**Files:** Create `src/WorkerDock.Application/Executions/IAttemptStore.cs`, `IExecutionControls.cs`, `src/WorkerDock.Infrastructure/Executions/SqlAttemptStore.cs`, `SqlExecutionControls.cs`, `src/WorkerDock.Domain/Executions/RetryDecision.cs`, `src/WorkerDock.Quartz/Dispatch/ExecuteTemplateJob.cs`, `RecoveryReviewJob.cs`, `src/WorkerDock.Runtime/Ownership/OwnershipMonitor.cs`; create Worker project/`Program.cs`; modify Scheduler/Executor registration; create `src/WorkerDock.Web/Endpoints/ExecutionControlsEndpoints.cs`, `tests/WorkerDock.IntegrationTests/AttemptLifecycleTests.cs`, `tests/WorkerDock.ProcessTests/ExecutionControlTests.cs`; update guarantees guide.

**Interfaces:**
- Consumes Tasks 05–07 records, dispatch and supervisor; Task 03 Quartz nodes.
- `AttemptLease(Guid AttemptId, Guid ExecutionId, int Ordinal, long Generation, string WorkerBootId, DateTimeOffset ExpiresUtc, DateTimeOffset? DeadlineUtc)` and `AttemptStart(string Decision, AttemptLease? Lease)`; Decision Started/AlreadyOwned/Completed/Held/NeedsReview.
- `IAttemptStore.BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) -> ValueTask<AttemptStart>`; `RenewAsync(AttemptLease lease, CancellationToken ct) -> ValueTask<AttemptLease?>`; `CompleteAsync(AttemptLease lease, TemplateResult result, bool localExitConfirmed, CancellationToken ct) -> ValueTask`; `ReviewExpiredAsync(int limit, CancellationToken ct) -> ValueTask<int>`.
- `IExecutionControls.StopAsync(Guid executionId, bool force, string reason, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; `ResolveAsync(Guid executionId, string decision, string reason, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>` where decision is ReconcileSucceeded/Cancel/AuthorizeRetry. Endpoints require Manage; force requires explicit confirmation in request plus reason.

- [ ] **Step 1 — Write tests.** Use deterministic barriers for two owners, expiry, stop-first/completion-first, lost DB acknowledgement, crash-before-launch and crash-after-effects. `AttemptLifecycleTests.Unknown_effects_hold_unless_explicitly_replay_safe` retains the job permit; `ExecutionControlTests.Infinite_attempt_stops_while_all_Quartz_slots_are_busy` saturates the pool. Include one bounded retry with stable idempotency key, permanent failure, retry-budget exhaustion and timeout grace.

```csharp
Assert.Equal("NeedsReview", uncertainUnsafe.State);
Assert.Equal(originalKey, authorizedRetry.IdempotencyKey);
Assert.Null(expiredOwnerRenewal);
Assert.Equal("Stopped", stopFirst.State);
Assert.Equal(0, automaticRetriesAfterStop);
Assert.Null(infiniteLease.DeadlineUtc);
Assert.Equal(TimeSpan.FromSeconds(30), configuredCancellationGrace);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~AttemptLifecycleTests` and `dotnet test tests/WorkerDock.ProcessTests --filter FullyQualifiedName~ExecutionControlTests`; expect ownership/control failures.
- [ ] **Step 3 — Implement.** Wrapper awaits the child; application-authorized attempt uses 10s renewal/60s lease and conservative monotonic safety deadline. Renew/complete require generation, boot ID and unexpired SQL-time fence. Control polling is independent of Quartz slots and scheduler availability. Persist stop before acknowledging, allow 30s cooperative grace then contained kill, preserve Stop unconfirmed/unknown outcome if unreachable. Timeout null is infinite; finite starts at admitted attempt/startup. Classify attempts and create approved future retry intents transactionally, with bounded exponential jitter and stable execution key; no sleeps or refire business retries. Mark abandoned work without deleting Quartz rows. Shutdown stops acquisition and drains for bounded configured time, then contains remaining processes.
- [ ] **Step 4 — Verify green.** Repeat both commands plus publication tests with actual wrapper instead of probe. Expected PASS across all injected boundary faults, no stale completion, no automatic retry after operator stop; local termination never claims remote rollback.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add durable attempt recovery timeout and stop controls"`.

### Task 09: Live limits, capability admission and per-job ordering

**Files:** Create `src/WorkerDock.Application/Workers/ICapacityCommands.cs`, `src/WorkerDock.Infrastructure/Workers/SqlCapacityCommands.cs`, `src/WorkerDock.Quartz/Capacity/CapacityCoordinator.cs`, `src/WorkerDock.Runtime/Workers/WorkerRegistration.cs`, `src/WorkerDock.Infrastructure/Dispatch/JobAdmission.cs`; modify AttemptStore/publisher/Worker composition and add management endpoints; create `tests/WorkerDock.IntegrationTests/CapacityPolicyTests.cs`, `JobAdmissionTests.cs`; update Quartz guide.

**Interfaces:**
- Consumes Task 08 start gate and Task 03 cluster group configuration; Task 05 permit/policy/worker tables.
- `CapacitySnapshot(long DesiredRevision, long EffectiveRevision, string State, IReadOnlyDictionary<string,int> TemplateLimits)`.
- `ICapacityCommands.SetTemplateLimitAsync(string templateId, int limit, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; `SetJobLimitAsync(Guid jobId, int limit, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; `SetWorkerLimitAsync(Guid workerId, int limit, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`.
- `CapacityCoordinator.ApplyPendingAsync(CancellationToken ct) -> ValueTask<CapacitySnapshot>`; `WorkerRegistration.RegisterAsync(Guid installationId, string bootId, string catalogDigest, int ceiling, CancellationToken ct) -> ValueTask`.
- `JobAdmission.AdmitAsync(Guid jobId, CancellationToken ct) -> ValueTask<int>` allocates logical permits and emits eligible intents under the business lock; it never executes/claims Quartz triggers.

- [ ] **Step 1 — Write race tests.** `CapacityPolicyTests.Offline_node_cannot_rejoin_with_old_policy` closes the gate, pauses acknowledgements, rejoins and attempts launch; `Decrease_does_not_kill_running_children` changes 3→1→0→2 with in-flight work. `JobAdmissionTests.Retry_and_review_hold_exclusive_permit` mixes Queue/Allow, job-limit decrease, retries and skip/backlog overflow. Test matching/mismatched catalog and host ceiling validation.

```csharp
Assert.Equal(0, startsThroughClosedGate);
Assert.Equal("Pending", policyWithMissingAcknowledgement.State);
Assert.Equal(0, killedByLimitDecrease);
Assert.Equal(1, maxSqlAttemptsWithLimitOneAcrossThreeNodes);
Assert.False(laterExecutionOvertookRetry);
Assert.Equal("CatalogMismatch", incompatibleNode.State);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~CapacityPolicyTests|FullyQualifiedName~JobAdmissionTests"`; expect propagation/ordering failures.
- [ ] **Step 3 — Implement.** Transactionally gate new children, put nodes in standby, apply the same Quartz limits snapshot, acknowledge and resume; fenced unavailable nodes require Quartz reservation recovery before promotion. Registration loads effective revision/catalog before acquisition. Local runtime capacity cannot exceed installed pool ceiling. Queue permits are allocated on dispatch admission and retained through retry/review; decreases allow holders' retries, shared/exclusive ordering is fair within the job, and skip/overflow is recorded. Deferred wrappers return without consuming attempts; release/enable/policy events make their intents eligible again. Map due time/priority without promising strict global FIFO; bounded aging updates pending trigger priority through Quartz only.
- [ ] **Step 4 — Verify green.** Repeat tests with multiple service-process nodes and real SQL. Expected PASS, capacity 1 remains 1 across versions/services; no new deployment for job creation or live limits.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: coordinate live capacity and logical job admission"`.

## Stage exit

- [ ] Run changed unit/integration/process suites and Release build. Demonstrate manual execution with test-only handlers, infinite execution/force stop, retry/review and three-node capacity.
- [ ] Record publication/ownership fault evidence. Remaining limitation: built-in SQL/HTTP handlers and full operator UX are delivered next; do not ship test handlers in runtime packages.
