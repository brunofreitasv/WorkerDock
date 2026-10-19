# Job command and manual execution guarantees

Jobs use immutable revisions. New jobs begin disabled with concurrency limit 1; enabling is an explicit management command. A successful edit appends a revision and requires the prior rowversion through `If-Match`. The configuration GET is restricted to managers and returns an opaque quoted `ETag`; safe command receipts contain only command/target IDs and state. The server creates new job IDs, actor identity and correlation IDs. Built-in templates are registered by the installed Worker packages, and accepted executions pin the job, template, and referenced profile revisions.

An accepted manual run commits one queued execution, its revision, audit event, 24-hour actor/session-and-job-scoped request record, command receipt and `ExecutionDispatch` outbox intent in one business transaction. Concurrent retries with the same request key return the original receipt during that window. An expired key may create a new run. The current manual command has no caller payload beyond job ID and request key; the stored request hash protects that input and future payload-bearing commands must compare their full canonical input. The execution idempotency key is separate from the request key and stays stable across attempts. Outbox publication to Quartz occurs later and is not part of this transaction. WorkerDock promises durable intent and guarded redelivery, not exactly-once external effects. Unknown effects require review unless the job policy explicitly permits replay.

The memory adapter is a development/test substitute for `IJobCommands`, selected only with `Tenant:Adapter=Memory` in Development. Its state is lost on process exit. The integration suite has memory-backed tests; see [development](development.md) for local commands.

Start a memory-backed HTTPS host in PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Tenant__Adapter = 'Memory'
$env:Tenant__InstanceId = [guid]::NewGuid().ToString()
dotnet run --project src/WorkerDock.Web --urls https://localhost:5001
```

Use a second PowerShell terminal to request a session-bound CSRF token, then send `Origin: https://localhost:5001` and `X-WorkerDock-CSRF: <token>` on POST/PUT requests. `POST /api/jobs` accepts a revision body with `jobId` set to the empty GUID; the server assigns its ID. `GET /api/jobs/{id}/configuration` returns the edit `ETag`; `PUT /api/jobs/{id}` requires that value in `If-Match`. `POST /api/jobs/{id}/runs` accepts `{ "requestKey": "new-client-key" }` once the job is enabled.

For tenant SQL provisioning, restricted identities, integration test prerequisites, and rollback, follow [Set up SQL Server](how-to/setup-sql-server.md). Do not apply an old migration ID to create a current database; the latest tenant chain is documented in the [database object reference](reference/database-objects.md).

## Durable attempts, retries and stop

Quartz invokes `ExecuteTemplateJob`, which checks immutable dispatch identity,
authorizes one attempt in the business transaction, reads the pinned descriptor,
and awaits Runtime supervision through child exit and outcome persistence.
There is no WorkerDock work-table polling/claim loop. Quartz recovery/redelivery
rechecks existing attempts and never grants permission to repeat their effects.
Short recovery-review jobs run on the separate schedule/control scheduler.

The default application renewal interval is 10 seconds and the lease is 60 seconds.
Begin, renewal and completion use SQL time under the short tenant business lock;
renewal/completion require the current generation, worker boot and active,
unexpired lease. Descriptor reads recheck that same fence and pinned catalog/profile
availability. They cannot substitute a newer revision. No Quartz, vault or child
call occurs while the business transaction is locked.

Runtime polls durable controls outside Quartz execution-slot capacity. A null
execution timeout means infinite duration; ownership still renews and stop still
works. Finite timeout starts at admission and includes startup. Runtime uses
monotonic elapsed time from each acknowledged ownership request, subtracts a
safety margin, requests cooperative cancellation, then contains the tree within
30 seconds or earlier when ownership safety requires it. A late acknowledgement
cannot revive a child past the conservative local boundary. Synchronize host/SQL
clocks for Quartz and operator timestamps. An unresponsive OS/host can outlive
local deadlines; leases protect WorkerDock records, not remote systems.

A stop response acknowledges the committed stop marker and audit; it does not
claim immediate termination. A committed stop wins against later handler success
and suppresses every automatic retry. If completion committed first, a later stop
preserves that outcome. Force stop requires explicit request confirmation and a
reason. A confirmed local exit is Stopped; unconfirmed exit remains NeedsReview
with the stop marker and job permit retained. Local containment never promises
that a remote transaction rolled back.

Retry classification is transactional and bounded by MaxAttempts. Only permitted
transient safe codes retry automatically; permanent/unlisted failures and a delay
hint alone do not grant permission. Exponential jitter is bounded by RetryMaxDelay.
Negative RetryAfter hints are ignored; nonnegative hints are capped, combined with
the jittered delay using the greater value, and capped again. Approved retries
create future one-shot outbox intents with the original execution idempotency key.
Quartz trigger retry is None, and the wrapper does not sleep/refire for backoff.
The logical job permit is retained during backoff and NeedsReview and released on
finalization.

Expiry marks an attempt Abandoned without deleting Quartz rows. A crash before
launch and a crash after effects are both uncertain unless no handler entry can
be proved. Uncertain effects become NeedsReview unless the pinned job policy
explicitly permits bounded replay. Managers can ReconcileSucceeded, Cancel, or
AuthorizeRetry (within the pinned total-attempt budget). Every decision requires
a reason and audit. Authorizing retry clears a prior stop only through this explicit
decision. Cancel/reconciliation can release an unresolved permit, so the operator
must acknowledge possible external overlap. Targets must honor the stable key or
provide their own transaction/lock when strict external deduplication is required.

Shutdown puts acquisition in standby, requests cancellation and drains for the
configured bounded interval (Worker:DrainSeconds, default 40), then forces local
containment. Cleanup still runs if the scheduler standby call fails. Unknown
effects or a lost outcome acknowledgement never become a fabricated success.
Status events feed the current [execution monitoring](monitoring.md) timeline.
Real SQL fences, restart, and migration proofs remain pending as described in
[database verification](database.md); memory and real-process tests establish separate facts.
