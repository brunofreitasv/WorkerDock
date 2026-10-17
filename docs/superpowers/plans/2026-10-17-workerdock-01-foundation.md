# WorkerDock Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce an audited tenant API/React shell and prove the selected Quartz persistence and calendar integration on real SQL Server.

**Architecture:** Business configuration and audit share a transaction. Independently configured Quartz schedule/execution stores sit in the same tenant database; tests exercise them before downstream runtime work.

**Tech Stack:** .NET 10, SQL Server 2022, Quartz 4.5.1, ASP.NET Core OIDC, React 19, TypeScript, Vite, MUI, xUnit/Vitest.

**Spec:** [Approved design](../specs/2026-10-17-workerdock-design.md), sections 2–8 and 12–13. Read the [index](2026-10-17-workerdock-implementation.md) for shared constraints and type conventions.

## Global Constraints

- All index constraints apply. "SQL Server 2022 minimum; default schema compatibility target 160."
- "Use public `IScheduler` APIs, not direct edits to Quartz rows, a replacement `IJobStore`, or a fork of Quartz."
- "Services check schema compatibility at startup/readiness and do not automatically migrate it."
- "State-changing browser requests require antiforgery validation and origin checks, including in anonymous mode."

## Review Focus

The five project cases and owners are in the index. This stage pins locale/DST behavior in Task 04 and additionally prevents invalid partial auth configuration from silently enabling anonymous mode in Task 02.

## File structure

Task 01 owns initial solution/build files, tenant schema/migrator and transactional settings. Task 02 adds authentication, endpoint policy composition and frontend workspace. Task 03 owns only Quartz persistence/topology. Task 04 owns the calendar evaluator and serializer; later schedule services consume that evaluator without duplicating time calculations.

### Task 01: Migrated tenant settings with atomic audit

**Files:** Create `WorkerDock.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`; create the Domain, Application, Infrastructure, Web and DatabaseMigrator projects; `src/WorkerDock.Domain/Tenants/TenantSettings.cs`, `src/WorkerDock.Application/Common/ActorContext.cs`, `src/WorkerDock.Application/Tenants/ITenantSettingsService.cs`, `src/WorkerDock.Infrastructure/Persistence/TenantSettingsService.cs`, `src/WorkerDock.Infrastructure/Persistence/BusinessTransaction.cs`, `src/WorkerDock.DatabaseMigrator/MigrationRunner.cs`, both hosts' `Program.cs`; `database/tenant/0001_bootstrap.up.sql`, `database/tenant/0001_bootstrap.down.sql`, `database/tenant/manifest.json`; `tests/WorkerDock.IntegrationTests/SqlDatabaseFixture.cs`, `MigrationTests.cs`, `TenantSettingsTests.cs`, and its project; `docs/development.md`, `docs/database.md`.

**Interfaces:**
- Consumes: deployment-only database connection configuration and expected instance ID.
- Produces `ActorContext(string Subject, string AuthenticationMode, Guid CorrelationId)`; authenticated Subject is a stable issuer-qualified subject identifier constructed server-side, never display name/email. Anonymous subject uses a session-scoped opaque ID; audit display may be Anonymous.
- Produces `TenantSettings(Guid InstanceId, string DisplayName, string DisplayTimeZone, byte[] Version)`; `ITenantSettingsService.GetAsync(CancellationToken ct) -> ValueTask<TenantSettings>` and `UpdateAsync(TenantSettings desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<TenantSettings>`.
- Produces `MigrationRunner.ApplyAsync(string databaseKind, string targetMigration, CancellationToken ct) -> ValueTask` and `RollbackAsync(string databaseKind, string targetMigration, CancellationToken ct) -> ValueTask`. Database kind is allowlisted `tenant`/`central`; central scripts arrive in Task 20.
- Internal `BusinessTransaction.RunAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> action, CancellationToken ct) -> ValueTask<T>` obtains the tenant application lock before mutable business rows. Callers append their mandatory audit event using that connection/transaction.

- [ ] **Step 1 — Write behavioral tests after preflight.** Verify the .NET 10 prerequisite from the index, then create only the projects/fixtures needed for these tests. `MigrationTests.Checksum_tamper_and_wrong_instance_fail_closed` verifies checksum rejection and instance binding. `TenantSettingsTests.Audit_failure_rolls_back_setting` injects an audit failure. `MigrationTests.Up_down_up_preserves_schema_contract` uses an empty disposable database; a populated destructive rollback is refused.

```csharp
Assert.Equal(original.DisplayName, reloaded.DisplayName); // audit insert failed
Assert.Equal(0, committedAuditEvents);
Assert.Equal("MigrationChecksumMismatch", tamperedResult.Code);
Assert.Equal("TenantInstanceMismatch", wrongInstanceResult.Code);
Assert.Equal("UnsafeRollback", populatedRollbackResult.Code);
```

