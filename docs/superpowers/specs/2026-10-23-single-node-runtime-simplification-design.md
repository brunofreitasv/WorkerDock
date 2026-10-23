# Single-node WorkerDock runtime simplification

## Intent

WorkerDock is still a draft with no deployed tenant databases. Make its initial deployment understandable and small: one tenant runtime service on one Windows machine, multiple isolated execution children, SQL Server as the durable source of job state, and no Quartz dependency or Quartz tables. Keep the operator's existing jobs, scheduling, alerts, monitoring, audit, and retention capabilities. Optimize for a simple model and fewer database objects without weakening the important execution guarantees.

## Goals and boundaries

- Deploy one runtime service per tenant. It owns schedule evaluation, execution admission, child supervision, alert evaluation and delivery, metrics, and retention. The web application remains a separate management and read surface; the optional Control Center remains separate.
- Keep concurrent execution through several child processes under that one service. Configurable tenant, template, and job limits still apply.
- Keep one-time and five-field cron schedules with the existing explicit time-zone, daylight-saving, pause/resume, and missed-work behavior. Preserve manual runs, bounded retry, stop, uncertain-outcome review, idempotency identities, audit, and safe monitoring.
- Keep alert rules, incidents, webhook delivery attempts, execution timelines and aggregates, and bounded retention/archive behavior.
- Remove Quartz packages, project, hosts, tests, docs, two persistent stores, and projection machinery that exists only to synchronize business state into Quartz.
- Produce one coherent fresh-install SQL schema and matching documentation. Since there are no tenant deployments, no upgrade or data conversion path is required.
- Keep unrelated in-progress changes in the working tree and incorporate their schema and UI changes into the new baseline.

The design does not add multi-machine scaling or automatic failover. An ordinary service or host outage pauses new work; restart resumes from durable SQL. External effects remain at least once where outcomes are uncertain. A separate SQL broker, distributed lock service, or per-template worker service is outside scope.

## Approaches considered

1. **Chosen: one combined scheduler/worker service per tenant.** Poll durable SQL state, materialize due occurrences, claim queued executions, and launch bounded child processes. This has the smallest deployment and persistence model; host failure pauses work until restart.
2. **One scheduler and multiple SQL-claiming workers.** Retains worker scaling but needs cross-process claims, capacity coordination, leases, and additional failure paths that the initial single-machine deployment does not need.
3. **Multiple schedulers and workers.** Adds service failover, but would recreate distributed coordination complexity and work against the primary goal.

## Runtime and persistence model

The runtime is a Windows `BackgroundService` with bounded, independently timed loops for schedules, execution dispatch, recovery, alerts, metrics, and retention. Each loop catches/report its own recoverable failure without permanently stopping unrelated loops; fatal schema/identity/configuration errors stop readiness. The process takes a tenant-scoped named OS mutex so a second runtime on the same machine cannot dispatch the same tenant. Database connections use the existing restricted runtime identity. The web process never executes templates.

`Schedules` is the source of due work. A schedule sweep reads a bounded page, uses the same authoritative evaluator as preview, and transactionally advances each schedule cursor while inserting `Executions`. It preserves the current unique occurrence identity and bounded `Skip`, `Coalesce`, and `CatchUp` recovery rules. There is no trigger copy or schedule outbox. Revisions and cursor checks prevent stale edits from producing occurrences. A small, project-owned evaluator replaces Quartz cron/calendar types and supports only the documented five-field grammar; its DST and sparse-schedule behavior must be proved against the existing schedule cases.

`Executions` is the durable queue. Manual acceptance and scheduled materialization commit a queued row in their business transaction. The dispatcher selects ready rows by `AvailableAtUtc` in bounded batches, then atomically applies the enabled/stop/capacity gates, creates an `Attempt`, changes execution state, and reserves capacity under a short SQL transaction. No SQL transaction spans a child, vault, or network call. The runtime starts one execution child per attempt using the existing `ExecutionSupervisor`, named-pipe protocol, and Windows Job Object containment. A configurable semaphore bounds total children; transactionally checked counts bound template and job concurrency. A child never queries for or claims jobs.

An attempt still has a deadline, ownership generation, heartbeat/lease, and safe outcome. On restart, the service first classifies expired running attempts. It never silently repeats work that might have produced external effects: prove no handler entry before automatic retry, otherwise mark `NeedsReview` unless the pinned policy explicitly permits replay. Stop and shutdown drain or contain children as currently documented. Retried executions remain the same logical execution with a future `AvailableAtUtc`, not a new scheduler trigger. Existing idempotency keys and audit reasons remain stable. `NeedsReview` retains the logical job permit until an explicit operator decision.

