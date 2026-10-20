# Troubleshooting

Start by checking the tenant's `/health/ready` endpoint, Windows service states, application/service logs, SQL connectivity, and system clock synchronization. Do not place credentials, connection strings, SQL text, or request bodies in diagnostic output.

| Symptom | Check | Recovery |
| --- | --- | --- |
| Web app is unavailable | IIS site and application pool, HTTPS certificate and SNI binding, Hosting Bundle, protected config/key directory, `/health/ready` | Correct the failing host/configuration prerequisite; preserve the tenant's Data Protection keys. |
| Login or management authorization fails | OIDC authority, client ID, secret reference supplied through the bootstrap environment, issuer, role claim type, and required role | Correct the identity-provider and role configuration. A missing role can allow authenticated reads while denying management. |
| Instance unexpectedly has no login | Inspect `Access` configuration and session mode | Missing OIDC settings select Anonymous mode and allow unauthenticated read and management access. Configure OIDC before exposing the instance. |
| Scheduler or worker is offline | Service status, event logs, tenant database reachability, runtime SQL role, and matching tenant instance ID | Restore SQL/network access and verify the appropriate migration and runtime-role setup. |
| Job remains pending or projection reports failure | Scheduler health, database availability, projection/outbox age, Quartz scheduler logs and schema compatibility | Restore dependencies and allow reconciliation to retry; do not edit Quartz tables directly. |
| No worker can execute a template | Worker heartbeat, installed catalog digest, exact template ID/version capability, and worker package contents | Deploy the reviewed template package to the worker fleet, then verify capability acknowledgements. Creating a job does not deploy code. |
| Execution is slow or stuck | Queue delay, host/SQL pressure, handler duration, timeout, worker capacity, child-process state | Check allowed downstream destinations and handler behavior. Infinite-timeout work may require an operator decision; service stop can terminate a child after drain. |
| Execution outcome is uncertain after a crash | Attempt history, ownership generation, external system state, and idempotency key | Reconcile the external effect before retrying. WorkerDock delivery is at least once. |
| SQL template cannot connect | Profile server/catalog/authentication options, secret reference, SQL permissions, TLS validation, and outbound policy | Correct the profile or secret provider and grant only the target operation's required access. Never paste a password into logs. |
| Secret-backed jobs, health checks, or alerts return `SecretUnavailable` | Verify the relevant host registered a production `ISecretVault` adapter, its service identity, reference allowlist, and secret permissions | Install or correct the trusted adapter in that process; the repository's default development provider is unavailable unless explicitly enabled and is not a production integration. |
| HTTP template cannot reach a destination | URL/host policy, DNS, TLS certificate validation, proxy/firewall rules, and egress allowlist | Permit only the approved destination and retry after confirming the remote effect is safe. |
| Archive retention fails | Scheduler identity ACL, configured archive root, free space, manifest/checksum/read-back result | Preserve source rows, resolve storage or permission issue, then retry the same logical batch. Do not hand-delete source data. |
| Upgrade is interrupted | Recovery journal, SQL migration history, backup verification, active release metadata, and service/IIS state | Follow [upgrade and restore](upgrade-and-restore.md); do not delete the journal or rerun blindly. The upgrade script does not auto-resume. |

## Diagnostic boundaries

The portable `tests/DeploymentSmoke.ps1` checks script parsing, configuration shape, and package-manifest integrity handling. It does not provision IIS or prove live SQL Server, OIDC, upgrade/restore, or workload behavior. The in-memory adapter is not evidence of SQL behavior. See [release checklist](../validation/release-checklist.md) for required environment-specific evidence.
