# Job command and manual execution guarantees

Task 05 stores immutable job revisions. New jobs begin disabled with concurrency limit 1; activation is a later explicit management command. A successful edit appends a revision and requires the prior rowversion through `If-Match`. The configuration GET is restricted to managers and returns an opaque quoted `ETag`; safe command receipts contain only command/target IDs and state. The server creates new job IDs, actor identity and correlation IDs. The two trusted template identities at this stage are `workerdock.sql-server` and `workerdock.http`, both version `1.0.0`; Task 10 replaces the seed metadata with installed package registration. Profiles are not yet registered, so a job with a profile reference cannot be run until Task 11 can resolve and pin its nonsecret revision.

An accepted manual run commits one queued execution, its revision, audit event, 24-hour actor/session-and-job-scoped request record, command receipt and `ExecutionDispatch` outbox intent in one business transaction. Concurrent retries with the same request key return the original receipt during that window. An expired key may create a new run. The current manual command has no caller payload beyond job ID and request key; the stored request hash protects that input and future payload-bearing commands must compare their full canonical input. The execution idempotency key is separate from the request key and stays stable across attempts. Outbox publication to Quartz occurs later and is not part of this transaction. WorkerDock promises durable intent and guarded redelivery, not exactly-once external effects. Unknown effects require review unless the job policy explicitly permits replay.

The memory adapter is a development/test substitute for `IJobCommands`, selected only with `Tenant:Adapter=Memory` in Development. Its state is lost on process exit. Run tests without SQL Server:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~JobCommandTests
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~AccessPolicyTests
dotnet build WorkerDock.slnx -c Release
```

Start a memory-backed HTTPS host in PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Tenant__Adapter = 'Memory'
$env:Tenant__InstanceId = [guid]::NewGuid().ToString()
dotnet run --project src/WorkerDock.Web --urls https://localhost:5001
```

Use a second PowerShell terminal to request a session-bound CSRF token, then send `Origin: https://localhost:5001` and `X-WorkerDock-CSRF: <token>` on POST/PUT requests. `POST /api/jobs` accepts a revision body with `jobId` set to the empty GUID; the server assigns its ID. `GET /api/jobs/{id}/configuration` returns the edit `ETag`; `PUT /api/jobs/{id}` requires that value in `If-Match`. `POST /api/jobs/{id}/runs` accepts `{ "requestKey": "new-client-key" }` once the job is enabled. The seed-based `InMemoryJobCommands` tests exercise manual runs before the activation command arrives.

For a local disposable SQL Server 2022+ instance, create a fresh development database with compatibility level 160 using an administrator account:

```powershell
sqlcmd -S localhost -E -Q "CREATE DATABASE [WorkerDockDev]"
sqlcmd -S localhost -E -Q "ALTER DATABASE [WorkerDockDev] SET COMPATIBILITY_LEVEL = 160"
$env:WORKERDOCK_SQL = 'Server=localhost;Database=WorkerDockDev;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
$env:WORKERDOCK_INSTANCE_ID = [guid]::NewGuid().ToString()
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0003_execution
```

Keep that instance ID stable for this database. Configure `Tenant__ConnectionString` and `Tenant__InstanceId` for the web host from deployment settings, and put the runtime database user in `WorkerDockRuntime`; only the migrator login needs DDL rights. Do not run the migrator against an existing user database. To run real disposable SQL integration tests on a dedicated test server, set `WORKERDOCK_TEST_SQL` to an authorized connection string for that server and execute:

```powershell
$env:WORKERDOCK_TEST_SQL = 'Server=localhost;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~JobSqlIntegrationTests
```

The SQL fixture creates and drops only a generated `WorkerDock_Test_<32 hex digits>` database, running with `READ_COMMITTED_SNAPSHOT` both off and on. SQL tests are currently blocked because the local Windows login is rejected; no SQL permission change is part of this task. Rollback `0003_execution` refuses populated jobs, executions, commands, worker state or dispatch intents; restore a backup for populated databases.

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
finalization; Task09 owns initial per-job admission/capacity coordination.

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
Minimal status events are persisted now for later operator history projections.
Real SQL fences/restart/migration proofs remain pending as described in
[database.md](database.md); memory and real-process tests establish separate facts.
