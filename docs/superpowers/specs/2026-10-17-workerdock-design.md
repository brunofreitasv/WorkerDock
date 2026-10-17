# WorkerDock design specification

Date: 2026-10-17 (America/Sao_Paulo)

Status: Revised to use Quartz.NET with SQL Server for scheduling and dispatch and approved by the user on 2026-10-17. Implementation planning is authorized; implementation remains gated on plan review and execution-method selection.

## 1. Purpose and agreed scope

WorkerDock is an open-source, self-hosted platform for creating, scheduling, executing, monitoring, and auditing general-purpose jobs. Operators configure jobs through a React dashboard using reusable templates. Developers extend execution through trusted .NET packages without modifying scheduler logic.

The required infrastructure is Windows/IIS and SQL Server. Redis, RabbitMQ, external message brokers, hosted control services, and telemetry collectors are not required. Entra ID is an optional configured authentication dependency; job destinations and production secret providers are deployment-specific integrations. Self-contained means that core scheduling, execution, history, alerts, and the dashboard run with the application's own services and SQL Server; SQL Server itself remains a separately installed prerequisite.

Initial design and load-test target: up to 10 independent tenants, 100 configured jobs per tenant, and 10 concurrent executions per tenant. These are validation targets, not guaranteed capacity or fixed product ceilings. Actual throughput depends on job duration, process startup, database latency, and host resources. Publish measured results with the test hardware and workload.

Built-in templates are SQL Server and HTTP. Job creation, edits, schedules, connection profiles, and concurrency changes are database operations: they do not install Windows services, deploy code, or restart workers. New worker-service installations add host capacity; installing or updating custom template code requires a controlled worker deployment.

Milvaion informs the separation of scheduling from execution, reusable worker contracts, operator monitoring, and observability. WorkerDock uses its own implementation, branding, and UI. Quartz.NET's SQL Server persistent store provides durable scheduling and worker dispatch without the external messaging infrastructure described by Milvaion [R1, R19]. WorkerDock adds product policies and execution supervision around Quartz rather than implementing a competing scheduling engine or worker queue.

## 2. Architecture and deployment boundaries

### 2.1 Independent tenants and central directory

A tenant is an independently deployed customer/security context with its own:

- IIS application and application pool, application URL, authentication settings, and cookie-protection keys.
- SQL Server database and database principals.
- Scheduler Windows service and one or more worker Windows services.
- Vault provider configuration, vault identity, connection profiles, network policies, jobs, logs, audit, and alerts.
- File-system directories, service identities, permissions, and upgrade lifecycle.

Dedicated databases may share a SQL Server instance, and tenants may share a Windows host. Windows-host administrators and SQL Server administrators remain trusted infrastructure administrators. Separate processes and databases do not isolate tenants from an administrator of their shared infrastructure. Customers requiring that boundary use separate hosts/instances and administrative domains.

The central control center is a separate IIS application with its own small SQL Server database and vault configuration. It registers an existing tenant's display name, expected instance identity, HTTPS URL, and health-credential reference. Registration does not deploy or reconfigure the tenant. The center lists tenant health and links to tenant dashboards; it neither proxies their management sessions nor accesses their databases or vaults.

The center's health credential authorizes only a minimal tenant health endpoint. It is not a management credential. A central outage does not interrupt tenant operation or direct access to tenant URLs.

### 2.2 Runtime components

| Component | Responsibility | Allowed operational dependencies |
| --- | --- | --- |
| Tenant web application | React assets, management APIs, read APIs, OIDC sessions, schema validation, configuration and audit transactions | Tenant SQL Server, configured identity provider; no execution of job handlers |
| Tenant scheduler service | Runs the Quartz schedule/control scheduler; projects configuration and dispatch intents into Quartz; applies occurrence/recovery policies and operational maintenance | Tenant SQL Server; alert vault credentials and approved webhook/archive destinations |
| Tenant worker service | Runs a node of the Quartz execution cluster; supervises isolated attempts, ownership fencing, stop requests and sanitized results | Tenant SQL Server and local execution host |
| Execution child process | One attempt or diagnostic execution, template validation, secret resolution, SQL/HTTP/custom handler execution | Tenant vault and explicitly permitted job destinations |
| Central web application | Tenant registry, central audit, minimal health probes, navigation | Central SQL Server, its identity provider/vault, registered approved health URLs |

Each execution runs in a child process started from installed WorkerDock binaries. It is not a new Windows service or deployment. The worker supervises children and remains responsive when a handler hangs. Long-running scheduling/execution lives outside IIS because its process lifecycle includes recycling and idle termination [R2].

Each tenant has two logical Quartz schedulers in its existing database: `WorkerDock.Schedule` uses `WDS_QRTZ_` tables for short schedule/control jobs; `WorkerDock.Execute` uses `WDE_QRTZ_` tables for execution dispatch. These are table sets, not extra databases or services. Only scheduler services start the first scheduler; only worker services start the second. This prevents a scheduler node from acquiring a long-running worker job and separates maintenance capacity from job capacity. Each cluster uses a stable scheduler name and unique node/boot identities; multiple scheduler instances are possible for resilience, although one is sufficient initially.

The scheduler service publishes execution triggers through a second, write-only Quartz scheduler handle configured with `ZeroSizeThreadPool`; that handle is never started. Quartz documents this write-only use [R19]. The web application writes WorkerDock commands and audit records, not Quartz tables. No Quartz dashboard, HTTP management endpoint, remote agent or additional inbound worker port is required. All user access remains through WorkerDock's policies.

### 2.3 Code organization

Names below define project responsibilities; folders within Infrastructure and Runtime remain distinct modules rather than requiring a project for every adapter.

| Project/module | Responsibility |
| --- | --- |
| WorkerDock.Domain | Job policies, execution states, scheduling and lifecycle invariants; no database or hosting dependencies |
| WorkerDock.Contracts | Template metadata/handler contracts, execution context, secret facade, safe logging and alert-sink interfaces |
| WorkerDock.Application | Use cases and persistence/coordination interfaces; depends on Domain and Contracts |
| WorkerDock.Infrastructure | WorkerDock SQL Server persistence and business-state transactions, secret adapters, outbound policy, audit, authentication and telemetry adapters |
| WorkerDock.Quartz | Quartz configuration, SQL stores, schedule/calendar adapter, idempotent projection, dispatch wrapper and lifecycle integration; depends on Application/Contracts |
| WorkerDock.Runtime | Worker supervision, attempt ownership, restricted IPC and Windows process containment using application interfaces |
| WorkerDock.Templates.SqlServer | SQL template schema, validation, connection builder, handler and result classification |
| WorkerDock.Templates.Http | HTTP template schema, validation, substitution, destination policy integration and handler |
| WorkerDock.Web | Tenant IIS composition root and tenant read/management endpoints |
| WorkerDock.Scheduler | Windows-service/foreground composition root for schedule/control Quartz jobs and the execution-store publisher |
| WorkerDock.Worker | Windows-service/foreground composition root for the Quartz execution node and worker supervision |
| WorkerDock.Executor | Child-process composition root loading installed, explicitly registered trusted templates |
| WorkerDock.ControlCenter | Central registry, central read/management APIs and health-probe composition root |
| WorkerDock.DatabaseMigrator | Explicit installation/upgrade tool for versioned SQL scripts using migration credentials |
| Frontend workspace | Separate tenant and central React entry points with shared UI, localization and API utilities |

