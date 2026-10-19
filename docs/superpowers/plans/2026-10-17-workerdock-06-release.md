# WorkerDock Release Implementation Plan

All WorkerDock-owned tables, triggers, keys, indexes and explicit SQL references belong to `[workerdock]` in each database. Each independent tenant database and the separate central registry database owns its own schema; this is not a shared multitenant database or browser-supplied tenant selector. Quartz uses `[workerdock].WDS_QRTZ_` and `[workerdock].WDE_QRTZ_`. System `sys.*` objects and database principals retain their SQL Server names. New objects require paired migrations, checksums and explicit least-privilege grants; runtime startup never creates schema.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a reproducible Windows release with tested upgrades, operational evidence and complete open-source documentation.

**Architecture:** Deployment packages install the already-built independent tenant/center applications and services. Release validation exercises the real SQL/Quartz/process/auth boundaries; documentation and measured results describe their practical guarantees.

**Tech Stack:** .NET publish, IIS/Windows service PowerShell, SQL Server backup/migrations, Playwright/xUnit, repository CI.

**Spec:** [Approved design](../specs/2026-10-17-workerdock-design.md), sections 3, 12–15 and all acceptance items. Depends on [Stage 05](2026-10-17-workerdock-05-operations-center.md).

## Global Constraints

- All [index](2026-10-17-workerdock-implementation.md) constraints apply.
- "Packages include placeholders only for deployment-specific values, never working credentials."
- "Actual throughput depends on job duration, process startup, database latency, and host resources. Publish measured results with the test hardware and workload."
- "Roll back only within the tested schema/application compatibility window; otherwise restore the documented backup."
- This plan prepares a release; external publication/deployment into a real customer environment is not implied by artifact preparation.

## Review Focus

The index's five cases have owning tests in earlier stages; Task 22 runs them together against packaged hosts. This stage also verifies interrupted upgrades, anonymous-mode exposure, long-running shutdown and installer re-entry without overwriting tenant identities.

## File structure

Task 21 owns installation/package configuration and upgrade orchestration. Task 22 owns adversarial/reproducibility evidence and CI. Task 23 consolidates documentation, sample package and license material; it changes runtime code only through separately tested fixes when validation finds a defect.

### Task 21: Windows packaging, least-privilege setup and upgrade/restore

**Files:** Create `deploy/Build-Packages.ps1`, `Install-Tenant.ps1`, `Install-Center.ps1`, `Install-Worker.ps1`, `Upgrade-Instance.ps1`, `Uninstall-Instance.ps1`, `WorkerDock.Deployment.psm1`, config examples, and paired `deploy/permissions.sql` scripts; add foreground/Windows service lifetime support; add `tests/DeploymentSmoke.ps1`; create `docs/deployment.md` and `docs/upgrades.md`.

**Interfaces:**
- Consumes all prior hosts, migrator, health, stop/standby and retirement controls.
- `Install-Tenant.ps1 -InstanceId <guid> -InstallRoot <absolute-path> -ConfigPath <absolute-path> -PackagePath <absolute-path>` installs one IIS app/pool plus scheduler and initial worker; central installation is separate. `Install-Worker.ps1` adds a generic worker installation, never a job definition.
- `Upgrade-Instance.ps1 -InstallRoot <path> -ConfigPath <path> -PackagePath <path> -BackupPath <new-UNC-.bak>` derives tenant/center identity from the persistent config. Operators block admission, inspect executions, decide how to handle infinite-timeout work, and stop the relevant services, IIS site and pool before invoking it. It verifies package and migration checksums, creates and verifies SQL and application backups, applies migrations and stages the new release; it leaves processes stopped for operator readiness checks.
- Package manifests include exact versions, schema window and checksums. No plaintext secrets in package/config examples. Bootstrap identities use Windows-integrated SQL where possible. Upgrade recovery is fail-closed and requires documented manual reconciliation; the scripts do not automatically resume an interrupted upgrade.
- Preflight requires a supported PowerShell 7 `pwsh`, Windows/IIS test host and .NET 10 Hosting Bundle/runtime. These prerequisites are not established by this plan review; unavailable tools are explicit blockers. Runtime grants belong to their feature migrations from Task 01 onward. Deployment permissions scripts only bind supplied principals to those versioned roles, with paired rollback of those assignments; they must not redefine business/Quartz objects outside migration history.

- [ ] **Step 1 — Write deployment scenarios.** In a disposable Windows/IIS host, install tenant A/B and center, rerun installation, add a worker, recycle IIS, upgrade with finite/infinite jobs and interrupt migration/deployment. Confirm separate identities, DB grants, key/archive/log ACLs, service recovery and direct URLs. Live Entra smoke uses a designated test registration only; credentials are external test environment inputs.

