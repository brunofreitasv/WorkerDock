# Deployment model

Follow the [production deployment guide](../how-to/deploy-production.md) for
application, SQL, and upgrade guidance. Use [tenant configuration](../how-to/configure-tenants.md)
for process settings and Control Center registration, and [SQL setup](../how-to/setup-sql-server.md)
for migration, snapshot, and runtime-role instructions. Operators choose their
own build, packaging, and process-management tools.

A tenant consists of a Web process, a Quartz scheduler process, and
one or more generic worker processes. Worker and Executor currently require
Windows because process containment and IPC use Windows APIs; IIS and Windows
Services are optional hosting choices. The optional Control Center is a
separate application with protected file state and no SQL database; it stores
tenant URLs and health credential references, and does not provision tenant
databases or copy tenant job data.
Creating jobs or changing template concurrency does not install or configure a
process. Add a worker only to add another process or host-capacity boundary.

Production readiness remains under validation. The repository does not include a
production `ISecretVault` integration for the Control Center, and its Production
host fails startup until a trusted adapter is registered. The tenant scheduler and
executor use an unavailable vault unless a trusted adapter is integrated; the
development environment mapping is disabled unless explicitly enabled. Do not
rely on secret-backed jobs, health probes, or alert sinks until the appropriate
vault integration is installed and verified. Complete the
[release checklist](../validation/release-checklist.md) against disposable Windows and SQL
infrastructure before live use.

Use SQL Server 2022 or newer with a separate database per tenant. Apply tenant
migrations before starting applications, then map each tenant runtime identity
to `WorkerDockRuntime` in its tenant database. The current Control Center uses
its host identity for protected file state and its vault adapter;
it has no SQL principal. Runtime identities must not have schema-change rights.
Keep configuration, credentials, and Data Protection keys outside release
packages, protected by the relevant application identities.

Your deployment process should preserve protected configuration and Data Protection keys across releases. Worker configurations need distinct installation GUIDs and the reviewed template catalog digest; additional workers must use the active tenant release. Resolve conflicting host names or installation identities before deploying.

Before opening the application to users, start the configured processes and Web host,
check `/health/ready`, process states and logs, and verify worker
heartbeats. Tenant readiness depends on SQL; Control Center readiness depends on
its persistent state journal. Readiness is not proof that a workload, identity-provider integration,
external secret provider, or restore path has been validated. The portable
`tests/DeploymentSmoke.ps1` checks PowerShell syntax, JSON config shape, and
runtime secret-reference examples only; it does not provision IIS or verify live
SQL Server, Entra, or workload behavior.

For upgrades and restores, follow [upgrade and restore](../how-to/upgrade-and-restore.md). Operators stop and start processes with their selected tooling and verify readiness and execution state. SQL Server is required for
each deployed tenant; the Control Center can run without SQL. Memory mode is for
Development and tests only.
