# Job Failure Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show useful per-attempt failure details in the tenant web app while letting each job suppress message, stack, and returned content.

**Architecture:** Pin `HideFailureDetails` on the immutable job revision and carry it in the execution descriptor. Templates create typed failure diagnostics; Executor redacts resolved secrets and bounds the result before IPC. The Worker validates and persists diagnostics by attempt, and monitoring reads them beside the existing safe code.

**Tech Stack:** .NET 10/C#, SQL Server T-SQL migrations, React/TypeScript/MUI, xUnit and Vitest.

**Spec:** `docs/superpowers/specs/2026-10-29-job-failure-diagnostics-design.md`

## Global Constraints

- IPC messages must stay within 65,536 bytes after JSON encoding.
- Hidden jobs must not persist or return message, stack, or response content.
- Known resolved secrets and JSON-escaped forms must be redacted in Executor before IPC for every job.
- Failure diagnostics are best effort; diagnostic failure cannot change execution outcome, retries, effects, or certainty.
- Existing job revisions default to unchecked; existing attempts have no retroactive diagnostics.
- Execution details retain their existing Read authorization; exception text is rendered only as text.
- Diagnostics follow execution retention and archive behavior; raw child stdout/stderr, SQL result rows, requests, credentials, and connection strings stay excluded.
- Begin from the current managed worktree, which contains uncommitted job-management changes used by these screens. Preserve those changes; stage only the current task's hunks (`git add -p` for an already-modified file). Use Luna for straightforward implementation and Sol for complex work or review; never use Astra.

## Review Focus

- An HTTP 500 without a thrown exception has a response body and status but no stack: Task 3 tests the unavailable-stack case.
- A resolved secret appears JSON-escaped in an error body: Task 2 tests both literal and escaped redaction before IPC.
- A job's setting changes after an execution was accepted: Task 1 tests that the execution keeps the pinned choice.
- A diagnostic contains control characters or expands beyond the IPC frame after JSON encoding: Task 2 tests bounded, valid serialization without changing the result.
- SQL reports no procedure name for an ad hoc query: Task 3 tests command kind and line without SQL text or a fabricated name.

---

## File map

- `src/WorkerDock.Domain/Jobs/JobRevision.cs`, job command/query classes, `JobWizard.tsx`, `ManagedJobEditor.tsx`, labels: author, persist, and edit the policy.
- `src/WorkerDock.Contracts/Templates/FailureDiagnostic.cs` (new), `TemplateResult.cs`, `ExecutionDescriptor.cs`: typed diagnostic and pinned policy contracts.
- `src/WorkerDock.Executor/FailureDiagnosticSanitizer.cs` (new), `ExecutorHost.cs`, `ChildHandle.cs`: redaction, bounds, IPC validation, and fallback diagnostics.
- `SqlTemplate.cs`, `SqlProfileDiagnostic.cs`, `HttpTemplate.cs`: template-specific SQL and HTTP details.
- `database/tenant/0003_job_failure_policy.{up,down}.sql` and `0004_attempt_failure_diagnostics.{up,down}.sql` (new), `manifest.json`, `current-state.sql`: independently migratable policy and diagnostic storage.
- `SqlAttemptStore.cs`, `SqlMonitoringQueries.cs`, retention/archive classes: durable diagnostic lifecycle and read projection.
- `MonitoringDtos.cs`, `ExecutionDetails.tsx`, `monitoringTypes.ts`: API projection and per-attempt display.

### Task 1: Pin the job-level hiding policy