Alerts use the same runtime loops. Rule evaluation commits incident and delivery rows directly in SQL; a delivery loop claims due deliveries and uses their existing lease/fencing, bounded retry, pinned sink revision, and stable delivery ID. Monitoring reads jobs, executions, attempts, timeline events, current runtime heartbeat, schedules, alerts, and aggregates directly. Retention operates over those durable records and still protects active work, deduplication windows, and archive integrity.

## Schema simplification

The current fresh-install snapshot has 71 tables, including 30 Quartz tables. The target is roughly 25 tables; the implementation should justify every table by an independent lifecycle, referential requirement, or bounded query. Fewer tables is useful only if the resulting transactions and queries stay clear.

Candidate durable tables:

| Area | Keep or create |
| --- | --- |
| Tenant and audit | `TenantSettings`, `SchemaHistory`, `AuditEvents` |
| Jobs | `Jobs`, `JobRevisions` |
| Work | `Schedules`, `Executions`, `Attempts` |
| Runtime status | one `RuntimeStatus` row for heartbeat, boot identity, readiness, and applied limits |
| Profiles and settings | `ConnectionProfiles`, `ConnectionProfileRevisions`, `IntegrationSettings`, `IntegrationSettingsRevisions` |
| Monitoring | one typed `OperationalEvents` timeline and `ExecutionMetricAggregates` |
| Alerts | `AlertSinks`, `AlertSinkRevisions`, `AlertRules`, `AlertRuleRevisions`, `AlertIncidents`, `AlertDeliveries`, `AlertDeliveryAttempts` |
| Retention | `ArchiveBatches`, `ArchiveBatchItems`, `RetentionRequestTombstones` |

Remove both Quartz table sets entirely. Remove `Outbox` and `ProjectionMaintenance` because execution, schedule, and alert work are already committed in their own tables. Remove `Workers`, `WorkerCapabilities`, `ServiceHealth`, and `PolicyRevisions` in favor of one runtime status row and installed template descriptors. Remove `TemplateCatalog` only after save-time and runtime validation can pin installed template ID/version/digest without its foreign key. Put per-template limits in tenant settings, with versioned edits and local acknowledgement; remove `TemplatePolicies` after equivalent gates work. Derive job permits from an explicit hold flag on `Executions` and transactional counts; remove `JobPermits`. Store manual request identity and expiry on `Executions` with a filtered uniqueness constraint; remove `ManualRequestDedup` while preserving the 24-hour response semantics and purge tombstones. Synchronous command results come from committed business state and audit IDs; remove `Commands` if every current receipt can still be produced. Consolidate `AttemptStatusEvents` and schedule recovery events into the typed operational timeline. Keep schedule scan and alert evaluation cursors on their owning rows rather than standalone tables. Fold egress policy and retention policy into their owning settings rows; record purge decisions in `AuditEvents`. Keep immutable revision tables where accepted work or queued alert deliveries need historical configuration.

These are design targets, not a license to drop a behavior to hit a number. During implementation, if a proposed merge makes transactional correctness or retention unclear, retain one focused table and document why.

Replace the draft migration chain with a coherent fresh-install baseline and regenerate `current-state.sql` from it or remove the redundant snapshot if the migration path alone is clearer. Keep the manifest/checksum and restricted-role model. Delete obsolete Quartz upstream SQL, notices that apply only to removed assets, process probes, fixture SQL, and dead classes/projects after references disappear. Update deployment, architecture, scheduling, execution guarantees, database object, monitoring, alert, retention, test, and setup docs plus relevant UI wording. Remove stale API fields that only described Quartz projection, or map them to actual runtime state where the operator still needs feedback.

## Verification and acceptance

- A clean dependency search finds no Quartz package, namespace, project, runtime schema, migration, configuration key, or active documentation reference.
- Fresh SQL provisioning yields only the designed tables and indexes; schema manifest and snapshot (if kept) match. No tenant runtime starts if schema or tenant identity is wrong.
- Tests cover cron preview/materialization parity, DST gaps and overlaps, sparse schedules, bounded catch-up, restart recovery, manual deduplication, job/template limits, retries, stop, uncertain outcomes, alerts and delivery retry, monitoring, and retention protection.
- A real SQL Server integration run starts the combined service, runs several child processes concurrently, restarts mid-attempt, verifies no silent duplicate external effect, and exercises the read/management API. Portable tests alone do not establish this.
- Build, applicable unit/integration tests, and frontend checks pass; deployment docs describe exactly one runtime service per tenant and its configurable concurrent-child limit.

## Builder decisions

Choose class/project names, polling intervals, and exact indexes from measured query patterns. Prefer SQL Server UTC for durable decisions. Preserve existing safe-code/API semantics where useful, but remove fields and abstractions that only represented projection lag or distributed capacity. Keep the current Windows child containment and secret boundary. Do not expand the product surface while simplifying it.
