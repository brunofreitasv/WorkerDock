# Trusted custom templates

WorkerDock executes reviewed, explicitly installed .NET packages. This is operational containment for trusted code, not a security sandbox for hostile assemblies. Windows host administrators and packages installed under the worker service identity remain trusted. Dashboard users cannot upload code, select assembly paths, install packages, or supply executable bootstrap arguments.

## Contract and registration

A template references `WorkerDock.Contracts` (`net10.0`). Implement `IWorkerTemplate`, publish one stable identifier and explicit version, and provide a `TemplateDescriptor` with JSON Schema, declarative UI hints and a metadata digest computed by `TemplateDescriptorMetadata.WithDigest`. Descriptor identity is immutable: changing metadata requires a new version. Exact duplicate registrations fail closed. The runtime checks its deployment catalog's expected digest against the installed exact version before validation or execution.

Register reviewed package instances explicitly in the Executor composition root. Supply matching `TemplateId@TemplateVersion` digest entries to `ExecutionSupervisorOptions` from the worker's trusted capability catalog. Deployment-authored executable paths and bootstrap arguments are separate from job configuration. No assembly scanning, job-authored loading, scripts, executable UI hints, or test packages are registered by the production root. The current roots register the built-in SQL Server and HTTP templates. Unknown exact versions return `TemplateVersionUnavailable`; incompatible digests return `TemplateMetadataMismatch`, with no handler entry.

## Add a template to a deployment

1. Build and review a .NET template package implementing `IWorkerTemplate`; give every metadata change a new descriptor version.
2. Reference and register its descriptor in the trusted lists in `src/WorkerDock.Web/Program.cs` and `src/WorkerDock.Worker/Program.cs`. Register its handler in `src/WorkerDock.Executor/Program.cs`. These roots are explicit by design; adding a job in the dashboard does not load an assembly.
3. Recompute `Worker:CatalogDigest` from the exact installed descriptor set using `TemplateMetadata.AggregateDigest`, and update every worker's protected configuration. Deploy matching Web, Worker, and Executor packages together. A catalog mismatch keeps the worker from acquiring work.
4. Start the worker, confirm its catalog registration and readiness, then select the new template in the job creation flow. New jobs begin disabled and require validation and activation.

The [tenant configuration guide](how-to/configure-tenants.md) explains where the worker digest and service variables live. No dashboard user can upload template code.

`Validate` returns bounded path/code issues without side effects. `ExecuteAsync` receives `context.Execution`, the child-local resolve-only vault and safe logging facade. Use the pinned `ProfileRevisionId` and nonsecret profile snapshot, stable `IdempotencyKey`, and optional `DeadlineUtc`. Secret values resolve at execution time; a profile revision pins reference metadata rather than a credential value. Missing deadline means unlimited handler duration, while startup, IPC setup and local termination remain bounded.

Authoring example (illustrative, not installed or executed):

```csharp
public async ValueTask<TemplateResult> ExecuteAsync(
    TemplateExecutionContext context, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    // Resolve only the declared reference; use and dispose within this scope.
    using var credential = await context.Secrets.ResolveAsync(
        new SecretReference("ExampleCredential"), ct);
    // Send context.Execution.IdempotencyKey to a reviewed idempotent destination.
    // Access credential.Reveal().Span explicitly; never log or return its value.
    await context.Log.WriteAsync("TemplateCompleted",
        new Dictionary<string, string> { ["outcome"] = "Success" }, ct);
    return new TemplateResult("Success", "Success", true,
        JsonSerializer.SerializeToElement(new { completed = true }), null);
}
```

Successful external effects can survive local termination. Honor cooperative cancellation, carry the stable execution key to a destination's idempotency facility, and report uncertainty conservatively. Outcomes are `Success`, `TransientFailure`, or `PermanentFailure`. `RetryAfter` is an untrusted hint; application retry policy bounds it. A local stop or timeout does not promise remote rollback or exactly-once effects.

## Child composition and secrets

The Executor constructs vault and logging adapters only after the worker grants execution. Adapters and resolved secret values never cross IPC or argv. The child has no WorkerDock persistence connection. `IntegrationConfiguration` is a versioned server-authored nonsecret bootstrap snapshot; it must not contain provider credentials or a user-selected tenant/provider identity. Production provider bootstrap authentication belongs to the tenant service identity/protected deployment configuration.

