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