- [ ] **Step 2 — Verify red.** Run `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~TenantSettingsTests"`; expect failures for unimplemented migration/transaction behavior, not missing infrastructure.
- [ ] **Step 3 — Implement.** Build the named interfaces and explicit schema: instance/settings, append-only audit, schema history and outbox envelope. Enforce rowversion, UTC, bounded values, schema checksum/history and instance identity; use transaction-owned `sp_getapplock` with checked results and bounded timeout. The migrator alone gets DDL rights. Add safe liveness/readiness to Web and a temporary test-only settings endpoint; Task 02 protects production routes before exposing mutations. Document test database ownership, SDK setup and rollback refusal. Pin compatible package versions/SDK and commit lock files.
- [ ] **Step 4 — Verify green.** Repeat the filtered test command; expected PASS for up/down/up, tampering, audit rollback and instance mismatch. Run `dotnet build WorkerDock.slnx -c Release`; expected zero errors. Execute schema tests with READ_COMMITTED_SNAPSHOT both on and off.
- [ ] **Step 5 — Commit.** Stage this task's files and lock files only; `git commit -m "feat: add migrated tenant settings and atomic audit"`.

### Task 02: Read/manage authorization and usable React shell

**Files:** Create `src/WorkerDock.Infrastructure/Authentication/AccessOptions.cs`, `AccessPolicies.cs`, `SessionEndpoints.cs`; modify `src/WorkerDock.Web/Program.cs`; create `frontend/package.json`, `frontend/package-lock.json`, `frontend/apps/tenant/package.json`, `index.html`, `vite.config.ts`, `tsconfig.json`, `src/main.tsx`, `src/App.tsx`, `src/api/client.ts`, `src/auth/SessionProvider.tsx`; shared `frontend/packages/ui/src/AppShell.tsx`, `locales/en.json`, `locales/pt-BR.json`; `tests/WorkerDock.IntegrationTests/AccessPolicyTests.cs`; `frontend/apps/tenant/src/auth/SessionProvider.test.tsx`; `docs/authentication.md`.

**Interfaces:**
- Consumes Task 01 ActorContext/settings and the Web host.
- Produces `AccessOptions(string? Authority, string? ClientId, string? ClientSecretReference, string? RequiredManagementRole, string RoleClaimType)`; resolve sign-in bootstrap credentials from protected deployment configuration, never the application DB. `ValidateAccessOptions(AccessOptions options) -> IReadOnlyList<string>` returns safe configuration codes.
- Produces named ASP.NET policies `WorkerDock.Read`, `WorkerDock.Manage`, and `SessionDto(bool Authenticated, bool CanRead, bool CanManage, string Mode, string Language, string TimeZone)` via `GET /api/session`. `GET /api/antiforgery` establishes a session token; every mutating API requires its header and same-origin checks.
- `GET/PUT /api/settings` consume Task 01 service; PUT requires Manage/If-Match and supplies actor/correlation from the server. Sign-in/sign-out use framework OIDC/cookie handlers; no identity tokens enter browser storage.

- [ ] **Step 1 — Write tests.** Table-drive anonymous, configured Entra without role, role-configured viewer/manager, invalid partial configuration, role-without-auth, wrong issuer/audience and custom role claim mapping. Use a local signed OIDC test issuer for token validation cases, not a bypass authentication handler. `AccessPolicyTests.Viewer_reads_but_cannot_manage_or_read_protected_configuration` checks direct endpoints; `Anonymous_mutation_without_antiforgery_is_rejected` checks anonymous CSRF. The shell test asserts hidden management navigation without suppressing monitoring/audit routes.

```csharp
Assert.Equal(200, viewerDashboard.StatusCode);
Assert.Equal(403, viewerSettings.StatusCode);
Assert.Equal(403, viewerMutation.StatusCode);
Assert.Equal(400, anonymousPostWithoutToken.StatusCode);
Assert.False(invalidConfiguration.HostStarted);
Assert.True(noRoleSession.CanManage);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~AccessPolicyTests` and `npm.cmd --prefix frontend run test -- --run SessionProvider`; expect failing policy/shell assertions.
- [ ] **Step 3 — Implement.** Code the exact spec access matrix, secure host-scoped cookies/code+PKCE, allowed authority/audience, configurable role claim and protected key directory. Set OIDC `SaveTokens = false`: the app needs sign-in claims, not retained access/refresh tokens; cookies contain no identity-provider token. Sign-out is a CSRF-protected POST. Resolve bootstrap credential references through deployment-only environment mapping until the shared adapter is available; no job-vault access is needed. Separate safe reads from protected settings/configuration. Render a responsive MUI shell with keyboard focus, translated mode/status and local language/zone preferences. Create the shared UI workspace manifest/TypeScript exports, Vitest setup and root scripts defined in the index; configure Vite static output and same-origin API use and bundle fonts/assets locally. Avoid paid components, React auth as a security boundary, broad CORS or a central membership check.
- [ ] **Step 4 — Verify green.** Repeat both commands, then frontend production build. Expected PASS and no auth tokens in storage/network DTOs. Read-only and anonymous modes must work without a real external Entra tenant in automated tests; document the live Entra deployment smoke check for Task 21.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add read and management policies with React shell"`.

