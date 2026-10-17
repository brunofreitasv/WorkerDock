# WorkerDock design specification

Date: 2026-10-17 (America/Sao_Paulo)

Status: Design sections approved in conversation; consolidated written specification awaiting user approval. No implementation is authorized by this document's creation. After written-spec approval, prepare and review the implementation plan and select its execution method before implementation.

## 1. Purpose and agreed scope

WorkerDock is an open-source, self-hosted platform for creating, scheduling, executing, monitoring, and auditing general-purpose jobs. Operators configure jobs through a React dashboard using reusable templates. Developers extend execution through trusted .NET packages without modifying scheduler logic.

The required infrastructure is Windows/IIS and SQL Server. Redis, RabbitMQ, external message brokers, hosted control services, and telemetry collectors are not required. Entra ID is an optional configured authentication dependency; job destinations and production secret providers are deployment-specific integrations. Self-contained means that core scheduling, execution, history, alerts, and the dashboard run with the application's own services and SQL Server; SQL Server itself remains a separately installed prerequisite.

Initial design and load-test target: up to 10 independent tenants, 100 configured jobs per tenant, and 10 concurrent executions per tenant. These are validation targets, not guaranteed capacity or fixed product ceilings. Actual throughput depends on job duration, process startup, database latency, and host resources. Publish measured results with the test hardware and workload.

Built-in templates are SQL Server and HTTP. Job creation, edits, schedules, connection profiles, and concurrency changes are database operations: they do not install Windows services, deploy code, or restart workers. New worker-service installations add host capacity; installing or updating custom template code requires a controlled worker deployment.

Milvaion informs the separation of scheduling from execution, reusable worker contracts, operator monitoring, and observability. WorkerDock uses its own implementation, branding, and UI. Its SQL Server coordination design replaces the external messaging infrastructure described by Milvaion [R1].

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
| Tenant scheduler service | Scheduled occurrence creation, retry promotion, abandoned-attempt recovery, alert evaluation/delivery, retention | Tenant SQL Server; alert vault credentials and approved webhook/archive destinations |
| Tenant worker service | Capability registration, admission, execution supervision, leases, stop requests, sanitized results | Tenant SQL Server and local execution host |
| Execution child process | One attempt or diagnostic execution, template validation, secret resolution, SQL/HTTP/custom handler execution | Tenant vault and explicitly permitted job destinations |
| Central web application | Tenant registry, central audit, minimal health probes, navigation | Central SQL Server, its identity provider/vault, registered approved health URLs |

Each execution runs in a child process started from installed WorkerDock binaries. It is not a new Windows service or deployment. The worker supervises children and remains responsive when a handler hangs. Long-running scheduling/execution lives outside IIS because its process lifecycle includes recycling and idle termination [R2].

### 2.3 Code organization

Names below define project responsibilities; folders within Infrastructure and Runtime remain distinct modules rather than requiring a project for every adapter.

| Project/module | Responsibility |
| --- | --- |
| WorkerDock.Domain | Job policies, execution states, scheduling and lifecycle invariants; no database or hosting dependencies |
| WorkerDock.Contracts | Template metadata/handler contracts, execution context, secret facade, safe logging and alert-sink interfaces |
| WorkerDock.Application | Use cases and persistence/coordination interfaces; depends on Domain and Contracts |
| WorkerDock.Infrastructure | SQL Server persistence/coordination, secret adapters, outbound policy, audit storage, authentication integration and telemetry adapters |
| WorkerDock.Runtime | Scheduler loops, worker supervision, restricted IPC and Windows process containment using application interfaces |
| WorkerDock.Templates.SqlServer | SQL template schema, validation, connection builder, handler and result classification |
| WorkerDock.Templates.Http | HTTP template schema, validation, substitution, destination policy integration and handler |
| WorkerDock.Web | Tenant IIS composition root and tenant read/management endpoints |
| WorkerDock.Scheduler | Windows-service/foreground composition root for scheduler and operational loops |
| WorkerDock.Worker | Windows-service/foreground composition root for worker supervision |
| WorkerDock.Executor | Child-process composition root loading installed, explicitly registered trusted templates |
| WorkerDock.ControlCenter | Central registry, central read/management APIs and health-probe composition root |
| WorkerDock.DatabaseMigrator | Explicit installation/upgrade tool for versioned SQL scripts using migration credentials |
| Frontend workspace | Separate tenant and central React entry points with shared UI, localization and API utilities |

