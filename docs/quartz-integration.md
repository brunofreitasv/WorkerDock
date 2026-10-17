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
permits single-job `ScheduleJob` and `AddJob` writes with only string
`ExecutionId`, `DeliveryId`, and `AttemptOrdinal` job data. It denies starting,
rescheduling, deletion, and other scheduler administration. The execution
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
