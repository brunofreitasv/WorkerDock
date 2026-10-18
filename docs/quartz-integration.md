# Quartz integration

Quartz.NET 4.5.1 provides two tenant scheduler stores. `WorkerDock.Schedule` uses
`WDS_QRTZ_` tables for schedule/control work; `WorkerDock.Execute` uses `WDE_QRTZ_`
tables for worker dispatch. The stores share a tenant database but never a table.
`QuartzTopology` verifies `TenantSettings.InstanceId` and the `0002_quartz`
schema-history entry before creating a SQL scheduler. Callers own scheduler
shutdown and dispose the topology. Started nodes in a store must use the same
scheduler name, compatible limits, and distinct boot IDs.

The public creation APIs are `CreateScheduleAsync`, `CreateExecutionAsync`, and
`CreateExecutionPublisherAsync`, each returning `IScheduler`. The SQL publisher
has its own handle with `ZeroSizeThreadPool` and is never started. Its facade
permits single-job `ScheduleJob` and `AddJob` writes with string identity metadata
and public identity/status reads. It denies starting,
arbitrary rescheduling, deletion, and other scheduler administration. A narrow priority-aging reschedule preserves the delivery, due time, execution group and one-shot schedule. The execution
scheduler is the only handle that starts worker jobs. Both SQL stores use
clustering, string job data, `System.Text.Json`, locked trigger acquisition,
and cluster-scoped Quartz execution groups. WorkerDock commands and audits
remain outside Quartz tables; the application does not mutate those tables
directly.

`QuartzTopology.CreateMemory(instanceId, "Development")` is a disposable,
single-process testing adapter through the same three creation methods. Quartz
RAMJobStore instances cannot share stored jobs. In this mode the publisher
facade forwards validated writes to the in-process execution scheduler, while
still denying `Start` and administrative calls. One memory topology accepts a
single boot ID and fixed pool/group limits. A fresh topology loses its jobs.
This adapter does not establish SQL durability, clustering, restart behavior,
capacity across nodes, migration correctness, or SQL permissions.

The SQL schema comes from the attributed Quartz 4.5.1 SQL Server script in
`database/upstream/quartz-4.5.1/`. `0002_quartz.up.sql` creates two prefixed
copies of its creation section and grants `WorkerDockRuntime` table DML plus
schema-history read. `0002_quartz.down.sql` refuses populated stores and drops
both table sets in a migrator-owned serializable transaction. The upstream
script is retained for attribution and must not be run directly because its
default drops existing Quartz tables. See [database.md](database.md) for exact
local setup and [development.md](development.md) for memory commands.

The SQL integration suite is gated by `WORKERDOCK_TEST_SQL` and a distinct
restricted SQL login in `WORKERDOCK_TEST_SQL_RUNTIME`. It runs the two
isolation settings, a separate non-started publisher process, process restart,
two or three worker processes, armed-node and cluster-check-in barriers while
the first probe is held, database-recorded probe overlap and distinct node
participation, group capacity, runtime scheduler writes, and up/down/up schema
checks.
The test fixture's paired SQL scripts create and remove a probe state/event
table and the restricted user inside only its generated database. Until that
suite passes on a disposable SQL
Server 2022+ instance, SQL clustering/capacity/restart and migration behavior
remain unverified.

## Schedule policy (WorkerDockFiveFieldV1)

`IScheduleEvaluator` is the authoritative validation, preview and one-time local
conversion port. `QuartzScheduleEvaluator` parses with `CronFormat.Unix`; it
never accepts a pasted dialect implicitly. Numeric five-field cron supports
lists, ranges and steps, with Sunday 0 or 7. Both day-of-month and day-of-week
must match. Names, macros, seconds/year fields and Quartz-specific extensions
are rejected. Original text and the fixed `WorkerDockFiveFieldV1` dialect belong
to each `ScheduleDefinition` revision.

Schedules use IANA zone IDs (including `Etc/UTC`; `UTC` is also accepted), UTC
instants, and an activation cursor. Preview is exclusive of both the supplied
cursor and activation. `Next` accepts 1–100 occurrences and at most 10,000 native
candidate evaluations per call. Exhaustion returns an empty list; validation
reports `ImpossibleSchedule`. A candidate budget exhaustion reports the safe
`ScheduleSearchLimit` code. A bounded preview can reject a very sparse
intersection; clients must not retry it in an unbounded loop.