Templates depend on Contracts and their client libraries, not Quartz or scheduler implementation. Hosts compose modules through dependency injection. The central application does not acquire a tenant persistence dependency or reuse tenant authorization as a bypass. EF Core implements ordinary WorkerDock reads/configuration persistence; parameterized SQL/stored procedures implement business-state invariants. Quartz owns its store's acquisition, locking and cluster bookkeeping. Use public `IScheduler` APIs, not direct edits to Quartz rows, a replacement `IJobStore`, or a fork of Quartz. Versioned SQL scripts are the authoritative schema migration source; there is no competing automatic EF or Quartz schema-creation path.

## 3. Platform baseline

- .NET 10 LTS, ASP.NET Core, C#, with compatible pinned stable package versions and current security patches [R3].
- Quartz.NET 4.5.1 is the researched baseline as of this revision, with SQL Server ADO.NET persistence, clustering and cluster-scoped execution groups. Pin matching package versions and validate their APIs/schema against the chosen release before implementation; upgrades require the same regression checks [R20-R22]. Quartz retains its Apache-2.0 license and notices [R23].
- SQL Server 2022 minimum; default schema compatibility target 160 [R4].
- Windows Server 2022 or newer, x64, IIS and the appropriate ASP.NET Core Hosting Bundle. Windows 11 supports local development. Validate the supported OS/package matrix during implementation planning.
- React, TypeScript, Vite, Material UI and TanStack Query. Production serves compiled assets from ASP.NET Core/IIS and does not require Node.js [R5-R7].
- WorkerDock source is released under MIT, with copyright attributed to WorkerDock contributors, contribution guidance and a security-reporting policy [R8]. Dependency licenses and notices remain applicable.

Installation supports a single Windows host with local or remote SQL Server. Workers may later run on additional Windows hosts with access only to their tenant database and permitted destinations. Local development runs the scheduler and supervisor as foreground processes without service installation, while retaining the same execution logic and child-process containment.

## 4. Logical data model

Every tenant database stores one tenant context. Tenant selection is deployment configuration, never an arbitrary browser-supplied database selector. Use opaque identifiers, UTC datetime2 timestamps, SQL rowversion for optimistic configuration edits, explicit foreign keys and bounded payload sizes.

| Record group | Essential data and invariants |
| --- | --- |
| Tenant settings | Stable instance identity, nonsecret settings, provider references, operational policies |
| Template catalog | Identifier/version, metadata/schema, descriptor digest, compatibility and enablement; registered by trusted worker packages |
| Template policies | Tenant-wide limit per template identifier across versions; desired/effective policy revision and node acknowledgements |
| Jobs | Name, current revision, enable/archive state and live per-job concurrency limit |
| Job revisions | Immutable template/version, configuration, profile identifier, timeout, retry/recovery and overlap policies |
| Schedules | Original cron text/dialect, zone, schedule revision, pause state, occurrence cursor, missed-work policy and Quartz projection revision; next-run display derived through the schedule adapter |
| Executions | Job revision, resolved profile revision, trigger kind, scheduled instant, request/occurrence key, idempotency key, status, availability time and correlation ID |
| Attempts | Attempt number, worker identity, ownership generation, lease expiry, start/deadline/end, result/error classification and stop state |
| Workers/capabilities | Service installation identity, current boot identity, heartbeat, local capacity and exact supported template versions |
| Service health | Scheduler/service boot identity, heartbeat and safe readiness observations |
| Job permits | Logical per-job admission/ordering permits retained across retry waiting and review holds; no duplicate template-capacity ledger |
| Projection outbox | Durable schedule/dispatch/control intents, entity revision, deterministic Quartz keys, payload digest, publication/reconciliation state and bounded retry time |
| Quartz-owned scheduling data | Separate schedule/control and execution table sets for jobs, triggers, calendars, fired triggers, locks and cluster check-ins; upstream schema and lifecycle |
| Connection-profile revisions | Nonsecret server/catalog/authentication options and credential references; no password or full connection string |
| Execution events/logs | Ordered status events and safe structured logs with sequence IDs, size limits and retention timestamps |
| Audit events | Actor, UTC time, action, target, correlation, safe before/after projection and outcome |
| Alert rules/incidents/deliveries | Conditions, thresholds, incident identity, suppression/reminders, sink reference and durable delivery attempts |
| Metric aggregates/archive manifests | Bounded aggregate summaries, archive batch identity/checksum, retention progress |
| Schema history | Applied migration identifiers, checksums and timestamps |

The central database contains registry entries, recent health observations, central audit events and its own schema history. It contains no tenant jobs, execution logs, connection profiles or tenant secret values.

An execution pins the job revision and nonsecret connection-profile revision selected at creation. Later edits affect future executions. Secret values still resolve at execution time, allowing credential rotation without job revision changes. Retire referenced profiles/revisions rather than deleting records still needed by retained executions.

Uniqueness includes scheduled occurrence `(ScheduleId, ScheduleRevision, ScheduledForUtc)`, attempt number per execution, and manual trigger request key per authenticated actor/job (or anonymous session/job). A reused manual key with different input is rejected. Within a 24-hour deduplication window after first acceptance, the same key returns the original execution/response for retries of the same request. Preserve a compact request record for that full window even if history retention is shortened. Outside the published window reuse may create a new execution; clients must not reuse old keys. Deliberate new manual runs receive new keys. This request-deduplication window is separate from the execution idempotency key, which remains stable across all its attempts.

Logical execution states are Queued, Running, RetryWaiting, NeedsReview, Succeeded, Failed, Stopped, TimedOut and Skipped. Held by disablement, waiting for capacity and missing capability are reasons attached to a nonterminal state, not successful outcomes. Attempts separately record Claimed/Starting, Running and a final success/failure/timeout/stop/abandonment outcome. Skipped executions have no attempt. Terminal execution state and remote-effect certainty are separate fields; NeedsReview remains nonterminal until reconciled.

Index WorkerDock's outstanding projection intents, per-job admission backlog, active ownership expiry and history by parent identity/time. Quartz owns indexes for trigger acquisition. Monitoring queries use pagination and time-window limits. Foreign keys within WorkerDock and retention ordering protect active work and referenced revisions; do not create cross-schema foreign keys into Quartz internals. Store only opaque identifiers, revision/generation numbers and safe correlation data as string-valued Quartz job data. Job configuration, secret references, SQL text and request bodies remain outside Quartz data and diagnostics.

## 5. Coordination and delivery guarantees

### 5.1 Ownership of scheduling and dispatch

| Concern | Owner |
| --- | --- |
| Persistent timers, trigger acquisition, database scheduling locks, execution-group capacity and cluster failover | Quartz SQL Server stores |
| Cron parsing and candidate calculation | Quartz, with the explicit WorkerDock policy calendar in section 6.1 |
| Job configuration/revisions, occurrence identity, missed-work policy, logical execution history and audit | WorkerDock application transactions |
| Per-job ordering across retries, retry safety decisions and uncertain-outcome review | WorkerDock business policies |
| Child lifetime, durable operator stop and stale-result rejection | WorkerDock supervisor and attempt ownership fence |
| Transfer of committed business intent into Quartz | WorkerDock transactional outbox and idempotent `IScheduler` projection |

Workers do not poll a WorkerDock work table and perform their own queue-claim algorithm. Quartz selects a persisted dispatch trigger and invokes a generic `ExecuteTemplateJob` wrapper. The wrapper validates the referenced execution and attempt authorization, starts its isolated child and stays awaited until that child's exit and outcome handling. Returning immediately after spawning a child would release Quartz capacity too early and is prohibited. Configure bounded acquisition with no application prefetch.

The schedule/control cluster runs short occurrence-dispatch, projection/reconciliation, recovery-review, alert and retention jobs. Give maintenance jobs bounded batches and execution times; never put user handlers on this cluster. Quartz job keys and trigger keys identify immutable WorkerDock entities/revisions rather than display names. Enable clustering on every started node sharing a store and use the same compatible configuration/schema [R21].