**Files:** Create `database/tenant/0003_job_failure_policy.up.sql` and `.down.sql`; modify `database/tenant/manifest.json`, `current-state.sql`, `JobRevision.cs`, `JobCommandValidation.cs`, `SqlJobCommands.cs`, `SqlJobDefinitionCommands.cs`, `SqlJobLifecycle.cs`, `SqlJobManagementQueries.cs`, `InMemoryJobCommands.cs`, `ExecutionDescriptor.cs`, `SqlAttemptStore.cs`, `JobWizard.tsx`, `ManagedJobEditor.tsx`, `JobDefinitionDraft.ts`, tenant locale labels. Test `tests/WorkerDock.IntegrationTests/JobCommandTests.cs`, `JobDefinitionEditTests.cs`, `frontend/apps/tenant/src/features/jobs/ManagedJobEditor.test.tsx`, and wizard tests.

**Interfaces:** `JobRevision(..., JsonElement? DynamicValues = null, bool HideFailureDetails = false)`; `ExecutionDescriptor(..., string? ValueResolutionError = null, bool HideFailureDetails = false)`. Later tasks read the descriptor flag. Duplicate jobs copy the flag; SQL queries and inserts round-trip it.

- [ ] **Step 1: Write failing tests.** Assert new jobs default `false`, editing and duplication preserve both values, and an accepted execution retains the original revision's flag after a later edit.
- [ ] **Step 2: Run focused tests; expect failure.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~JobCommandTests|FullyQualifiedName~JobDefinitionEditTests"`; `npm --prefix frontend run test -- --run ManagedJobEditor`.
- [ ] **Step 3: Add the `0003` migration, fresh snapshot column, and manifest hashes.** `HideFailureDetails` is `bit NOT NULL DEFAULT(0)` on `JobRevisions`.
- [ ] **Step 4: Implement the two signatures and thread the flag through SQL/memory save, load, duplication, and dispatch.** Keep it on immutable job revision, never mutable job state.
- [ ] **Step 5: Add the checkbox to both create and edit forms.** Label it “Hide detailed failure output”; explain that unchecked messages and response bodies can contain unknown private data visible to Read users. Add en and pt-BR labels.
- [ ] **Step 6: Re-run the focused tests; expect pass.** Confirm the create/edit payload includes `hideFailureDetails` and the pinned execution descriptor does not change on edit.
- [ ] **Step 7: Stage only this task's hunks and commit.** `git commit -m "feat: pin failure detail policy to job revisions"` after reviewing `git diff --cached`.

### Task 2: Define and sanitize a bounded diagnostic contract

**Files:** Create `src/WorkerDock.Contracts/Templates/FailureDiagnostic.cs`, `src/WorkerDock.Executor/FailureDiagnosticSanitizer.cs`; modify `TemplateResult.cs`, `ExecutorHost.cs`, `ChildHandle.cs`. Test `tests/WorkerDock.ProcessTests/FailureDiagnosticTransportTests.cs` (new).

**Interfaces:** `FailureDiagnostic(string Kind, string? ExceptionType = null, string? Message = null, string? StackTrace = null, string? ResponseContent = null, int? SqlNumber = null, int? SqlState = null, string? SqlProcedure = null, int? SqlLine = null, string? CommandKind = null, string? HttpMethod = null, int? HttpStatus = null, string? MediaType = null, int? ResponseBytes = null, bool Truncated = false, bool Hidden = false)`; add `FailureDiagnostic? Diagnostic = null` as the last `TemplateResult` argument. `FailureDiagnosticSanitizer.Sanitize(FailureDiagnostic? input, bool hide, Func<string,string> redact): FailureDiagnostic?` returns a validated bounded projection.