The immutable `WorkerDockPolicyCalendar` matches nominal local times using two
Quartz expressions, one for each day restriction. Nonexistent recurring wall
times are skipped. Repeated times, including interval expressions and half-hour
transitions, run once at the earlier UTC instant (the larger valid offset).
An actual match at a gap end remains eligible. `GetNextIncludedTimeUtc` returns
an included input unchanged, otherwise advances via bounded Quartz candidates;
exhaustion returns `DateTimeOffset.MaxValue`. Native Quartz trigger acquisition
and its internal calendar-filter loop remain owned by Quartz. Create projections
through `QuartzScheduleEvaluator.CreateTrigger`, which validates the bounded
search before exposing a native trigger; do not schedule arbitrary unvalidated
cron/calendar pairs.

Register a `WorkerDockPolicyCalendar` under the revision's calendar name with
`IScheduler.AddCalendar`, then schedule the trigger returned by `CreateTrigger`.
Recurring triggers explicitly use `FireAndProceed`; one-time native simple
triggers use `FireNow`. A misfire is a dispatcher wake-up, not a new nominal
occurrence. Durable cursor/catch-up projection remains later-stage work.

`ResolveLocalOnce` accepts only `DateTimeKind.Unspecified`. It rejects gaps and
requires an explicitly selected valid offset during an overlap. A forged offset
is rejected even outside an overlap. Both overlap choices are allowed for a
one-time schedule; the recurring earlier-offset rule does not override that
explicit choice.

Every SQL topology handle registers `WorkerDockPolicyCalendarSerializer` through
Quartz's scheduler-local JSON registry. The custom payload holds schema version
1, original expression, zone and policy identifier; Quartz adds its type envelope
and null standard calendar metadata. There is no execution configuration or job
data. Unknown schema versions/policies fail closed. Changes to this payload need
an explicit compatibility/migration plan, not silent reinterpretation. Refresh
future projections after time-zone database changes; preserve historical UTC
instants.

Memory tests cover literal expected fire sequences, serialized calendar copies,
stored RAMJobStore trigger/calendar retrieval, and actual Quartz job execution
with `UseTimeProvider`. SQL restart cases cover the same seven DST/interval/AND
fixtures and remain pending `WORKERDOCK_TEST_SQL`. An in-process JSON round trip
is not a SQL persistence, cluster or migration proof.

