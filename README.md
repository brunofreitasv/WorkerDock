# WorkerDock

<img src="frontend/packages/ui/src/brand/workerdock-mark.png" alt="WorkerDock logo" width="72" height="72">

WorkerDock is a self-hosted platform for creating, scheduling, running, monitoring, and auditing jobs. It includes a React operator dashboard, .NET services, SQL Server, and trusted SQL Server and HTTP job templates. Each tenant has its own database and one Windows runtime service that launches bounded child processes. The optional Control Center links to independently deployed tenants. No external message broker is required.

## Getting started

Choose the path that matches what you want to try:

1. **No SQL Server:** [Run a local in-memory instance](docs/tutorials/run-in-memory.md). This disposable development mode starts the web dashboard and API without a database.
2. **Real SQL Server:** [Set up runtime database objects](docs/how-to/setup-sql-server.md), then [run locally with SQL Server](docs/tutorials/run-with-sql-server.md).
3. **Windows deployment:** [Deploy to production](docs/how-to/deploy-production.md), then [configure each tenant](docs/how-to/configure-tenants.md).

The [documentation index](docs/index.md) groups tutorials, how-to guides, reference, and explanation using [Diátaxis](https://diataxis.fr/). It also links to template authoring, authentication, scheduling, monitoring, and troubleshooting.

For a quick visual tour, see the [tenant and process map](docs/reference/architecture.md#deployment-map), [job flow](docs/reference/scheduling.md#job-flow), and [runtime versus test database map](docs/how-to/setup-sql-server.md#runtime-and-test-databases).

## SQL assets

[`database/tenant`](database/tenant) is the only production SQL migration source. Its checksummed manifest contains the `0001_initial` fresh-install baseline and guarded rollback; apply it to a new tenant database with `scripts/Invoke-TenantMigration.ps1`. There is no tenant database upgrade converter because no tenant databases are deployed. The Control Center keeps state in protected files and has no SQL assets. SQL fixtures under [`tests/sql`](tests/sql) and [`tests/WorkerDock.IntegrationTests/Sql`](tests/WorkerDock.IntegrationTests/Sql) are for disposable integration tests only. For a fresh database only, `database/tenant/current-state.sql` is an alternative snapshot; it is not an upgrade path. See the [database object reference](docs/reference/database-objects.md) and [SQL setup guide](docs/how-to/setup-sql-server.md) for supported commands and permissions.

## Project status

The implementation is still in release validation. Production secret-vault adapters are deployment-specific and are not included; the Control Center requires one to start in Production. Portable builds and tests do not establish live SQL Server, Windows/IIS, Entra ID, upgrade/restore, or workload readiness. See the [release checklist](docs/validation/release-checklist.md) and [recorded evidence](docs/validation/release-evidence.md) before production use.

WorkerDock uses the [MIT License](LICENSE). Read [SECURITY.md](SECURITY.md) for security reporting and [CONTRIBUTING.md](CONTRIBUTING.md) before contributing.
