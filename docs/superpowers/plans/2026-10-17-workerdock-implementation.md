# WorkerDock Implementation Plan

> **Historical architecture note:** This plan predates the approved [database-free Control Center design](../specs/2026-10-19-database-free-control-center-design.md). Its central database architecture and related migration tasks are superseded; tenant SQL and Quartz requirements remain.

All WorkerDock-owned tables, triggers, keys, indexes and explicit SQL references belong to `[workerdock]` in each database. Each independent tenant database and the separate central registry database owns its own schema; this is not a shared multitenant database or browser-supplied tenant selector. Quartz uses `[workerdock].WDS_QRTZ_` and `[workerdock].WDE_QRTZ_`. System `sys.*` objects and database principals retain their SQL Server names. New objects require paired migrations, checksums and explicit least-privilege grants; runtime startup never creates schema.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved WorkerDock platform through six reviewable, independently verifiable stages.

**Architecture:** Each independent tenant runs an IIS React/API application, a scheduler service and generic worker services against its own SQL Server database. Quartz owns scheduling and dispatch; WorkerDock owns business state, audit, publication reconciliation and supervised execution. The separate control center registers existing tenants and offers health observations and dashboard links.

**Tech Stack:** .NET 10/C#, ASP.NET Core, Quartz.NET 4.5.1, SQL Server 2022, React/TypeScript/Vite/Material UI/TanStack Query, Windows services/IIS.

**Spec:** [Approved design](../specs/2026-10-17-workerdock-design.md), approved 2026-10-17. Read it together with each stage plan.

## Global Constraints

The following requirements apply to every stage; quoted sentences retain the spec's exact values.

- "SQL Server 2022 minimum; default schema compatibility target 160."
- ".NET 10 LTS, ASP.NET Core, C#, with compatible pinned stable package versions and current security patches."
- "Windows Server 2022 or newer, x64, IIS and the appropriate ASP.NET Core Hosting Bundle. Windows 11 supports local development."
- "React, TypeScript, Vite, Material UI and TanStack Query. Production serves compiled assets from ASP.NET Core/IIS and does not require Node.js."
- "Redis, RabbitMQ, external message brokers, hosted control services, and telemetry collectors are not required."
- "Every tenant database stores one tenant context. Tenant selection is deployment configuration, never an arbitrary browser-supplied database selector."
- "WorkerDock does not claim exactly-once effects."
- "Dashboard users cannot upload assemblies or scripts, execute arbitrary code, or install packages."
- "Every SQL object change has a numbered forward script and paired rollback script, checksums, schema-history entry, prerequisites and transactional boundaries."
- "On a decrease, existing work drains; no new attempt starts until capacity is below the new limit. Zero pauses admission."
- "Defaults: safe execution logs 30 days, execution history 90 days, audit 365 days."
- "Provide English and Brazilian Portuguese from the first release, including validation text and dates/numbers."
- Quartz baseline is 4.5.1; two stores use `[workerdock].WDS_QRTZ_` and `[workerdock].WDE_QRTZ_`; string-only opaque job data, locked acquisition, no direct runtime Quartz-table mutations or automatic schema creation.
- Secrets resolve at execution time; never persist their values or emit raw driver errors/bodies. Anonymous read/manage mode and optional Entra management-role protection must match spec section 7 exactly.
- Infinite timeout remains renewable/stoppable. A 10-second application heartbeat, 60-second lease and 30-second cancellation grace are the initial defaults; shutdown/startup/network setup remain bounded.
- WorkerDock uses MIT; preserve Apache-2.0 and other dependency notices. Do not reuse Milvaion code, branding or UI.
- During development, provide an explicit in-memory testing instance through the same application ports. It is disposable and single-process; it must never be presented as proof of SQL Server durability, Quartz clustering, security isolation or migration correctness. Real SQL Server remains the required production store. Keep all SQL schema changes in paired forward/rollback scripts and keep `docs/development.md`/`docs/database.md` current with exact mock and real local setup/test commands.