### 5.2 Atomic intent and idempotent publication

A management command commits its WorkerDock mutation, audit event and projection intent together. It returns Accepted with a command/execution ID and visible pending/applied/failed state. Quartz API calls happen afterward. Sharing a SQL database does not make an application transaction and a Quartz store transaction atomic; the design does not depend on ambient distributed transactions.

The publisher processes durable intents through `IScheduler`, using deterministic job/trigger keys, immutable identity payloads and a payload digest. After publication it records acknowledgement in WorkerDock. A crash before publication leaves an intent to retry; a crash after publication leaves a replay that recognizes the same key. A mismatched existing payload is an integrity error, never silently overwritten. Schedule edits project in entity-revision order and recheck current desired state, so an old command cannot restore a disabled or replaced schedule. Old-revision triggers are removed through Quartz APIs and rejected by the dispatcher even before cleanup completes.

An occurrence dispatcher commits the schedule cursor, deduplicated execution records, safe events and dispatch intents in one WorkerDock transaction. The uniqueness key `(ScheduleId, ScheduleRevision, ScheduledForUtc)` makes repeated/recovered dispatcher calls harmless. Execution publication uses a stable delivery identity tied to the execution and authorized attempt ordinal. A consumed trigger may already be absent when its publication acknowledgement is retried: reconcile the WorkerDock attempt/terminal state first rather than interpreting absence as proof that the work never ran.

The execution wrapper atomically checks the current authorization, enabled/archive/stop state, compatible capability, policy revision and ownership generation before creating an attempt. Duplicate deliveries cannot start the same authorized attempt twice. A completed attempt is a no-op; a live owner prevents a second launch; an expired owner follows section 5.5 and is never silently restarted as a fresh attempt. A crash after authorization but before launch remains an uncertain starting attempt unless the supervisor can prove no handler ran.

Use a short transaction-owned application lock for WorkerDock business transitions at the initial workload, acquired before mutable business rows [R9]. It is not Quartz's trigger-acquisition lock. Never call Quartz, a vault, a network endpoint or a child inside that transaction, and never acquire application locks while directly manipulating Quartz rows. Use bounded timeouts, checked lock results and deadlock retry without replaying external effects. Conditional heartbeat updates cannot revive expired ownership. Validate application transactions and Quartz integration under the supported SQL isolation settings [R10].

A bounded Quartz control job retries outbox publication and reconciles incomplete publication/recovery states after startup and periodically. It checks Quartz through public APIs and WorkerDock execution state, repairs missing eligible dispatches and exposes stale/error projections. It does not replace Quartz acquisition with a second due-work polling engine. Durable business state is authoritative for whether an external attempt is authorized; Quartz state is authoritative for trigger acquisition. Listeners supply diagnostics/wake-up hints, not the only durable record of completion.

### 5.3 Concurrency, capability and ordering

Use one Quartz execution group per template identifier, shared by all versions, with `ExecutionLimitScope.Cluster`. Configure `AcquireTriggersWithinLock = true` explicitly: Quartz's default SQL acquisition permits transient overshoot, whereas locked acquisition serializes capacity admission. Quartz owns the fired-trigger reservation ledger and releases it on wrapper completion or cluster recovery [R22]. There is no second WorkerDock template-slot table. A configured SQL-template limit of 1 remains one across all worker services; increasing it to N needs no new service deployment.

Each worker's Quartz pool has a configured installation ceiling for local children, plus a live WorkerDock admission limit no higher than that ceiling. Live changes within the ceiling require no restart; increasing the process's installed ceiling is a host configuration change. The supervisor cannot start a child over its current local limit. Deferred wrappers return promptly without consuming a business attempt, and the publisher issues a new delivery only after the relevant hold clears. Worker heartbeats/control polling remain responsive outside the Quartz execution pool even when every slot runs an infinite-timeout job.

Template-limit changes store desired and effective revisions. Close the affected template's application start gate transactionally, ask active worker nodes to enter acquisition standby, apply the new Quartz limit snapshot and acknowledge the revision, then reopen the gate and resume acquisition. Standby does not terminate existing children. Wrappers recheck the gate before launch; nodes joining/rejoining must load the effective revision before starting acquisition. An unavailable node cannot be counted as updated: keep the change visibly pending until it acknowledges or its ownership is fenced and Quartz has recovered its reservations. No restart is required, but policy propagation is not instantaneous. On a decrease, existing work drains; no new attempt starts until capacity is below the new limit. Zero pauses admission. The gate protects the interval in which node configurations differ.

The first release uses a common installed template/version capability set across a tenant's execution cluster. Only nodes matching the approved catalog digest enter acquisition. Both built-in templates are installed in the generic worker. A custom package update drains/updates the affected pool before activating the new catalog; adding jobs never changes that catalog. Heterogeneous specialized worker pools and routing are deferred, avoiding unsupported-template acquisitions masquerading as failed jobs.

WorkerDock keeps logical per-job permits because Quartz's per-job nonconcurrency attribute alone does not express a configurable N or ownership across separate retry triggers. Allocate a permit when a logical execution is admitted for dispatch, retain it while waiting for Quartz capacity, retry backoff or NeedsReview, and release it only on finalization/reconciliation. Executions waiting behind the permit remain a visible bounded policy backlog; they are published when the predecessor releases admission. Workers never claim this backlog directly.

Queue/skip policies use an exclusive permit; allow uses shared permits up to the live job limit. An exclusive permit cannot overlap shared holders, and later shared work cannot bypass an earlier exclusive waiter. Skip records a skipped occurrence if the job already has outstanding nonterminal work. Backlog overflow records a skipped result. Lowering a job limit blocks new logical admissions but does not block existing holders' retries; they still obey Quartz template and host limits. Retry waiting occupies no Quartz execution slot.

Map execution priority to Quartz trigger priority. Its ordering includes trigger due time, so do not promise a globally strict priority queue. Bounded aging may raise pending priorities through `IScheduler` on a maintenance cadence, without moving future retry deadlines earlier. Per-job admission preserves serialized order; across independent jobs there is no strict FIFO or starvation-free latency guarantee. Monitor queue delay and saturation.

### 5.4 Attempt ownership and cancellation

Quartz cluster check-ins detect failed scheduler nodes and reclaim their Quartz reservations. WorkerDock retains a separate attempt ownership fence solely to control child lifetime, durable stop and authoritative outcomes; it is not a second dispatch lease. Proposed application defaults are a 10-second heartbeat and 60-second attempt lease, checked against SQL Server time with generation, boot identity, active state and unexpired-lease predicates.

Quartz timing uses host clocks, so synchronize clocks on scheduler, worker and database hosts; use a supported skew budget and validate clock drift operationally. Configure and test Quartz check-in/recovery timing separately from the application fence. Lease expiry alone must not cause manual deletion of a Quartz reservation or imply that cluster recovery has completed.

The supervisor maintains a conservative monotonic local deadline based on acknowledged renewal and request elapsed time. Before losing ownership, it stops admission, requests cancellation and terminates children within the last acknowledged lease where the OS remains responsive. Infinite execution timeout does not disable ownership deadlines. An unresponsive host can outlive them, as described below.

Completion commits the sanitized outcome/events and either a retry dispatch intent or finalization/job-permit release using an ownership-checked transaction. Repeated completion submissions are idempotent; stale owners cannot overwrite authoritative state. The wrapper then finishes and Quartz releases its execution reservation. A crash after the business commit but before Quartz completion produces a harmless wrapper replay. If outcome persistence fails after effects, report uncertainty rather than confirmed success.

