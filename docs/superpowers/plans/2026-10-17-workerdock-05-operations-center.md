# WorkerDock Operations and Control Center Implementation Plan

All WorkerDock-owned tables, triggers, keys, indexes and explicit SQL references belong to `[workerdock]` in each database. Each independent tenant database and the separate central registry database owns its own schema; this is not a shared multitenant database or browser-supplied tenant selector. Quartz uses `[workerdock].WDS_QRTZ_` and `[workerdock].WDE_QRTZ_`. System `sys.*` objects and database principals retain their SQL Server names. New objects require paired migrations, checksums and explicit least-privilege grants; runtime startup never creates schema.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add durable observability, webhook alerting, safe archival and a separate central tenant directory.

**Architecture:** Tenant SQL remains authoritative for audit, execution diagnostics, incidents and retention. Quartz runs bounded operational jobs; the central IIS app has its own database/vault identity and observes only registered tenants' minimal health endpoints.

**Tech Stack:** Existing platform, OpenTelemetry .NET, SQL aggregate queries, compressed local archives, webhook sinks, React center entry point.

**Spec:** [Approved design](../specs/2026-10-17-workerdock-design.md), sections 2, 7–8, 10–12. Depends on [Stage 04](2026-10-17-workerdock-04-scheduling-ui.md).

## Global Constraints

- All [index](2026-10-17-workerdock-implementation.md) constraints apply.
- "Telemetry exporter failure does not block job execution."
- "Defaults: safe execution logs 30 days, execution history 90 days, audit 365 days."
- "If archiving fails or storage fills, retain source data and report the retention failure."
- "Registration does not deploy or reconfigure the tenant."

## Review Focus

Project focus 5 is pinned by Task 17 (oversized diagnostics/exporter outage) and Task 19 (disk-full/ACL failure). Task 20 tests a registered URL returning another instance's identity and treats old successful health as stale.

## File structure

Task 17 extends the safe logging/audit/read model; 18 owns incidents and sink delivery; 19 owns archive manifests/retention. Task 20 is a separate web composition root and database, reusing only safe auth/UI/egress infrastructure contracts, never tenant persistence wiring.

### Task 17: Bounded logs, audit queries, aggregates and optional OpenTelemetry

**Files:** Create `src/WorkerDock.Application/Observability/IExecutionEvents.cs`, `IExecutionEventQueries.cs`, `src/WorkerDock.Infrastructure/Observability/SqlExecutionEvents.cs`, `SqlExecutionEventQueries.cs`, `MetricAggregation.cs`, `TelemetryRegistration.cs`, `AuditEventProjection.cs`, and `src/WorkerDock.Web/Endpoints/ExecutionEventEndpoints.cs`; modify SafeExecutionLog/host registrations without changing Task 16 monitoring contracts; `database/tenant/0006_observability.up.sql`, `0006_observability.down.sql`, manifest; `tests/WorkerDock.IntegrationTests/ObservabilityTests.cs`, `tests/WorkerDock.UnitTests/TelemetryProjectionTests.cs`; `docs/observability.md`.

**Interfaces:**
- Consumes Task 07 safe log contract, Task 08 required lifecycle events and Task 16 read DTOs.
- `ExecutionEvent(Guid ExecutionId, Guid? AttemptId, string Source, long Sequence, DateTimeOffset AtUtc, string Code, IReadOnlyDictionary<string,string> SafeFields)`; identity is (ExecutionId, AttemptId, Source, Sequence), treating null AttemptId as a fixed execution-level scope. Source is a server-assigned bounded enum (Lifecycle/Supervisor/Handler); monotonic sequences persist for that scope across retries of a write. Independent attempts may each start handler sequence at 1. SQL uniqueness must cover nullable execution-level scope too.
- `IExecutionEvents.AppendAsync(IReadOnlyList<ExecutionEvent> events, CancellationToken ct) -> ValueTask` for bounded optional diagnostics; mandatory lifecycle events remain in the attempt transaction. `MetricAggregation.AggregateAsync(DateTimeOffset throughUtc, int batchSize, CancellationToken ct) -> ValueTask<int>`.
- Safe event/log paging is a separate additive `IExecutionEventQueries` contract and `/api/monitoring/executions/{id}/events` read route; do not change the Task 16 `IMonitoringQueries` contract. The DTO allowlists bounded safe fields and paginates by stable event identity.
- Stable meter/source name `WorkerDock`; metrics: executions, retries, timeouts, duration, trigger delay, queue delay and heartbeat age. Labels limited to template ID, outcome and component; execution/correlation identifiers only in safe traces/logs.

