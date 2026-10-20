# WorkerDock Templates Implementation Plan

> **Historical architecture note:** This plan is archived planning history, not an operational procedure. It predates the approved [database-free Control Center design](../specs/2026-10-19-database-free-control-center-design.md). Any central SQL database, schema, or migration statements are superseded; tenant SQL and Quartz requirements remain. Do not apply `database/central` for current setup or import.

All WorkerDock-owned tables, triggers, keys, indexes and explicit SQL references belong to `[workerdock]` in each database. Each independent tenant database and the separate central registry database owns its own schema; this is not a shared multitenant database or browser-supplied tenant selector. Quartz uses `[workerdock].WDS_QRTZ_` and `[workerdock].WDE_QRTZ_`. System `sys.*` objects and database principals retain their SQL Server names. New objects require paired migrations, checksums and explicit least-privilege grants; runtime startup never creates schema.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver trusted extensible templates, secret resolution, safe SQL/HTTP execution and usable configuration forms.

**Architecture:** Installed handlers implement the shared contract; metadata drives forms but cannot execute UI code. Secrets resolve only in the execution child, and outbound access passes deployment-bound destination policy.

**Tech Stack:** Stage 02 runtime, Microsoft.Data.SqlClient, HttpClient/SocketsHttpHandler, JSON Schema 2020-12 subset, React/MUI.

**Spec:** [Approved design](../specs/2026-10-17-workerdock-design.md), sections 8–10. Depends on [Stage 02](2026-10-17-workerdock-02-execution.md).

## Global Constraints

- All [index](2026-10-17-workerdock-implementation.md) constraints apply.
- "Only nonsecret provider options, identifiers and secret references enter SQL Server."
- "Custom trusted handlers must honor this contract; third-party code is reviewed and tested before deployment, not treated as untrusted code that the platform can prove harmless."
- "HTTP retries are governed by the job policy, not an additional hidden client retry loop."

## Review Focus

Project focus 3 (secret rotation/failure) is exercised by Task 10 and both real handlers. URL normalization, DNS rebinding, locale-dependent SQL parameter values and hostile metadata are additional cases owned here.

## File structure

Contracts remain those from Task 07. Infrastructure owns vault/egress/catalog adapters; each built-in owns its configuration and handler. The frontend schema renderer has no template-specific execution logic and consumes the same bounded schema fixtures as the API.

### Task 10: Vault adapter, safe logging and immutable template metadata

**Files:** Create `src/WorkerDock.Infrastructure/Secrets/EnvironmentSecretVault.cs`, `SecretVaultRegistration.cs`, `Logging/SafeExecutionLog.cs`, `Templates/TemplateCatalog.cs`, `TemplateSchemaValidator.cs`; `src/WorkerDock.Application/Templates/ITemplateCatalog.cs`; modify Executor/Worker registration; `database/tenant/0004_templates.up.sql`, `0004_templates.down.sql`, manifest; `tests/WorkerDock.UnitTests/SecretBoundaryTests.cs`, `tests/WorkerDock.IntegrationTests/TemplateCatalogTests.cs`, `tests/fixtures/template-schemas.json`; `docs/secrets.md`, update custom-template guide.

**Interfaces:**
- Consumes Task 07 `ISecretVault`, `SecretValue`, `ISafeExecutionLog`, descriptor/handler contracts and Task 09 catalog digest admission.
- `ITemplateCatalog.RegisterAsync(IReadOnlyList<TemplateDescriptor> descriptors, CancellationToken ct) -> ValueTask<string>` returns aggregate digest; `ListAsync(CancellationToken ct) -> ValueTask<IReadOnlyList<TemplateDescriptor>>`; `GetAsync(string id, string version, CancellationToken ct) -> ValueTask<TemplateDescriptor?>`.
- `TemplateSchemaValidator.Validate(TemplateDescriptor descriptor, JsonElement configuration) -> IReadOnlyList<ValidationIssue>`; explicit supported subset is spec section 9.1. Add annotations `x-workerDock-secretReference` and `x-workerDock-sensitive` with boolean values, interpreted only by built-in server/UI controls.
- Environment adapter maps approved logical names to explicitly configured environment variable names. Missing/unapproved references produce `SecretUnavailable` without value, provider exception or reference internals in viewer output.