```csharp
Assert.Equal(originalInstanceId, reinstalled.InstanceId);
Assert.Equal(1, schedulerServicesForTenantA);
Assert.True(tenantExecutionContinuedDuringIisRecycle);
Assert.Equal("DrainRequiresOperatorDecision", infiniteUpgrade.Code);
Assert.False(runtimePrincipalCanAlterSchema);
Assert.True(restoredDatabaseIncludesBothQuartzStoresAndBusinessState);
```

- [ ] **Step 2 — Verify red.** The planned `DeploymentTests` and pre-implementation deployment smoke were not run before implementation. The final portable smoke covers script parsing, config section shape, and manifest checksum acceptance/rejection; live installer and upgrade red/green evidence remains required on the disposable Windows/IIS/SQL host.
- [x] **Step 3 — Implement.** Build static frontends into respective IIS outputs and service/executor binaries into tenant packages. Scripts validate resolved paths and instance ownership, are repeatable, keep runtime accounts unprivileged and never delete DBs on uninstall. Use native PowerShell file operations, hidden service helpers, explicit TLS/SNI bindings and no visible helper windows. Install scripts require the appropriate administrative context only at install time. Upgrade records fail-closed recovery state, verified SQL/application backups, schema compatibility and restart reconciliation; automatic resume is not supported, so operators follow the manual restore/reconciliation guide. Rollback refuses incompatible states and offers full documented restore. Implement SQL grants as versioned paired scripts covering Quartz DML and business/audit routines.
- [ ] **Step 4 — Verify green.** Run `pwsh -File tests/DeploymentSmoke.ps1`; expected portable syntax, configuration-shape and manifest-integrity checks to pass. Repeat the live deployment smoke and interrupted upgrade/restore exercise on the disposable Windows/IIS and SQL host; expected PASS with complete evidence, no secrets in packages/script logs and no job-triggered Windows service deployment. Record unavailable live infrastructure as an unmet release check, not a simulated success.
- [x] **Step 5 — Commit.** Stage task files; `git commit -m "feat: package Windows deployments and safe upgrades"` (`0714652`).

### Task 22: Full fault/security/accessibility and measured workload validation

**Files:** Create `tests/WorkerDock.IntegrationTests/ReleaseFaultMatrixTests.cs`, `TenantIsolationTests.cs`, `tests/WorkerDock.ProcessTests/ClusterFaultTests.cs`, `tests/load/Invoke-Workload.ps1`, `frontend/e2e/accessibility.spec.ts`, `.github/workflows/ci.yml`, `.github/workflows/windows-integration.yml`; create `docs/validation/release-evidence.md`, `docs/validation/workload-results.md` during execution.

**Interfaces:**
- Consumes packaged hosts and all task-defined interfaces; no new production interface.
- Workload script parameters: `-Tenants 10 -JobsPerTenant 100 -ConcurrencyPerTenant 10 -ConfigPath <test-config> -OutputPath <evidence-directory>`. Inputs point only to disposable test deployments; workloads include bounded SQL/HTTP fixtures and intentional failure cases in separate runs.
- Fault matrix rows identify spec acceptance number, test command, fixture version, expected invariant, observed result and artifact path. Tests with missing required environment fail/report blocked; CI unit-only jobs do not label integration evidence green.

- [ ] **Step 1 — Add missing system tests.** Exercise SQL disconnect/recovery, process/host suspension, stale completion, Quartz acquired-before-app-start crashes, publisher ack loss, template-policy change/rejoin, secret rotation, cross-tenant DB/token access, all SSRF forms, archive failure and stop while capacity saturated. Browser accessibility checks cover keyboard/focus, non-color states, contrast, mobile layout and en/pt-BR validation. Existing meaningful tests are reused, not duplicated just for coverage counts.

```csharp
Assert.Equal(0, staleOwnerSuccessfulCompletions);
Assert.Equal(0, forbiddenCrossTenantAccesses);
Assert.Equal(0, secretSentinelLeaks);
Assert.Equal(0, acceptedRunsSilentlyLost);
Assert.True(maxAuthorizedSqlAttempts <= configuredSqlLimit);
Assert.Equal(0, activeRowsDeletedByRetention);
```

- [x] **Step 2 — Verify the new tests fail for their targeted defect or missing behavior.** Run each affected filtered test class and `npm.cmd --prefix frontend run e2e -- accessibility.spec.ts`; record the actual red result. If existing implementation already satisfies a new regression case, record PASS without deliberately breaking working code; reproduce a mutation/fault to show the assertion detects the unsafe outcome where practical.

  Evidence: the SQL template browser flow reproduced a disabled save because the initially selected template did not receive schema defaults. After initializing defaults, the SQL flow and complete tenant E2E suite passed. This does not cover the still-missing system fault matrix.