Quartz interruption is a cooperative signal, not remote rollback or process termination [R24]. The persisted WorkerDock stop request and supervisor protocol remain authoritative; an interrupt of the local wrapper can accelerate cancellation. Stop handling does not depend on an HTTP call to a particular worker or on the scheduler service being available.

### 5.5 Recovery, retries and the external-effects boundary

Enable Quartz recovery for idempotent occurrence/control dispatchers and the guarded execution wrapper. A recovered Quartz invocation is infrastructure redelivery, not authorization to repeat a SQL/HTTP side effect. The wrapper always reads WorkerDock's attempt generation and outcome before acting. A recovery observation of an expired attempt routes through the same transaction as the periodic recovery reviewer, so only one can classify it.

WorkerDock owns business retry classification, total-attempt budget, backoff/jitter, stop suppression and uncertain-outcome review. Set `RetryPolicy.None` explicitly on execution dispatch triggers and do not request immediate refire from the wrapper; schedule explicit one-shot dispatch triggers for approved future attempts instead [R25]. Publication retries and recovered wrapper calls do not consume a business attempt unless a new attempt is authorized and started. Do not sleep inside a Quartz job during backoff. Execution dispatch triggers retain overdue work rather than skipping it; use explicit ignore-misfire behavior and rely on WorkerDock state guards to reject obsolete deliveries.

Recovery marks an expired attempt abandoned without editing Quartz tables. If the job explicitly permits replay of uncertain outcomes, create a bounded retry intent with the same execution idempotency key; dispatch still waits for Quartz capacity. Otherwise the execution becomes NeedsReview, retains its logical job permit and prevents accidental overlap under queue policy. A manager can reconcile, cancel or explicitly authorize retry; every decision is audited. Missing/errored Quartz triggers without a corresponding completed attempt are visible reconciliation failures, never silently counted as success or discarded.

A durable operator stop always suppresses automatic replay, including during lease recovery. If termination cannot be confirmed, retain NeedsReview with the stop-request marker until reconciliation; do not label a still-unconfirmed process terminated. Releasing an unresolved job permit requires an audited management decision acknowledging possible external overlap.

At-least-once here means eligible work can be redelivered after an interruption. Finite retry limits, permanent failures, manual cancellation, and review holds bound automatic delivery; eventual successful execution is not guaranteed. WorkerDock does not claim exactly-once effects.

An external SQL commit or HTTP action can succeed before the worker records completion. Ownership tokens protect WorkerDock's records, not external systems. An abandoned process or remote request may continue after its lease expires; admission limits therefore bound authorized attempts, not all possible lingering external operations. Strict external deduplication or mutual exclusion requires a target-side idempotency key/transaction/lock. SQL transaction mode alone does not solve uncertain commit acknowledgement, and an HTTP idempotency header only works if the destination honors it.

When SQL Server is unavailable, stop new admission and scheduling. Existing supervisors follow lease-loss handling. Do not report an unpersisted result as confirmed. Recovery and alerts resume when persistence is available.

## 6. Scheduling and lifecycle semantics

### 6.1 Time and cron

Support one-time schedules and five-field cron at minute precision: minute, hour, day of month, month and day of week. The initial supported grammar includes numeric fields, lists, ranges and steps; Sunday is 0 or 7. If day of month and day of week are both restricted, both must match; explain this in the editor. Reject unsupported extensions rather than interpreting them differently across components. Keep the original expression and explicit WorkerDock dialect with each schedule revision; never infer a dialect from an arbitrary pasted string.

Persist a canonical IANA zone ID; the backend validates/resolves it on Windows and supplies the available-zone list. Persist instants in UTC. One-time input in a nonexistent local time is rejected; ambiguous input requires selecting an offset. For recurring schedules, nonexistent local occurrences are skipped, and repeated local occurrences run once at the earlier UTC instant. This applies to interval expressions too.

Use Quartz's `CronExpression` with explicit five-field `CronFormat.Unix` parsing and a native `CronTrigger` for each recurring schedule revision. One-time schedules use native simple triggers. Quartz 4.x combines restricted day fields by union, shifts missing fixed wall times to the gap end and can fire interval schedules through both repeated hours [R11]. Those defaults do not override the approved WorkerDock semantics.

A versioned WorkerDock policy calendar attached to the cron trigger filters Quartz candidates: require both restricted day fields to match, reject gap-shifted instants that do not match a nominal wall-clock occurrence, and reject the later offset of an ambiguous wall-clock time. Reuse Quartz expression matching on nominal local calendar values and separate day-field expressions to apply the intersection; do not implement another cron parser. The calendar's next-included calculation must advance strictly using bounded Quartz candidate searches, not an unbounded minute scan. A real gap-end match remains eligible even if a nonexistent earlier occurrence mapped there. Register its versioned nonsecret data with Quartz's System.Text.Json calendar serializer extension [R26].

Validation, preview, dispatch and bounded catch-up enumeration use the same filtered Quartz trigger calculation. Persisted calendar round-trip and DST/AND fixtures are mandatory early integration tests, including interval expressions and non-hour transitions. Reject impossible/out-of-range schedules with a useful message instead of spinning. Browser summaries are explanatory only; server previews are authoritative. Refresh future trigger/calendar projections after time-zone rule updates without rewriting historical instants. This adapter is a compatibility policy around Quartz, not a separate timer or acquisition engine.

### 6.2 Missed work and controls

| Action/policy | Normative behavior |
| --- | --- |
| Missed schedule: skip | Default. Skip occurrences older than the configured lateness tolerance and record the skipped range/count |
| Missed schedule: run once now | Coalesce eligible missed occurrences into one execution associated with the latest missed occurrence; record the coalesced range |
| Missed schedule: catch-up | Create at most the configured count within the configured age window, oldest first; record overflow skips and advance the cursor atomically |
| Intentional pause | Suppress future schedule triggers; queued work and manual run-now continue. Resume starts with future occurrences and does not replay the paused interval |
| Disable job | Suppress all new triggers and claims, hold queued/retry-waiting work, allow running work to finish. Re-enable releases held work and resumes future scheduling |
| Run now | Durable manual execution using the current revision, subject to capacity/overlap and management authorization |
| Edit/duplicate | Edit creates a revision. Duplicate creates a new disabled job with copied nonsecret configuration/references for review; it does not copy execution history |
| Delete | Confirm and archive the job, suppress triggers and cancel unstarted work; running attempts finish without scheduling further retries. Retain audit/history according to policy |

Normal short trigger delay is not a missed occurrence: a configurable lateness tolerance, initially 60 seconds, distinguishes it from downtime. Age/count bounds are required for catch-up; already materialized work is not retroactively dropped by missed-schedule policy. Schedule delay metrics measure both trigger lateness and execution-capacity wait separately.

Use explicit `FireAndProceed` misfire handling on recurring cron schedule triggers and `FireNow` on one-time schedule triggers to wake the occurrence dispatcher after delay [R27-R28]. A misfire wake-up is not itself a scheduled occurrence: the durable WorkerDock cursor applies skip/coalesce/bounded catch-up to the filtered occurrence sequence. Native Quartz misfire behavior alone cannot express that full policy. Bound enumeration by time range, count and evaluation cost; record skipped ranges without allocating unbounded execution rows. Recovery/startup reconciliation closes a cursor gap if a trigger was consumed before the dispatcher committed. The cursor starts at schedule activation, not at eventual projection time, so delayed publication also obeys missed-work policy.

Pause, disable, resume and schedule edits commit their business state/cursor boundary and projection intent atomically. Apply Quartz pause/resume/reschedule through the publisher; guard dispatch and child admission against the committed state while projection is pending. Resume deliberately advances past the paused interval. Queued executions already accepted remain governed by the table above. A stale trigger cannot bypass disablement simply because its cleanup has not reached Quartz yet.

