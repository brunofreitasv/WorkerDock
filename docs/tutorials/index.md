# WorkerDock documentation

Choose a path by what you need to do. WorkerDock groups its documentation into tutorials, how-to guides, reference, and validation. This follows the learning, task, and technical-reference distinctions in the [Diátaxis framework](https://diataxis.fr/), with validation evidence collected separately.

For a visual introduction, start with the [deployment map](../reference/architecture.md#deployment-map),
the [job flow](../reference/scheduling.md#job-flow), and the
[runtime/test database map](../how-to/setup-sql-server.md#runtime-and-test-databases).

## Tutorials: learn by running WorkerDock

- [Run locally without SQL Server](run-in-memory.md): start the disposable development instance and see what the memory adapter can do.
- [Run locally with SQL Server](run-with-sql-server.md): use a dedicated local tenant database and the SQL adapter.

## How-to guides: complete a specific task

- [Set up SQL Server](../how-to/setup-sql-server.md): create tenant runtime databases and apply migrations; provision disposable test databases separately. The Control Center has no SQL setup.
- [Deploy to production](../how-to/deploy-production.md): build packages and install the tenant, scheduler, workers, and Control Center on Windows/IIS.
- [Configure tenants](../how-to/configure-tenants.md): find tenant settings, service configuration, secret references, and Control Center registration.
- [Upgrade or restore](../how-to/upgrade-and-restore.md).
- [Add a custom template](../how-to/add-custom-template.md).
- [Configure authentication and role access](../how-to/configure-authentication.md).
- [Configure the Control Center](../how-to/configure-control-center.md).
- [Manage jobs](../how-to/manage-jobs.md).
- [Manage retention and archives](../how-to/manage-retention-and-archives.md).
- [Troubleshoot an instance](../how-to/troubleshooting.md).

## Reference: look up exact behavior and settings

- [Configuration keys and locations](../reference/configuration.md).
- [Database objects and script ownership](../reference/database-objects.md): tenant runtime migrations, test fixtures, and Quartz source provenance.
- [Database migration and SQL test details](../reference/database-migrations.md).
- [SQL Server template](../reference/sql-template.md) and [HTTP template](../reference/http-template.md).
- [Schedules and job lifecycle](../reference/scheduling.md) and [execution guarantees](../reference/execution-guarantees.md).
- [Monitoring](../reference/monitoring.md), [observability](../reference/observability.md), and [alerts](../reference/alerts.md).
- [Outbound destination policy](../reference/outbound-policy.md) and [secret facade](../reference/secrets.md).
- [Deployment model](../reference/deployment-model.md) and [third-party notices](../reference/third-party-notices-dotnet.md).

## Validation: run checks and review evidence

- [Local development checks](../validation/development-checks.md): run the suites and identify which need SQL Server.
- [Release checklist](../validation/release-checklist.md), [recorded evidence](../validation/release-evidence.md), [fault matrix](../validation/fault-matrix.md), and [workload procedure](../validation/workload-results.md).

## Design and architecture

- [Architecture and component boundaries](../reference/architecture.md).
- [Quartz scheduling and dispatch](../reference/quartz-integration.md).

Start with a tutorial or task guide for an end-to-end procedure; use reference pages to inspect behavior, contracts, and design. Memory-mode results do not prove SQL persistence or production readiness. The [release checklist](../validation/release-checklist.md) identifies validation that still needs a real deployment.
