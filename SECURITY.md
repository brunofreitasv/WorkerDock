# Security policy

WorkerDock is self-hosted software. Operators are responsible for securing Windows, IIS, SQL Server, identity-provider registrations, network egress, service identities, secret stores, backups, and custom template packages.

## Reporting a vulnerability

Do not report exploitable vulnerabilities in public issues. Use the repository host's private vulnerability reporting feature if available. If private reporting is unavailable, contact the maintainers through a verified private channel listed by the repository owner. Include affected version or commit, deployment assumptions, impact, and reproduction details. Do not include live credentials, customer data, or destructive payloads. No response-time commitment is currently published.

## Deployment security

Configure HTTPS and OIDC with a tenant-specific issuer and protected Data Protection keys. Runtime identities should have only their required SQL roles and filesystem access; use a separate migration identity. Keep secrets outside package files and source control. Restrict worker egress to approved destinations and review custom template code before installing it.

When OIDC settings are absent, the application enters **Anonymous** mode. The authorization policies then allow both reading and management without authentication. This mode is suitable only for isolated local development; exposing it to a network permits unauthenticated users to view and modify jobs, settings, and operational data. Configure authentication before exposing any instance.

The central Control Center stores registry information and performs constrained health probes. It does not provision tenant instances or proxy tenant management sessions. Tenant dashboards enforce their own authentication policies. Shared Windows or SQL Server administrators remain trusted administrators and can cross the isolation boundary.

Job handlers may cause external side effects. Delivery and retries are at least once; WorkerDock cannot guarantee exactly-once effects. Use downstream idempotency where available and review uncertain outcomes before replay. See [architecture](docs/architecture.md), [deployment](docs/deployment.md), and [troubleshooting](docs/troubleshooting.md).
