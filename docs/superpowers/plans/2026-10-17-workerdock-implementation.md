# WorkerDock Implementation Plan

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
- Quartz baseline is 4.5.1; two stores use `WDS_QRTZ_` and `WDE_QRTZ_`; string-only opaque job data, locked acquisition, no direct runtime Quartz-table mutations or automatic schema creation.
- Secrets resolve at execution time; never persist their values or emit raw driver errors/bodies. Anonymous read/manage mode and optional Entra management-role protection must match spec section 7 exactly.
- Infinite timeout remains renewable/stoppable. A 10-second application heartbeat, 60-second lease and 30-second cancellation grace are the initial defaults; shutdown/startup/network setup remain bounded.
- WorkerDock uses MIT; preserve Apache-2.0 and other dependency notices. Do not reuse Milvaion code, branding or UI.

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
| [03 Templates](2026-10-17-workerdock-03-templates.md) | 10–13 | Usable SQL/HTTP templates, vault adapter, profile diagnostics and metadata forms |
| [04 Scheduling and UI](2026-10-17-workerdock-04-scheduling-ui.md) | 14–16 | Scheduled jobs, complete operator workflow and monitoring in English/pt-BR |
| [05 Operations and center](2026-10-17-workerdock-05-operations-center.md) | 17–20 | Durable observability, alerts, retention/archive and independent central registry |
| [06 Release](2026-10-17-workerdock-06-release.md) | 21–23 | Repeatable Windows deployment, release evidence and complete OSS documentation |

Within each task, write the specified behavioral test, observe its relevant failure, implement the stated interface, verify and commit. Test snippets pin assertions rather than provide whole test fixtures. Arrange their named inputs in the listed test class; fixture methods are test-local unless an interface is explicitly exported. Never count a missing SDK/database or an unrelated compile failure as the expected red test. Do not write tests that merely assert scaffolding/file existence.

At every stage exit, report changes, decisions, commands/results and limitations before continuing under the approved execution method. A failure of the approved architecture's required proof pauses dependent work for a concrete design correction, not an unapproved switch to a custom engine or broker.

## File structure and shared conventions

Paths below and in stage plans are repository-relative. `src/<Project>/<Project>.csproj` is created when that project's first deliverable is implemented, targeting `net10.0`; Windows-only Runtime/Executor tests target `net10.0-windows`. The solution is `WorkerDock.slnx`. Use `Directory.Build.props`, `Directory.Packages.props`, `global.json`, package lock files and `frontend/package-lock.json`; commit exact resolved versions at first setup, without floating package references. Do not create empty projects just to match this map.

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

Migration sequence: tenant `0001_bootstrap`, `0002_quartz`, `0003_execution`, `0004_templates`, `0005_scheduling`, `0006_observability`, `0007_alerts`, `0008_retention`; central `0001_registry`. Every pair is `.up.sql`/`.down.sql` with manifest/checksum entries. Later changes to already committed objects get a new migration rather than editing applied history. Down scripts must refuse unsafe data loss and identify restore prerequisites.

## Toolchain and validation prerequisites

Read-only local inspection found .NET SDK **7.0.202 only** and Node **24.15.0**. .NET 10 is required before implementation builds; no SDK or dependency was installed during planning. SQL Server availability and a disposable test login have not been established. Do not infer a usable test database from a local service name.

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
| 8 secrets, redaction, diagnostics and least privilege | 01–03, 07, 10–12, 17, 20–22 |
| 9 trusted templates, SQL and HTTP safety | 07, 10–13, 23 |
| 10 complete operator UX and localization | 02, 13, 15–16, 20, 22 |
| 11 audit/metrics/logs/alerts/retention/archive | 01, 17–19 |
| 12 health/migrations/upgrade/rollback | 01, 03, 16, 19–22 |
| 13 acceptance items 1–7 and 15–16 | 03–09, 14–15, 22 |
| 13 acceptance items 8–14 | 02, 10–13, 16–23 |
| 14 documentation/release limitations | Each owning task's guide, consolidated in 23 |
| 15 approval gates | This review index and execution handoff below |

## Review record and execution handoff

Planning-only artifacts: no scaffolding, application code, dependency installation, migrations executed or implementation tests run. Self-review completed: all spec sections and acceptance items map to tasks; task tests pin the five review-focus cases; exported profile/execution contracts and migration numbering are consistent. Structural verification found seven linked documents, 23 unique ordered tasks and 115 task steps, with no unresolved placeholders, completed implementation boxes or broken plan links. The plan set is approximately 12,100 words versus 9,800 in the spec. All implementation checkboxes remain unchecked.

Recommended method: **Subagent-driven**, because isolated process control, SQL/Quartz race boundaries and authorization deserve review at each task boundary. Native execution is supported as the lower-overhead alternative, with one independent whole-branch review at the end. No execution method has been selected yet. The user explicitly invoked using-git-worktrees during planning; create a managed worktree from the committed plan set and preserve it for implementation after approval.

- [ ] User reviews and approves these six stage plans.
- [ ] User selects Subagent-driven or Native execution.
- [ ] Reuse the user-requested managed worktree prepared with the plan set; verify its baseline, then invoke the selected execution skill after approval.
- [ ] Execute task 01 only after those gates; continue in dependency order with stage reports.
