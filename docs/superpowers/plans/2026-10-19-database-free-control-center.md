# Database-free Control Center Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run and manage the optional Control Center without a central database or SQL objects, while preserving tenant links, audit, health, security, and safe upgrades.

**Architecture:** A single center process holds an exclusive writer handle on a protected, append-only registration/audit journal and rebuilds its in-memory directory at startup. A separate atomic health snapshot keeps advisory observations across restarts. Tenant SQL Server and Quartz remain unchanged.

**Tech Stack:** .NET 10 / ASP.NET Core, existing `ICentralRegistry` and React UI, Windows/IIS, PowerShell 7, protected NTFS files, SHA-256, xUnit.

**Spec:** [Database-free Control Center design](../specs/2026-10-19-database-free-control-center-design.md).

## Global Constraints

- .NET 10 LTS, Windows Server 2022 or newer, and PowerShell 7 remain the deployment baseline; tenant databases still require SQL Server 2022 or newer.
- A fresh Control Center requires no central SQL Server database, schema, migrator, or SQL connection string.
- Each tenant retains its own SQL Server database, scheduler, workers, vault identity, and dashboard.
- The center stores only tenant display names, HTTPS URLs, expected instance IDs, logical health credential references, health observations, and sanitized audit metadata.
- Resolved secrets never enter files, logs, audit, metrics, or API responses.
- `Central:InstanceId` is stable and nonempty; `Central:Storage:DataDirectory` is absolute and outside release/web directories.
- Production has one center process and one journal writer; multiple processes sharing the state directory are unsupported.
- Existing `/api/tenants` routes, eight-byte ETags, Entra/role authorization, CSRF, health URL validation, and vault/outbound policy remain.
- Acknowledged registration mutations and their audit events survive restart; a failed/uncertain write is never reported as success.
- Center install/upgrade/restore never migrates a tenant database; tenant deployment and SQL migration behavior remain unchanged.
- Legacy `database/central` scripts remain available for old installs and are not part of fresh center setup.

## Review Focus

1. Torn final journal frame after a crash: Task 1 test recovers only the incomplete suffix and preserves the prior acknowledged entry.
2. Complete frame changed or wrong center instance ID: Task 1 test rejects startup/readiness without silently dropping data.
3. Two center processes or a disk write failure: Task 1 and Task 2 tests reject the second writer and keep the API from acknowledging uncertain mutations.
4. Stale ETag or health update racing a registration removal: Task 2 tests return 412 for edits and never resurrect a removed registration.
5. Legacy import with an unsafe URL, duplicate ID, or nonempty target: Task 5 tests reject the import without changing the target.

---

## File structure

- `src/WorkerDock.ControlCenter/Registry/CentralJournalStore.cs` owns the framed append/replay protocol, center identity, exclusive lock, integrity checks, and flush.
- `src/WorkerDock.ControlCenter/Registry/CentralJournalMutation.cs` defines bounded journal payloads and sanitized audit fields.
- `src/WorkerDock.ControlCenter/Registry/FileCentralRegistry.cs` implements the existing registry interface, pagination, ETags, mutation serialization, and fail-closed state.
- `src/WorkerDock.ControlCenter/Registry/CentralHealthSnapshot.cs` owns atomic storage of advisory last-health observations.
- `src/WorkerDock.ControlCenter/Registry/CentralStorageOptions.cs` validates the persistent data directory.
- `src/WorkerDock.ControlCenter/Program.cs` selects Memory only for disposable development and File for production, with no SQL connection.
- `deploy/WorkerDock.Deployment.psm1`, `Install-Center.ps1`, `Upgrade-Instance.ps1`, and `Build-Packages.ps1` handle protected state, file backups, and a center package without a migrator.
- `src/WorkerDock.CentralImport/` is a one-time, offline SQL-to-journal importer; it is not loaded by the center runtime.
- Center setup, deployment, configuration, SQL reference, and upgrade docs distinguish current file storage from legacy SQL migration.

### Task 1: Durable journal protocol

**Files:**
- Create: `src/WorkerDock.ControlCenter/Registry/CentralJournalStore.cs`
- Create: `src/WorkerDock.ControlCenter/Registry/CentralJournalMutation.cs`
- Test: `tests/WorkerDock.UnitTests/CentralJournalStoreTests.cs`