### Task 03: Two SQL-backed Quartz schedulers with strict cluster limits

**Files:** Create `src/WorkerDock.Quartz/WorkerDock.Quartz.csproj`, `Hosting/QuartzTopology.cs`, `Hosting/QuartzNodeOptions.cs`, `Hosting/QuartzConnections.cs`; `database/upstream/quartz-4.5.1/sql-server.sql`, `NOTICE.txt`, `database/tenant/0002_quartz.up.sql`, `0002_quartz.down.sql`; modify tenant manifest; create `tests/WorkerDock.IntegrationTests/QuartzTopologyTests.cs`, `QuartzCapacityTests.cs`; `docs/quartz-integration.md`.

Also create `tests/WorkerDock.TestHost/WorkerDock.TestHost.csproj`, `Program.cs`, `ProbeJob.cs` and `tests/WorkerDock.IntegrationTests/QuartzProcessFixture.cs`. Each cluster node runs in its own TestHost process with the same scheduler name and database, distinct boot ID and barrier-driven test control. Credentials are supplied via scoped environment/configuration, never command-line arguments. Fixture teardown owns only its spawned processes/database.

**Interfaces:**
- Consumes Task 01 database identity/manifest and disposable SQL fixture.
- Produces `QuartzNodeOptions(Guid InstanceId, string BootId, int HostCeiling)` and `QuartzTopology.CreateScheduleAsync(QuartzNodeOptions options, CancellationToken ct) -> ValueTask<IScheduler>`, `CreateExecutionAsync(QuartzNodeOptions options, CancellationToken ct) -> ValueTask<IScheduler>`, `CreateExecutionPublisherAsync(QuartzNodeOptions options, CancellationToken ct) -> ValueTask<IScheduler>`. Each creation checks tenant identity/schema; caller owns lifetime.
- Returned names/prefixes are exactly `WorkerDock.Schedule`/`WDS_QRTZ_` and `WorkerDock.Execute`/`WDE_QRTZ_`. Publisher shares execution-store identity but uses its own non-started write-only handle. No public network API.

- [ ] **Step 1 — Write real SQL tests.** Persist one opaque-ID trigger via the non-started publisher, restart, execute from two worker nodes and assert one delivery. Hold probe jobs with a barrier (not sleeps) and set one template cluster group to 1. `QuartzCapacityTests.Locked_acquisition_does_not_multiply_capacity` contends from three nodes. Also exercise least-privilege DML, unavailable DB, string-only job data, node restart and upstream schema round trip.

```csharp
Assert.False(publisherStarted);
Assert.Equal(1, observedDeliveryCount);
Assert.Equal(1, maximumSimultaneousProbeJobs);
Assert.Equal(0, schedulerStoreWorkerJobCount);
Assert.Empty(jobDataKeys.Except(new[] { "ExecutionId", "DeliveryId", "AttemptOrdinal" }));
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~QuartzTopologyTests|FullyQualifiedName~QuartzCapacityTests"`; expect missing topology/persistence/capacity behavior.
- [ ] **Step 3 — Implement.** Use Quartz 4.5.1 core and SQL Server store APIs with an explicit Microsoft.Data.SqlClient package reference, System.Text.Json serializer, string job data, clustering/unique boot IDs, `AcquireTriggersWithinLock = true` and cluster-scoped template groups. Never start the `ZeroSizeThreadPool` publisher. Wrap attributed upstream scripts for both prefixes without changing Quartz's schema contract; include runtime role grants now so the proof does not run only as a database owner. Make downgrade refuse active/pending work. Record the exact public APIs proven; if the pinned release cannot supply these semantics, stop dependent tasks and return a concrete spec correction rather than inventing a queue.
- [ ] **Step 4 — Verify green.** Repeat filtered tests and schema up/down/up under both supported isolation settings. Expected PASS with three real Quartz nodes and persisted restart evidence; SQLite/in-memory does not count.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: integrate clustered Quartz SQL Server stores"`.

