# Deployment model

Follow the [production deployment guide](../how-to/deploy-production.md) for
application, SQL, and upgrade guidance. Use [tenant configuration](../how-to/configure-tenants.md)
for process settings and Control Center registration, and [SQL setup](../how-to/setup-sql-server.md)
for migration, snapshot, and existing runtime-user instructions. Operators choose their
own build, packaging, and process-management tools.

A tenant consists of a Web process and one Windows runtime service. The runtime
polls SQL for schedules and queued work, then starts bounded execution child
processes. Worker and Executor currently require Windows because process containment
and IPC use Windows APIs; IIS and Windows Services are optional hosting choices. The optional Control Center is a
separate application with protected file state and no SQL database; it stores
tenant URLs and health credential references, and does not provision tenant
databases or copy tenant job data.
The runtime is single instance per tenant on its host. A tenant-scoped named
mutex rejects a second service; `Worker:HostCeiling` controls the maximum
concurrent execution children. Per-template and per-job limits further bound
admission. Creating jobs or changing those limits does not install another
service.

Production readiness remains under validation. The repository does not include a
production `ISecretVault` integration for the Control Center, and its Production
host fails startup until a trusted adapter is registered. The tenant runtime and
executor use an unavailable vault unless a trusted adapter is integrated; the
development environment mapping is disabled unless explicitly enabled. Do not
rely on secret-backed jobs, health probes, or alert sinks until the appropriate
vault integration is installed and verified. Complete the
[release checklist](../validation/release-checklist.md) against disposable Windows and SQL
infrastructure before live use.

Use SQL Server 2022 or newer with a separate database per tenant. Apply the
`0001_initial` baseline before starting applications. The selected runtime
database user must already exist in the tenant database; setup validates and
grants that user directly. The current Control Center uses
its host identity for protected file state and its vault adapter;
it has no SQL principal. Runtime identities must not have schema-change rights.
Keep configuration, credentials, and Data Protection keys outside release
packages, protected by the relevant application identities.

Your deployment process should preserve protected configuration and Data Protection keys across releases. Runtime configuration needs the reviewed template catalog digest and a child ceiling from 1 through 1000. Resolve conflicting tenant instance identities before deploying.

Before opening the application to users, start the Web host and runtime service,
check `/health/ready`, process states and logs, and verify the runtime heartbeat
and child ceiling in monitoring. Tenant readiness depends on SQL; Control Center readiness depends on
its persistent state journal. Readiness is not proof that a workload, identity-provider integration,
external secret provider, or restore path has been validated. The portable
`tests/DeploymentSmoke.ps1` checks PowerShell syntax, JSON config shape, and
runtime secret-reference examples only; it does not provision IIS or verify live
SQL Server, Entra, or workload behavior.

There is no tenant upgrade converter because no tenant databases are deployed.
For restore procedures, follow [upgrade and restore](../how-to/upgrade-and-restore.md).
Operators stop and start processes with their selected tooling and verify
readiness and execution state. SQL Server is required for each deployed tenant;
the Control Center can run without SQL. Memory mode is for Development and tests only.
