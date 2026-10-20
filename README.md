# WorkerDock

<img src="frontend/packages/ui/src/brand/workerdock-mark.svg" alt="WorkerDock logo" width="72" height="72">

WorkerDock is a self-hosted platform for creating, scheduling, running, monitoring, and auditing jobs. It includes a React operator dashboard, .NET services, SQL Server and Quartz.NET for deployed scheduling, and trusted SQL Server and HTTP worker templates. Each tenant has its own database and runtime services; the optional Control Center links to independently deployed tenants. No external message broker is required.

## Getting started

Choose the path that matches what you want to try:

1. **No SQL Server:** [Run a local in-memory instance](docs/tutorials/run-in-memory.md). This disposable development mode starts the web dashboard and API without a database.
2. **Real SQL Server:** [Set up runtime database objects](docs/how-to/setup-sql-server.md), then [run locally with SQL Server](docs/tutorials/run-with-sql-server.md).
3. **Windows deployment:** [Deploy to production](docs/how-to/deploy-production.md), then [configure each tenant](docs/how-to/configure-tenants.md).

The [documentation index](docs/index.md) groups tutorials, how-to guides, reference, and explanation using [Diátaxis](https://diataxis.fr/). It also links to template authoring, authentication, scheduling, monitoring, and troubleshooting.

For a quick visual tour, see the [tenant and process map](docs/architecture.md#deployment-map), [job flow](docs/scheduling.md#job-flow), and [runtime versus test database map](docs/how-to/setup-sql-server.md#runtime-and-test-databases).

## SQL assets

Only [`database/tenant`](database/tenant) is current production SQL. Its checksummed manifest contains the tenant runtime migrations and paired rollbacks; run them with `WorkerDock.DatabaseMigrator` for every tenant database. The Control Center has no production SQL assets: it stores its state in protected files and rejects a central connection string. [`database/central`](database/central) is an archive of the schema used by pre-file-center releases. Do not apply it to a new installation or use it during import; the legacy importer reads an already-existing old database. SQL fixtures under [`tests/sql`](tests/sql) and [`tests/WorkerDock.IntegrationTests/Sql`](tests/WorkerDock.IntegrationTests/Sql) are disposable integration-test assets only. See [database object reference](docs/reference/database-objects.md) and the [SQL setup guide](docs/how-to/setup-sql-server.md) for supported commands and permissions.

## Project status

The implementation is still in release validation. Production secret-vault adapters are deployment-specific and are not included; the Control Center requires one to start in Production. Portable builds and tests do not establish live SQL Server, Windows/IIS, Entra ID, upgrade/restore, or workload readiness. See the [release checklist](docs/release-checklist.md) and [recorded evidence](docs/validation/release-evidence.md) before production use.

WorkerDock uses the [MIT License](LICENSE). Read [SECURITY.md](SECURITY.md) for security reporting and [CONTRIBUTING.md](CONTRIBUTING.md) before contributing.