- [ ] **Step 1 — Write tests.** `SecretBoundaryTests.Rotation_and_provider_failure_preserve_redaction` rotates a synthetic value between attempts; a thrown exception embeds both values deliberately. `TemplateCatalogTests.Digest_cannot_change_under_same_id_and_version` rejects mutation; validate supported schema fixtures and reject remote `$ref`, scripts/HTML UI hints, unsupported keywords and unexpected properties.

```csharp
Assert.Equal("[REDACTED]", secret.ToString());
Assert.DoesNotContain(oldSentinel, emittedDiagnostics);
Assert.DoesNotContain(newSentinel, emittedDiagnostics);
Assert.Equal("SecretUnavailable", failure.SafeCode);
Assert.Equal("MetadataVersionConflict", changedDescriptor.Code);
Assert.Contains(issues, issue => issue.Code == "UnsupportedSchemaKeyword");
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.UnitTests --filter FullyQualifiedName~SecretBoundaryTests` and `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~TemplateCatalogTests`; expect vault/schema/catalog failures.
- [ ] **Step 3 — Implement.** Use resolve-only facade, scoped value lifetime, safe error-code projection and allowlisted bounded structured logs; known-secret redaction is secondary protection. No resolve-secret endpoint or arbitrary stdout capture. Validate schema before registration, pin immutable descriptor digests, register only trusted installed handlers and expose catalog safely to forms. Require common pool catalog digest before activation. Add nonsecret profiles/provider settings schema and retired-revision protection. Document an internal-vault adapter replacing only registration/resolve, with per-tenant identity and runtime rotation.
- [ ] **Step 4 — Verify green.** Repeat tests; run sentinel inspection over application/Quartz records, API DTOs, logs and child arguments using synthetic fixtures. Expected PASS with no secret values and unchanged template/scheduler contracts.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add secret facade and validated template catalog"`.

### Task 11: SQL Server profile, builder, handler and diagnostics

**Files:** Create SQL template project; `src/WorkerDock.Templates.SqlServer/SqlJobConfiguration.cs`, `SqlTemplate.cs`, `SqlConnectionBuilder.cs`, `SqlProfileDiagnostic.cs`; `src/WorkerDock.Contracts/Connections/ConnectionProfileRevision.cs`, `src/WorkerDock.Application/Connections/IConnectionProfiles.cs`, `src/WorkerDock.Infrastructure/Connections/SqlConnectionProfiles.cs`, `src/WorkerDock.Web/Endpoints/ConnectionProfilesEndpoints.cs`; `tests/WorkerDock.IntegrationTests/SqlTemplateTests.cs`, `SqlProfileTests.cs`, `tests/sql/target-fixture.up.sql`, `target-fixture.down.sql`; `docs/sql-template.md`.