The development environment adapter uses an explicit logical-name-to-environment-variable allowlist. Enable it with `WORKERDOCK_DEVELOPMENT_VAULT=1` and configure the nonsecret `WORKERDOCK_DEVELOPMENT_SECRET_MAP` JSON object. It performs no prefix-based or arbitrary environment lookup. See [secrets.md](secrets.md) for exact registration, rotation, provider replacement and identity rules.

`SecretValue.Reveal()` explicitly exposes read-only scoped memory. Dispose it promptly; disposal clears its backing array and subsequent access fails. `ToString()` always returns `[REDACTED]`. Child wrappers redact known resolved values from result JSON and safe log event/field names and values as defense in depth. This cannot make hostile code safe. Handlers must return only deliberate safe projections: never raw exceptions, request/response bodies, SQL text, credentials, headers, connection strings, or stdout/stderr. The installed `SafeExecutionLog` accepts a fixed event/field/code vocabulary and at most eight bounded fields; see [secrets](secrets.md). The diagnostic adapter emits optional structured traces; execution events are persisted by the observability components. Parent stdout/stderr are drained to `Stream.Null` and never stored or forwarded.

## Windows process ownership and IPC v1

`WorkerDock.Application.Execution.IExecutionSupervisor` is platform-neutral. `WorkerDock.Runtime` implements it on `net10.0-windows`; Quartz references the application port and the Windows worker root injects Runtime. Each attempt owns a separate unnamed, noninheritable Windows Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; neither breakaway flag is enabled. Only the supervisor holds its job handle. Forced termination uses `TerminateJobObject` and checks active-process accounting plus process exit within five seconds. Abrupt supervisor exit closes the last job handle and Windows kills contained descendants. Normal completion and malformed/closed IPC also confirm local tree exit before returning an outcome.

The trusted bootstrap process starts without invoking template composition. The supervisor attaches it to its job before sending the sole allow-execute message. Assignment/handshake failure kills the process and grants no execution permission. If the supervisor dies before assignment, the bootstrap expires without permission after 15 seconds. No administrator elevation is required for the ordinary Windows 11/Server 2022 same-identity case; inability to assign containment fails closed.

The parent creates a fresh random local named pipe with `PipeOptions.CurrentUserOnly`, restricting access to the same Windows identity. Use a distinct service identity per tenant. A pipe name and random 256-bit nonce are per attempt. Bootstrap environment values carry only this nonsecret transport capability, attempt ID, generation and parent PID; the child removes them before template composition. Both endpoints verify the opposite process PID through Windows named-pipe APIs. The parent verifies protocol version, nonce, attempt ID, generation and advertised child PID before permission. The child verifies version, attempt ID and generation on permission. Same-account processes are additionally excluded by PID checks; host administrators remain trusted.

Frames contain a four-byte little-endian signed length followed by UTF-8 JSON, maximum 65,536 bytes and JSON depth 32. Invalid/oversized inbound lengths are rejected before allocating the payload. Sequence: child `Handshake(Version, AttemptId, Generation, Nonce, ProcessId)`; parent `ExecuteRequest(Version, AttemptId, Generation, MetadataDigest, ExecutionDescriptor)`; parent may send `ExecutionControl("Cancel")`; child sends one bounded `TemplateResult`. Nonsecret descriptors pin exact version, immutable configuration, profile snapshot, deployment integration snapshot, deadline and stable execution key. The startup timeout defaults to 10 seconds, is capped at one minute and is shortened by a finite deadline. Result writing and cancel transmission have five-second bounds. Handler runtime is governed by the application attempt policy; disposal/force stop acts on only that attempt's job tree.

## Verification

Run on Windows 11 or Windows Server 2022+ with .NET 10:

```powershell
dotnet test tests/WorkerDock.ProcessTests --filter FullyQualifiedName~ContainmentTests
dotnet build WorkerDock.slnx -c Release
```

The ProcessTests executable supplies isolated supervisor, malformed-peer and executor fixture modes. `WorkerDock.TestTemplates` is referenced only by test outputs. Real child/grandchild tests prove abrupt supervisor cleanup and sibling-preserving force stop; these tests do not need SQL Server and do not substitute for SQL durability/Quartz clustering proofs.

Windows API references: [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), [AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject), and [PipeOptions.CurrentUserOnly](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0).

## Validated catalog and schema subset

Compute every descriptor digest using the public `TemplateDescriptorMetadata.WithDigest` API from `WorkerDock.Contracts`. Canonical UTF-8 JSON sorts object properties ordinally, preserves array order and includes identifier, version, display name, description, configuration schema and UI hints; it excludes the digest field itself. Numeric lexical spelling remains part of canonical metadata. The aggregate digest uses ordinal `(Id,Version)` ordering and `id|version|digest\n` for each descriptor. Identifiers cannot contain its separators.