Quartz references: [five-field parser and DST behavior](https://www.quartz-scheduler.net/documentation/quartz-4.x/cron-expressions.html),
[time provider](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/time-and-timeprovider.html).
Public signatures were verified against the installed Quartz 4.5.1 XML API
reference and assembly (upstream commit `81aa09d71cc73c4e83f225d026ed1da4896852d9`).

Schedule validation codes for the later editor use these English / pt-BR texts:

| Code | English | Português (Brasil) |
| --- | --- | --- |
| InvalidCron | Use a supported five-field numeric cron expression. | Use uma expressão cron numérica de cinco campos compatível. |
| InvalidTimeZone | Select a valid IANA time zone. | Selecione um fuso horário IANA válido. |
| InvalidSchedule | The schedule definition is invalid. | A definição do agendamento é inválida. |
| ImpossibleSchedule | No future occurrence exists for this schedule. | Não existe ocorrência futura para este agendamento. |
| InvalidOccurrenceCount | Request between 1 and 100 occurrences. | Solicite entre 1 e 100 ocorrências. |
| ScheduleSearchLimit | The occurrence search limit was reached. Simplify the schedule. | O limite de busca de ocorrências foi atingido. Simplifique o agendamento. |
| AmbiguousLocalTime | Select which UTC offset to use for this repeated local time. | Selecione o deslocamento UTC para este horário local repetido. |
| NonexistentLocalTime | This local time does not exist because the clock moves forward. | Este horário local não existe porque o relógio avança. |
| InvalidLocalOffset | The selected offset is not valid for this time and zone. | O deslocamento selecionado não é válido para este horário e fuso. |
| InvalidLocalTimeKind | Supply a local wall time without an inferred UTC offset. | Informe um horário local sem deslocamento UTC inferido. |
| InvalidLocalTime | The local time is outside the supported instant range. | O horário local está fora do intervalo de instantes compatível. |

## Dispatch publication and reconciliation

One common `ProjectionEnvelope` reads the existing `dbo.Outbox`; its `IntentId`
is the outbox EventId, distinct from ExecutionId. Version 1 allowlists
ExecutionDispatch, ScheduleProjection and AlertDispatch. Only ExecutionDispatch
has a decoder in Task06. Unknown kinds, the two deferred kinds and unsupported
versions remain failed with `UnsupportedProjection`, never acknowledged.
Execution payloads contain `executionId`, `jobRevision` and optional
`attemptOrdinal` (default 1). No configuration/profile/secret payload enters
Quartz. The existing initial outbox digest remains compatible; ordinals after
one add `|ordinal` to that canonical execution/revision digest.

`SqlProjectionStore` reads the pinned JobRevision, template identity, policy
priority, execution availability and existing attempt ordinals. DeliveryId is
the immutable outbox intent ID. The port never inserts attempts, claims work,
allocates permits or authorizes effects. An existing attempt, Running,
NeedsReview or a terminal execution retires that delivery. Only Queued and
RetryWaiting executions without that attempt may be published. Task08's
transactional start gate must still recheck eligibility after acquisition.

Quartz job/trigger keys encode execution, delivery and authorized ordinal in
the template group; execution groups use that same template ID. Each durable,
recovery-requesting job stores string ExecutionId, DeliveryId, AttemptOrdinal,
TemplateId, EntityRevision, DueUtc and PayloadDigest. This second digest hashes
canonical execution/delivery/ordinal/template/revision/UTC due ticks, excluding
priority and Quartz fire bookkeeping. Replay verifies the stored identity and
type before repairing a missing trigger. It rejects a stored trigger with a
different due time, wrong template execution group, unexpected input, repeat
schedule or retry policy. Triggers explicitly use FireNow misfires and
RetryPolicy.None. Guarded recovery means the future worker start gate decides
whether an attempt may run; Quartz recovery never authorizes a business retry.

All Quartz calls use public APIs outside a business transaction. A crash before
Quartz write leaves Pending/Failed intent. A crash after write but before ack
replays the same deterministic identity; concurrent insert losers reread and
verify instead of overwriting. A consumed trigger is checked against business
state first. Acknowledgement independently records DispatchedAtUtc and
Published. Published projections retain bounded periodic review, allowing
eligible missing triggers to be repaired after acknowledgement. Terminal/active
business state moves them to Retired. Failed writes and integrity errors retain
LastErrorCode and RetryAtUtc; a failed published review stays Published with an
error code and a future ReviewAtUtc. Permanent Error/Complete trigger states
remain visible as ProjectionTriggerError, requiring investigation rather than
an automatic reset or a silently successful acknowledgement. No direct Quartz
SQL writes are used.

Scheduler directly and idempotently registers projection/reconciliation control
jobs at startup (15s/60s), before starting only WorkerDock.Schedule. Each firing
has a 100-envelope batch and 20s cancellation deadline; pending publication and
published review each have reserved batch capacity. The execution publisher
uses ZeroSizeThreadPool and is never started. Quartz's public lazy JobType
stores the exact future `WorkerDock.Quartz.Dispatch.ExecuteTemplateJob,
WorkerDock.Quartz` identity with explicit execution metadata flags. Task06
memory tests prove that unloaded identity can be stored/replayed without
starting execution. Task08 installs its guarded implementation; no test or
no-op executor is shipped in the Scheduler.

Run `PublicationRecoveryTests` for in-process faults, concurrent publishers,
actual probe consumption and control-job bootstrap. `ProjectionSqlTests` adds
restricted-runtime SQL reads/writes, lost ack, concurrent replay after a fresh
Quartz handle, retirement, retry metadata, instance fencing and paired migration
checks in both isolation modes. These SQL proofs, SQL type loading and actual
multi-process/cluster restart/fault proofs are pending until the disposable SQL
fixture and Task08 worker are available. Memory success is not evidence for
those properties. See [database.md](database.md) for exact setup/tests and
[development.md](development.md) for memory tests.


## Live capacity, capability admission and logical job permits

Apply the paired `0003c_capacity` migration after `0003b_attempts`. Quartz remains
the only cluster reservation ledger; no WorkerDock template-slot table exists.
Every template version uses its template identifier as the execution group, with
cluster scope and locked acquisition. `CapacityCoordinator` changes snapshots
through `IScheduler.Standby`, `SetExecutionLimits`, and `Start`; it never calls
Quartz while a tenant business transaction holds the business lock.

A template command closes all execution start gates and advances the shared
capacity revision in the same transaction as its receipt and audit. This common
snapshot intentionally coordinates the homogeneous tenant pool. Workers enter
standby, install that exact snapshot, and acknowledge it. The policy remains
`Pending` while any compatible worker has not acknowledged. After 60 seconds
without a heartbeat its boot is fenced, but it is counted as recovered only after
Quartz's public `QueryClusterNodes` no longer lists it and
`QueryFireInstances(State = null)` reports no acquired or executing reservations.
Neither lease expiry nor application fencing deletes a Quartz reservation.
A fenced boot cannot register or authorize attempts again.

The worker uses a stable `Worker:InstallationId`, a fresh boot ID, a configured
`Worker:HostCeiling` (1 through 1024), and the installed common
`Worker:CatalogDigest`. A new scheduler initially has zero limits for every
known and unknown group. It can check in and recover an old boot without
acquiring business work. Registration and revision acknowledgement precede
positive acquisition limits. The independent five-second policy/heartbeat loop
stays responsive when all Quartz execution threads run infinite jobs. A live
worker limit must be between zero and its installation ceiling. Attempt
transactions serialize local authorizations against that live limit; existing
authorizations drain after decreases.

The approved catalog digest is uppercase SHA-256 of UTF-8 text, ordered by
ordinal template identifier and version. Each enabled catalog row contributes
`TemplateId|TemplateVersion|MetadataDigest` followed by a single LF. Worker
configuration must describe the actual installed common package set. A mismatch
is visible as `CatalogMismatch` and cannot enter acquisition. Job creation does
not change this catalog or require a new service.

Each dispatch has immutable `CapacityRevision` string metadata, included in its
dispatch digest. Start authorization rechecks that epoch against the current
effective template revision, worker acknowledgement, catalog, readiness, live
host budget and logical job permit in one tenant business transaction. A wrapper
acquired under an old epoch defers without an attempt, even after the new gate
reopens. Eligible unstarted work is reissued from the business outbox under a
deterministic execution/attempt-ordinal/epoch delivery identity; the old outbox
entry is retired. No acquired Quartz job is rewritten to adopt a newer epoch.
Due time, pinned revision and idempotency key survive reissue.

`JobAdmission` allocates logical permits while materializing dispatch. Queue and
Skip use exclusive permits; Allow uses shared permits up to the live job limit.
The SQL admission sequence orders executions within one job, including mixed
revision policies; later shared executions cannot bypass an exclusive waiter.
Retries and NeedsReview retain their permit. Finalization or explicit audited
reconciliation releases it and wakes eligible successors. Decreasing the job
limit blocks new logical admissions while existing holders can still retry.
Overlap skips and backlog overflow persist `SkippedOverlap` and
`SkippedBacklog` status events. Held wrappers consume no business attempt;
policy/enable/worker-room/permit changes make their outbox intents eligible on
the next publisher maintenance pass.

Trigger priority starts at the execution priority. Maintenance can add at most
100 aging points, capped at 200, through Quartz's public reschedule API. Aging
preserves the original due time, including future retry deadlines. Quartz due
time still participates in ordering; this is not strict global FIFO or a
starvation-free cross-job latency promise.

`GET /api/capacity` exposes desired/effective revisions, policy state and worker
readiness under Read authorization. Manage and CSRF are required for
`PUT /api/templates/{templateId}/limit`, `PUT /api/jobs/{jobId}/limit`, and
`PUT /api/workers/{installationId}/limit`, each with `{ "limit": 1 }`. Template
receipts remain Pending until the corresponding revision is effective.
The Development web-only memory adapter does not run a capacity cluster; the
capacity algorithm, wrapper and actual RAMJobStore tests are disposable fixtures.

Current verification establishes memory policy races, actual local Quartz
3-to-1-to-0-to-2 draining, publication/ownership fault guards and Windows process
containment. Real SQL adapter/migration tests, restricted-runtime grants,
three-service live policy propagation, and capacity-one across real SQL workers
remain pending until an authorized disposable SQL instance and runtime login run
the commands in database.md. SQL sources remain consistently in `dbo` for this
task; the separately requested complete conversion to `workerdock` schema is
pending before Stage 03.
