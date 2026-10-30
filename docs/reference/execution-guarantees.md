# Job command and manual execution guarantees

Jobs use immutable revisions. New jobs begin disabled with concurrency limit 1; enabling is an explicit management command. A successful edit appends a revision and requires the prior rowversion through `If-Match`. The configuration GET is restricted to managers and returns an opaque quoted `ETag`; safe command receipts contain only command/target IDs and state. The server creates new job IDs, actor identity and correlation IDs. Built-in templates are registered by the installed Worker packages, and accepted executions pin the job, template, and referenced profile revisions.

An accepted manual run commits one queued execution, its revision, audit event, 24-hour actor/session-and-job-scoped request record, and command receipt in one business transaction. Concurrent retries with the same request key return the original receipt during that window. An expired key may create a new run. The current manual command has no caller payload beyond job ID and request key; the stored request hash protects that input and future payload-bearing commands must compare their full canonical input. The execution idempotency key is separate from the request key and stays stable across attempts. The runtime claims queued rows directly from SQL. WorkerDock promises durable intent and bounded redelivery, not exactly-once external effects. Unknown effects require review unless the job policy explicitly permits replay.

For dynamic-capable template revisions, the immutable revision also pins a typed dynamic-value plan and resolver version. The plan names only schema-declared scalar targets; it cannot point into secret or sensitive fields. At admission, scheduled executions pin their original occurrence instant and manual executions pin their accepted request time. A manual `referenceDateOverride` is an optional date-only value included in the deduplication payload; reusing a request key with a different override conflicts. A duplicate request with the same key and override returns the original accepted execution. Leaving the override empty uses the request's local date in the plan timezone.

Dynamic sources are **ReferenceDate**, **ScheduledTimestamp**, **FirstExecutionStart**, **ExecutionId**, and **IdempotencyKey**. ReferenceDate uses the original scheduled occurrence's local date, so a job due October 20 and run October 21 still uses October 20; retries retain that context. Manual executions use the override when supplied, otherwise their accepted request date. ScheduledTimestamp is the original occurrence instant and is unavailable to manual executions. FirstExecutionStart is set on the first accepted attempt and retained across retries; it is not a continually changing current time. ExecutionId and IdempotencyKey are stable per execution. Month/day shifts and period boundaries use the pinned timezone; date-only values remain dates, while timestamp outputs support compatible offset/UTC ISO or Unix formats. A retry or recovery reuses the first persisted resolved-value snapshot instead of recalculating from the current clock.

The manager-only template-values preview runs the resolver against an explicit sample context and returns bounded validation issues plus example values marked as examples. Preview does not execute template code, contact SQL/HTTP targets, or resolve vault secrets. Execution snapshots contain resolved nonsecret dynamic values only; secret references continue through the existing execution-time vault path and are never copied into a dynamic plan or snapshot. The SQL Server and HTTP built-ins expose version 1.1.0 only in the fresh-install catalog. Custom templates remain pinned to their own versions.

The memory adapter is a development/test substitute for `IJobCommands`, selected only with `Tenant:Adapter=Memory` in Development. Its state is lost on process exit. The integration suite has memory-backed tests; see [development](../validation/development-checks.md) for local commands.

Start a memory-backed HTTPS host in PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Tenant__Adapter = 'Memory'
$env:Tenant__InstanceId = [guid]::NewGuid().ToString()
dotnet run --project src/WorkerDock.Web --urls https://localhost:5001
```

Use a second PowerShell terminal to request a session-bound CSRF token, then send `Origin: https://localhost:5001` and `X-WorkerDock-CSRF: <token>` on POST/PUT requests. `POST /api/jobs` accepts a revision body with `jobId` set to the empty GUID; the server assigns its ID. `GET /api/jobs/{id}/configuration` returns the edit `ETag`; `PUT /api/jobs/{id}` requires that value in `If-Match`. `POST /api/jobs/{id}/runs` accepts `{ "requestKey": "new-client-key" }` once the job is enabled.

For fresh tenant SQL provisioning, restricted identities, integration test prerequisites, and guarded rollback, follow [Set up SQL Server](../how-to/setup-sql-server.md). Apply the `0001_initial` baseline to a new database; the schema is listed in the [database object reference](database-objects.md).

## Durable attempts, retries and stop

