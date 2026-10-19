# WorkerDock

WorkerDock is an open-source, self-hosted platform for scheduling, executing, monitoring, and auditing jobs. It uses a React dashboard, .NET services, Quartz.NET with SQL Server persistence, and isolated child processes for job attempts. The supported deployment model is Windows with IIS and SQL Server; SQL Server remains a separately installed prerequisite.

## Status

This repository contains an implementation in progress. The documented design and deployment guides describe intended and implemented behavior, but do not establish that the full product has passed release validation. Live IIS, SQL Server, identity-provider, upgrade/restore, and workload validation must be completed on the target environment before production use. See the [release checklist](docs/release-checklist.md) and [release evidence guidance](docs/superpowers/plans/2026-10-17-workerdock-06-release.md).

## Requirements and setup

Deployed instances require Windows Server 2022 or newer, IIS, PowerShell 7, the .NET 10 Hosting Bundle, and SQL Server 2022 or newer (compatibility level 160 by default). Windows 11 supports local development. Each tenant uses an independent database; the Control Center has a separate database. Redis, RabbitMQ, and an external message broker are not required. Entra ID/OIDC is optional, while job destination integrations and production secret providers are deployment-specific.

Use the [database setup guide](docs/database.md) for local SQL Server and migration instructions, [Windows deployment](docs/deployment.md) for package and installation steps, and [upgrade and restore](docs/upgrades.md) before changing a deployed release. The deployment examples in `deploy/config/` are placeholders; replace them in protected deployment storage and provide credentials through the configured secret mechanisms. Never commit real credentials.

For frontend-only work, use the package scripts in `frontend/package.json`. For .NET work, use the pinned SDK in `global.json` and `WorkerDock.slnx`. A memory adapter exists for development and tests; it is not a deployed SQL substitute.

## Architecture

Read [architecture](docs/architecture.md) for service boundaries, data ownership, scheduling, execution guarantees, authentication, and operational limits. [Troubleshooting](docs/troubleshooting.md) covers common setup and runtime issues. Existing focused operational guides include [retention and archive operations](docs/retention-archive.md), [database setup](docs/database.md), [deployment](docs/deployment.md), and [upgrades](docs/upgrades.md).

## Security and contributions

Review [SECURITY.md](SECURITY.md) before deployment and [CONTRIBUTING.md](CONTRIBUTING.md) before changing or extending the code. Custom template packages execute trusted code with access to the credentials and network destinations granted to their service identity. Review and deploy packages through a controlled worker release.

## License

WorkerDock is licensed under the MIT License; see [LICENSE](LICENSE). Third-party dependency licenses and notices continue to apply.