Templates depend on Contracts and their client libraries, not scheduler implementation. Hosts compose modules through dependency injection. The central application does not acquire a tenant persistence dependency or reuse tenant authorization as a bypass. EF Core implements ordinary reads and configuration persistence; explicit parameterized SQL/stored procedures implement atomic coordination. Versioned SQL scripts are the authoritative schema migration source; there is no competing automatic EF schema-creation path.

## 3. Platform baseline

- .NET 10 LTS, ASP.NET Core, C#, with compatible pinned stable package versions and current security patches [R3].
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
| Template policies | Live concurrency limit per template identifier, shared across its installed versions |
| Jobs | Name, current revision, enable/archive state and live per-job concurrency limit |
| Job revisions | Immutable template/version, configuration, profile identifier, timeout, retry/recovery and overlap policies |
| Schedules | One-time/cron definition, zone, schedule revision, pause state, next due instant, last evaluated cursor, missed-work policy |
| Executions | Job revision, resolved profile revision, trigger kind, scheduled instant, request/occurrence key, idempotency key, status, availability time and correlation ID |
| Attempts | Attempt number, worker identity, ownership generation, lease expiry, start/deadline/end, result/error classification and stop state |
| Workers/capabilities | Service installation identity, current boot identity, heartbeat, local capacity and exact supported template versions |
| Service health | Scheduler/service boot identity, heartbeat and safe readiness observations |
| Reservations | Logical job permits and attempt-level template/worker capacity reservations; ownership linked to execution/attempt |
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

Index due/available work by state, eligibility time and priority; index active leases by expiry and all history by parent identity/time. Monitoring queries use pagination and time-window limits. Foreign keys and retention ordering protect active work and the revisions it references.

## 5. Coordination and delivery guarantees

### 5.1 Atomic scheduling and claiming

The first implementation favors a single short transaction-owned coordination application lock per tenant database for state transitions that affect schedules, admission, capacity, stop/disable decisions or recovery. This intentionally serializes small metadata changes for the agreed workload. It does not serialize job execution. Independent tenant databases have independent locks. SQL Server transaction-owned application locks release on commit/rollback [R9].

All coordinating writers, including the API, scheduler and workers, use the same protocol. Acquire the coordination lock before mutable coordination rows, use bounded lock timeouts, check every lock return code, and roll back on failure before retrying with jitter. Heartbeat renewals use conditional row updates and do not revive expired ownership. No network call or handler runs inside a coordination transaction. Migration scripts include the required routines and permissions.

Scheduling transaction: recheck job/schedule state, apply the occurrence policy, insert executions/events, and advance the cursor atomically. An edit increments schedule revision under the same protocol and computes a new next occurrence; already created executions remain unchanged. The unique key is a final duplicate-insertion guard.

Claim transaction: select eligible work supported by the worker, recheck enabled/archive/stop state, verify live limits and local capacity, acquire permits, create an attempt and lease, and commit. Only then start the child. Do not prefetch more attempts than can start. A crash between commit and startup leaves a recoverable leased attempt. Missing template capability appears as a blocked reason on queued work, not a stream of failed attempts.

Normal database isolation and explicit locking must be tested under both READ_COMMITTED_SNAPSHOT settings. Do not assume READPAST alone guarantees correctness or skips page locks [R10]. Bounded application retries handle coordination deadlocks; they do not replay external job effects.