**Interfaces:**
- Consumes Task 07 template/vault contracts, Task 10 catalog/schema and Task 05 revision pinning.
- `ConnectionProfileRevision(Guid ProfileRevisionId, Guid ProfileId, long Revision, string Server, IReadOnlyList<string> AllowedCatalogs, string Authentication, string? UserName, SecretReference? PasswordReference, bool Encrypt, bool TrustServerCertificate)`; constrain authentication to Integrated/VaultPassword. No raw connection-string input.
- `ProfileEditorDto(ConnectionProfileRevision Current, byte[] Version, bool Retired)`; `IConnectionProfiles.SaveAsync(ConnectionProfileRevision profile, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`, `ListAsync(CancellationToken ct) -> ValueTask<IReadOnlyList<ProfileEditorDto>>`, `GetCurrentAsync(Guid profileId, CancellationToken ct) -> ValueTask<ProfileEditorDto>`, `GetRevisionAsync(Guid profileRevisionId, CancellationToken ct) -> ValueTask<ConnectionProfileRevision>` and `RetireAsync(Guid profileId, byte[] expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`. Profile count/results are bounded; old referenced revisions remain readable internally after retirement. All exposed profile reads require Manage.
- `SqlConnectionBuilder.BuildAsync(ConnectionProfileRevision profile, string catalog, ISecretVault vault, CancellationToken ct) -> ValueTask<SqlConnection>` creates a closed SqlConnection using SqlConnectionStringBuilder; never returns a connection string DTO.
- `SqlJobConfiguration(Guid ProfileId, string Catalog, string CommandKind, string CommandText, IReadOnlyList<SqlParameterConfiguration> Parameters, int? CommandTimeoutSeconds, bool UseTransaction)`; `SqlParameterConfiguration(string Name, SqlDbType DbType, JsonElement? Value, SecretReference? Secret, bool UseExecutionKey, bool IsNull, int? Size, byte? Precision, byte? Scale)` permits exactly one value source. The SQL handler consumes the supervisor-provided nonsecret `context.Execution.ProfileConfiguration`, verifies its revision identity against `context.Execution.ProfileRevisionId`, and resolves credentials through the facade. It does not query WorkerDock persistence. Authoring selects ProfileId and execution creation pins its revision. Null command timeout inherits outer policy; explicit 0 is unlimited only when outer timeout is infinite.
- Management profile diagnostics create a bounded durable execution with an internal connectivity-only handler; no command text is accepted. It uses the `workerdock.sql-server` execution group and does not appear as an authorable job template. Profile lists/details/diagnostics require Manage.

- [ ] **Step 1 — Write real target tests.** `SqlTemplateTests.Typed_parameters_do_not_interpolate_sql` includes decimal under pt-BR, null, Unicode and injection text; `Commit_ack_loss_is_unknown_not_safe_retry` simulates lost acknowledgement. Test procedure and query modes, profile catalog rejection, revision pinning/rotation, transaction rollback and finite/infinite command timeout. Diagnostic returns safe auth/catalog code only.

```csharp
Assert.Equal(12.34m, persistedDecimal);
Assert.Equal(injectionLiteral, persistedText);
Assert.Equal("CatalogNotAllowed", deniedCatalog.SafeCode);
Assert.True(lostCommitAck.EffectsMayHaveOccurred);
Assert.DoesNotContain(secretSentinel, safeDiagnosticJson);
Assert.Equal(originalProfileRevision, execution.ProfileRevisionId);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~SqlTemplateTests|FullyQualifiedName~SqlProfileTests"`; expect handler/profile failures.
- [ ] **Step 3 — Implement.** Built-in ID `workerdock.sql-server`, version `1.0.0`, uses typed SqlParameters and validated procedure identifiers. Build TLS/auth settings explicitly, enforce catalog allowlist and immutable profile resolution. The runtime deadline constrains command timeout and connection setup stays bounded. Optional local transaction, row/affected-count-only safe result, no query/parameter/result row diagnostics. Idempotency key is an explicit target parameter when configured. Diagnostic uses the same runtime/capacity path, bounded 30-second deadline, and fixed connectivity/catalog checks. Test fixture SQL includes forward/rollback scripts; production never auto-installs target objects.
- [ ] **Step 4 — Verify green.** Repeat tests with least-privilege target credentials and sentinel checks; expected PASS and no exposed execute-SQL endpoint. Exercise cancellation without claiming uncertain commit rollback.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add parameterized SQL Server template and profile diagnostics"`.

### Task 12: Destination-safe HTTP handler

**Files:** Create HTTP template project; `src/WorkerDock.Templates.Http/HttpJobConfiguration.cs`, `HttpTemplate.cs`, `ParameterSubstitution.cs`; `src/WorkerDock.Application/Outbound/IOutboundPolicy.cs`, `src/WorkerDock.Infrastructure/Outbound/OutboundPolicy.cs`, `ValidatedConnectionFactory.cs`; `tests/WorkerDock.IntegrationTests/HttpTemplateTests.cs`, `OutboundPolicyTests.cs`; `docs/http-template.md`, `docs/outbound-policy.md`.

