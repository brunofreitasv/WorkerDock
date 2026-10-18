# Trusted custom templates

WorkerDock executes reviewed, explicitly installed .NET packages. This is operational containment for trusted code, not a security sandbox for hostile assemblies. Windows host administrators and packages installed under the worker service identity remain trusted. Dashboard users cannot upload code, select assembly paths, install packages, or supply executable bootstrap arguments.

## Contract and registration

A template references `WorkerDock.Contracts` (`net10.0`). Implement `IWorkerTemplate`, publish one stable identifier and explicit version, and provide a `TemplateDescriptor` with JSON Schema, declarative UI hints and a metadata digest. Descriptor identity is immutable: changing metadata requires a new version. Exact duplicate registrations fail closed. Task 10 supplies canonical metadata/schema registration and safe/reference schema annotations; the runtime checks its deployment catalog's expected digest against the installed exact version before calling validation or execution.

Register reviewed package instances explicitly in the Executor composition root. Supply matching `TemplateId@TemplateVersion` digest entries to `ExecutionSupervisorOptions` from the worker's trusted capability catalog. Deployment-authored executable paths and bootstrap arguments are separate from job configuration. No assembly scanning, job-authored loading, scripts, executable UI hints, or test packages are registered by the production root. The initial root intentionally has no built-ins; SQL and HTTP registration belongs to their template tasks. Unknown exact versions return `TemplateVersionUnavailable`; incompatible digests return `TemplateMetadataMismatch`, with no handler entry.

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
    await context.Log.WriteAsync("ExampleCompleted",
        new Dictionary<string, string> { ["Outcome"] = "Completed" }, ct);
    return new TemplateResult("Success", "ExampleCompleted", true,
        JsonSerializer.SerializeToElement(new { completed = true }), null);
}
```

Successful external effects can survive local termination. Honor cooperative cancellation, carry the stable execution key to a destination's idempotency facility, and report uncertainty conservatively. Outcomes are `Success`, `TransientFailure`, or `PermanentFailure`. `RetryAfter` is an untrusted hint; application retry policy bounds it. A local stop or timeout does not promise remote rollback or exactly-once effects.

## Child composition and secrets

The Executor constructs vault and logging adapters only after the worker grants execution. Adapters and resolved secret values never cross IPC or argv. The child has no WorkerDock persistence connection. `IntegrationConfiguration` is a versioned server-authored nonsecret bootstrap snapshot; it must not contain provider credentials or a user-selected tenant/provider identity. Production provider bootstrap authentication belongs to the tenant service identity/protected deployment configuration.

The initial environment adapter is explicitly development-only: set `WORKERDOCK_DEVELOPMENT_VAULT=1` and provide `WORKERDOCK_SECRET_<logical reference>`. Names accept ASCII letters, digits and underscore, up to 80 characters. It fails closed by default. Replace this adapter in the installed child composition root for the tenant's production vault; never add a parent/web resolve endpoint. Isolate environment/file permissions per tenant.

`SecretValue.Reveal()` explicitly exposes read-only scoped memory. Dispose it promptly; disposal clears its backing array and subsequent access fails. `ToString()` always returns `[REDACTED]`. Child wrappers redact known resolved values from result JSON and safe log event/field names and values as defense in depth. This cannot make hostile code safe. Handlers must return only deliberate safe projections: never raw exceptions, request/response bodies, SQL text, credentials, headers, connection strings, or stdout/stderr. Logs accept bounded event codes and at most 32 bounded fields. The initial safe diagnostic adapter emits optional structured traces; durable execution-log persistence is provided by the observability task. Parent stdout/stderr are drained to `Stream.Null` and never stored or forwarded.

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