Retry policy contains maximum total attempts, exponential base delay, maximum delay, jitter and retryable outcome classification. Automatic retry and replay of uncertain effects require explicit job policy. Configuration/validation errors are permanent. Backoff does not occupy a template/worker execution slot. A manager-authorized retry retains the execution's revision and idempotency key; run-now is a new execution and key.

### 6.3 Timeouts, cancellation and process containment

Timeout is either a positive duration or explicit No timeout; represent infinity as absence of an execution deadline, not zero accidentally accepted as immediate expiry. A finite attempt deadline starts when the attempt is claimed, so startup is bounded too. SQL/HTTP command timeout may inherit this setting, be shorter, or be unlimited only when no outer finite deadline constrains it. Infinite handler duration does not make process startup, IPC, network connection establishment, lease renewal or shutdown waits unbounded.

The worker starts a child in a Windows Job Object with kill-on-close, prevents breakaway and attaches containment before allowing handler execution. Start/handshake failures must not run an uncontained handler. Named-pipe IPC uses restrictive ACLs, per-attempt identity/nonce, bounded messages and a documented protocol. Pass only nonsecret execution descriptors/references, never secret values in command-line arguments. Child containment is for operational control, not a sandbox for hostile .NET code. Job Objects can terminate associated process groups; .NET cancellation is cooperative [R12-R13].

| Control/state | Behavior |
| --- | --- |
| Stop requested | API durably records actor/reason and suppresses retries before acknowledging. Supervisor polls control state and signals cancellation |
| Graceful stop | Allow a configurable grace period, initially 30 seconds; confirm local exit before releasing active capacity normally |
| Force stop | After explicit impact confirmation, terminate only the attempt's contained process tree; record local termination separately from remote outcome |
| Unreachable worker | Display Stop unconfirmed until the supervisor acknowledges/termination is observed or recovery records abandonment; API acceptance alone is not termination |
| Finite timeout | Request cancellation, then enforce process termination after the bounded grace period. Record timeout, local termination and any uncertain external result |
| Infinite timeout | Continue until completion, operator stop, lease loss, shutdown or process failure; long-running alerts remain available |

Completion and stop requests serialize through the coordination protocol. If completion commits first, stop returns already finished. If stop commits first, later completion records what the handler reported but the execution is finalized as stopped, with effects possibly completed; it is never automatically retried. A stop also cancels unstarted/waiting attempts. Deliberate manager retry is a separate audited action.

The UI separates lifecycle status from outcome certainty: Stopped or TimedOut may have Remote outcome unknown. Terminal local status is not a rollback claim. Graceful service shutdown stops claims, drains for a bounded interval, then cancels/terminates remaining children; interruptions follow the configured recovery policy unless an operator stop was recorded.

## 7. Authentication, authorization and tenant access

This section supersedes the early conversation's tenant-membership requirement. There is no additional membership gate for an authenticated dashboard viewer.

| Configuration | Dashboard, monitoring, safe logs, audit | Management APIs and panel |
| --- | --- | --- |
| No authentication or authorization supplied | Anonymous | Anonymous |
| Entra ID configured, no required management role | Any valid authenticated user | Any valid authenticated user; no role check |
| Entra ID configured with required management role | Any valid authenticated user, including users without roles | Authenticated user must have the configured role |
| Role without authentication, or partial/invalid configured authentication | Startup/readiness configuration error | No fallback to anonymous mode |

Valid authentication requires the configured allowed issuer/directory and audience, signature and lifetime validation. A token for another tenant application/audience is not accepted. Support either a shared Entra directory or independent Entra directories. Each deployment retains its own application registration, credentials, cookies and data-protection keys even when the provider is shared. Map role claim names/values explicitly; never infer a management role from being a central administrator.

ASP.NET Core handles OIDC authorization-code sign-in with PKCE and secure HttpOnly host-scoped cookies. Identity-provider tokens stay on the server rather than browser storage. HTTPS, session expiry, sign-out and protected key storage are required deployment settings. The API derives read/manage capabilities; React uses them for presentation but never authorizes an operation itself [R14].

Management covers every mutation: job/profile/template-policy/settings edits, trigger/retry, pause/disable, stop/force-stop, deletion, alert/retention changes and central registration. Reads of full execution configuration, SQL text, request bodies, connection profiles and vault configuration also require management permission. Viewing monitoring includes safe summaries, durations, histories and sanitized audit differences, not full configuration payloads. Personal language/time-zone display preferences are local user preferences.

Use the same read/manage policies on endpoints, not just page navigation. State-changing browser requests require antiforgery validation and origin checks, including in anonymous mode. GET endpoints do not mutate operational state; enrollment/diagnostic operations are management POSTs. No permissive cross-origin API policy is required because each React app and API share an origin [R15].

Anonymous mode deliberately permits anyone able to reach the deployment to read and manage it. Show the active mode clearly and document network restriction and how to enable Entra/roles. It preserves deployment separation but provides no user-level restriction. The central health endpoint always uses its separate health-only credential and does not inherit anonymous management permissions.

## 8. Secret facade and least privilege

Contracts expose a small asynchronous resolve-only secret facade accepting a logical reference and cancellation. Provider selection/identity is bound by trusted deployment configuration, not a browser-supplied tenant or provider name. Each tenant has independent provider settings and an optional environment-backed development adapter. An adapter for the company's internal library can replace it without changing scheduler or template contracts.

Only nonsecret provider options, identifiers and secret references enter SQL Server. Bootstrap database access should use the Windows service identity where possible; any required bootstrap credential comes from protected deployment configuration, never the application database. Full secret-bearing connection strings are neither job data nor API input/output. Production vault bootstrap authentication is external to job configuration.

Execution children resolve needed job secrets directly. Supervisors and web APIs do not expose a resolve-secret endpoint. The scheduler's alert delivery jobs have a separately scoped identity/reference set for sink credentials; the central application resolves only its registered health credentials. Local process environments and file permissions are isolated per tenant; development environment secrets are not a substitute for a production vault.

Log/audit/telemetry contracts use safe field projections, event codes and bounded values. Redact known resolved secret values as defense in depth; do not rely on regex or redaction alone. Never persist raw driver exceptions, resolved runtime request/response bodies, connection strings, authorization headers or arbitrary child stdout/stderr in diagnostics. Authored nonsecret request-body templates are job configuration and may be stored in protected revisions; secret substitutions resolve only at runtime. Safe error codes map to useful messages and correlation IDs. Custom trusted handlers must honor this contract; third-party code is reviewed and tested before deployment, not treated as untrusted code that the platform can prove harmless.

Connection tests are durable, management-authorized diagnostic executions through installed workers, use named profiles and bounded deadlines, and return sanitized connectivity/authentication/catalog results. They do not accept arbitrary SQL. Diagnostics count against template and host capacity and are audited.

Runtime database principals have only their required tenant data/routine permissions. Normal services do not have schema-change rights. Migration credentials are separate. Execution credentials to target SQL databases are distinct from WorkerDock persistence credentials and receive only the procedure/query permissions the configured jobs need. Ordinary roles cannot update/delete audit events; a restricted retention routine performs eligible purges. Database administrators remain able to alter data; this is append-only application auditing, not an immutable external ledger.

## 9. Extensible templates and built-ins

### 9.1 Contract and deployment lifecycle