- [ ] **Step 1: Write failing transport tests.** Assert literal and JSON-escaped secrets are absent; hidden output has null Message/StackTrace/ResponseContent; metadata remains; oversized Unicode/control-character input produces a valid frame below 65,536 bytes and `Truncated=true`; malformed child diagnostics are dropped while the safe result remains.
- [ ] **Step 2: Run `dotnet test tests/WorkerDock.ProcessTests --filter FullyQualifiedName~FailureDiagnosticTransportTests`; expect failure.**
- [ ] **Step 3: Implement sanitizer and transport.** Bound message to 4 KiB UTF-8, stack to 8 KiB, response content to 16 KiB, and final serialized result to at most 48 KiB; truncate on Unicode scalar boundaries with an explicit marker. Redact all string fields via the existing resolved-secret facade before IPC, including metadata. For hidden jobs discard detail fields before serialization. Validate type/range/length again in `ChildHandle` and drop only invalid diagnostics. Keep `SafeCode` and `SafeSummary` semantics unchanged.
- [ ] **Step 4: Re-run focused tests; expect pass.**
- [ ] **Step 5: Stage only this task's hunks and commit.** `git commit -m "feat: transport bounded redacted failure diagnostics"` after reviewing `git diff --cached`.

### Task 3: Capture SQL, HTTP, and executor failures

**Files:** Modify `src/WorkerDock.Templates.SqlServer/SqlTemplate.cs`, `SqlProfileDiagnostic.cs`, `src/WorkerDock.Templates.Http/HttpTemplate.cs`, `src/WorkerDock.Executor/ExecutorHost.cs`, `src/WorkerDock.Runtime/Processes/ChildHandle.cs`, `src/WorkerDock.Runtime/Ownership/OwnershipMonitor.cs`. Test existing SQL/HTTP template suites plus `tests/WorkerDock.IntegrationTests/FailureDiagnosticTemplateTests.cs` (new).

**Interfaces:** Templates return `TemplateResult.Diagnostic` using Task 2's record. Kind values are `Sql`, `Http`, `Template`, `Process`. SQL fills number/state/procedure/line/command kind; HTTP fills method/status/media type/byte count and text body for failed responses. Process failures carry safe code and state, with no invented stack.

- [ ] **Step 1: Write failing tests.** SQL `THROW 51000` yields message, number 51000, procedure and line, while an ad hoc query omits procedure and SQL text. HTTP 500 text body yields status/content and no stack; invalid UTF-8 or binary body yields status/media type/byte count but no content. An exception in a template or Executor yields type/message/stack when available. A disconnected child yields only safe process metadata.
- [ ] **Step 2: Run `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~FailureDiagnosticTemplateTests` and the relevant ProcessTests filter; expect failure.**
- [ ] **Step 3: Populate typed diagnostics at the catch/response sites.** Preserve existing outcome, `SafeCode`, `RetryAfter`, and effects calculations. Capture the HTTP error body before its current byte clearing; do not capture successful response text or SQL result rows. Let Task 2's Executor sanitizer enforce the policy before IPC.
- [ ] **Step 4: Re-run focused tests; expect pass.**
- [ ] **Step 5: Stage only this task's hunks and commit.** `git commit -m "feat: capture failed job diagnostics"` after reviewing `git diff --cached`.

### Task 4: Persist diagnostics with attempts and retention

**Files:** Create `database/tenant/0004_attempt_failure_diagnostics.up.sql` and `.down.sql`; modify `database/tenant/manifest.json`, `current-state.sql`, `src/WorkerDock.Infrastructure/Executions/SqlAttemptStore.cs`, `AttemptLifecycle.cs`, `InMemoryAttemptStore.cs`, `src/WorkerDock.Infrastructure/Retention/SqlRetentionService.cs`, `ArchiveWriter.cs`, `docs/reference/database-migrations.md`, `docs/how-to/deploy-production.md`, and `docs/how-to/setup-sql-server.md`. Test `tests/WorkerDock.IntegrationTests/AttemptLifecycleTests.cs` and `FailureDiagnosticPersistenceTests.cs` (new), plus relevant SQL migration/retention tests.

**Interfaces:** Task 1 adds `JobRevisions.HideFailureDetails bit NOT NULL DEFAULT(0)`. Add `AttemptFailureDiagnostics(AttemptId uniqueidentifier PRIMARY KEY REFERENCES Attempts ON DELETE CASCADE, DiagnosticJson nvarchar(max) NOT NULL CHECK(ISJSON(DiagnosticJson)=1))`. Persist one already-sanitized diagnostic per attempt in the same transaction that finishes the attempt; `InMemoryAttemptStore` mirrors this for development. Archive serialized diagnostics with their attempt and delete them with execution retention.

