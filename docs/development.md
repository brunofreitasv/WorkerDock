# Local development

Install .NET SDK 10.0.402 and Node.js 24.15.0 or a compatible maintained Node release (Node is needed only to build/test assets). From the repository root:

```powershell
dotnet restore WorkerDock.slnx --locked-mode
npm.cmd --prefix frontend ci
npm.cmd --prefix frontend run test -- --run
npm.cmd --prefix frontend run build
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~AccessPolicyTests|FullyQualifiedName~WebHostTests|FullyQualifiedName~InMemoryTenantSettingsTests|FullyQualifiedName~MigrationInputTests|FullyQualifiedName~MigrationPrerequisiteTests|FullyQualifiedName~Invalid_database_kind_fails_before_connecting|FullyQualifiedName~Invalid_settings_and_actor_are_rejected_before_connecting'
dotnet build WorkerDock.slnx -c Release
```

Quartz memory tests use the same `QuartzTopology` creation methods as the SQL
adapter, with a disposable single-process RAM store. Run them separately so a
missing SQL Server is never counted as a Quartz pass:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzTopologyTests'
```

`QuartzTopology.CreateMemory(instanceId, "Development")` is selected by the
composition root when a local Quartz host is added. The current browser memory
instance exercises tenant settings, job commands and execution controls; it does
not start a Quartz scheduler or execute submitted work. See
[quartz-integration.md](quartz-integration.md) for the scheduler port and its
memory/SQL behavior.

If npm fails with `UNABLE_TO_VERIFY_LEAF_SIGNATURE` on a machine whose corporate CA is in the Windows trust store, use `$env:NODE_OPTIONS='--use-system-ca'` in that restore shell, then repeat `npm.cmd --prefix frontend ci`. Do not disable TLS verification. This is a local restore workaround, not an application runtime requirement.

## Disposable memory instance

First create/trust the ASP.NET development HTTPS certificate if this machine has not been configured: `dotnet dev-certs https --trust`. Trusting a certificate is a local developer setup decision. Then, in a fresh PowerShell session from the repository root:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Tenant__Adapter='Memory'
$env:Tenant__InstanceId=[guid]::NewGuid().ToString()
Remove-Item Env:Tenant__ConnectionString -ErrorAction SilentlyContinue
Remove-Item Env:Access__Authority,Env:Access__ClientId,Env:Access__ClientSecretReference,Env:Access__RequiredManagementRole -ErrorAction SilentlyContinue
npm.cmd --prefix frontend run build
dotnet run --project src/WorkerDock.Web --urls https://localhost:5001
```

Open `https://localhost:5001/`. The same host serves the compiled React app, `/api/session`, `/api/settings`, `/api/antiforgery` and health routes. The shell clearly labels anonymous access: anyone able to reach this instance can read and manage it. Bind only to localhost during local development. Set Entra configuration as described in [authentication.md](authentication.md) when user restrictions are required.

For asset watch mode in another shell, run `npm.cmd --prefix frontend run dev`; it rebuilds assets into the same web host's `wwwroot`. Refresh the browser after a build. There is no separate API origin or permissive CORS proxy. Production serves the compiled assets without Node.js.

A manual versioned update, from another PowerShell session with the trusted certificate:

```powershell
$origin='https://localhost:5001'
$settings=Invoke-WebRequest "$origin/api/settings" -SessionVariable browser
$csrf=Invoke-RestMethod "$origin/api/antiforgery" -WebSession $browser
$headers=@{'If-Match'=$settings.Headers.ETag[0];'X-WorkerDock-CSRF'=$csrf.requestToken;'Origin'=$origin}
Invoke-RestMethod "$origin/api/settings" -Method Put -WebSession $browser -Headers $headers -ContentType 'application/json' -Body '{"displayName":"Team","displayTimeZone":"UTC"}'
```

The mutation requires the server antiforgery header, a same-origin `Origin`, and the quoted GET `ETag` in `If-Match`. The browser shell supplies these automatically (the browser sends `Origin`). API/auth routes reject plaintext HTTP. The old `/_test/settings` route and fixed development CSRF header were removed. Preferences store only language/time zone locally. Anonymous browser identities are opaque protected cookies; actors and correlation IDs come from the server.

`InMemoryTenantSettingsService` implements the same `ITenantSettingsService` port as SQL. It exercises version checks and atomic settings/audit behavior but is disposable, single-process and available only in Development. Restart loses settings/audit. `/health/live` reports liveness; `/health/ready` verifies the selected tenant settings adapter. Operational dashboard/monitoring/audit routes currently report `available:false`; later stages provide their data projections. The shell displays that limitation instead of fabricated results.

