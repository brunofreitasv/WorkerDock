# Secret facade and trusted metadata

`ISecretVault` is asynchronous and resolve-only. Logical references select only mappings approved by protected deployment configuration. Provider selection, tenant identity, bootstrap authentication and environment-variable names are never selected by job JSON or browser input. Web APIs and worker supervisors do not resolve job secrets. The contained execution child constructs its adapters only after the parent grants execution permission.

## Environment development adapter

Set `WORKERDOCK_DEVELOPMENT_VAULT=1` in the child service's protected development environment and configure the nonsecret mapping, for example:

```powershell
$env:WORKERDOCK_DEVELOPMENT_SECRET_MAP='{"database.password":"ACME_DEV_DATABASE_PASSWORD"}'
```

Provision `ACME_DEV_DATABASE_PASSWORD` separately through a protected local secret source, rather than command arguments or checked-in files. There is no prefix-based lookup: the logical name `database.password` can resolve only the explicitly mapped variable. Mappings are copied at construction, limited to 128 entries, and names are 1–128 ASCII letters/digits, underscore, dash or dot. Values are read anew on every resolve, so rotation between attempts does not change the immutable job/profile reference. Missing, empty, oversized, unapproved or failing lookups produce only `SecretUnavailable`; provider messages, variable names and reference internals are not attached to the exception or result. Cancellation returns a fresh cancellation exception without provider text.

`SecretVaultRegistration.EnvironmentMetadata` is installed nonsecret provider metadata. Its options schema accepts an empty object; environment mappings and authentication remain deployment-only. `TemplateSchemaValidator.ValidateProviderOptions` and `ValidateNonsecretSettings` validate generic provider/profile descriptors without introducing a provider-settings or connection-profile store. Sensitive settings require the `x-workerDock-secretReference: true` annotation; literal sensitive fields cannot enter nonsecret settings. Task 11 owns concrete SQL profile revisions; Task 12A owns audited tenant integration settings and hard deployment constraints.

## Internal vault adapter

Replace only the child registration factory with an installed adapter implementing `ISecretVault.ResolveAsync`. Bind the internal library client to that tenant's own service identity and approved reference namespace. Use that library's runtime credential rotation; do not cache resolved credentials in job revisions, metadata, SQL, Quartz data, web DTOs or supervisor arguments. An installed `SecretProviderDescriptor` supplies the generic nonsecret options schema and built-in UI hints; it is deployment-authored metadata, not a provider implementation uploaded through the dashboard. Tenant vault settings store only the installed provider ID and schema-validated public options. The environment provider accepts an empty options object; mappings and bootstrap authentication remain protected deployment configuration. Vault settings updates retain audited, immutable configuration revisions and never expose resolved values. A scheduler's alert identity and a central application's health identity have separate reference allowlists.

Return a fresh `SecretValue` per successful resolution and honor cancellation. The child facade maps arbitrary provider exceptions to `SecretUnavailableException` without an inner exception. Its attempt scope tracks and disposes every returned value, including handler omissions. Handlers should still use `using var secret` promptly. `ToString()` is always `[REDACTED]`; disposal clears the backing character array and `Reveal()` then fails. Environment strings and client-library internals may have their own managed lifetimes; clearing `SecretValue` is not a promise to erase the whole process's memory.

## Safe diagnostics

`SafeExecutionLog` accepts only `TemplateStarted`, `TemplateCompleted`, `TemplateFailed` and `SecretUnavailable`. Its maximum eight fields use fixed names: `executionId`, `attemptId`, `correlationId` (GUIDs), `durationMs`, `affectedRows` (nonnegative integers), `outcome` (the three contract outcomes) and `safeCode` (the installed fixed code vocabulary). Values are bounded to 80 characters. Unknown events, fields or codes reject the complete event before emission. Extend the vocabulary only through reviewed code changes. There is no generic message/exception/body/header/SQL field.

Known resolved values are additionally redacted from structured handler results and wrapped diagnostic fields. This is secondary protection: reviewed handlers must return safe summaries and must not deliberately encode credentials in nominally safe fields. Raw child stdout/stderr is drained and discarded; raw provider/driver exceptions are never returned. Durable diagnostics storage remains the observability task; the development adapter emits optional structured traces only.

## Verification

From the repository root on Windows:

```powershell
dotnet test tests/WorkerDock.UnitTests -c Release --filter FullyQualifiedName~SecretBoundaryTests
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~TemplateCatalogTests
dotnet test tests/WorkerDock.ProcessTests -c Release --filter FullyQualifiedName~Provider_failure_and_rotated_attempts
```

The synthetic rotation fixtures inspect safe exception projections, child outcomes, diagnostic files and child command arguments. The complete process suite also checks discarded stdout/stderr and JSON-escaped redaction. No real credentials are test fixtures. See `database.md` for separate live SQL catalog, migration and restricted-grant proofs; memory and Windows process success do not establish those properties.
