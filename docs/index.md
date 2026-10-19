# WorkerDock documentation

Choose a path by what you need to do. The [Diátaxis framework](https://diataxis.fr/) separates learning by doing, task instructions, technical facts, and background explanations.

## Tutorials: learn by running WorkerDock

- [Run locally without SQL Server](tutorials/run-in-memory.md): start the disposable development instance and see what the memory adapter can do.
- [Run locally with SQL Server](tutorials/run-with-sql-server.md): use a dedicated local tenant database and the SQL adapter.
- [Run local development checks](development.md): choose the test suites that need no SQL Server and identify those that do.

## How-to guides: complete a specific task

- [Set up SQL Server](how-to/setup-sql-server.md): create runtime databases and apply migrations; provision disposable test databases separately.
- [Deploy to production](how-to/deploy-production.md): build packages and install the tenant, scheduler, workers, and Control Center on Windows/IIS.
- [Configure tenants](how-to/configure-tenants.md): find tenant settings, service configuration, secret references, and Control Center registration.
- [Upgrade or restore](upgrades.md).
- [Add a custom template](custom-templates.md).
- [Troubleshoot an instance](troubleshooting.md).

## Reference: look up exact behavior and settings

- [Configuration keys and locations](reference/configuration.md).
- [Database objects and script ownership](reference/database-objects.md).
- [Database migration and SQL test details](database.md).
- [Authentication and role policy](authentication.md).
- [SQL Server template](sql-template.md) and [HTTP template](http-template.md).
- [Schedules and job lifecycle](scheduling.md), [manual jobs](manual-jobs.md), and [execution guarantees](execution-guarantees.md).
- [Monitoring](monitoring.md), [observability](observability.md), [alerts](alerts.md), and [retention/archive operations](retention-archive.md).
- [Outbound destination policy](outbound-policy.md) and [secret facade](secrets.md).
- [Release checklist](release-checklist.md), [recorded validation evidence](validation/release-evidence.md), [fault matrix](validation/fault-matrix.md), and [workload procedure](validation/workload-results.md).

## Explanation: understand the design

- [Architecture and component boundaries](architecture.md).
- [Quartz scheduling and dispatch](quartz-integration.md).
- [Control Center and tenant isolation](control-center.md).

Start with a tutorial or task guide for an end-to-end procedure; use the reference and explanation pages to inspect a contract or design decision. Memory-mode results do not prove SQL persistence or production readiness. The [release checklist](release-checklist.md) identifies validation that still needs a real deployment.