- [ ] **Step 1 — Write tests.** `ObservabilityTests.Oversized_logs_and_exporter_failure_do_not_replay_success` sends oversize/flooded diagnostics and unreachable exporter. Verify truncation/drop marker, bounded queue, committed success, event idempotency and aggregate counts. Add only `SELECT` on `[workerdock].AuditEvents` for the runtime read principal so Task 16 audit queries work; preserve the existing `INSERT` grant and verify the runtime principal still cannot update/delete audit. Authentication/authorization events use safe coalescing rather than unbounded inserts.

```csharp
Assert.Equal("Succeeded", execution.State);
Assert.Equal(1, executedBusinessAttempts);
Assert.True(logDroppedCount > 0);
Assert.True(bufferedEvents <= configuredBufferLimit);
Assert.Equal(1, storedEventsForRepeatedSequence);
Assert.Equal(2, storedEventsForSequenceOneFromTwoAttempts);
Assert.DoesNotContain("execution_id", metricLabelNames);
Assert.True(runtimeCanSelectAudit);
Assert.False(runtimeCanUpdateAudit);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~ObservabilityTests` and `dotnet test tests/WorkerDock.UnitTests --filter FullyQualifiedName~TelemetryProjectionTests`; expect bounds/aggregate/projection failures.
- [ ] **Step 3 — Implement.** Allowlist event fields, bound field/event/batch sizes and local rotating log buffers, sanitize driver/provider exceptions into codes, deduplicate sequences. Aggregate counts/windows before history purge and keep metric labels bounded. Instrument Activity/Meter/log bridges with optional exporters disabled by default and bounded queues/sampling. Record auth events actually observable by the app, denied-request counts and worker lifecycle changes, not invented upstream events or each heartbeat. Expose safe log paging through the separate `IExecutionEventQueries` contract and route, leaving Task 16 `IMonitoringQueries` unchanged.
- [ ] **Step 4 — Verify green.** Repeat tests with collector disabled and failing; expected PASS, core execution/metrics remain visible from SQL, no secret sentinel anywhere in captured telemetry/logs/audit.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add bounded observability and optional OpenTelemetry"`.

### Task 18: Durable incidents and extensible webhook delivery

**Files:** Create `src/WorkerDock.Contracts/Alerts/IAlertSink.cs`, `src/WorkerDock.Domain/Alerts/AlertRule.cs`, `src/WorkerDock.Application/Alerts/IAlertRules.cs`, `src/WorkerDock.Infrastructure/Alerts/SqlAlertRules.cs`, `IncidentEvaluator.cs`, `WebhookAlertSink.cs`, `src/WorkerDock.Quartz/Alerts/AlertEvaluationJob.cs`, `AlertDeliveryJob.cs`, `src/WorkerDock.Web/Endpoints/AlertEndpoints.cs`; `frontend/apps/tenant/src/features/alerts/AlertRulesPage.tsx`; `database/tenant/0007_alerts.up.sql`, `0007_alerts.down.sql`, manifest; `tests/WorkerDock.IntegrationTests/AlertDeliveryTests.cs`, `frontend/e2e/alerts.spec.ts`; `docs/alerts.md`.