## Review Focus

These five implied boundary cases are assigned explicit tests in the linked tasks; they are not deferred review suggestions.

1. Concurrent browser edits and duplicate POSTs must not overwrite revisions or create duplicate runs — Task 05.
2. A policy change while a node is offline/rejoining must not admit children using stale limits — Task 09.
3. Credential rotation/unavailability during an attempt must never disclose the old/new secret or silently change tenant identity — Tasks 10–12.
4. A locale change or repeated DST hour must not change an already selected UTC occurrence — Tasks 04 and 15.
5. Disk-full/archive permission failure and oversized diagnostics must preserve authoritative job state and unarchived data — Tasks 17 and 19.

## Scope and execution order

This is the review index, not an extra implementation task. Execute the linked plans in order. Each stage produces a runnable increment, with its limitations explicit; do not call a partial stage the finished product.

| Stage plan | Tasks | Runnable increment and exit gate |
| --- | --- | --- |
| [01 Foundation](2026-10-17-workerdock-01-foundation.md) | 01–04 | Audited tenant settings API, access-controlled React shell, disposable SQL migrations and proven Quartz stores/calendar |
| [02 Execution](2026-10-17-workerdock-02-execution.md) | 05–09 | Manual executions through Quartz and isolated test handlers, durable stop/recovery and tenant-wide capacity |
| [03 Templates](2026-10-17-workerdock-03-templates.md) | 10–12, 12A, 13 | Usable SQL/HTTP templates, vault adapter, protected integration settings, profile diagnostics and metadata forms |
| [04 Scheduling and UI](2026-10-17-workerdock-04-scheduling-ui.md) | 14–16 | Scheduled jobs, complete operator workflow and monitoring in English/pt-BR |
| [05 Operations and center](2026-10-17-workerdock-05-operations-center.md) | 17–20 | Durable observability, alerts, retention/archive and independent central registry |
| [06 Release](2026-10-17-workerdock-06-release.md) | 21–23 | Repeatable Windows deployment, release evidence and complete OSS documentation |

Within each task, write the specified behavioral test, observe its relevant failure, implement the stated interface, verify and commit. Test snippets pin assertions rather than provide whole test fixtures. Arrange their named inputs in the listed test class; fixture methods are test-local unless an interface is explicitly exported. Never count a missing SDK/database or an unrelated compile failure as the expected red test. Do not write tests that merely assert scaffolding/file existence.

At every stage exit, report changes, decisions, commands/results and limitations before continuing under the approved execution method. The user's later in-memory testing directive allows development to advance while local SQL access is unavailable; carry missing real-SQL proofs as explicit unresolved acceptance items and never relabel mock evidence as production proof. A failure observed on real SQL/Quartz requires a concrete design correction, not an unapproved switch to a custom engine or broker.

## File structure and shared conventions

