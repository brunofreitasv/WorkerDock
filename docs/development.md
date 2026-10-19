# Local development and verification

Choose a startup tutorial first: [run without SQL Server](tutorials/run-in-memory.md) or [run with SQL Server](tutorials/run-with-sql-server.md). The memory web host is disposable and does not launch the scheduler or workers. The SQL tutorial covers the separate processes and durable state.

Use the SDK pinned by global.json and the Node version in frontend/package.json. Run commands from the repository root on Windows.

## Checks that do not need SQL Server

~~~powershell
dotnet restore WorkerDock.slnx --locked-mode
npm.cmd --prefix frontend ci
npm.cmd --prefix frontend run test -- --run
npm.cmd --prefix frontend run build
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet test tests/WorkerDock.ProcessTests -c Release
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store!=Sql'
dotnet build WorkerDock.slnx -c Release
pwsh -NoProfile -File tests/DeploymentSmoke.ps1
~~~

The integration filter excludes fixtures marked Store=Sql. Windows process tests exercise contained child processes, while memory Quartz tests exercise only an in-process RAM store. These results do not establish SQL persistence, Quartz clustering, migration behavior, or database permissions.

For browser tests, install the Playwright Chromium version required by the frontend package, then run:

~~~powershell
npm.cmd --prefix frontend run e2e -- --workers=1
~~~

The browser suite launches its own test hosts. See [release evidence](validation/release-evidence.md) for recorded results and [fault coverage](validation/fault-matrix.md) for what the suites do and do not prove.

## SQL-backed integration tests

First provision a disposable SQL Server 2022+ instance as described in [Set up SQL Server](how-to/setup-sql-server.md). Supply three independent connections through a protected local secret source: a provisioner that may create and drop generated test databases, a restricted runtime login, and a restricted SQL-target login. The fixture creates and removes only its generated WorkerDock_Test databases.

~~~powershell
$env:WORKERDOCK_TEST_SQL='<disposable-server-provisioner-connection-string>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-restricted-runtime-login>'
$env:WORKERDOCK_TEST_SQL_TARGET_RUNTIME='<same-server-restricted-target-login>'
dotnet test tests/WorkerDock.IntegrationTests -c Release
~~~

Do not put credentials in source, command arguments, committed configuration or logs. If the connections are absent, SQL fixtures report SQL_TEST_BLOCKED; that is not a passing SQL result. The [database reference](database.md) retains focused SQL fixture commands and rollback constraints.

## Deployment validation

Portable script checks do not install IIS or exercise Entra ID. Before release, follow the [production deployment guide](how-to/deploy-production.md) on disposable Windows/SQL infrastructure and record the live outcomes in the [release checklist](release-checklist.md). Production secret-vault adapters are deployment work; the repository's environment-backed adapter is for development.