**Interfaces:**
- Consumes Task 17 aggregates/events, Task 12 outbound policy, Task 10 vault adapter and Task 06 projection.
- `AlertRule(Guid Id, string Condition, int Threshold, TimeSpan Window, TimeSpan Grace, TimeSpan ReminderInterval, Guid SinkId, bool Enabled)`; condition RepeatedFailure/RetryExhausted/Timeout/LongRunning/MissedExecution/WorkerUnavailable.
- `IAlertRules.SaveAsync(AlertRule rule, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; incidents store rule/subject/incident dedup key and Open/Resolved/Unknown state.
- `AlertSinkDefinition(Guid Id, string Type, JsonElement Configuration, int MaxAttempts, TimeSpan RetryBaseDelay, TimeSpan RetryMaxDelay, TimeSpan Timeout, bool Enabled)`; only installed sink types, finite positive timeout and bounded retry policy. Webhook configuration has approved URL, safe headers and secret header references, never inline credentials. Add `IAlertSinks.SaveAsync(AlertSinkDefinition sink, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>` and `GetAsync(Guid id, CancellationToken ct) -> ValueTask<AlertSinkEditorDto>`, where `AlertSinkEditorDto(AlertSinkDefinition Definition, byte[] Version)`. Rules/sinks each expose bounded List/Get/Save and rowversion-protected disable via Save (no hard deletion of referenced revisions); add RuleEditorDto equivalently. Management-only configuration routes are `/api/alert-rules` and `/api/alert-sinks`; safe incident/delivery projections use Read policy. Each delivery pins the sink revision and resolves its credential at send time.
- `AlertEnvelope(Guid DeliveryId, Guid IncidentId, string Kind, DateTimeOffset AtUtc, string SafeSummary, string CorrelationId)`; `AlertDeliveryResult(string Outcome, string SafeCode, TimeSpan? RetryAfter)` uses Accepted/TransientFailure/PermanentFailure. `IAlertSink.DeliverAsync(AlertEnvelope alert, CancellationToken ct) -> ValueTask<AlertDeliveryResult>`; sink instance gets protected configuration and scoped vault adapter at composition time.
- `AlertDeliveryLease(Guid DeliveryId, int AttemptOrdinal, long Generation, Guid OwnerBootId, DateTimeOffset ExpiresUtc)`; `IAlertDeliveryStore.TryBeginAsync(Guid deliveryId, int expectedAttemptOrdinal, Guid ownerBootId, CancellationToken ct) -> ValueTask<AlertDeliveryLease?>`, `RenewAsync(AlertDeliveryLease lease, CancellationToken ct) -> ValueTask<bool>`, `CompleteAsync(AlertDeliveryLease lease, AlertDeliveryResult result, CancellationToken ct) -> ValueTask<bool>`. SQL-time ownership uses 10s renewal/60s lease, generation fencing and cancellation on failed renewal. No other node sends under a valid lease; crash/expiry can still cause a duplicate remote delivery. Completion atomically schedules bounded next-attempt intent or final state. DeliveryId remains stable across attempts; attempt-specific Quartz keys differ. AlertDispatch envelopes contain delivery/attempt references only; stale duplicate triggers cannot consume another retry.
- `IncidentEvaluator.EvaluateAsync(DateTimeOffset throughUtc, int limit, CancellationToken ct) -> ValueTask<int>`; webhook uses stable delivery ID header, explicit maximum attempts/backoff/jitter and no internal HttpClient retry.

**Task 18 implementation decisions:** Incidents are deduplicated by `(RuleId, Condition, SubjectKey)` and transition Open → Unknown → Open/Resolved without treating missing telemetry as recovery. `RepeatedFailure`, `RetryExhausted`, `Timeout`, and `MissedExecution` count distinct matching subjects in the configured rolling `Window`; `LongRunning` and `WorkerUnavailable` use `Window` as the minimum age and require `Threshold=1`. Execution conditions use job identity when grouping repeated incidents and execution identity for single-execution conditions; missed work uses schedule identity and unavailable workers use stable worker identity. `Grace` is the continuous-condition delay before opening; reminders repeat only while Open; recovery emits one resolution notification.

Alert delivery intent uses Task 06's existing typed `ProjectionEnvelope`/Outbox stream with `Kind=AlertDispatch`, `SchemaVersion=1`, an opaque payload containing only delivery ID and attempt ordinal, and a digest over those immutable values. It is projected to a deterministic Quartz key; each firing must acquire the SQL lease for the expected ordinal and generation before resolving secrets or sending. The lease is renewed while the bounded sink call runs. An expired lease may cause the same `DeliveryId` to be sent again, so receiver-side deduplication is recommended and no exactly-once claim is made. Rule/sink versions are immutable snapshots; each delivery pins its sink revision and its secret references are resolved at send time. Management routes require Manage plus the existing CSRF/origin protection; incident and delivery reads require Read.

- [ ] **Step 1 — Write tests.** `AlertDeliveryTests.Lost_ack_retries_same_delivery_without_duplicate_incident` records receiver acceptance then drops acknowledgement; evaluate repeated failure/reminder/recovery after restart. Test all six conditions, infinite-job long-running threshold, unknown worker observations, grace suppression and retry exhaustion. Secret-bearing webhook credentials stay references and use the validated destination path.

```csharp
Assert.Equal(1, openIncidentCount);
Assert.Single(receiverRequests.Select(r => r.DeliveryId).Distinct());
Assert.Equal(configuredMaximumAttempts, exhausted.AttemptCount);
Assert.Equal("Unknown", unavailableObservation.ConditionState);
Assert.Equal(1, resolvedRecoveryNotifications);
Assert.DoesNotContain(webhookSecret, allStoredAlertData);
Assert.Equal(1, ownersGrantedForSameDeliveryAttempt);
Assert.False(staleDeliveryOwnerCanComplete);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~AlertDeliveryTests` and `npm.cmd --prefix frontend run e2e -- alerts.spec.ts`; expect incident/delivery/UI failures.
- [ ] **Step 3 — Implement.** Commit evaluation cursor/incident/delivery intent atomically, then project Quartz one-shot attempts. Add IAlertSinks/IAlertDeliveryStore and their SQL implementations beside IAlertRules/SqlAlertRules. Claim/fence delivery attempts across scheduler nodes, recover expired leases and reject stale completions; only the application retry policy creates another attempt. Give alert operations bounded timeout/capacity separate from user execution. Provide manager rules/sink configuration and safe incident/delivery read views, exhausted-delivery retry and recovery notices. RetryAfter is capped by sink RetryMaxDelay and never bypasses MaxAttempts; manual retry is audited with a new bounded retry budget and the same logical delivery ID. UI describes deduplication and receiver-side duplicate possibility. Document webhook payload, credential facade and how optional email sinks implement the interface; do not add an email service dependency.
- [ ] **Step 4 — Verify green.** Repeat checks with restart, webhook faults and denied destination. Expected PASS; scheduler/DB total outage is documented as unable to notify until restored, not falsely tested as guaranteed delivery.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add durable alert rules and webhook delivery"`.

### Task 19: Retention with verified archive-before-delete

**Files:** Create `src/WorkerDock.Application/Retention/IRetentionService.cs`, `src/WorkerDock.Infrastructure/Retention/ArchiveWriter.cs`, `SqlRetentionService.cs`, `src/WorkerDock.Quartz/Retention/RetentionJob.cs`, `src/WorkerDock.Web/Endpoints/RetentionEndpoints.cs`; `frontend/apps/tenant/src/features/settings/RetentionPage.tsx`; `database/tenant/0008_retention.up.sql`, `0008_retention.down.sql`, manifest; `tests/WorkerDock.IntegrationTests/RetentionTests.cs`, `ArchiveFailureTests.cs`; `docs/retention-archive.md`.

**Interfaces:**
- Consumes Task 17 data/aggregates, Task 18 active incidents, Task 06 outstanding intents and Task 01 audit/migration permissions.
- `RetentionPolicy(int LogDays, int ExecutionDays, int AuditDays, bool ArchiveBeforeDelete, int ArchiveDays)` defaults 30/90/365 for the first three; archive root is deployment configuration, never arbitrary API path.
- `ArchiveManifest(Guid BatchId, string RelativePath, string Sha256, long RowCount, DateTimeOffset FinalizedUtc)`; `ArchiveWriter.WriteAsync(Guid batchId, Stream safeRecords, CancellationToken ct) -> ValueTask<ArchiveManifest>`; `VerifyAsync(ArchiveManifest manifest, CancellationToken ct) -> ValueTask<bool>`.
- `IRetentionService.RunBatchAsync(DateTimeOffset nowUtc, int maxRows, CancellationToken ct) -> ValueTask<int>` returns rows deleted only after eligibility/archive verification. Manager settings update audit/rowversion like other configuration.
- `RetentionPolicyEditorDto(RetentionPolicy Policy, byte[] Version)`; `IRetentionService.GetPolicyAsync(CancellationToken ct) -> ValueTask<RetentionPolicyEditorDto>` and `SavePolicyAsync(RetentionPolicy policy, byte[] expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>` back management-only GET/PUT `/api/settings/retention`. Positive bounded day values, explicit archive opt-in and deployment-owned archive root apply to both validation paths.

- [ ] **Step 1 — Write failure tests.** `ArchiveFailureTests.Disk_full_or_acl_denial_retains_source` injects partial write/finalization/checksum failures, retry same batch and attempted root escape/reparse target. `RetentionTests.Active_and_replayable_records_survive_short_retention` protects attempts/revisions/24h keys/outbox/open incidents; SQL principals cannot invoke ordinary direct audit deletion.

```csharp
Assert.Equal(originalRowCount, rowsAfterArchiveFailure);
Assert.Equal(0, deletesWithInvalidChecksum);
Assert.Equal(1, finalizedLogicalArchivesForSameBatch);
Assert.True(activeExecutionExists);
Assert.True(unexpiredRequestKeyExists);
Assert.Equal(30, defaults.LogDays);
Assert.Equal(90, defaults.ExecutionDays);
Assert.Equal(365, defaults.AuditDays);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~RetentionTests|FullyQualifiedName~ArchiveFailureTests"`; expect retention/archive safeguards to fail.
- [ ] **Step 3 — Implement.** Bounded eligibility batches, compression, restrictive tenant directory ACLs, containment/reparse checks, flush/finalize/verify manifest before deletion. Preserve unresolved intents, active dependencies and compact dedup tombstones at least 24h after confirmed Quartz retirement, as well as the original request window. Remove obsolete Quartz objects via API before marking retirement; absence alone is insufficient. Use restricted purge routine with purge-summary audit. Aggregate before deletion, expose failures visibly and keep archive retention separate from history. Documentation distinguishes archives from whole-database backups.
- [ ] **Step 4 — Verify green.** Repeat tests, recover from full disk with same batch and perform archive integrity read-back. Expected PASS and no unarchived deletion or escaping configured root.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add verified archival and bounded retention"`.

### Task 20: Independent central tenant directory and advisory health

**Files:** Create ControlCenter project/`Program.cs`, `Registry/TenantRegistration.cs`, `Registry/CentralRegistry.cs`, `Health/TenantHealthProbe.cs`, `Endpoints/RegistryEndpoints.cs`; `database/central/0001_registry.up.sql`, `0001_registry.down.sql`, `manifest.json`; modify migrator central-kind support; create `frontend/apps/center/package.json`, `index.html`, `vite.config.ts`, `tsconfig.json`, `src/main.tsx`, `src/App.tsx`, `src/TenantDirectory.tsx`, `src/RegistrationEditor.tsx`; `tests/WorkerDock.IntegrationTests/ControlCenterIsolationTests.cs`, `frontend/e2e/control-center.spec.ts`; `docs/control-center.md`.

**Interfaces:**
- Consumes Task 16 `TenantHealthDto`, Task 12 outbound policy and Task 10 separately configured vault, plus shared Task 02 auth policies with independent registration/cookies/keys.
- `TenantRegistration(Guid Id, string DisplayName, Guid ExpectedInstanceId, Uri DashboardUrl, SecretReference HealthCredential, byte[] Version)`; stored centrally only.
- `CentralRegistry.SaveAsync(TenantRegistration registration, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; `ListAsync(string? search, PageRequest page, CancellationToken ct) -> ValueTask<Page<TenantDirectoryEntry>>`; `RemoveAsync(Guid id, byte[] expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask` removes the link, never a deployment.
- `TenantDirectoryEntry(Guid Id, string DisplayName, Uri DashboardUrl, string Health, DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastSuccessUtc)`; `TenantHealthProbe.CheckAsync(Guid registrationId, CancellationToken ct) -> ValueTask<TenantDirectoryEntry>`. Probe has bounded timeout; observational refresh POST uses Read policy (authenticated under Entra, anonymous when no auth is configured), antiforgery and rate limiting. This narrowly scoped exception permits updating a cached observation only; URL/credential/configuration changes always require Manage. Test refresh under all three access modes and forbid caller-supplied destinations/credentials.

- [ ] **Step 1 — Write isolation tests.** Two tenant databases/principals and center principal are genuinely distinct. `ControlCenterIsolationTests.Central_outage_does_not_stop_tenants` stops the center while both run; `Wrong_instance_or_stale_success_is_not_healthy` returns mismatched identity, rotates credential and ages observation. Cross-tenant tokens fail audience validation, health-only token cannot manage authenticated tenant, and anonymous tenant endpoints remain intentionally public.

```csharp
Assert.False(centerPrincipalCanReadTenantJobs);
Assert.False(tenantAPrincipalCanReadTenantB);
Assert.Equal("IdentityMismatch", wrongInstance.Health);
Assert.Equal("Stale", agedSuccess.Health);
Assert.True(bothTenantsCompletedWhileCenterStopped);
Assert.Equal(401, healthOnlyWithoutUserSession.StatusCode);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~ControlCenterIsolationTests` and `npm.cmd --prefix frontend run e2e -- control-center.spec.ts`; expect directory/isolation/health failures.
- [ ] **Step 3 — Implement.** Center owns only registry/health/audit/schema settings; reuse infrastructure modules without registering tenant persistence. HTTPS/approved destinations, fixed minimal health path bound to registered instance, scoped credential from central vault and no cookies/token proxy to tenant. Display direct links and last attempt/success/stale/unknown. Registration/update/removal requires Manage; tenant entry still requires tenant authorization. Probe only while IIS active plus rate-limited refresh; document no continuous health guarantee or provisioning. Provide separate center en/pt-BR build and auth deployment examples for shared/independent directories.
- [ ] **Step 4 — Verify green.** Repeat checks and central migration up/down/up; expected PASS with no tenant job/config/vault data in central DB/UI, no extra Windows service.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add isolated central tenant directory and health links"`.

## Stage exit

- [ ] Demonstrate alert retry/recovery, archive failure preservation and center-down tenant continuity. Run changed suites and both frontend builds.
- [ ] Core features are now present; Stage 06 must still prove installation, upgrades, complete security/fault coverage and release documentation before release readiness is claimed.