Real SQL setup/tests are in [database.md](database.md). Memory and OIDC fixture results do not establish SQL durability, migration correctness, security isolation or Quartz clustering. Running the full unfiltered integration suite without `WORKERDOCK_TEST_SQL` produces explicit `SQL_TEST_BLOCKED` failures for live SQL cases; report them separately, never as passes.

## Schedule policy tests without SQL

The evaluator and memory Quartz tests use the same application scheduling port
and native Quartz policy calendar as SQL projections. Run from the repository
root; no SQL variables are needed:

```powershell
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter '(FullyQualifiedName~CalendarPersistenceTests|FullyQualifiedName~CalendarExecutionTests)&Store!=Sql'
```

These prove filtering, one-time conversion, bounded enumeration, in-process JSON
serialization, stored memory trigger calculations and actual memory job dispatch
with an injected clock. They do not prove SQL calendar blobs survive restart.
The browser memory host has no schedule editor; that editor and its
preview endpoint arrive in the scheduling UI stage. See
[quartz-integration.md](quartz-integration.md) for the five-field and DST policy.

## Publication recovery without SQL

Run the same publication/reconciliation code against a disposable RAMJobStore
and in-memory business/outbox test adapters:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~PublicationRecoveryTests|FullyQualifiedName~QuartzTopologyTests'
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet build WorkerDock.slnx -c Release
```

The fault tests cover both sides of Quartz writes and application ack, concurrent
publishers, identity/digest corruption, completed/active executions with consumed
triggers, unsupported payloads, future authorized retry deadlines, permanent
trigger errors, actual memory probe firing and direct control-job startup. The
Scheduler contains no test executor; its guarded generic wrapper is invoked by
the Worker. Use [database.md](database.md) for migration 0003a_projection, restricted
runtime setup, Scheduler startup and real SQL publication tests. RAM restart
means loss of its disposable store; no SQL or multi-process guarantee is inferred.

## Attempt ownership and execution controls

The same `IAttemptStore`, `IAttemptSignals` and `IExecutionControls` ports have
SQL and disposable memory implementations. `InMemoryJobCommands` shares its
accepted executions and control command receipts with `InMemoryAttemptStore`.
The Development web host exposes stop/resolve routes against that same memory
state. The browser host does not launch executors or start Quartz; seeded tests
exercise execution without a SQL login. Restart discards all memory state.

Run these exact commands from the repository root on Windows:

```powershell
dotnet restore WorkerDock.slnx --locked-mode
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~AttemptLifecycleTests|FullyQualifiedName~ExecutionControlsEndpointTests|FullyQualifiedName~PublicationRecoveryTests'
dotnet test tests/WorkerDock.ProcessTests -c Release --filter FullyQualifiedName~ExecutionControlTests
dotnet test tests/WorkerDock.ProcessTests -c Release
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet build WorkerDock.slnx -c Release
```

The control tests use real contained Windows child/grandchild processes through
the production Quartz wrapper and Runtime supervisor. They fill both execution
slots with infinite attempts, observe ownership renewal, force-stop one tree
while preserving its sibling, enforce the 30-second timeout grace, and reject a
late renewal after the monotonic safety deadline despite host UTC skew. Memory
business state in these tests is not proof of SQL fencing or durable stop.
Publication tests also invoke the actual generic wrapper across lost publication
and completion acknowledgements. SQL-only fixtures remain separate; use the
exact migration/setup commands in [database.md](database.md).

`POST /api/executions/{executionId}/stop` accepts
`{ "force": false, "confirmForce": false, "reason": "operator request" }`.
Force requires both booleans to be true and a nonblank reason. The request uses
the same session CSRF header and Origin as other management commands.
`POST /api/executions/{executionId}/resolve` accepts
`{ "decision": "Cancel", "reason": "target checked; possible overlap acknowledged" }`;
other decisions are `ReconcileSucceeded` and `AuthorizeRetry`. Both routes require
Manage. The response is a command receipt after the stop/decision and audit commit.


## Task 09 memory capacity verification

These commands need no SQL login and exercise the production policy model,
admission decisions, generic wrapper and actual RAMJobStore limits:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~CapacityPolicyTests|FullyQualifiedName~JobAdmissionTests|FullyQualifiedName~CapacityEndpointTests|FullyQualifiedName~PublicationRecoveryTests|FullyQualifiedName~AttemptLifecycleTests'
dotnet test tests/WorkerDock.ProcessTests -c Release
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet build WorkerDock.slnx -c Release
```

