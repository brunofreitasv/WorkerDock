# WorkerDock

<img src="frontend/packages/ui/src/brand/workerdock-mark.png" alt="WorkerDock logo" width="64">

WorkerDock is a self-hosted platform for defining, scheduling, executing, and monitoring SQL Server and HTTP jobs. Each tenant has a web application, a SQL Server database, and one Windows runtime service. The optional Control Center provides a directory and health view for independently deployed tenants.

The project is currently in **release validation**. See [release status](#release-status) before planning a production deployment.

## Capabilities

| Area | What WorkerDock provides |
| --- | --- |
| Job management | Guided creation, disabled drafts, versioned trusted templates, and explicit activation. |
| Execution | Manual and scheduled runs, per-job and host capacity limits, timeouts, retries, and stop controls. |
| Operations | Execution history, monitoring, alerts, audit records, and retention settings. |
| Tenant boundaries | A separate database and runtime service per tenant; the Control Center does not execute tenant jobs. |

## Architecture

```mermaid
flowchart LR
    Operator["Operator"] --> Web["Tenant web app"]
    Web <--> Database[("Tenant SQL Server database")]
    Runtime["Tenant Windows runtime service"] <--> Database
    Runtime --> Child["Execution child"]
    Child --> Target["Approved SQL Server or HTTP target"]
    Center["Optional Control Center"] -. "links and health" .-> Web
```

The runtime starts bounded child processes to run trusted templates. It reads queued work directly from the tenant database; no external message broker is required. See [architecture](docs/reference/architecture.md) and [execution guarantees](docs/reference/execution-guarantees.md) for details.

## Get started

| Goal | Guide |
| --- | --- |
| Explore the UI without SQL Server | [Run in memory](docs/tutorials/run-in-memory.md). Data is temporary and jobs do not execute. |
| Run jobs locally with SQL Server | [Set up SQL Server](docs/how-to/setup-sql-server.md), then [run the tenant and runtime](docs/tutorials/run-with-sql-server.md). |
| Prepare a Windows deployment | [Deployment guide](docs/how-to/deploy-production.md) and [tenant configuration](docs/how-to/configure-tenants.md). |
| Work on the code | [Contributor guide](CONTRIBUTING.md) and [development checks](docs/validation/development-checks.md). |

The [documentation index](docs/index.md) links to task guides and technical references. The tenant SQL schema is defined by the checksummed manifest in [database/tenant](database/tenant); `current-state.sql` is for a new, empty database only.

## Release status

WorkerDock is not yet validated for production use. Production secret-vault adapters are deployment-specific and are not included in this repository. Portable builds and tests do not establish readiness for live SQL Server, Windows/IIS, Entra ID, backup and restore, or workload requirements. Review the [release checklist](docs/validation/release-checklist.md) and [release evidence](docs/validation/release-evidence.md) before deployment.

WorkerDock uses the [MIT License](LICENSE). See [SECURITY.md](SECURITY.md) for security reporting.