**Interfaces:**
- Consumes Task 07 handler/vault/context and Task 08 retry classification.
- `OutboundTarget(Uri Uri, string PolicyName)`; `ApprovedDestination(string Host, int Port, IReadOnlyList<IPAddress> Addresses)`; `IOutboundPolicy.ResolveAsync(OutboundTarget target, CancellationToken ct) -> ValueTask<ApprovedDestination>`.
- `ValidatedConnectionFactory.CreateHandler(string policyName) -> SocketsHttpHandler` binds approved addresses through ConnectCallback, validates actual endpoint and preserves original hostname/SNI. Separate policies for tenant jobs, alert delivery and central health.
- `HttpJobConfiguration(string Method, string Url, IReadOnlyDictionary<string,string> Query, IReadOnlyDictionary<string,string> Headers, IReadOnlyDictionary<string,SecretReference> SecretHeaders, IReadOnlyDictionary<string,JsonElement> Parameters, string? ContentType, string? Body, int? TimeoutSeconds, IReadOnlyList<int> SuccessStatuses, string? IdempotencyHeader, int MaxResponseBytes, string ResponseMode)`; substitution sources are these named bounded configuration values and `context.Execution.IdempotencyKey` only. ResponseMode is Discard/SafeSummary: SafeSummary retains status/duration/byte count, never response-body text. Null timeout inherits the outer attempt; 0 is unlimited only with no finite outer deadline; negative is invalid. Empty SuccessStatuses means 200–299. Invalid/conflicting sensitive and ordinary headers are rejected.
- HTTP template receives a configured HttpClient through its constructor; Executor composition uses ValidatedConnectionFactory. The template project does not reference Infrastructure/Application. A parsed Retry-After delta/date is returned as `TemplateResult.RetryAfter`, never implemented by sleeping or retrying in the handler; Task 08 applies the cap and safety policy.

- [ ] **Step 1 — Write controlled-server/DNS tests.** `OutboundPolicyTests.Dns_rebinding_cannot_change_connected_endpoint` swaps DNS answers after resolution; mixed allowed/denied answers fail closed. Cover IPv4/IPv6 and mapped literals, normalized hosts/trailing dots, private allowlists, metadata/link-local/loopback, redirects, userinfo, ambient proxy and TLS mismatch. `HttpTemplateTests.Timeout_or_5xx_may_follow_remote_effect` ensures no hidden retry; include CR/LF/header injection, body/query encoding, bounded Retry-After and oversized body/response.

```csharp
Assert.Equal(0, forbiddenDestinationConnections);
Assert.Equal(1, requestsWithoutApplicationRetryAuthorization);
Assert.Equal(stableExecutionKey, capturedIdempotencyHeader);
Assert.Equal("ResponseTooLarge", oversizedResponse.SafeCode);
Assert.True(lostResponse.EffectsMayHaveOccurred);
Assert.DoesNotContain(tokenSentinel, diagnosticJson);
Assert.Equal("SafeSummary", defaultResponseMode);
Assert.DoesNotContain(responseBodySentinel, persistedSummary);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~HttpTemplateTests|FullyQualifiedName~OutboundPolicyTests"`; expect policy/substitution failures. Local test-server access uses an explicitly injected test policy, never relaxes the production loopback deny rule.
- [ ] **Step 3 — Implement.** Built-in ID `workerdock.http`, version `1.0.0`; default expected status 2xx. Use bounded request/response handling, contextual substitutions and secret-header references. Disable redirects and default proxy, attach credentials only after destination approval, forbid Host/TLS overrides, validate every new connection. Classify transient/permanent/uncertain results for Task 08; never retry inside HttpClient. Emit safe summaries only; infinite handler timeout does not remove response-size or connection-setup bounds.
- [ ] **Step 4 — Verify green.** Repeat tests with rebinding/redirect fixture and secret rotation. Expected PASS with actual peer address evidence, no forbidden connections, no duplicate request from a hidden retry handler.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add SSRF-protected HTTP worker template"`.

