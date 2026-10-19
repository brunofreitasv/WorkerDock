# Contributing

Thanks for contributing to WorkerDock. Keep changes focused, preserve tenant isolation and least privilege, and document operator-visible behavior and limitations.

## Development

- Install the .NET SDK version pinned in `global.json` and a supported Node.js version for frontend work.
- Use SQL Server 2022 for database integration work. The in-memory adapter is for development and unit tests only.
- Follow the numbered, checksummed migration workflow in `database.md`; include paired forward and rollback scripts when a migration permits a safe rollback.
- Keep secrets, customer data, and real deployment configuration out of source control. Use the placeholder examples under `deploy/config/` as templates only.
- Keep Quartz-owned tables under Quartz lifecycle/API ownership. Do not edit Quartz persistence rows directly.

Run the relevant checks for your change. Common commands are:

```powershell
dotnet test WorkerDock.slnx
npm.cmd --prefix frontend run test -- --run
npm.cmd --prefix frontend run build
pwsh -File .\tests\DeploymentSmoke.ps1
```

These commands do not replace live Windows/IIS/SQL, identity-provider, upgrade/restore, security, or measured workload validation. Do not report those as passing unless they were run and evidence was recorded.

## Templates and deployment

Template packages are trusted executable code, installed through a worker deployment. A job definition cannot install code or create a Windows service. Implement handlers through the public contracts, honor cancellation and execution deadlines, and log only safe structured values. Do not put secrets, full connection strings, SQL text, or request bodies in logs or Quartz job data. Test failure, cancellation, and uncertain external outcomes.

Use the smallest required permissions. Runtime identities must not have schema-change rights; schema changes use the separate migrator identity. Do not broaden schema compatibility windows without upgrade evidence.

## Pull requests

Describe the user-visible change, affected boundaries, verification performed, and any unresolved limitations. Link relevant design or operational documentation. Keep sample configuration placeholder-only. Do not include generated packages, secrets, customer data, or unreviewed dependency license material.