To run every integration case that does not need SQL, including non-connection
migration validation, use:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store!=Sql'
```

Memory success does not establish SQL transactions, migrations, grants, restart
recovery or three-node capacity. Use the real-SQL commands in database.md before
claiming those properties have been demonstrated.

## Task 09A custom schema verification

Production and local SQL installations use `[workerdock]` for business tables,
migration history, both Quartz stores and test probes. Fresh database setup,
least-privilege runtime user setup, migration, rollback and all real SQL test
commands are in [database.md](database.md#custom-schema-fresh-installation-task-09a).
Existing unpublished prototype `dbo` databases require a fresh database or a
separately reviewed data-move upgrade; there is no automatic conversion.

These exact commands run every SQL-independent integration case, all unit and
Windows process cases, and the Release build without a SQL login:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store!=Sql'
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet test tests/WorkerDock.ProcessTests -c Release
dotnet build WorkerDock.slnx -c Release
```

`CustomSqlSchemaTests` checks the source SQL contract, paired manifest bytes and
public Quartz prefix option; these are not proof of actual SQL execution. Run
`MigrationTests.Fresh_custom_schema_installs_and_rolls_back_all_pairs` and the
other database.md SQL suites on real SQL Server with RCSI OFF and ON before
claiming migrations, rollback, grants or clustering are validated.

## Stage 02 final review regression checks

Run the focused memory/process regressions, then the complete SQL-independent suite and Release build:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~JobCommandTests|FullyQualifiedName~CommandOutcomeTests|FullyQualifiedName~JobAdmissionTests|FullyQualifiedName~CustomSqlSchemaTests'
dotnet test tests/WorkerDock.ProcessTests -c Release --filter FullyQualifiedName~Oversized_prelaunch_request
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store!=Sql'
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet test tests/WorkerDock.ProcessTests -c Release
dotnet build WorkerDock.slnx -c Release
```

Manual run commands become Applied when publication or attempt admission commits; a permanent projection rejection or cancellation before publication becomes Failed. Transient publication failures leave Pending for retry. Running stop commands remain Pending until local completion confirms termination; unconfirmed abandonment becomes Failed. Command outcomes and their audit append commit together. Request-key replays return the original command identity with its current outcome.

`0003d_dispatch` adds the durable maintenance cursor and indexes. Reconciliation publishes/reviews its bounded outbox batch first, then allows two seconds for one eligible job's admission maintenance. The indexed cursor cycles over jobs with queued/retry work, including across restarts. Each job reads at most 10,032 unpermitted candidates (10,000 is the maximum supported backlog) and changes at most 32 admission decisions, held deliveries, and capacity bindings per pass. Existing permits are counted separately. Quartz remains the only acquisition owner.

Job validation budgets the serialized descriptor, 1 KiB of execute-envelope overhead and 8 KiB for future pinned nonsecret profile/settings fields inside the 64 KiB frame. Runtime checks the actual complete frame before launching. A proven rejection before handler permission records known no effects; ambiguous permission writes remain uncertain.

Memory/source-contract passes do not establish SQL latency, durability or grants. Follow database.md for real SQL setup; live SQL remains SQL_TEST_BLOCKED until its credentials are available.

## Task 10 vault/catalog verification

Use the development-only secret adapter and explicit nonsecret mapping described in [secrets.md](secrets.md). Approved mappings are protected deployment configuration; there is no secret-resolution API or arbitrary environment lookup. SQL profiles and audited tenant integration settings remain owned by their separate tasks.

Run focused behavior checks and then every SQL-independent Release check:

```powershell
dotnet restore WorkerDock.slnx --locked-mode
dotnet test tests/WorkerDock.UnitTests -c Release --filter FullyQualifiedName~SecretBoundaryTests
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~TemplateCatalogTests
dotnet test tests/WorkerDock.ProcessTests -c Release --filter FullyQualifiedName~Provider_failure_and_rotated_attempts
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store!=Sql'
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet test tests/WorkerDock.ProcessTests -c Release
dotnet build WorkerDock.slnx -c Release
```

Shared schema fixtures live at `tests/fixtures/template-schemas.json`; server tests consume them now and Task 13 uses the same corpus for forms. The catalog metadata routes require Manage and expose descriptor/schema/hints and selection status only. Installed SQL/HTTP handler references are added by Tasks 11/12; the current roots deliberately register an empty installed package list and cannot activate a pool whose catalog digest differs.

Task 10 real-SQL migration/permission/promotion tests are separate in [database.md](database.md#task-10-immutable-template-catalog). Their status remains **SQL_TEST_BLOCKED** without the provisioned disposable SQL logins. Memory and process tests do not prove SQL durability or grant enforcement.

Authoring boundary still pending Task 13: the existing job-save path retains its built-in-only gate and has not yet adopted catalog schema validation. Task 10 validates registration metadata and validates configuration again inside the child before any handler effects. Task 13 must replace that authoring gate with trusted registered-catalog policy and enforce server schema validation before enable/run-now; saving a revision today is not evidence that its template schema has passed.