### Task 12A: Protected tenant vault and outbound settings

**Files:** Create `src/WorkerDock.Application/Settings/IIntegrationSettings.cs`, `IntegrationSettingsDtos.cs`, `src/WorkerDock.Infrastructure/Settings/SqlIntegrationSettings.cs`, `src/WorkerDock.Web/Endpoints/IntegrationSettingsEndpoints.cs`; `frontend/apps/tenant/src/features/settings/VaultSettingsPage.tsx`, `OutboundPolicyPage.tsx`; `tests/WorkerDock.IntegrationTests/IntegrationSettingsTests.cs`, `frontend/e2e/integration-settings.spec.ts`. Extend Task 10's nonsecret settings through a new `database/tenant/0004b_integration_settings.up.sql`/`.down.sql` pair and manifest entry. `0004a_profiles` precedes it; preserve manifest ordering and rollback pairing. Update secrets/outbound guides.

**Interfaces:**
- Consumes Task 10 vault provider registration, Task 12 outbound policy and Tasks 01/02 audit/access controls. Execute this task after 12 and before 13; `0004b` follows `0004_templates` and `0004a_profiles`.
- `VaultSettingsDto(string ProviderId, JsonElement PublicOptions, byte[] Version)`; ProviderId must equal an installed deployment-approved provider. PublicOptions are schema-validated nonsecret options/references only, never bootstrap identity, credentials, plugin paths or a tenant selector.
- `EgressPolicyDto(string Name, IReadOnlyList<string> Hosts, IReadOnlyList<int> Ports, IReadOnlyList<string> PrivateCidrs, bool AllowHttp, byte[] Version)` for fixed tenant-job and tenant-alert policy names. Deployment hard denials cannot be changed through this DTO.
- `IIntegrationSettings.GetVaultAsync(CancellationToken ct) -> ValueTask<VaultSettingsDto>`, `SaveVaultAsync(VaultSettingsDto settings, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`, `GetEgressAsync(string name, CancellationToken ct) -> ValueTask<EgressPolicyDto>`, `SaveEgressAsync(EgressPolicyDto policy, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`. Save requires matching rowversion; API GET/PUT `/api/settings/vault` and `/api/settings/egress/{name}` all require Manage.
- Worker/alert composition loads the latest validated nonsecret settings before starting a new attempt/delivery. Populate Task 07's versioned `IntegrationConfiguration` JSON field on the server-authored ExecutionDescriptor for child bootstrap; this is not job-editable configuration and contains no resolved values. In-flight attempts retain their validated snapshot; the UI does not promise immediate revocation of an already issued remote request. Secret values still resolve at use time.

- [ ] **Step 1 — Write tests.** `IntegrationSettingsTests.Manager_cannot_override_deployment_identity_or_denials` rejects other provider/tenant/identity and forbidden private/metadata destinations. Viewer GET/PUT is forbidden, stale ETag returns 412, and failed validation/audit leaves settings unchanged. A newly started attempt gets the new policy revision while the previous attempt retains its snapshot; no secret value reaches child descriptor/audit/API.

```csharp
Assert.Equal(403, viewerRead.StatusCode);
Assert.Equal("DeploymentPolicyViolation", weakenedPolicy.Code);
Assert.Equal(412, staleWrite.StatusCode);
Assert.Equal(previousRevision, alreadyRunning.SettingsRevision);
Assert.Equal(updatedRevision, nextAttempt.SettingsRevision);
Assert.DoesNotContain(secretSentinel, allSettingsOutputs);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~IntegrationSettingsTests` and `npm.cmd --prefix frontend run e2e -- integration-settings.spec.ts`; expect missing protected-settings behavior. Create the Playwright config/root e2e script here, then Task 13 extends it.
- [ ] **Step 3 — Implement.** Persist nonsecret revisions with audit and immutable deployment constraints; render reference/provider metadata and host/port/CIDR controls with en/pt-BR validation. Do not offer arbitrary vault assembly installation or secret-value entry/reveal. Wire worker/alert child bootstrap and new connections to the selected snapshot; document environment adapter rotation limitations and production-vault replacement.
- [ ] **Step 4 — Verify green.** Repeat tests plus OutboundPolicyTests/SecretBoundaryTests and frontend build. Expected PASS for anonymous/no-role/role-configured management modes, no identity escape or secret disclosure.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add protected tenant integration settings"`.