### 5.2 Concurrency and ordering

- Template limits apply across the tenant, all worker instances and template versions. Installing another worker does not multiply the configured limit.
- Per-worker capacity bounds local children. The worker's boot identity and database registration prevent a restarted process from inheriting previous ownership.
- A per-job permit is allocated when an execution first starts and retained through retry waiting. Default queue overlap therefore prevents a later execution from overtaking a retrying execution. Attempt-level template/worker capacity is released between confirmed attempts.
- Queue/skip executions use an exclusive job permit; allow executions use shared permits up to the live per-job limit. Shared permits cannot bypass an existing exclusive permit, and exclusive permits wait for all other permits to finish. Overlap skip records a skipped occurrence when the job already has an outstanding nonterminal execution. Queue mode has a configurable backlog bound; overflow is recorded as skipped with a reason and alert eligibility.
- Capacity updates use the coordination transaction. Increases permit additional claims immediately. Template/worker decreases block new attempts until usage permits them; job-limit decreases block new logical executions while existing permit holders can finish their retries, still respecting attempt-level template/worker limits. This avoids deadlocking retry-waiting executions after a limit reduction. No decrease kills running work. A zero template/worker capacity means admission paused.
- Eligible attempts use priority plus bounded aging; ties use oldest availability time and stable ID. Aging reduces starvation but cannot guarantee latency during sustained overload. Within a serialized job, retain execution order.

### 5.3 Leases and ownership

Proposed defaults are a 10-second heartbeat and 60-second attempt lease, configurable with validation that leaves room for cancellation and shutdown. SQL Server time is authoritative for due times and lease comparisons. Renew attempt ownership and its reservations together using worker boot identity, generation, active state and an unexpired lease predicate. A late heartbeat cannot resurrect a recovered attempt.

The supervisor maintains a conservative monotonic local deadline based on the acknowledged renewal and request elapsed time. If it cannot renew before its safety margin, it stops admission, requests cancellation and terminates contained children before the last known lease deadline where the OS is responsive. Infinite execution timeout never disables this ownership deadline. A hung/suspended host can still outlive it; the recovery guarantees below apply.

Completion uses an ownership-checked transaction to persist the outcome/events, release attempt capacity, and either schedule retry or finalize the execution/release its job permit. Retried completion submissions are idempotent. Expired or replaced owners cannot overwrite the authoritative outcome. Child-process completion is not reported as durable success until that transaction commits.

### 5.4 Recovery and the external-effects boundary

The scheduler atomically marks expired attempts abandoned and releases their attempt reservations. If the job explicitly permits replay of uncertain outcomes, it schedules a bounded retry with the same execution idempotency key. Otherwise the execution becomes NeedsReview, retains its logical job permit and prevents accidental overlapping work under queue policy. A manager can reconcile the result, cancel it, or explicitly authorize retry; each action is audited.

A durable operator stop always suppresses automatic replay, including during lease recovery. If termination cannot be confirmed, retain NeedsReview with the stop-request marker until reconciliation; do not label a still-unconfirmed process terminated. Releasing an unresolved job permit requires an audited management decision acknowledging possible external overlap.

At-least-once here means eligible work can be redelivered after an interruption. Finite retry limits, permanent failures, manual cancellation, and review holds bound automatic delivery; eventual successful execution is not guaranteed. WorkerDock does not claim exactly-once effects.

An external SQL commit or HTTP action can succeed before the worker records completion. Ownership tokens protect WorkerDock's records, not external systems. An abandoned process or remote request may continue after its lease expires; admission limits therefore bound authorized attempts, not all possible lingering external operations. Strict external deduplication or mutual exclusion requires a target-side idempotency key/transaction/lock. SQL transaction mode alone does not solve uncertain commit acknowledgement, and an HTTP idempotency header only works if the destination honors it.

When SQL Server is unavailable, stop new admission and scheduling. Existing supervisors follow lease-loss handling. Do not report an unpersisted result as confirmed. Recovery and alerts resume when persistence is available.

