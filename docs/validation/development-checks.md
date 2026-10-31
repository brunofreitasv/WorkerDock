# Check your changes

Run commands from the repository root in PowerShell on Windows. Use the .NET SDK in [global.json](../../global.json) and the Node version in [frontend/package.json](../../frontend/package.json). Start with the checks for the code you changed, then run the wider checks before a release.

## 1. Get dependencies

```powershell
dotnet restore WorkerDock.slnx --locked-mode
npm.cmd --prefix frontend ci
```

If either command fails, fix that first. Later commands need these dependencies.

## 2. Check the UI

```powershell
npm.cmd --prefix frontend run test -- --run
npm.cmd --prefix frontend run build
```

The first command runs frontend unit tests. The second builds both web apps.

## 3. Check .NET code without SQL Server

```powershell
dotnet test tests/WorkerDock.UnitTests -c Release
dotnet test tests/WorkerDock.ProcessTests -c Release
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store!=Sql'
dotnet build WorkerDock.slnx -c Release
pwsh -NoProfile -File tests/DeploymentSmoke.ps1
```

These checks cover unit behavior, Windows child-process behavior, SQL-independent integration paths, the solution build, and portable deployment scripts. They do **not** prove SQL Server behavior or a live IIS deployment. For the focused dynamic-value flow, run `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~DynamicValuesEndToEndTests`.

## 4. Check in a browser

Install the Playwright Chromium version required by the frontend package, then run:

```powershell
& .\frontend\node_modules\.bin\playwright.cmd install chromium
npm.cmd --prefix frontend run e2e -- --workers=1
```

The browser suite starts its own test hosts. A failing test names the page or action to investigate.

## 5. Check SQL behavior when you have a disposable server

[Set up a disposable SQL Server 2022+ instance](../how-to/setup-sql-server.md). Supply **three separate connections** from a protected local secret source: a database provisioner, a restricted WorkerDock runtime login, and a restricted target login. The tests create and remove only their own `WorkerDock_Test_*` databases.

```powershell
$env:WORKERDOCK_TEST_SQL='<disposable-server-provisioner-connection-string>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-restricted-runtime-login>'
$env:WORKERDOCK_TEST_SQL_TARGET_RUNTIME='<same-server-restricted-target-login>'
dotnet test tests/WorkerDock.IntegrationTests -c Release
```

Do not place passwords in source, command arguments, committed configuration, or logs. If the connections are missing, `SQL_TEST_BLOCKED` means SQL behavior has **not** been checked. The focused dynamic SQL date test is `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~SqlTemplateTests.Dynamic_reference_date_binds_as_sql_date`.

## Before a release

Portable checks do not install IIS or exercise an identity provider. Follow the [release checklist](release-checklist.md) on disposable Windows and SQL infrastructure and record actual results in [release evidence](release-evidence.md). The [fault matrix](fault-matrix.md) shows which failures are covered. Production secret-vault adapters must be supplied by the deployment.