Each package supplies a stable identifier, explicit version, display name, description, JSON Schema configuration descriptor, validation constraints, UI hints, sensitive/reference field declarations and handler. Use the JSON Schema 2020-12 subset for object/array/primitive types, properties, required, additionalProperties, enum/const, numeric/string/array bounds, bounded portable patterns, local definitions/references and discriminated oneOf alternatives. Use the same accepted formats/keywords on frontend and backend, with cross-validator fixtures; reject unsupported keywords and remote schema references. Metadata provides localization keys with fallback labels. UI hints select built-in controls, never executable JavaScript/HTML or uploaded components.

API schema validation and client validation provide immediate feedback; template-specific worker validation runs before effects. Worker registration publishes immutable metadata digests and capabilities. A different descriptor with an existing identifier/version is rejected. Jobs pin exact versions; workers only claim versions they advertise. Retiring a version prevents new selections while preserving existing metadata/history and visibly blocking executions if no compatible worker remains.

Installed packages are explicitly registered from trusted deployment locations. Dashboard users cannot upload assemblies or scripts, execute arbitrary code, or install packages. The SDK and conformance examples cover cancellation, idempotency, safe logging, schema evolution and deterministic validation. Installing/updating packages requires draining and updating the affected worker installations; scheduler core stays unchanged.

### 9.2 SQL Server template

Job configuration selects a named connection profile; execution creation pins its current nonsecret revision. Other configuration includes allowed catalog, stored-procedure name or parameterized SQL command text, typed parameters (including nullability, size, precision/scale), optional local transaction and command timeout/inheritance. Catalog selection is constrained by the named profile; arbitrary connection-string overrides are rejected.

A dedicated builder uses Microsoft.Data.SqlClient.SqlConnectionStringBuilder with explicit encryption/certificate-validation settings. Windows-integrated or vault-resolved database authentication can be selected by the profile. Command parameters carry values separately from SQL text; no string interpolation substitutes SQL values or identifiers [R16]. Procedure identifiers are validated as identifiers; stored procedures remain responsible for safe dynamic SQL internally.

Parameter values may be literals or secret references according to schema. The safe default output is completion/affected-row summary, with bounded explicitly selected result handling only when configured. Parameters, returned rows and raw command text are excluded from viewer logs/audit. The management UI can edit command text; there is no generic execute-SQL API. Transactions commit only after successful execution and roll back on a confirmed failure where possible; cancellation or lost commit acknowledgement can still produce an uncertain outcome.

A stable execution key can be mapped to an explicit procedure/query parameter for target-side idempotency. WorkerDock does not inject a target idempotency table or grant elevated database permissions automatically.

### 9.3 HTTP template and common outbound policy

Configuration includes method, URL, query parameters, headers, content type/body, timeout/inheritance, expected success statuses and bounded response handling. Default success is 2xx. Store sensitive headers/tokens as secret references. Named parameter substitution uses URL/query/header/body-specific validation and encoding, not arbitrary expressions. Header values reject CR/LF; substitutions cannot override the validated destination/Host or weaken TLS validation.

Prefer HTTPS. Tenant management defines allowed hosts and ports within the deployment's egress boundary. Explicit internal HTTP permission and approved private address ranges support internal services. Deployment-level denials cannot be relaxed by a job. Reject loopback, link-local, unspecified, multicast and known metadata endpoints, unsupported schemes, URL userinfo and unapproved ports. Validate normalized hosts, all resolved addresses and the actual address used for connection; preserve TLS hostname validation. Revalidate new connections and avoid a second unvalidated DNS resolution. Automatic redirects and ambient proxy use are disabled in the initial release. Domain allowlisting alone does not prevent DNS rebinding [R17].

Reuse this outbound-policy implementation for webhook delivery and central health probes with separate deployment-specific allowlists. Credentials are attached only to validated destinations and are not logged. Changes to a registered URL are management operations that revalidate the destination and credential association.

HTTP retries are governed by the job policy, not an additional hidden client retry loop. Classify permanent versus transient responses; bounded Retry-After may inform retry delay. Even network errors/5xx can follow an external effect, so replay requires the configured safety policy. An optional stable idempotency header preserves the execution key across attempts. Drain/read responses with size limits, and store only configured safe summaries by default; infinite timeout never disables response-size limits.

## 10. Operator UI

Use React/TypeScript, Vite, Material UI with WorkerDock-specific theming, and TanStack Query. The reasons are a familiar extensible component model, reusable accessible administration controls, static IIS deployment and predictable API-state handling. Avoid paid UI-component dependencies. Both applications share UI conventions but have independent runtime configuration and origins [R5-R7].

| Area | Required experience |
| --- | --- |
| Central directory | Searchable tenants, health, last successful/attempted check, stale/unknown distinction and direct dashboard links; authorized registration/edit/removal |
| Tenant dashboard | Current health, recent outcomes, success/failure rates, upcoming work, long-running jobs, worker availability and actionable failures |
| Jobs | Search/filter by name, status and template; next-run information, safe details/history, management edit/duplicate/run/pause/disable/archive |
| Creation wizard | Template, configuration, schedule, execution policy, review; sensible defaults, examples, field descriptions and server validation |
| Schedule editor | Guided controls and cron text, plain-language explanation, server next-run preview and time-zone selection |
| Execution detail | Status timeline, duration, attempts, sanitized logs, retry reasons, stop requests, correlation and remote-outcome certainty |
| Workers/templates | Capabilities, heartbeat, version availability, local capacity and tenant-wide concurrency; show desired/effective limits and pending propagation; distinguish services from job definitions |
| Management settings | Profiles, nonsecret vault configuration, destination policies, alert rules and retention with appropriate protected reads |
| Audit | Searchable safe actor/action/target/time history for all allowed viewers; no configuration-secret leakage |

Provide English and Brazilian Portuguese from the first release, including validation text and dates/numbers. Default display zone is the tenant's configured zone; viewers can override it locally. Always label the selected zone. Users can choose English or pt-BR independently of stored timestamps.

Use responsive layouts, keyboard focus/order, accessible labels/status announcements, non-color-only states, useful empty/error states and confirmations explaining impact. Force-stop confirmation explicitly distinguishes local process termination from remote rollback. No-timeout selection explains capacity remains occupied until completion/stop/interruption.

Visible monitoring screens refresh every five seconds initially, pause polling while hidden and back off on errors. Show last-updated and stale/error state. Use cursor/server pagination, bounded filters and incremental logs. Actions invalidate relevant cached reads. No browser socket backplane or external cache is needed. Read-only users receive no management controls and cannot bypass the same policy through APIs.

## 11. Audit, metrics, logs, alerts and retention

Administrative mutations and their audit events commit in the same tenant transaction; fail the mutation if required auditing cannot persist. Record actor identity or Anonymous/System, action, target, timestamp, correlation and sanitized before/after projections. Worker lifecycle auditing captures registration, boot, shutdown and availability transitions, not every heartbeat. Authentication/authorization events are recorded where the application receives them; upstream Entra events are not invented. Rate-limit/coalesce repeated denied-request diagnostics to avoid database exhaustion while preserving counts.

Execution logs are bounded, structured and correlated across scheduler, supervisor and child. Event sequences prevent duplicate completion/event submissions. Log truncation/dropped diagnostics are visible; losing optional diagnostics does not by itself fail a successful job or cause replay. Persisting the authoritative outcome and required state events is mandatory: external effects with unavailable completion persistence remain uncertain. Protect and rotate local operational logs using the same secret-safe policy.

Dashboard metrics derive from tenant records and bounded aggregates: executions, success/failure, duration, retries, timeout, trigger delay, queue delay and heartbeat age. Instrument OpenTelemetry metrics, traces and logs; configure optional exporters, bounded buffers and sampling. Telemetry exporter failure does not block job execution. Avoid secret, URL, query, job-parameter or high-cardinality execution identifiers in metric labels; use correlation IDs in safe traces/logs [R18].