## 6. Scheduling and lifecycle semantics

### 6.1 Time and cron

Support one-time schedules and five-field cron at minute precision: minute, hour, day of month, month and day of week. The initial supported grammar includes numeric fields, lists, ranges and steps. If day of month and day of week are both restricted, both must match; explain this in the editor. Reject unsupported extensions rather than interpreting them differently across components.

Persist a canonical IANA zone ID; the backend validates/resolves it on Windows and supplies the available-zone list. Persist instants in UTC. One-time input in a nonexistent local time is rejected; ambiguous input requires selecting an offset. For recurring schedules, nonexistent local occurrences are skipped, and repeated local occurrences run once at the earlier UTC instant. This applies to interval expressions too.

Use one backend schedule evaluator for validation, next-run preview and scheduling. Cronos is the proposed parsing/calendar-candidate library, behind a WorkerDock timezone adapter. Its built-in timezone behavior differs from the agreed DST rules [R11]; do not directly use that behavior. Enumerate nominal local calendar matches, discard invalid wall times, resolve ambiguous matches to the earliest instant and filter against the UTC cursor. Keep evaluation bounded, reject impossible schedules and test DST gaps, repeated hours, leap days and non-hour transitions. Browser summaries are explanatory only; server occurrence previews are authoritative. Record/refresh calculation state after time-zone rule updates without rewriting historical instants.

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

Normal short polling delay is not a missed occurrence: a configurable lateness tolerance, initially 60 seconds, distinguishes it from downtime. Age/count bounds are required for catch-up; work already materialized in the queue is not retroactively dropped by missed-schedule policy. Schedule delay metrics measure both trigger lateness and queue wait separately.

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

Execution children resolve needed job secrets directly. Supervisors and web APIs do not expose a resolve-secret endpoint. The scheduler's alert loop has a separately scoped identity/reference set for sink credentials; the central application resolves only its registered health credentials. Local process environments and file permissions are isolated per tenant; development environment secrets are not a substitute for a production vault.

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
| Workers/templates | Capabilities, heartbeat, version availability, local capacity and tenant-wide concurrency; distinguish services from job definitions |
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

A bounded independent loop in the scheduler delivers through IAlertSink; webhook is built in and email/other sinks remain extensions. Deduplicate by rule/subject/incident, use configurable reminder intervals, stable delivery IDs, retry/backoff/jitter and a maximum delivery attempt count. Receiver acknowledgement can be lost, so duplicate delivery remains possible. Exhausted deliveries are visible and manager-retryable. Alert credentials resolve only at delivery and obey outbound policy. The platform cannot deliver an alert from a fully stopped scheduler or unavailable database; document external monitoring of readiness for that failure case without making it required infrastructure.

Defaults: safe execution logs 30 days, execution history 90 days, audit 365 days. Rules are independently configurable. Never purge active/nonterminal executions, needed revisions, open incidents, active delivery attempts or valid deduplication keys. Aggregation before purge keeps explicitly documented historical summaries without implying raw history remains available.

Optional archive-before-delete writes compressed safe records to an administrator-configured tenant root with restrictive ACLs, a batch manifest/checksum and atomic finalization. Verify the durable archive before batched SQL deletion. If archiving fails or storage fills, retain source data and report the retention failure. Archive retries reuse batch identity and avoid duplicate logical export. Audit purge uses a restricted routine and records a purge summary. Archives have a separately configured retention period and are not a database backup or disaster-recovery substitute.

## 12. Health, deployment, migrations and upgrades

Provide minimal liveness and readiness endpoints with separate authorization/exposure settings. Tenant readiness reports web/database/schema health and stored scheduler/worker availability; no compatible capacity is degraded, not falsely healthy. Public/central projections contain only instance identity, overall component states and observation timestamps, never jobs, connection details, exception text or secrets. The health credential supports controlled rotation through vault references and never satisfies user authentication or a management-role check. Anonymous read/management endpoints remain open by definition; the health credential gives no additional rights there.