### Task 04: Authoritative cron policy and persisted calendar

**Files:** Create `src/WorkerDock.Domain/Scheduling/ScheduleDefinition.cs`, `src/WorkerDock.Application/Scheduling/IScheduleEvaluator.cs`, `src/WorkerDock.Quartz/Scheduling/QuartzScheduleEvaluator.cs`, `WorkerDockPolicyCalendar.cs`, `WorkerDockPolicyCalendarSerializer.cs`; modify `QuartzTopology.cs`; create `tests/WorkerDock.UnitTests/ScheduleEvaluatorTests.cs` and project, `tests/WorkerDock.IntegrationTests/CalendarPersistenceTests.cs`; update `docs/quartz-integration.md`.

**Interfaces:**
- Produces `ScheduleDefinition(Guid ScheduleId, long Revision, string Kind, string? Cron, string ZoneId, DateTimeOffset? OneTimeUtc, DateTimeOffset ActivatedUtc)`; Kind is `Once`/`Cron`, dialect is explicitly WorkerDock five-field numeric grammar.
- `IScheduleEvaluator.Validate(ScheduleDefinition schedule) -> IReadOnlyList<string>` and `Next(ScheduleDefinition schedule, DateTimeOffset afterExclusiveUtc, int count) -> IReadOnlyList<DateTimeOffset>`; count 1–100, bounded candidate evaluations with safe `ScheduleSearchLimit` error rather than hanging. No result is an impossible/exhausted schedule.
- `ResolveLocalOnce(DateTime localUnspecified, string zoneId, TimeSpan? selectedOffset) -> DateTimeOffset` belongs to IScheduleEvaluator. Reject non-Unspecified input, nonexistent local time, or an offset not valid in that zone at that instant; require selectedOffset when ambiguous. This is the authoritative conversion used by Task 14's editor API, not a browser-only check.
- Calendar and serializer implement the pinned Quartz public contracts; calendar payload holds only schema version, expression, zone and policy, no execution configuration.

- [ ] **Step 1 — Write fixtures.** `ScheduleEvaluatorTests.Dst_gap_skips_and_overlap_uses_earlier_offset` uses `30 2 * * *`, America/New_York after 2026-03-08 05:00Z → March 9 06:30Z, and `30 1 * * *` after 2026-11-01 04:00Z → November 1 05:30Z then November 2 06:30Z. Add every-minute repeated-hour, Australia/Lord_Howe half-hour transition, leap day, Sunday 0/7 and DOM/DOW AND fixtures. For `0 0 13 * 5` after 2026-10-01 00:00Z in UTC, first match is November 13, not October 2/13. Test invalid/impossible input and bounded advancement. `CalendarPersistenceTests.Restart_preserves_filtered_occurrences` compares before/after SQL round trip.

```csharp
Assert.Equal(DateTimeOffset.Parse("2026-03-09T06:30:00Z"), gapNext.Single());
Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:30:00Z"), fallNext[0]);
Assert.Equal(DateTimeOffset.Parse("2026-11-02T06:30:00Z"), fallNext[1]);
Assert.Equal(DateTimeOffset.Parse("2026-11-13T00:00:00Z"), bothDays.Single());
Assert.Equal(beforeRestart, afterRestart);
Assert.Equal("AmbiguousLocalTime", missingOverlapOffset.Code);
Assert.Equal("InvalidLocalOffset", forgedOffset.Code);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.UnitTests --filter FullyQualifiedName~ScheduleEvaluatorTests` and `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~CalendarPersistenceTests`; expect policy/calendar failures.
- [ ] **Step 3 — Implement.** Parse with Quartz `CronFormat.Unix`; persist original dialect text and use the spec's native-trigger/custom-calendar filtering. Reuse Quartz matching for nominal local times and both day-field restrictions. Reject nonexistent one-time local input; ambiguous one-time selection must supply its offset before producing OneTimeUtc. Register the custom calendar serializer in every schedule-store handle. Preview and persistence use the same evaluator. Do not use Cronos or write a parser.
- [ ] **Step 4 — Verify green.** Repeat both commands and run actual Quartz trigger execution against the calendar fixtures with controllable time where supported, otherwise explicit fire-time calculations plus persisted trigger assertions. Expected identical preview/trigger sequence; no loop at gap end or at the later overlap offset.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: preserve WorkerDock cron policies through Quartz calendars"`.

## Stage exit

- [ ] Run the stage checks from the index; record .NET/Quartz/SQL versions, topology/capacity/calendar evidence and schema rollback result.
- [ ] Demonstrate audited settings, read/manage policy matrix and the React shell. Remaining limitations: no user job handler/runtime yet; proceed to Stage 02 only after the integration proofs pass.
