# WorkerDock Release Implementation Plan

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

**Files:** Create `deploy/Build-Packages.ps1`, `Install-Tenant.ps1`, `Install-Center.ps1`, `Install-Worker.ps1`, `Upgrade-Instance.ps1`, `Uninstall-Instance.ps1`, `config/tenant.example.json`, `center.example.json`, `worker.example.json`, `deploy/permissions.sql`, `deploy/permissions.rollback.sql`; modify service hosts for foreground/Windows service lifetime and Web/Center publish targets; `tests/WorkerDock.ProcessTests/DeploymentTests.cs`, `tests/deployment/Invoke-DeploymentSmoke.ps1`; `docs/deployment.md`, `docs/upgrades.md`, update database/authentication guides.

**Interfaces:**
- Consumes all prior hosts, migrator, health, stop/standby and retirement controls.
- `Install-Tenant.ps1 -InstanceId <guid> -InstallRoot <absolute-path> -ConfigPath <absolute-path> -PackagePath <absolute-path>` installs one IIS app/pool plus scheduler and initial worker; central installation is separate. `Install-Worker.ps1` adds a generic worker installation, never a job definition.
- `Upgrade-Instance.ps1 -ConfigPath <path> -PackagePath <path> -BackupPath <path> -DrainSeconds <positive-int>` blocks admission, drains or reports explicit infinite-job stop requirement, stops, backs up, migrates, deploys, starts and checks readiness. It never silently force-stops jobs or invents a lossless downgrade.
- Package manifests include exact versions, schema window and checksums. No plaintext secrets in package/config examples. Bootstrap identities use Windows-integrated SQL where possible.
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

- [ ] **Step 2 — Verify red.** Run `dotnet test tests/WorkerDock.ProcessTests --filter FullyQualifiedName~DeploymentTests` and `pwsh -File tests/deployment/Invoke-DeploymentSmoke.ps1 -ConfigPath <disposable-test-config>`; expect missing installer/upgrade behavior, not a false PASS on a non-IIS host.
- [ ] **Step 3 — Implement.** Build static frontends into respective IIS outputs and service/executor binaries into tenant packages. Scripts validate resolved paths and instance ownership, are repeatable, keep runtime accounts unprivileged and never delete DBs on uninstall. Use native PowerShell file operations, hidden service helpers, explicit TLS/bindings and no visible helper windows. Install scripts require the appropriate administrative context only at install time. Upgrade records resumable steps, backup/checksum, schema compatibility and restart reconciliation; rollback refuses incompatible states and offers full documented restore. Implement SQL grants as versioned paired scripts covering Quartz DML and business/audit routines.
- [ ] **Step 4 — Verify green.** Repeat deployment smoke and interrupted upgrade/restore exercise; expected PASS with complete evidence, no secrets in packages/script logs and no job-triggered Windows service deployment. Record unavailable live Entra credentials as an unmet release check, not a simulated success.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: package Windows deployments and safe upgrades"`.

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

- [ ] **Step 2 — Verify the new tests fail for their targeted defect or missing behavior.** Run each affected filtered test class and `npm.cmd --prefix frontend run e2e -- accessibility.spec.ts`; record the actual red result. If existing implementation already satisfies a new regression case, record PASS without deliberately breaking working code; reproduce a mutation/fault to show the assertion detects the unsafe outcome where practical.
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
- [ ] **Step 3 — Implement example and finish guides.** Use MIT with WorkerDock contributors and retain dependency license/notice attribution. Document all project responsibilities, setup/permissions/schema versions, authenticated viewers versus managers, no required role behavior, anonymous exposure, replaceable vault, trusted package deployment, five-field AND/DST semantics, at-least-once/uncertain effects, no timeout/stop limits, Quartz ownership/outbox recovery, observability/alerts/retention and troubleshooting codes. Include clock sync, stale health, common catalog deployment, finite shutdown, restore window, no central provisioning and no strict global priority guarantee. Link actual validation results and keep examples placeholder-only.
- [ ] **Step 4 — Verify green.** Run conformance test, build sample and execute the documented clean setup in disposable environment. Check internal links and package/license inventory, run required final verification after any fixes and obtain the whole-branch review required by the chosen execution skill. Expected reproducible walkthroughs and no unresolved required acceptance failures.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "docs: complete WorkerDock OSS and operational release guides"`.

## Release exit

- [ ] Report concrete changes, verification results, measured limits and any remaining blockers. Do not call the full product complete if required live/deployment/fault evidence is missing.
- [ ] Use the selected execution workflow's review/verification/branch-finishing gates. Do not merge, publish or remove the user's managed worktree merely because this plan reached its last checkbox.