- [ ] **Step 3 — Resolve only demonstrated gaps and wire repeatable CI.** Each product fix gets its targeted red/green check and its own commit if independently reviewable. Windows integration runs real SQL/Job Objects/IIS on an appropriately provisioned runner; build/unit/frontend jobs need no production secrets. Run the measured target with CPU/memory/SQL specs, job duration/mix, concurrency, warmup/measurement interval, throughput, p50/p95/p99 duration/trigger/queue delays, error/retry rates and clock skew recorded. Do not invent a throughput SLA; invariant violations fail the release, measured performance is reported honestly.
- [ ] **Step 4 — Verify green.** `dotnet test WorkerDock.slnx -c Release`, `npm.cmd --prefix frontend run test -- --run`, `npm.cmd --prefix frontend run e2e`, `npm.cmd --prefix frontend run build`, deployment smoke, then `pwsh -File tests/load/Invoke-Workload.ps1 -Tenants 10 -JobsPerTenant 100 -ConcurrencyPerTenant 10 -ConfigPath <test-config> -OutputPath <evidence-directory>`. Expected all required invariant/accessibility tests PASS and a populated workload report; unmet infrastructure is a release blocker. Re-run only suites affected by fixes plus required final checks.
- [ ] **Step 5 — Commit.** Stage tests/workflows/evidence after scrubbing host/customer/secret data; `git commit -m "test: verify WorkerDock release invariants and workload"`.

### Task 23: OSS documentation, SDK example and release handoff

**Files:** Create `README.md`, `LICENSE`, `NOTICE`, `CONTRIBUTING.md`, `SECURITY.md`, `CHANGELOG.md`, `docs/architecture.md`, `docs/troubleshooting.md`, `docs/release-checklist.md`, `examples/WorkerDock.SampleTemplate/WorkerDock.SampleTemplate.csproj`, `SampleTemplate.cs`, `tests/WorkerDock.UnitTests/SampleTemplateConformanceTests.cs`; consolidate all task-owned guides and update evidence links.

**Interfaces:**
- Consumes Task 07 public SDK and Task 10 metadata/validation; sample handler is a deterministic harmless transformation using safe logs/cancellation, not arbitrary code execution from the UI.
- Release handoff includes package checksums/schema window, measured evidence and known limitations. Actual release tagging/publication remains a separate user action/authorization.

- [ ] **Step 1 — Verify documented workflows and add the SDK conformance test.** Walk through fresh development setup, IIS/service setup, SQL migrations/rollback, Entra optional-role/anonymous configuration, vault replacement, SQL/HTTP creation, custom package installation, retries/recovery/stop, alerts and archival. `SampleTemplateConformanceTests.Metadata_and_cancellation_follow_public_contract` validates exact version/schema and cancellation without resolving real secrets.

```csharp
Assert.Empty(validationIssues);
Assert.Equal("1.0.0", descriptor.Version);
Assert.True(cancellationObservedBeforeSideEffects);
Assert.DoesNotContain(secretSentinel, capturedSafeLogs);
```

- [ ] **Step 2 — Check the failing deliverable.** Run `dotnet test tests/WorkerDock.UnitTests --filter FullyQualifiedName~SampleTemplateConformanceTests`; expected missing sample behavior before implementation. Record each documentation walkthrough's first reproducibility blocker rather than writing tests that assert prose/file existence.
- [x] **Step 3 — Implement example and finish guides.** Added MIT license, OSS/operator guides, trusted .NET sample template, and generated frontend/.NET package-license inventories. Tenant and center package builds include and checksum the notices and license files. The docs continue to label live deployment evidence pending.
- [ ] **Step 4 — Verify green.** Conformance tests (2), sample build, package build/content checks and deployment smoke passed; the Task 23 diff received an independent review. A clean SQL Server/IIS/Entra setup walkthrough and whole-product release verification remain pending on the required disposable infrastructure, so this step remains open.
- [x] **Step 5 — Checkpoint.** Reviewed Task 23 deliverables are checkpointed as `docs: add WorkerDock OSS guides and sample template`. Step 4 and release exit remain open pending SQL Server/IIS/Entra and system validation.

## Release exit

- [ ] Report concrete changes, verification results, measured limits and any remaining blockers. Do not call the full product complete if required live/deployment/fault evidence is missing.
- [ ] Use the selected execution workflow's review/verification/branch-finishing gates. Do not merge, publish or remove the user's managed worktree merely because this plan reached its last checkbox.