**Interfaces:**
- Produces: `CentralJournalStore.Open(string dataDirectory, Guid instanceId) -> CentralJournalStore`, `IReadOnlyList<CentralJournalRecord> Records`, `AppendAsync(CentralJournalMutation mutation, CancellationToken ct) -> ValueTask<long>`, `IsWritable`, `Dispose()`. `CentralJournalRecord(long Sequence, CentralJournalMutation Mutation)` supplies the ETag revision. `TenantRegistrationSnapshot(Guid Id, string DisplayName, Guid ExpectedInstanceId, string DashboardUrl, string HealthCredentialReference, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)` is journal state. `CentralJournalMutation(string Action, Guid RegistrationId, TenantRegistrationSnapshot? Before, TenantRegistrationSnapshot? After, DateTimeOffset AtUtc, ActorContext Actor, string SafeDetailsJson)` is the append payload. An internal `ICentralJournalWriter.AppendAndFlushAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) -> ValueTask` seam permits deterministic partial-write and flush-failure tests.
- A mutation has action `Registered`/`Updated`/`Removed`/`ImportedRegistration`/`LegacyAudit`; registration ID; optional before/after snapshot; UTC time; actor ID/mode; correlation ID; safe audit projection. It contains no resolved secret.
- The store writes one identity header followed by length-prefixed, SHA-256-checked frames (maximum payload 16 KiB), each with increasing sequence and previous-frame hash. It flushes each acknowledged append to disk.

- [ ] **Step 1: Write failing journal tests.** In `CentralJournalStoreTests`, cover reopen after two acknowledged appends, exclusive second-writer rejection, wrong identity, complete-frame corruption, recoverable partial final frame, oversize payload, and injected write/flush failure. Assert that only an incomplete final frame is truncated and that a failed write makes `IsWritable` false.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.UnitTests --filter FullyQualifiedName~CentralJournalStoreTests`; expect missing types/methods.
- [ ] **Step 3: Implement the codec and store.** Use a single writer handle with `FileShare.Read`; bound frame reads before allocation; verify sequence, chain, payload hash, and identity during replay. Append and flush before publishing the mutation. On uncertain I/O failure, mark the store unwritable. Do not repair a complete corrupt frame.
- [ ] **Step 4: Run green.** Repeat the focused test command; expect all cases to pass.
- [ ] **Step 5: Commit.** `git add src/WorkerDock.ControlCenter/Registry/CentralJournal* tests/WorkerDock.UnitTests/CentralJournalStoreTests.cs` then `git commit -m "feat: add durable center journal"`.

### Task 2: File registry and persisted health

**Files:**
- Create: `src/WorkerDock.ControlCenter/Registry/FileCentralRegistry.cs`
- Create: `src/WorkerDock.ControlCenter/Registry/CentralHealthSnapshot.cs`
- Test: `tests/WorkerDock.UnitTests/FileCentralRegistryTests.cs`
- Modify: `src/WorkerDock.ControlCenter/Registry/TenantRegistration.cs` only if a small shared projection helper is needed.

**Interfaces:**
- Consumes: Task 1 `CentralJournalStore` and existing `ICentralRegistry`, `TenantRegistrationValidation`, `TenantDirectoryCursor`.
- Produces: `FileCentralRegistry.Open(string dataDirectory, Guid instanceId, TimeProvider time) -> FileCentralRegistry` implementing every `ICentralRegistry` method and `IDisposable`; `CentralHealthSnapshot.Open(string dataDirectory) -> CentralHealthSnapshot` with `Get(Guid id)`, `SaveAsync(Guid id, HealthObservation value, CancellationToken ct)`, and `RemoveAsync(Guid id, CancellationToken ct)`.
- Preserve existing `BitConverter.GetBytes(longRevision)` eight-byte ETags, 412 behavior, list ordering and two-minute Stale projection. Health snapshot is never used as an edit version.

- [ ] **Step 1: Write failing registry tests.** `FileCentralRegistryTests` create/update/delete with old and current ETags, restart and search/pagination, audit actor/correlation and sanitized before/after in one committed journal event, health restart/Stale, bad health snapshot -> Unknown, and refresh/remove race -> not found. Inject a failed append and assert no successful API result or projected edit.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.UnitTests --filter FullyQualifiedName~FileCentralRegistryTests`; expect missing registry/snapshot behavior.
- [ ] **Step 3: Implement registry and snapshot.** Serialize mutations behind one semaphore; validate inputs and actor before append; project state only after flush. Write health JSON via a same-directory temporary file, flush, then atomic replace; ignore orphan health records on replay. A corrupt health file emits a safe warning and starts Unknown.
- [ ] **Step 4: Run green.** Repeat the focused test command; expect all cases to pass.
- [ ] **Step 5: Commit.** Stage only Task 2 files and commit `feat: persist center directory without SQL`.

