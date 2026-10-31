# Contributing to WorkerDock

Want to change WorkerDock? Start with a small change you can see and check. These steps assume Windows and PowerShell in the repository root.

## 1. Get ready

Install the .NET SDK version in [global.json](global.json) and a compatible maintained Node.js release (the frontend uses Node 24.15.0). Then run:

```powershell
dotnet restore WorkerDock.slnx --locked-mode
npm.cmd --prefix frontend ci
```

To see the app before editing it, follow the [memory demo](docs/tutorials/run-in-memory.md). It needs no SQL Server and cannot execute jobs.

## 2. Find the right folder

| What you want to change | Start here |
| --- | --- |
| Tenant screens and forms | `frontend/apps/tenant` |
| Control Center screens | `frontend/apps/center` |
| Shared UI pieces | `frontend/packages/ui` |
| Tenant HTTP API | `src/WorkerDock.Web` |
| Scheduling and execution | `src/WorkerDock.Worker`, `src/WorkerDock.Runtime`, and `src/WorkerDock.Executor` |
| Built-in SQL or HTTP templates | `src/WorkerDock.Templates.SqlServer` or `src/WorkerDock.Templates.Http` |
| Tenant database schema | `database/tenant` |
| Checks | `tests` and frontend test files |

The [architecture map](docs/reference/architecture.md#deployment-map) shows how these pieces connect.

## 3. Make and check one change

Edit the smallest area that solves the problem. Run the checks for that area in [Check your changes](docs/validation/development-checks.md). If people will see different behavior, update its guide or reference page too. In a pull request, say what changed, what you ran, and what remains unverified.

## Rules that protect users

- Keep passwords, tokens, customer data, and real deployment settings out of the repository and logs.
- Keep each tenant's data and runtime separate. Runtime database identities must not have schema-change rights.
- Use the checksummed [tenant migration workflow](docs/reference/database-migrations.md) for schema changes. Do not edit a migration that has already been published.
- Treat template packages as trusted code installed through deployment. The browser cannot upload code. Follow [Add a custom template](docs/how-to/add-custom-template.md) and test failure, cancellation, and uncertain outcomes.
- A local build or memory test does not prove live SQL Server, Windows/IIS, sign-in, backup/restore, security, or workload readiness. Use the [release checklist](docs/validation/release-checklist.md) for those claims.