The tenant runtime reads bounded candidate IDs from SQL, then atomically
rechecks each execution, job, revision, and capacity before inserting an
attempt. A local semaphore limits concurrent child processes. SQL time and the
tenant business transaction lock decide which runtime may claim work. Expired
attempts are classified before new admissions; uncertain handler entry moves
the execution to `NeedsReview` unless its pinned policy allows bounded replay.

The default application renewal interval is 10 seconds and the lease is 60 seconds.
Begin, renewal and completion use SQL time under the short tenant business lock;
renewal/completion require the current generation, worker boot and active,
unexpired lease. Descriptor reads recheck that same fence and pinned catalog/profile
availability. They cannot substitute a newer revision. No runtime, vault or child
call occurs while the business transaction is locked.

Runtime polls durable controls independently of execution admission. A null
execution timeout means infinite duration; ownership still renews and stop still
works. Finite timeout starts at admission and includes startup. Runtime uses
monotonic elapsed time from each acknowledged ownership request, subtracts a
safety margin, requests cooperative cancellation, then contains the tree within
30 seconds or earlier when ownership safety requires it. A late acknowledgement
cannot revive a child past the conservative local boundary. Synchronize host/SQL
clocks for operator timestamps. An unresponsive OS/host can outlive
local deadlines; leases protect WorkerDock records, not remote systems.

A stop response acknowledges the committed stop marker and audit; it does not
claim immediate termination. A committed stop wins against later handler success
and suppresses every automatic retry. If completion committed first, a later stop
preserves that outcome. Force stop requires explicit request confirmation and a
reason. A confirmed local exit is Stopped; unconfirmed exit remains NeedsReview
with the stop marker retained. Local containment never promises
that a remote transaction rolled back.

Retry classification is transactional and bounded by MaxAttempts. Only permitted
transient safe codes retry automatically; permanent/unlisted failures and a delay
hint alone do not grant permission. Exponential jitter is bounded by RetryMaxDelay.
Negative RetryAfter hints are ignored; nonnegative hints are capped, combined with
the jittered delay using the greater value, and capped again. Approved retries
set a future SQL `AvailableAtUtc` with the original execution idempotency key.
The runtime poll observes the bounded backoff without occupying numeric capacity.
Only `Running` attempts count against the host and per-job numeric limits. Queued,
retry-waiting, and `NeedsReview` executions consume neither limit. Job capacity
defaults to one; zero pauses new starts. `Allow` may overlap up to both numeric
limits, `Queue` stays serial, and `Skip` discards overlapping occurrences. For
Queue and Skip, the per-job “Wait for review before starting new runs” setting
controls whether an unresolved `NeedsReview` execution also holds that job's
admission. With the setting off, later runs may proceed while the earlier
execution remains in review. Templates do not impose a numeric capacity limit.

Expiry marks an attempt Abandoned. A crash before
launch and a crash after effects are both uncertain unless no handler entry can
be proved. Uncertain effects become NeedsReview unless the pinned job policy
explicitly permits bounded replay. Managers can ReconcileSucceeded, Cancel, or
AuthorizeRetry (within the pinned total-attempt budget). Every decision requires
a reason and audit. Authorizing retry clears a prior stop only through this explicit
decision. Cancel/reconciliation resolves review, so the operator
must acknowledge possible external overlap. Targets must honor the stable key or
provide their own transaction/lock when strict external deduplication is required.

Shutdown stops new claims, requests cancellation and drains for the
configured bounded interval (`Worker:DrainSeconds`, default 40), then forces local
containment. Unknown
effects or a lost outcome acknowledgement never become a fabricated success.
Status events feed the current [execution monitoring](monitoring.md) timeline.
Real SQL fences, restart, and migration proofs remain pending as described in
[database verification](database-migrations.md); memory and real-process tests establish separate facts.
## Per-attempt failure diagnostics

Execution details expose optional diagnostics beneath the matching attempt to existing Read-authorized users. The execution includes its pinned template version and job revision. Older attempts have no retroactive diagnostic. Diagnostics do not change retry, certainty, or effects semantics and do not appear in broad lists, audit summaries, alerts, or generic logs.

Expand **Failure diagnostics** for bounded exception messages, client stacks, SQL number/state/procedure/line and command kind, or HTTP method/status and response content. Missing stacks are unavailable; non-success HTTP status responses need not have an exception stack. A truncated label means the available output was bounded. SQL query text is never included.

**Hide detailed failure output** on the pinned job revision suppresses message, stack and response content before persistence; safe reproduction metadata remains visible. Details follow execution retention and archive/purge behavior.