### Task 3: Database-free host composition and API compatibility

**Files:**
- Create: `src/WorkerDock.ControlCenter/Registry/CentralStorageOptions.cs`
- Modify: `src/WorkerDock.ControlCenter/Program.cs`
- Modify: `src/WorkerDock.ControlCenter/Endpoints/RegistryEndpoints.cs` only if storage-unavailable error mapping needs a focused change.
- Remove from center runtime: `src/WorkerDock.ControlCenter/Registry/SqlCentralRegistry.cs`; Task 5 implements a separate read-only legacy table reader in the importer project.
- Test: `tests/WorkerDock.IntegrationTests/ControlCenterIsolationTests.cs`
- Test: `tests/WorkerDock.IntegrationTests/ControlCenterFileHostTests.cs`

**Interfaces:**
- Consumes: Task 2 `FileCentralRegistry.Open` and existing `ICentralRegistry`/`ITenantHealthProbe`.
- Produces: production `Central:Storage:DataDirectory` configuration; Development-only `Central:Adapter=Memory`; `/health/ready` 200 only when the journal is usable, otherwise 503. No `Central:ConnectionString` branch.

- [ ] **Step 1: Write failing host tests.** Start the center with a temporary data directory, no SQL environment/configuration, and a fake vault. Verify register/restart/list, safe health refresh, Read/Manage and CSRF policies, no tenant job services, and 503 after an injected journal write failure. Reject relative/web-root/release data paths and `Memory` in Production.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~ControlCenterFileHostTests|FullyQualifiedName~ControlCenterIsolationTests"`; expect missing file adapter/options.
- [ ] **Step 3: Wire the host.** Bind and validate `CentralStorageOptions.DataDirectory`; select the File adapter by default, keep Memory only in Development, and remove SQL construction and `CentralDatabaseRequired`. Keep existing health allowlist and auth/CSRF wiring unchanged. Make storage failures return safe `PersistenceUnavailable` responses.
- [ ] **Step 4: Run green and build.** Repeat the focused integration command and run `dotnet build src/WorkerDock.ControlCenter/WorkerDock.ControlCenter.csproj`; expect PASS with no SQL service.
- [ ] **Step 5: Commit.** Stage Task 3 files and commit `feat: start control center without a database`.

### Task 4: Fresh install, package, upgrade, and restore

**Files:**
- Modify: `deploy/config/center.example.json`
- Modify: `deploy/WorkerDock.Deployment.psm1`
- Modify: `deploy/Install-Center.ps1`
- Modify: `deploy/Upgrade-Instance.ps1`
- Modify: `deploy/Build-Packages.ps1`
- Test: `tests/deploy/center-storage.Tests.ps1`
- Modify: `docs/how-to/deploy-production.md`, `docs/upgrades.md`

**Interfaces:**
- Consumes: Task 3's `Central:Storage:DataDirectory` and journal format 1.
- Produces: center package manifest `stateFormatCompatibility: {minimum: 1, maximum: 1}` and web component only; tenant package keeps `schemaCompatibility` and its migrator. `Assert-ConfigIdentity` requires a center data directory instead of a connection string. Center `-BackupPath` is a new `.zip` file; tenant `-BackupPath` stays a new SQL `.bak` path.

- [ ] **Step 1: Write failing PowerShell tests.** `tests/deploy/center-storage.Tests.ps1` calls extracted pure helpers with temporary paths: center config accepts an absolute state path/no connection string, rejects release/web-root paths, package verification accepts a center state-format manifest without a migrator, and a center backup archive round-trips journal/health/config/previous release with matching SHA-256. Assert tenant schema checks still reject missing/mismatched schema data.
- [ ] **Step 2: Run red.** `pwsh -NoProfile -File tests/deploy/center-storage.Tests.ps1`; expect absent helper/center manifest behavior.
- [ ] **Step 3: Implement scripts and example config.** Installer creates persistent state outside releases, applies `Protect-SensitiveDirectory` for the IIS pool, and never invokes SQL. Package builder omits the center migrator and records state format 1. Center upgrade requires stopped IIS, verifies a new archive and state format, switches release, and leaves IIS stopped; tenant SQL backup/migration path is unchanged. Uninstall preserves center state. Document center restore with same instance ID.
- [ ] **Step 4: Run green and package smoke.** Repeat the PowerShell test; run `pwsh -File deploy/Build-Packages.ps1 -Version 0.1.0-center-file-smoke -OutputRoot .\artifacts\center-file-smoke`. Inspect the center manifest for web + state format and absence of migrator/SQL scripts; inspect the tenant package for its unchanged migrator/schema compatibility.
- [ ] **Step 5: Commit.** Stage Task 4 files and commit `feat: deploy center with protected file state`.