- [ ] **Step 1: Write failing tests.** Assert one diagnostic per attempt survives retry and reload, hidden jobs never store detail fields, a stale completion cannot overwrite a winner, old rows read with null diagnostics, archived attempts include diagnostics, and purged attempts remove diagnostics.
- [ ] **Step 2: Run focused IntegrationTests and SQL migration/retention checks; expect failure.** Use the existing live-SQL test gate for SQL-dependent tests rather than a new connection convention.
- [ ] **Step 3: Implement the `0004` schema migration, fresh snapshot, and manifest SHA-256 values.** Keep down migration consistent with existing rollback rules.
- [ ] **Step 4: Add idempotent diagnostic insertion to the owned completion transaction.** Do not write diagnostics for success; mirror storage in memory mode.
- [ ] **Step 5: Extend archive projection and purge with the same attempt identity.** Update migration/deployment docs to the new manifest tip.
- [ ] **Step 6: Re-run focused tests and migration verification; expect pass.** Confirm database `current-state.sql` matches an up-migrated database.
- [ ] **Step 7: Stage only this task's hunks and commit.** `git commit -m "feat: persist attempt failure diagnostics"` after reviewing `git diff --cached`.

### Task 5: Show diagnostics in execution details

**Files:** Modify `src/WorkerDock.Application/Monitoring/MonitoringDtos.cs`, `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`, `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs` if projection requires it, `frontend/apps/tenant/src/features/monitoring/monitoringTypes.ts`, `ExecutionDetails.tsx`, tenant locale labels, help content, `docs/reference/execution-guarantees.md`, `docs/reference/http-template.md`, `docs/reference/secrets.md`. Test `tests/WorkerDock.IntegrationTests/MonitoringJobsSqlTests.cs` or new execution-detail tests, and a focused `ExecutionDetails.test.tsx` (new).

**Interfaces:** Add `FailureDiagnostic? Diagnostic` to `ExecutionAttemptDto`; `SqlMonitoringQueries.ExecutionAsync(Guid executionId, CancellationToken ct)` joins `AttemptFailureDiagnostics` and deserializes the already-sanitized envelope. The existing Read-authorized endpoint keeps its route and status codes. Frontend attempt type mirrors the record in camelCase.

- [ ] **Step 1: Write failing API/UI tests.** Assert visible attempts show redacted message, stack, SQL metadata, and HTTP body; hidden attempts show only safe metadata and a clear hidden label; absent stack is labeled unavailable; truncated fields are marked; React treats malicious HTML-like diagnostic text as text.
- [ ] **Step 2: Run focused API and UI tests; expect failure.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~ExecutionDetailsDiagnosticTests`; `npm --prefix frontend run test -- --run ExecutionDetails`.
- [ ] **Step 3: Implement query/DTO/UI projection and docs.** Keep diagnostics off list, audit, alerts, and generic logs. Group the expandable detail beneath the matching attempt; use existing locale system for en and pt-BR.
- [ ] **Step 4: Re-run focused tests; expect pass.** Then run `dotnet build WorkerDock.slnx -c Release` and `npm --prefix frontend run build`; expect zero errors.
- [ ] **Step 5: Stage only this task's hunks and commit.** `git commit -m "feat: display per-attempt failure diagnostics"` after reviewing `git diff --cached`.

## Self-review

The five tasks cover the policy, contract/redaction, template capture, durable lifecycle, and execution-detail presentation from the spec. Each Review Focus case has an owning test step. Later task types and field names match the interfaces above. Before implementation, verify the live-SQL test gate and frontend script names in this checkout; use their existing commands without adding alternate infrastructure.