### Task 13: Metadata forms, profile settings and manual execution UI

**Files:** Create `frontend/packages/ui/src/forms/TemplateForm.tsx`, `SecretReferenceField.tsx`, `SchemaValidation.ts`, `TemplateForm.test.tsx`; `frontend/apps/tenant/src/features/templates/TemplatePicker.tsx`, `features/profiles/ProfileEditor.tsx`, `features/jobs/ManualJobEditor.tsx`; `src/WorkerDock.Web/Endpoints/TemplatesEndpoints.cs`; `frontend/e2e/templates.spec.ts`; extend Task 12A's `frontend/playwright.config.ts`; modify API client/locales/App routes; `docs/manual-jobs.md`.

**Interfaces:**
- Consumes Tasks 05/10–12 DTOs/endpoints and Task 02 session capabilities.
- Extend Task 05's IJobCommands here with `EnableManualAsync(Guid jobId, byte[] expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>` for a valid schedule-free draft only. Define `POST /api/jobs/{id}/enable-manual` here, then route both manual and scheduled activation through Task 14's shared lifecycle implementation. A draft is never auto-enabled by SaveAsync; template validation must pass before activation/run-now. Extend JobCommandTests to prove invalid drafts and stale versions cannot activate.
- `TemplateForm(props: { descriptor: TemplateDescriptorDto; value: Record<string, unknown>; onChange(value: Record<string, unknown>): void; errors: Record<string, string> }): ReactElement`; DTO is a projection of Task 07 descriptor with schema/UI hints, never runtime types.
- Server `GET /api/templates`, `GET /api/templates/{id}/{version}` require Read; `POST /api/template-validation` requires Manage and returns path/code issues without echoing values. Profile endpoints and diagnostic execution follow Task 11; no raw passwords accepted.

- [ ] **Step 1 — Write cross-validator/browser tests.** Consume `tests/fixtures/template-schemas.json` on client and server. `templates.spec.ts` creates a SQL and HTTP manual job using keyboard navigation and secret references, tests validation, runs now and observes execution IDs; viewer cannot open profile/config reads. Hostile schema/HTML stays text or is rejected.

```typescript
expect(clientIssuePaths).toEqual(serverIssuePaths);
await expect(page.getByLabel('Secret reference')).toBeVisible();
await expect(page.getByLabel('Password value')).toHaveCount(0);
await expect(page.getByRole('status')).toContainText('Execution accepted');
expect(protectedProfileResponse.status()).toBe(403);
```

- [ ] **Step 2 — Verify red.** `npm.cmd --prefix frontend run test -- --run TemplateForm` and `npm.cmd --prefix frontend run e2e -- templates.spec.ts`; expect missing form/flow assertions.
- [ ] **Step 3 — Implement.** Render only built-in schema controls/discriminated alternatives; localize help/defaults/errors. Keep SQL command/body editors management-only, show reference labels without values, safe diagnostic result and retirement state. Implement guarded manual activation, then let the editor save a disabled draft, explicitly activate it and run through job/command APIs with CSRF/idempotency key. Respond to 412 conflicts by preserving unsaved input. No job creation triggers deployment/restart. Document the walkthrough and limitations of schema-generated forms.
- [ ] **Step 4 — Verify green.** Repeat frontend tests, server schema fixtures and production build. Expected PASS for both built-ins, keyboard interaction, English/pt-BR and direct API role enforcement.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add metadata-driven template and profile forms"`.

## Stage exit

- [ ] Run handler/schema/security/browser checks; demonstrate SQL and HTTP manual execution through the same worker service with per-template limit 1 and N.
- [ ] Verify examples use synthetic values and include SQL fixture rollbacks. Remaining limitation: scheduling and complete monitoring arrive in Stage 04.