Alert rules cover repeated failures, exhausted retries, timeout, long-running jobs (including infinite-timeout jobs), missed work and unavailable workers. Persist evaluation cursors/incidents and create delivery records atomically to survive scheduler restart. Distinguish a condition that is clear from one that is unknown because data is unavailable. Use grace periods and recovery events to avoid flapping.

A bounded Quartz control job delivers through IAlertSink; webhook is built in and email/other sinks remain extensions. Deduplicate by rule/subject/incident, use configurable reminder intervals, stable delivery IDs, retry/backoff/jitter and a maximum delivery attempt count. Persist the next delivery time and project a Quartz trigger; avoid a second timer loop or hidden sink retries. Receiver acknowledgement can be lost, so duplicate delivery remains possible. Exhausted deliveries are visible and manager-retryable. Alert credentials resolve only at delivery and obey outbound policy. The platform cannot deliver an alert from a fully stopped scheduler or unavailable database; document external monitoring of readiness for that failure case without making it required infrastructure.

Defaults: safe execution logs 30 days, execution history 90 days, audit 365 days. Rules are independently configurable. Never purge active/nonterminal executions, needed revisions, open incidents, active delivery attempts or valid deduplication keys. Aggregation before purge keeps explicitly documented historical summaries without implying raw history remains available.

Retain unresolved projection intents and compact execution/delivery deduplication records until associated Quartz work is confirmed retired and its replay window has closed. Remove obsolete jobs/triggers/calendars through `IScheduler`; ordinary retention never deletes Quartz rows directly. Do not infer that business history is safe to purge solely because a trigger is currently absent. Quartz's internal operational records do not substitute for WorkerDock's retained execution history or audit.

Optional archive-before-delete writes compressed safe records to an administrator-configured tenant root with restrictive ACLs, a batch manifest/checksum and atomic finalization. Verify the durable archive before batched SQL deletion. If archiving fails or storage fills, retain source data and report the retention failure. Archive retries reuse batch identity and avoid duplicate logical export. Audit purge uses a restricted routine and records a purge summary. Archives have a separately configured retention period and are not a database backup or disaster-recovery substitute.

## 12. Health, deployment, migrations and upgrades

Provide minimal liveness and readiness endpoints with separate authorization/exposure settings. Tenant readiness reports web/database/schema health, both Quartz stores' availability, stored scheduler/worker availability, catalog/policy compatibility and overdue projection intents; no compatible capacity is degraded, not falsely healthy. Public/central projections contain only instance identity, overall component states and observation timestamps, never jobs, connection details, exception text or secrets. The health credential supports controlled rotation through vault references and never satisfies user authentication or a management-role check. Anonymous read/management endpoints remain open by definition; the health credential gives no additional rights there.

Central health probes run with bounded deadlines while the central IIS process is active, and on a rate-limited refresh request. Probes are advisory; IIS idling or central downtime stops them. Persist last observed/attempt times and show stale/unknown rather than declaring an old success current. Central monitoring does not promise continuous outage alerting and does not require a new Windows service.

Ship deployment packages and documented PowerShell installation/configuration procedures for IIS, scheduler and worker services. Separate install-time administrative permissions from runtime accounts. Include TLS bindings, least-privilege SQL/file rights, protected data-protection keys, Windows service recovery, archive directories and health checks. Service identities/vault settings can vary per host; tenant identity must match the connected database. Packages include placeholders only for deployment-specific values, never working credentials.

Every SQL object change has a numbered forward script and paired rollback script, checksums, schema-history entry, prerequisites and transactional boundaries. The migrator takes an exclusive migration lock and refuses incompatible/modified history. Services check schema compatibility at startup/readiness and do not automatically migrate it. Schema and data changes include rollback/restore implications; irreversible data transformations require backup/restore instructions and explicit maintenance approval rather than a fictitious lossless down script.

Include the selected Quartz release's upstream SQL Server schema for both configured prefixes, with source version/license attribution and checksums. Wrap installation/upgrades in WorkerDock's numbered forward/rollback workflow; preserve upstream columns, indexes and constraints. Test both table sets with the pinned Quartz package, string job data and custom calendar serialization. Back up Quartz and WorkerDock tables together. A rollback that destroys pending triggers or cannot read a newer serialized calendar requires the documented backup restore, not an advertised nondestructive downgrade. Never use runtime startup to silently install/upgrade Quartz schemas.

The web database principal has no Quartz-table permissions. Scheduler principals have schedule-store access and the execution-store access required by the write-only publisher. Worker principals have execution-store access and their WorkerDock execution/control routines. Grant the DML/select permissions Quartz actually requires on its own table sets, but no schema-change rights. Control-center and execution-target credentials remain separate. Verify these grants with integration tests rather than assuming Quartz can run using only WorkerDock stored-procedure permissions.

Upgrade procedure: block new admission/triggers, place affected Quartz nodes in standby, drain with a bounded timeout (explicitly stop infinite jobs when necessary), stop affected services/apps, back up, apply reviewed migrations, deploy compatible binaries, restart and verify readiness/capabilities and projection reconciliation. Do not wait indefinitely for Quartz shutdown while an infinite job runs. Roll back only within the tested schema/application compatibility window; otherwise restore the documented backup. Service scripts support repeatable installation/upgrade without silently deleting tenant data. Worker-only template updates preserve version metadata and keep work for unavailable versions visibly blocked. Do not mix incompatible Quartz versions/schema or capability digests within an execution cluster.

## 13. Delivery stages and validation

This is a design-stage delivery outline, not the implementation plan. Written-spec approval permits preparing the detailed plan; implementation still requires plan review and execution-method selection.

| Stage | Reviewable outcome | Required evidence |
| --- | --- | --- |
| 1. Foundation | Project boundaries, configuration, WorkerDock and both Quartz schemas/up/down scripts, read/manage auth policies, atomic audit and React shell | Build/schema round trip, pinned Quartz integration proof for write-only publication, clustered limits and policy-calendar serialization, full access-policy matrix, no embedded credentials |
| 2. Execution engine | Quartz execution wrapper, transactional outbox/reconciliation, cluster limits and live policy propagation, attempt ownership, recovery, isolated children and stop controls | Real SQL/process race and fault tests across publication/acquisition/completion boundaries; no duplicate authorized attempt or stale-owner completion |
| 3. Built-in templates | Vault adapter seam, SQL/HTTP handlers, profiles, egress enforcement, metadata forms and manual execution | Real target database/local test server, secret-sentinel tests, parameter/response bounds, SSRF and idempotency scenarios |
| 4. Scheduling/operator UI | Quartz schedule/control integration, cron policy calendar, missed/overlap/retry policies, lifecycle, wizard and monitoring, English/pt-BR | Persisted preview/dispatch equivalence across DST, cursor recovery and bounded catch-up, interaction/access tests and keyboard checks |
| 5. Operations/control center | Tenant registry/health, metrics, alerts, retention/archive and optional exports | Independent-tenant isolation tests, central-outage continuity, webhook retry/dedup and archive-before-delete tests |
| 6. Release readiness | Install/upgrade packages, complete docs, accessibility/security review and measured workload results | Windows/IIS deployment and rollback exercise, all required regression checks, documented limitations and workload report |

The detailed plan splits these outcomes into small dependent changes. No phase is considered complete solely because unit tests pass. Use real SQL Server for lock/lease/schema behavior and real Windows child processes for containment; in-memory substitutes cannot establish those properties. Tests use disposable databases, bounded fixtures and synthetic secret values. Do not alter existing user databases while validating migrations.

Acceptance evidence must cover:

1. Simultaneous scheduler nodes materialize one occurrence. Competing Quartz worker nodes with locked acquisition admit no more than the configured template capacity; demonstrate this on SQL Server, not an in-memory store.
2. Limits span versions/services; desired/effective updates, standby/rejoin and unreachable nodes cannot bypass the start gate. Decreases drain, retry waiting preserves per-job serialization, and full execution pools do not block control heartbeats/stops.
3. Faults before/after business commit, Quartz publication, publication acknowledgement, child launch, external effects and outcome commit produce durable recovery without duplicate authorized attempts. A trigger consumed before acknowledgement is not blindly republished as fresh work.
4. Expired owners cannot renew/complete; SQL disconnect, clock skew, host restart and Quartz recovery do not report false success. Recovering a wrapper never bypasses NeedsReview, a stop marker or a completed attempt.
5. Infinite jobs remain renewable/stoppable; a noncooperative handler can be force-stopped without stopping sibling jobs; no child escapes containment on supervisor exit.
6. Stop/completion and disable/claim races obey their transactional ordering; operator stops suppress automatic retry.
7. Schedule previews equal persisted Quartz dispatch for gaps, repeated hours including interval expressions, non-hour changes, leap days, both day fields restricted, pauses, bounded catch-up and edits. Verify the custom calendar survives SQL serialization/restart and always advances its search.
8. Read-only authenticated users can view dashboard/monitoring/audit but cannot mutate or read protected configuration when a management role is configured. No-role and anonymous modes match the matrix exactly.
9. Tenant A service credentials/database principals cannot access tenant B. With Entra enabled, central health credentials cannot satisfy user read/management policies. Anonymous mode remains deliberately public under its documented rules. Incorrect issuers/audiences, CSRF and direct endpoint bypass attempts fail where the corresponding protection is configured.
10. Synthetic secrets never appear in database job/history/audit records, API responses, safe logs, child arguments or telemetry; raw error/body leakage is tested.
11. Outbound tests include private allowlists, mixed DNS answers, rebinding, IPv4/IPv6 forms, redirects, metadata/loopback/link-local, proxies and TLS host validation.
12. Forward/rollback migrations for WorkerDock and both Quartz stores, backup restore and least-privilege service access work; archive failure cannot delete unarchived records; active work, publication intents and request-deduplication records remain protected.
13. The center can be stopped while tenant scheduling/execution/direct access continue; stale health stays clearly stale.
14. Report results for the agreed 10-tenant/100-job/10-concurrent target and distinguish configuration scale from measured throughput and latency.
15. Restart with dispatch triggers older than the misfire threshold preserves accepted work; retries release Quartz slots during backoff; disabled jobs and superseded schedule revisions cannot execute through stale triggers. Reconciliation surfaces errored/missing projections and repairs only currently authorized work.
16. New job creation and live template-limit changes require no process deployment. A mismatched template catalog prevents a node from acquiring work; a controlled catalog upgrade preserves pinned execution versions and safe blocked states.

## 14. Documentation deliverables

Ship architecture/project boundaries; Windows local setup and IIS/service deployment; SQL Server setup, permissions, forward/rollback migrations and backup/restore; Entra shared/independent configuration, role mapping and anonymous mode; secret-facade replacement; SQL/HTTP template guides; custom package authoring/versioning; scheduling/DST/retries/concurrency/delivery guarantees; stop/force-stop and uncertain-outcome runbooks; safe logs/audit/metrics/alert sinks; retention/archive; upgrades and troubleshooting. Document Quartz ownership, the two stores, pinned version/schema, outbox reconciliation, policy-calendar behavior, trigger misfires versus business missed-work policy, capacity propagation, catalog upgrades, clock synchronization and recovery versus retry. Include runnable examples only when implementation is approved, with synthetic values and placeholders for all secrets.

Release limitations are explicit: minute cron precision; trusted installed code only; a common template capability set within each tenant execution cluster; asynchronous Quartz projection and capacity propagation; no strict global priority/FIFO guarantee; local force-stop cannot promise remote rollback; physical host/DB administrators remain trusted; no central automated provisioning; advisory central health; external telemetry and optional sinks are not needed for core operation. Requirements such as anonymous access and infinite timeouts are intentional supported modes, with their effects shown in the UI and documentation.

## 15. Review record and next gate

The user approved the conversational architecture, data model, coordination, schedule semantics (amended to infinite timeout/manual stop), isolated runtime, corrected read-versus-management authorization, secret handling, templates/outbound policy, React approach, operations/retention, and platform/staged-delivery outline.

The user subsequently requested Quartz.NET with SQL Server as the scheduling and dispatch foundation. This revision replaces the custom timer/worker-claim design and Cronos dependency with Quartz stores, native triggers, cluster execution groups and a guarded execution wrapper. WorkerDock retains business policies, audit, outbox publication and child supervision. Two table sets preserve separate scheduler/worker services; an explicit policy calendar preserves the previously agreed cron/DST semantics. These integration choices and their required proofs are part of this written-spec review.

Inline self-review checks requirements, consistency, placeholders and ambiguity. These details remain reviewable at this written-spec gate. No source implementation, scaffold, dependency installation, migration execution or implementation plan is part of this change.

The user approved this revised written specification on 2026-10-17 and explicitly requested the writing-plans step. Next gate: review the implementation plans and select an execution method before implementation begins.

## References

- [R1: Milvaion architecture overview](https://github.com/Milvasoft/milvaion#architecture) — conceptual inspiration only.
- [R2: IIS application-pool process model](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/add/processmodel).
- [R3: .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
- [R4: SQL Server 2022 lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2022).
- [R5: Vite static deployment](https://vite.dev/guide/static-deploy.html).
- [R6: Material UI overview](https://mui.com/material-ui/getting-started/).
- [R7: TanStack Query overview](https://tanstack.com/query/latest/docs/framework/react/overview).
- [R8: MIT license](https://opensource.org/license/mit).
- [R9: SQL Server sp_getapplock](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver17).
- [R10: SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17).
- [R11: Quartz cron formats and DST behavior](https://www.quartz-scheduler.net/documentation/quartz-4.x/cron-expressions.html).
- [R12: Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
- [R13: .NET cooperative cancellation](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads).
- [R14: ASP.NET Core OIDC](https://learn.microsoft.com/aspnet/core/security/authentication/configure-oidc-web-authentication).
- [R15: ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
- [R16: Microsoft.Data.SqlClient security practices](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/application-security-scenarios-sql-server?view=sql-server-ver17).
- [R17: OWASP SSRF prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).
- [R18: OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/).
- [R19: Quartz configuration, SQL store and write-only scheduler](https://www.quartz-scheduler.net/documentation/quartz-4.x/configuration/reference.html).
- [R20: Quartz.NET 4.5.1 release](https://github.com/quartznet/quartznet/releases/tag/v4.5.1).
- [R21: Quartz clustering and recovery](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/advanced-enterprise-features.html).
- [R22: Quartz execution groups, cluster limits and acquisition locking](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/execution-groups).
- [R23: Quartz.NET Apache-2.0 license](https://www.quartz-scheduler.net/license.html).
- [R24: Quartz interruption and cancellation](https://www.quartz-scheduler.net/documentation/faq.html#how-do-i-stop-a-job-that-is-currently-executing).
- [R25: Quartz retry behavior](https://www.quartz-scheduler.net/documentation/quartz-4.x/how-tos/retrying-failed-jobs).
- [R26: Quartz custom calendar serialization](https://www.quartz-scheduler.net/apidoc/4.x/api/Quartz.Serialization.SystemTextJson.Calendars.CalendarSerializer-1.html).
- [R27: Quartz cron triggers and misfires](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/crontriggers.html).
- [R28: Quartz simple triggers and misfires](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/simpletriggers.html).
