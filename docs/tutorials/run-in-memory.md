# Try WorkerDock on your computer

This is the fastest way to see the Tenant UI. It uses temporary memory instead of SQL Server. You can make a draft job, but you **cannot run jobs** in this mode. Your draft disappears when you stop the app.

## 1. Get the tools

Use Windows with the .NET SDK version in [global.json](../../global.json) (10.0.402 or a newer patch in that band) and a compatible maintained Node.js release (the project uses Node 24.15.0). Open PowerShell in the repository root.

Run these commands once to get dependencies, build the UI, and trust the local HTTPS certificate:

```powershell
dotnet restore WorkerDock.slnx --locked-mode
npm.cmd --prefix frontend ci
npm.cmd --prefix frontend run build
dotnet dev-certs https --trust
```

If Windows asks whether to trust the certificate, accept it for local development.

## 2. Start the Tenant UI

In the same PowerShell window, run:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Tenant__Adapter='Memory'
$env:Tenant__InstanceId=[guid]::NewGuid().ToString()
Remove-Item Env:Tenant__ConnectionString -ErrorAction SilentlyContinue
Remove-Item Env:Access__Authority,Env:Access__ClientId,Env:Access__ClientSecretReference,Env:Access__RequiredManagementRole -ErrorAction SilentlyContinue
dotnet run --project src/WorkerDock.Web --urls https://localhost:5001
```

Leave this window open. Open **<https://localhost:5001/>** in your browser. The web app serves both the UI and API. Keep it on localhost: this Development setup allows management without signing in.

## 3. Make a draft

Open **Jobs** → **Create job**. Choose the HTTP template, enter a name, and save a **disabled draft**. You should see it in Jobs. Stop the app with **Ctrl+C** and start it again: the draft is gone because memory data is temporary.

The `/health/live` route can report liveness. The `/health/ready` route stays unavailable because this mode has no durable database. The runtime service and job executor do not run here.

## Optional: open the Control Center

Open a **second** PowerShell window in the repository root:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Central__Adapter='Memory'
$env:Central__InstanceId=[guid]::NewGuid().ToString()
$env:Central__Health__AllowedHosts__0='localhost'
dotnet run --project src/WorkerDock.ControlCenter --urls https://localhost:5002
```

Open **<https://localhost:5002/>**. Its tenant list is also temporary. It cannot health-probe the demo tenant on port 5001: tenant probes require HTTPS on port 443.

To run real jobs, continue with [SQL Server setup](../how-to/setup-sql-server.md) and the [SQL tutorial](run-with-sql-server.md). To change code, use the [developer checks](../validation/development-checks.md).