### Task 5: Legacy import and complete documentation

**Files:**
- Create: `src/WorkerDock.CentralImport/WorkerDock.CentralImport.csproj`
- Create: `src/WorkerDock.CentralImport/Program.cs`
- Create: `src/WorkerDock.CentralImport/LegacyCenterReader.cs`
- Create: `src/WorkerDock.ControlCenter/Registry/CentralJournalBootstrap.cs`
- Test: `tests/WorkerDock.UnitTests/CentralJournalBootstrapTests.cs`
- Test: `tests/WorkerDock.IntegrationTests/CentralLegacyImportSqlTests.cs` (gated on `WORKERDOCK_TEST_SQL`)
- Modify: `docs/control-center.md`, `docs/reference/configuration.md`, `docs/reference/database-objects.md`, `docs/how-to/setup-sql-server.md`, `docs/tutorials/run-in-memory.md`, `docs/tutorials/run-with-sql-server.md`, `docs/architecture.md`, `docs/database.md`, `README.md`, and the original design spec's top-level supersession note.

**Interfaces:**
- Consumes: Tasks 1–4 journal format, old `[workerdock].CentralSettings`/`TenantRegistrations`/`TenantHealthObservations`/`AuditEvents`, and existing `database/central` migration history.
- Produces: `LegacyRegistration(Guid Id, string DisplayName, Guid ExpectedInstanceId, string DashboardUrl, string HealthCredentialReference, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)`; `LegacyAuditEvent(Guid Id, DateTimeOffset AtUtc, string ActorId, string AuthenticationMode, string Action, string EntityType, string EntityId, Guid CorrelationId, string SafeDetailsJson)`; `LegacyHealthObservation(Guid RegistrationId, string Health, string? SafeCode, DateTimeOffset LastAttemptUtc, DateTimeOffset? LastSuccessUtc)`. `CentralJournalBootstrap.ImportAsync(string dataDirectory, Guid instanceId, IReadOnlyList<LegacyRegistration> registrations, IReadOnlyList<LegacyAuditEvent> audit, IReadOnlyList<LegacyHealthObservation> health, CancellationToken ct) -> Task<ImportReceipt>`. `WorkerDock.CentralImport` reads legacy SQL with a read-only identity and calls bootstrap; it is a separate migration tool, not a runtime dependency.

- [ ] **Step 1: Write failing import tests.** Synthetic unit cases preserve IDs/timestamps/safe audit counts/latest health observations, assign new ETags, and reject identity mismatch, duplicate IDs, unsafe URL/secret-bearing URL, invalid credential reference, and nonempty target without partial output. SQL-gated integration creates a disposable legacy center DB, imports it, reopens the file registry, and compares safe records without printing secret references.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.UnitTests --filter FullyQualifiedName~CentralJournalBootstrapTests`; expect missing import types.
- [ ] **Step 3: Implement importer and docs.** Read only legacy center tables; validate every row through current registration validation before writing an isolated temporary journal, then atomically move the verified journal into an empty target. Keep old SQL migrations for restore/import only. Document offline backup/import, side-by-side cutover, rollback limits, fresh center setup with no SQL, file ACLs, backup/restore, local memory mode, and tenant-only SQL setup. Mark older central SQL statements as superseded.
- [ ] **Step 4: Run green and review.** Run the focused unit test and `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~CentralLegacyImportSqlTests` when `WORKERDOCK_TEST_SQL` is available; otherwise record it as not run. Run `dotnet build WorkerDock.slnx`, the center integration tests from Task 3, and a documentation search for active instructions that still require a central database. Expect all available checks to pass and only explicitly labeled legacy references to remain.
- [ ] **Step 5: Commit.** Stage Task 5 files and commit `feat: import legacy centers and document database-free setup`.

## Release gate

Do not claim production readiness from unit tests alone. Before release, exercise fresh install, center-only upgrade and restore on disposable Windows/IIS storage; confirm ACLs, exclusive writer behavior across app-pool recycle, verified backup recovery, and center-down tenant continuity. The legacy SQL import requires a disposable SQL Server 2022+ run when the test connection is available. Neither gate changes the tenant SQL Server requirement.