Pass only descriptors obtained from explicitly referenced installed packages to the trusted constructor list of `TemplateCatalog` (development) or `SqlTemplateCatalog` (SQL). `RegisterAsync` validates and clones the complete batch before mutation, checks the actual descriptor digest against the supplied pin and trusted package list, and rejects changed metadata under the same identifier/version with `MetadataVersionConflict`. SQL publication uses the tenant business transaction and checks the tenant instance. It closes the common pool's start gates and advances Pending capacity policy before commit; workers must still satisfy common-catalog admission. It never enables existing jobs automatically. Worker supervisor digest pins come from installed descriptors.

`0004_templates` recognizes only the exact two `0003_execution` built-in 1.0.0 placeholder digests with NULL descriptor metadata. Their first trusted publication promotes the existing row once, retaining its PK and all job-revision FKs. A nonmatching digest, other identifier/version or already published descriptor receives no exception to immutability. Existing job revisions remain pinned and require validation before execution. Retiring a version disables selection and preserves its descriptor; re-registration cannot unretire it. SQL denies metadata replacement/deletion via an immutable trigger. Populated catalog rollback refuses to discard metadata.

The Read-authorized `GET /api/templates` and `GET /api/templates/{id}/{version}` return validated descriptor metadata plus `selectable`; Manage-authorized `POST /api/template-validation` returns only bounded path/code issues. There is no browser package-registration, secret-resolution, upload, or execution endpoint. Legacy placeholders without published descriptors are omitted from form metadata.

The closed JSON Schema 2020-12 subset accepts `type` (object, array, string, integer, number, boolean, null, or a unique union), `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, positive `multipleOf`, `minLength`, `maxLength`, `minItems`, `maxItems`, `uniqueItems`, `pattern`, `format`, `$defs`, `$ref` and discriminated `oneOf`. Boolean child schemas are supported. `title`, `description`, `default`, `x-workerDock-labelKey` and `x-workerDock-descriptionKey` provide text/fallback annotations. `$schema`, if present, must be the exact 2020-12 URI. Unknown keywords fail registration. Local references are restricted to `#/$defs/<simple-name>`; remote, unresolved and cyclic references fail. oneOf requires 2–16 alternatives with a common required string const discriminator and distinct values. Supported formats are `uuid` and RFC3339-style `date-time` with seconds and an explicit zone. String lengths count Unicode scalar values. Numeric bounds and multipleOf use exact decimal arithmetic. Nonzero numeric values/bounds are supported only when their normalized representation has at most 28 significant digits, decimal scale at most 28, and fits Decimal; unsupported precision/range is rejected before conversion rather than rounded.

Patterns are at most 256 ASCII characters and exclude groups, alternation, backslash constructs, nested character classes/subtraction and control characters; use simple anchored literal/character-class/quantifier patterns. Regex automata exceeding the engine budget are rejected at registration. Nonbacktracking evaluation has a 50 ms bound. Length/item limits are nonnegative integers at most 16,384. Metadata schema/hints and configuration each have a 48 KiB UTF-8 budget, issues are capped at 64, schema recursion is capped at 16 and evaluation has a shared 32,768-operation budget. Array length failures stop array evaluation immediately. Each uniqueItems pair comparison consumes that same budget; exhaustion returns ValidationBudgetExceeded and rejects the configuration, including across nested arrays. Schema validation returns only bounded paths/codes and never echoes offending configuration values or unexpected property names.

`x-workerDock-secretReference` and `x-workerDock-sensitive` accept booleans only. A true secret-reference field has string type, contains a logical name, and cannot declare a default value. UI hints accept only root `labelKey`, `descriptionKey`, and `fields`; each named property accepts `control`, `labelKey`, `descriptionKey`, `placeholderKey`. Controls are `text`, `textarea`, `number`, `checkbox`, `select`, `secretReference`, `sensitive`. Localization keys use simple identifier characters. Raw HTML, scripts, component paths, event handlers and arbitrary hints are rejected. Only built-in server/UI controls interpret these annotations.

`tests/fixtures/template-schemas.json` is the shared server/client fixture contract, including scalar Unicode length, exact decimal multiples, local references, discriminated alternatives and format rules. Server and frontend form-validator tests consume it. New supported keywords/formats require matching fixture and frontend/backend changes.