Central health probes run with bounded deadlines while the central IIS process is active, and on a rate-limited refresh request. Probes are advisory; IIS idling or central downtime stops them. Persist last observed/attempt times and show stale/unknown rather than declaring an old success current. Central monitoring does not promise continuous outage alerting and does not require a new Windows service.

Ship deployment packages and documented PowerShell installation/configuration procedures for IIS, scheduler and worker services. Separate install-time administrative permissions from runtime accounts. Include TLS bindings, least-privilege SQL/file rights, protected data-protection keys, Windows service recovery, archive directories and health checks. Service identities/vault settings can vary per host; tenant identity must match the connected database. Packages include placeholders only for deployment-specific values, never working credentials.

Every SQL object change has a numbered forward script and paired rollback script, checksums, schema-history entry, prerequisites and transactional boundaries. The migrator takes an exclusive migration lock and refuses incompatible/modified history. Services check schema compatibility at startup/readiness and do not automatically migrate it. Schema and data changes include rollback/restore implications; irreversible data transformations require backup/restore instructions and explicit maintenance approval rather than a fictitious lossless down script.

Upgrade procedure: block new admission/triggers, drain with a bounded timeout (explicitly stop infinite jobs when necessary), stop affected services/apps, back up, apply reviewed migrations, deploy compatible binaries, restart and verify readiness/capabilities. Roll back only within the tested schema/application compatibility window; otherwise restore the documented backup. Service scripts support repeatable installation/upgrade without silently deleting tenant data. Worker-only template updates preserve version metadata and keep work for unavailable versions visibly blocked.

## 13. Delivery stages and validation

This is a design-stage delivery outline, not the implementation plan. Written-spec approval permits preparing the detailed plan; implementation still requires plan review and execution-method selection.

| Stage | Reviewable outcome | Required evidence |
| --- | --- | --- |
| 1. Foundation | Project boundaries, configuration, tenant schema/up/down scripts, read/manage auth policies, atomic audit and React shell | Build checks, schema round trip on disposable SQL Server, full access-policy matrix, no embedded credentials |
| 2. Execution engine | Durable claiming, limits, ownership, recovery, isolated children, finite/infinite timeouts and stop controls | Real SQL/process race and fault tests; no duplicate authorized claim or stale-owner completion |
| 3. Built-in templates | Vault adapter seam, SQL/HTTP handlers, profiles, egress enforcement, metadata forms and manual execution | Real target database/local test server, secret-sentinel tests, parameter/response bounds, SSRF and idempotency scenarios |
| 4. Scheduling/operator UI | Cron/zones, missed/overlap/retry policies, lifecycle, wizard and monitoring, English/pt-BR | Preview/dispatch equivalence across DST, interaction tests, access tests and keyboard checks |
| 5. Operations/control center | Tenant registry/health, metrics, alerts, retention/archive and optional exports | Independent-tenant isolation tests, central-outage continuity, webhook retry/dedup and archive-before-delete tests |
| 6. Release readiness | Install/upgrade packages, complete docs, accessibility/security review and measured workload results | Windows/IIS deployment and rollback exercise, all required regression checks, documented limitations and workload report |

The detailed plan splits these outcomes into small dependent changes. No phase is considered complete solely because unit tests pass. Use real SQL Server for lock/lease/schema behavior and real Windows child processes for containment; in-memory substitutes cannot establish those properties. Tests use disposable databases, bounded fixtures and synthetic secret values. Do not alter existing user databases while validating migrations.

Acceptance evidence must cover:

1. Simultaneous schedulers materialize one occurrence, and competing workers admit no more than configured capacity.
2. Template limits span versions/services; decreases drain without killing work; retry waiting preserves default per-job serialization.
3. Process crashes before launch, after external effects and before commit produce the documented retry/review behavior.
4. Expired workers cannot renew/complete; SQL disconnect and host restart do not report false success.
5. Infinite jobs remain renewable/stoppable; a noncooperative handler can be force-stopped without stopping sibling jobs; no child escapes containment on supervisor exit.
6. Stop/completion and disable/claim races obey their transactional ordering; operator stops suppress automatic retry.
7. Schedule previews equal dispatch for gaps, repeated hours, non-hour changes, leap days, pauses, catch-up limits and edits.
8. Read-only authenticated users can view dashboard/monitoring/audit but cannot mutate or read protected configuration when a management role is configured. No-role and anonymous modes match the matrix exactly.
9. Tenant A service credentials/database principals cannot access tenant B. With Entra enabled, central health credentials cannot satisfy user read/management policies. Anonymous mode remains deliberately public under its documented rules. Incorrect issuers/audiences, CSRF and direct endpoint bypass attempts fail where the corresponding protection is configured.
10. Synthetic secrets never appear in database job/history/audit records, API responses, safe logs, child arguments or telemetry; raw error/body leakage is tested.
11. Outbound tests include private allowlists, mixed DNS answers, rebinding, IPv4/IPv6 forms, redirects, metadata/loopback/link-local, proxies and TLS host validation.
12. Forward/rollback migrations and least-privilege service access work; archive failure cannot delete unarchived records; retained active work remains referentially intact.
13. The center can be stopped while tenant scheduling/execution/direct access continue; stale health stays clearly stale.
14. Report results for the agreed 10-tenant/100-job/10-concurrent target and distinguish configuration scale from measured throughput and latency.

## 14. Documentation deliverables

Ship architecture/project boundaries; Windows local setup and IIS/service deployment; SQL Server setup, permissions, forward/rollback migrations and backup/restore; Entra shared/independent configuration, role mapping and anonymous mode; secret-facade replacement; SQL/HTTP template guides; custom package authoring/versioning; scheduling/DST/retries/concurrency/delivery guarantees; stop/force-stop and uncertain-outcome runbooks; safe logs/audit/metrics/alert sinks; retention/archive; upgrades and troubleshooting. Include runnable examples only when implementation is approved, with synthetic values and placeholders for all secrets.

Release limitations are explicit: minute cron precision; trusted installed code only; local force-stop cannot promise remote rollback; physical host/DB administrators remain trusted; no central automated provisioning; advisory central health; external telemetry and optional sinks are not needed for core operation. Requirements such as anonymous access and infinite timeouts are intentional supported modes, with their effects shown in the UI and documentation.

## 15. Review record and next gate

The user approved the conversational architecture, data model, coordination, schedule semantics (amended to infinite timeout/manual stop), isolated runtime, corrected read-versus-management authorization, secret handling, templates/outbound policy, React approach, operations/retention, and platform/staged-delivery outline.

The written specification consolidates those approvals and makes coordination races, profile/version pinning, cron semantics, health staleness, retention safeguards and upgrade boundaries explicit. Inline self-review checks requirements, consistency, placeholders and ambiguity. These details remain reviewable at this written-spec gate. No source implementation, scaffold, dependency installation, migration execution or implementation plan is part of this change.

Next required action: user review and approval of this written file. Only then invoke the writing-plans skill, prepare the implementation plan, obtain its review and execution-method selection, and begin the authorized implementation stages.

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
- [R11: Cronos parsing and DST behavior](https://github.com/HangfireIO/Cronos).
- [R12: Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
- [R13: .NET cooperative cancellation](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads).
- [R14: ASP.NET Core OIDC](https://learn.microsoft.com/aspnet/core/security/authentication/configure-oidc-web-authentication).
- [R15: ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
- [R16: Microsoft.Data.SqlClient security practices](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/application-security-scenarios-sql-server?view=sql-server-ver17).
- [R17: OWASP SSRF prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).
- [R18: OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/).
