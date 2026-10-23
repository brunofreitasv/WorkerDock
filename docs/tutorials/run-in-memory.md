# Run WorkerDock with memory adapters

This starts the Tenant web UI and Control Center without SQL Server. The Worker
and the tenant runtime service require SQL-backed tenant state and do not run in this mode.
Memory data is disposable and is lost when either process stops.

## Prerequisites

- Windows, .NET SDK 10.0.402 (see `global.json`), and Node.js 24.15.0 or a
  compatible maintained Node release.
- From the repository root, restore and build the UI:

```powershell
dotnet restore WorkerDock.slnx --locked-mode
npm.cmd --prefix frontend ci
npm.cmd --prefix frontend run build
dotnet dev-certs https --trust
```

Trust the development HTTPS certificate once per machine if prompted.

## Tenant UI

In a PowerShell window at the repository root:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Tenant__Adapter='Memory'
$env:Tenant__InstanceId=[guid]::NewGuid().ToString()
Remove-Item Env:Tenant__ConnectionString -ErrorAction SilentlyContinue
Remove-Item Env:Access__Authority,Env:Access__ClientId,Env:Access__ClientSecretReference,Env:Access__RequiredManagementRole -ErrorAction SilentlyContinue
dotnet run --project src/WorkerDock.Web --urls https://localhost:5001
```

Open <https://localhost:5001/>. The same host serves the React frontend and
Tenant API; there is no separate frontend/API process. Anonymous Development
access allows anyone who can reach this local instance to manage it, so keep it
bound to localhost. `/health/live` reports liveness. `/health/ready` stays
unavailable because the monitoring adapter cannot verify durable persistence.

To confirm the UI is using memory state, open **Jobs**, select the HTTP
template, enter a name and save a disabled draft. It appears in the current
browser session; after restarting the tenant host, the draft is gone. This
mode does not run the worker or execute jobs.

## Control Center

For this disposable Development tutorial, Memory is selected explicitly and
its registry is lost on restart. A fresh persistent Control Center instead
uses a protected file directory configured with
`Central:Storage:DataDirectory`; it still needs no central SQL database.

In a second PowerShell window at the repository root:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Central__Adapter='Memory'
$env:Central__InstanceId=[guid]::NewGuid().ToString()
$env:Central__Health__AllowedHosts__0='localhost'
dotnet run --project src/WorkerDock.ControlCenter --urls https://localhost:5002
```

Open <https://localhost:5002/>. The Control Center has its own disposable
in-memory registry. Its health probe requires an HTTPS tenant endpoint on port
443, so the tenant on port 5001 is useful for browsing but is not a valid
health-probe target for this tutorial. Stop either host with Ctrl+C. No SQL
cleanup is needed.

For SQL-backed local development, follow [Run WorkerDock with SQL Server](run-with-sql-server.md)
and the [SQL Server setup guide](../how-to/setup-sql-server.md).