Paths below and in stage plans are repository-relative. `src/<Project>/<Project>.csproj` is created when that project's first deliverable is implemented. Use `net10.0` for Domain, Contracts, Application, Infrastructure, Quartz, templates, Web, ControlCenter, Scheduler, DatabaseMigrator, UnitTests and IntegrationTests. Use `net10.0-windows` for Runtime, Worker, Executor and ProcessTests. Quartz depends on the Application supervisor port, not Windows Runtime; Worker injects Runtime's implementation. Integration tests launch Windows binaries as external fixtures rather than referencing their Windows-only assemblies. This keeps the dependency graph compatible with the [target-framework rules](https://learn.microsoft.com/en-us/dotnet/standard/frameworks). TestHost and TestTemplates use `net10.0`; Windows-only containment fixtures live in ProcessTests. The solution is `WorkerDock.slnx`. Use `Directory.Build.props`, `Directory.Packages.props`, `global.json`, package lock files and `frontend/package-lock.json`; commit exact resolved versions at first setup, without floating package references. Do not create empty projects just to match this map.

| Location | Boundary |
| --- | --- |
| `src/WorkerDock.Domain/` | Immutable business records, policy decisions and state enums |
| `src/WorkerDock.Contracts/` | Public template/vault/alert/logging contracts; no Quartz dependency |
| `src/WorkerDock.Application/` | Named commands/queries and persistence ports |
| `src/WorkerDock.Infrastructure/` | SQL adapters, authentication, secrets, egress, archive and telemetry |
| `src/WorkerDock.Quartz/` | Both scheduler configurations, calendars, outbox projection and wrappers |
| `src/WorkerDock.Runtime/` | Child process, IPC, ownership and independent supervisor control loop |
| `src/WorkerDock.Templates.SqlServer/`, `src/WorkerDock.Templates.Http/` | Installed built-in handlers and metadata |
| `src/WorkerDock.Web/`, `src/WorkerDock.ControlCenter/` | Separate web composition roots and endpoints |
| `src/WorkerDock.Scheduler/`, `src/WorkerDock.Worker/`, `src/WorkerDock.Executor/` | Separate process composition roots |
| `src/WorkerDock.DatabaseMigrator/` | Explicit schema installation/upgrade/rollback |
| `database/tenant/`, `database/central/`, `database/upstream/quartz-4.5.1/` | Paired migrations, history/checksum manifests, attributed upstream source |
| `frontend/apps/tenant/`, `frontend/apps/center/`, `frontend/packages/ui/` | Independent entry points, shared accessible components/localization |
| `tests/WorkerDock.UnitTests/`, `tests/WorkerDock.IntegrationTests/`, `tests/WorkerDock.ProcessTests/` | Pure policies; real SQL/API integration; real Windows process behavior |
| `tests/WorkerDock.TestHost/`, `tests/WorkerDock.TestTemplates/`, `frontend/e2e/` | Test-only process/handler probes and browser scenarios |
| `deploy/`, `docs/`, `.github/workflows/` | Installation, operator/developer documentation and repeatable validation |

Use `Guid` for persisted opaque IDs, `DateTimeOffset` normalized to UTC at boundaries and UTC `datetime2` in SQL. Version concurrency uses `byte[]` SQL rowversion represented as an opaque ETag on HTTP; reject stale `If-Match` with 412. `CancellationToken ct` terminates operations. Interfaces use `ValueTask<T>` unless otherwise stated. API DTOs are separate safe projections, not entity serialization. JSON strings use bounded `JsonElement` values; secrets have dedicated reference types.

All command paths require the common `ActorContext` and audit transaction. All runtime policy transitions use the business lock; Quartz calls are outside it. A `CommandReceipt(Guid CommandId, Guid TargetId, string State)` acknowledges durable intent; projection status is queried independently. The stage that introduces a type owns its fields and exported signatures; later stages reference that task, not a second definition.

Use safe RFC ProblemDetails with a stable `code` and field paths: validation 400, unauthenticated API 401, forbidden 403, missing resource 404, conflicting idempotency/policy state 409 and stale ETag 412. No raw exception or submitted value is echoed. IDs/current revisions/actor identity are server-controlled even if a command carries a proposed record; route and body IDs must agree. State-changing commands require CSRF; read-policy diagnostic refresh must not alter configuration.

Each task creates or extends its own schema objects and least-privilege grants in a new paired migration. Task 01 creates the general outbox envelope; Task 05 extends it for execution dispatch instead of creating a second outbox. Task 08 creates minimal attempt status events consumed by Task 16; Task 17 adds optional log/aggregate storage. Test-only ports may be substituted before their production owner task, but never wire a no-op audit, secret or safety implementation into shipping hosts. The three-node Quartz proof in Task 03 uses separate processes; all test projects/fixtures needed by a task are created there, not assumed to exist from a later stage.

Test command convention: use the VSTest-compatible xUnit runner and Microsoft.NET.Test.Sdk, pinned in Task 01, so `dotnet test ... --filter FullyQualifiedName~...` has one documented meaning. Frontend root scripts are defined in Task 02: `test` runs Vitest and `build` builds current app workspaces; Task 12A adds `e2e` and its Playwright config. Arguments after `--` pass through. Missing test infrastructure is a blocker, not an expected red behavior test.

Initial migration sequence: tenant `0001_bootstrap`, `0002_quartz`, `0003_execution`, `0004_templates`, `0004a_integration_settings`, `0005_scheduling`, `0006_observability`, `0007_alerts`, `0008_retention`; central `0001_registry`. Every pair is `.up.sql`/`.down.sql` with manifest/checksum entries. Manifest order is explicit, not an integer parse of migration IDs. Tasks that extend objects after an earlier task's committed migration add an ordered suffix migration (for example `0003a_attempts`), updating the manifest; this initial list is not permission to rewrite applied history. Down scripts must refuse unsafe data loss and identify restore prerequisites.

## Toolchain and validation prerequisites

Execution preflight on 2026-10-17 found .NET SDK **10.0.402** alongside 7.0.202; Node was previously observed at **24.15.0**. No SDK was installed by this task. Local SQL Server rejects the current Windows login, and WORKERDOCK_TEST_SQL is unset; an approved disposable test connection remains required. Do not infer a usable test database from a local service name.

The [Microsoft Windows support matrix](https://learn.microsoft.com/en-us/dotnet/core/install/windows) supports the selected .NET 10/Windows Server 2022 x64 pairing. The [Quartz 4 quick start](https://www.quartz-scheduler.net/documentation/quartz-4.x/quick-start.html) places DI, hosting and System.Text.Json in core: do not copy a Quartz 3 package recipe. [Vite](https://vite.dev/guide/) requires modern Node; the observed Node 24 meets the documented floor. Choose React 19 with compatible [MUI peer dependencies](https://mui.com/material-ui/getting-started/installation/), then pin resolved packages in Task 02. No CDN assets or hosted fonts are required at runtime.

Task 01 preflight installs/locates a supported .NET 10 SDK under the execution permissions and pins its exact version; otherwise report the actual blocker. Integration fixtures accept `WORKERDOCK_TEST_SQL` from the process environment, redact it completely, create only uniquely named `WorkerDockTest_<guid>` databases and refuse dropping any database not created by that fixture. Use Windows integrated authentication when available. No Docker or external broker is required. Failure to provision the disposable fixture is a failing required check, never a green skip.

Use xUnit for .NET, Vitest/Testing Library for frontend units and Playwright for real browser flows. Task-local commands select the named test class. At stage exits run all tests for changed components plus `dotnet build WorkerDock.slnx -c Release`; frontend stages also run `npm.cmd --prefix frontend run build` and their browser checks. Native process tests require Windows; memory-only SQL substitutes cannot satisfy acceptance. Preserve synthetic fault/secret fixtures in test projects only.

## Spec coverage map

| Spec sections/acceptance | Implementing tasks |
| --- | --- |
| 1–3 product boundaries, deployment model, runtime/project choices | 01–04, 07, 20–23 |
| 4 data/revision/idempotency model | 01, 05–06, 10–11, 14, 17–20 |
| 5 Quartz ownership, atomic intent, capacity, leases, recovery | 03, 05–09, 14, 22 |
| 6 cron, DST, missed/overlap behavior, finite/infinite execution, stop | 04, 08–09, 14–16 |
| 7 complete authentication/authorization matrix, API/CSRF parity | 02, 13, 15–16, 20, 22 |
| 8 secrets, redaction, diagnostics and least privilege | 01–03, 07, 10–12, 12A, 17, 20–22 |
| 9 trusted templates, SQL and HTTP safety | 07, 10–13, 23 |
| 10 complete operator UX and localization | 02, 13, 15–16, 20, 22 |
| 11 audit/metrics/logs/alerts/retention/archive | 01, 17–19 |
| 12 health/migrations/upgrade/rollback | 01, 03, 16, 19–22 |
| 13 acceptance items 1–7 and 15–16 | 03–09, 14–15, 22 |
| 13 acceptance items 8–14 | 02, 10–13, 16–23 |
| 14 documentation/release limitations | Each owning task's guide, consolidated in 23 |
| 15 approval gates | This review index and execution handoff below |

## Review record and execution handoff

The full-plan review on 2026-10-17 identified interface and sequencing gaps beyond the initial structural checks. The corrections below belong to the user-requested managed worktree; this is the authoritative plan set for implementation. No product code is part of this review.

Planning-only artifacts: six stage plans contain 24 tasks and 120 task steps, including new Task 12A. No scaffolding, application code, dependency installation, migrations executed or implementation tests run. The original structural review was insufficient to prove contract readiness. The full review corrected the issues listed below; all implementation checkboxes remain unchecked.

| Finding | Correction and owning task |
| --- | --- |
| Quartz integration could be wired to a Windows-only Runtime dependency | Explicit target-framework graph and platform-neutral supervisor port, Tasks 03/07/08 |
| Three Quartz cluster nodes were not given a reproducible process fixture | Separate TestHost processes, Task 03 |
| Outbox interface supported only execution payloads | Versioned generic projection envelope with execution/schedule/alert kinds, Task 06 |
| Child request omitted template identity and mixed runtime services into IPC | Serializable descriptor plus child-local facade context, Task 07 |
| Completion/retry contract could not convey timeout cause or HTTP Retry-After | Typed completion and bounded retry delay, Tasks 07/08/12 |
| Edit/profile/preview screens lacked complete server contracts | Protected edit/profile queries and explicit local-time conversion, Tasks 05/11/14 |
| Vault/egress management had no owned deliverable | Dedicated Task 12A with deployment-bound validation and role tests |
| New-job wizard could leave a partially scheduled enabled job | Disabled draft and idempotent final activation, Tasks 05/14/15 |
| Alert delivery and durable log deduplication were underspecified | Delivery ownership/sequence identities and sink CRUD, Tasks 17/18 |
| Health refresh exception conflicted with management-mutation wording | Read access only for observational refresh; configuration stays management-only, Task 20 |
| Phase dependencies postponed required migration grants and test bootstrap | Ownership rules above and explicit baseline schemas/fixtures |

Readiness verdict at plan approval: sufficiently specified to begin Task 01. The user subsequently directed an in-memory testing instance first, with SQL Server adapters and complete setup/migration scripts retained for later real database use. In-memory tests permit staged development but cannot validate SQL locks, migrations, Quartz clustering or durability; label these checks pending, not passed. Production/release readiness still requires the real SQL/Windows proofs described in the task and stage exits. .NET 10.0.402 was observed at execution preflight; local SQL Server rejected the Windows login.

Selected method: **Subagent-driven** by explicit user invocation. The managed worktree is active. Each task gets an independent review; deferred SQL evidence must be closed before a production release claim.

- [x] User reviews and approves these six stage plans (2026-10-17: "go ahead").
- [x] User selects Subagent-driven execution (explicit skill invocation, 2026-10-17).
- [x] Reuse the user-requested managed worktree prepared with the plan set; verified detached linked worktree at 9339d87 with only reviewed plan edits and no implementation baseline to test.
- [x] Began Task 01 after approval on 2026-10-17; continue in dependency order with per-task review and separate unresolved real-SQL evidence.

Task 09A (2026-10-18): the user requested a pre-release fresh-install rewrite of all existing tenant migration pairs and runtime references to [workerdock]. No deployed/applied database was available. Unpublished prototype dbo databases require a fresh database or a separately reviewed additive data-move upgrade; rewriting released/applied history remains prohibited. Real SQL migration, permissions and clustering evidence is still pending.
