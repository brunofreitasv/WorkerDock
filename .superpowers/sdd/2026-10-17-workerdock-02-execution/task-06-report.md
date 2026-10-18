# Task 06 implementation report

Status: DONE_WITH_CONCERNS. Implementation and memory verification are complete. Real SQL, SQL-loaded wrapper identity and cluster/process restart proofs remain pending because WORKERDOCK_TEST_SQL is unset. No SQL database or permissions were changed on a live instance.

## Implemented

- Added DispatchIntent with a canonical SHA-256 over immutable execution/delivery/ordinal/template/revision/UTC-due fields; trigger priority and Quartz fire bookkeeping are excluded.
- Added the common ProjectionEnvelope and the specified IProjectionStore port. The ExecutionDispatch decoder strictly checks version, identifiers, revision, optional authorized attempt ordinal, payload fields and digest. Unknown/deferred kinds stay visibly failed and unacknowledged. Initial Task05 outbox payload/digest remain compatible; future retry payloads explicitly carry attemptOrdinal and its ordinal-aware digest.
- Added read-only IDispatchStateStore and DispatchState. SqlProjectionStore reads authoritative pinned execution/revision/template/priority/due and existing attempt ordinals, without claiming jobs, allocating attempts or running effects. Initial ordinal is 1; later ordinals require an explicit outbox payload and cannot jump beyond the highest recorded attempt plus one. Delivery identity is stable per outbox intent.
- Added separate bounded IProjectionReviewStore metadata/candidate operations to retain reconciliation after initial acknowledgement. Pending publication and published review reserve separate batch capacity, including alternating single-item batches.
- QuartzPublisher uses deterministic execution/delivery/ordinal keys and template execution groups, public APIs outside business transactions, insert-race rereads, durable jobs, requested guarded recovery, explicit FireNow misfires and RetryPolicy.None. It verifies immutable stored identity/type before replay and checks trigger timing, group, one-shot schedule, input and retry policy. It never overwrites an existing delivery identity. Permanent trigger Error/Complete states stay visible as ProjectionTriggerError.
- DispatchReconciler checks business state before deciding that a missing trigger requires repair. Existing attempts, Running, NeedsReview and terminal executions retire the delivery; only eligible Queued/RetryWaiting state publishes. Lost write/ack responses replay safely. Safe error codes/retry deadlines persist independently from publication acknowledgement, which always uses envelope.IntentId and its digest.
- Added the paired 0003a_projection suffix migration, manifest order/checksums, bounded safe error/review metadata, an outbox review index and least-privilege SELECT on Attempts. Rollback guards live review/error/publication metadata. Committed 0001–0003 migration scripts/checksums were not changed; 0004_templates remains reserved.
- Added directly and idempotently bootstrapped projection/reconciliation Quartz control jobs (15s/60s), each with a 100-envelope batch and 20s cancellation deadline. Scheduler starts only WorkerDock.Schedule; its execution publisher is retained, uses ZeroSizeThreadPool and is never started.
- Scheduler stores the exact future WorkerDock.Quartz.Dispatch.ExecuteTemplateJob, WorkerDock.Quartz identity using the verified public lazy JobType API with explicit execution metadata flags. No no-op executor or test executor is shipped. Task08 supplies the guarded implementation and must recheck authorization after acquisition.
- Added real SQL fixtures for restricted runtime publication/reads/ack, concurrent replay after fresh Quartz handles, terminal retirement, retry metadata, tenant fencing and paired migration guards. Added exact memory, migration, runtime Scheduler and disposable SQL test commands to docs/database.md, docs/development.md and docs/quartz-integration.md.

## Approved dependency adjustments

The controller explicitly approved extending Task03's publisher facade with GetJobDetail/GetTrigger/GetTriggerState and string-only immutable identity metadata. Start, standby, deletion, rescheduling, clearing and other administration remain denied. Existing ScheduleJob supports missing-trigger repair; no reset/delete operation was added. A control-only job-factory overload on QuartzTopology allows dependency-injected maintenance jobs without putting service instances in JobDataMap. Its actual invocation is tested.

The controller approved the read-only business-state port, published-candidate review port and bounded metadata writes, and required the ordered 0003a_projection suffix rather than consuming reserved 0004_templates. It also approved storing an exact unloaded wrapper identity only after a focused public-API proof; that proof passes in memory. SQL type loading remains pending.

## TDD evidence

1. RED: `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~PublicationRecoveryTests --no-restore` against explicit publication/reconciliation stubs: 23 failed, 0 passed. Failures were NotImplementedException from QuartzPublisher.PublishAsync or DispatchReconciler.ReconcileAsync, as expected. Initial test compilation exposed Quartz 4.5.1 signature differences; those test signatures were corrected before recording behavioral RED. Full output is in task-06-red.log beside this report.
2. GREEN: the same focused command after implementation: 23 passed, 0 failed.
3. RED: `dotnet test tests/WorkerDock.IntegrationTests --no-restore --filter 'FullyQualifiedName~Unloaded_wrapper|FullyQualifiedName~Control_maintenance'`: 2 failed. Bootstrap threw NotImplementedException; eagerly resolving the future wrapper threw `Job type WorkerDock.Quartz.Dispatch.ExecuteTemplateJob, WorkerDock.Quartz cannot be resolved.` This proved why lazy identity and explicit metadata flags were necessary.
4. GREEN: the full focused command after direct registration and lazy identity implementation: 25 passed, 0 failed.
5. Added real consumption/control-factory execution, facade denial and explicit retry ordinal coverage; focused suite reached 28 passing.
6. RED: `dotnet test tests/WorkerDock.IntegrationTests --no-restore --filter FullyQualifiedName~Existing_trigger_cannot`: 1 failed with `Assert.Throws() Failure: No exception was thrown` for a stored trigger bypassing the future deadline/template group.
7. GREEN: full focused command after stored-trigger validation: 29 passed, 0 failed.

The 29 focused cases cover before/after Quartz write, before/after ack, actual probe consumption before ack replay, concurrent publishers, changed priority with a stable digest, forged input/stored digest, unsupported kinds/versions, stale pinned revision, errored published trigger, eligible missing trigger repair, future retry due time/ordinal, unloaded exact wrapper storage/replay, control bootstrap and actual bounded maintenance execution, prohibited facade calls and earlier/wrong-group trigger corruption.

## Final verification

- `dotnet test WorkerDock.slnx -c Release --no-restore --logger 'trx;LogFileName=task06-full.trx' --results-directory .superpowers/sdd/2026-10-17-workerdock-02-execution/test-results`: UnitTests 28 passed; IntegrationTests 106 passed, 33 failed, 0 skipped. Every failure has exactly the SQL_TEST_BLOCKED fixture message below; none reached a database operation. The shared fixed TRX filename produced a logger overwrite notice when IntegrationTests followed UnitTests; the full console log retains both suite summaries at task-06-full.log.
- `dotnet test tests/WorkerDock.IntegrationTests -c Release --no-restore --filter 'Store!=Sql&FullyQualifiedName!~WorkerDock.IntegrationTests.JobSqlIntegrationTests&FullyQualifiedName!~WorkerDock.IntegrationTests.TenantSettingsTests&FullyQualifiedName!~WorkerDock.IntegrationTests.MigrationTests'`: 102 passed, 0 failed, output clean. This broad standalone filter also excludes four pre-connection validation cases in the mixed SQL classes; those four passed in the full run above.
- `dotnet build WorkerDock.slnx -c Release --no-restore`: succeeded, 0 warnings, 0 errors, including new Scheduler project and lockfile.
- All four tenant migration up/down SHA-256 pairs match the manifest. New SQL and manifest files were normalized to required LF bytes before hashing/staging, avoiding checkout checksum drift.
- `git diff --cached --check`: exit 0, no whitespace findings.

## Files changed

WorkerDock.slnx; database/tenant/0003a_projection.up.sql, .down.sql and manifest.json; docs/database.md, development.md and quartz-integration.md; src/WorkerDock.Application/Dispatch/{DispatchIntent,ProjectionEnvelope,IProjectionStore}.cs; src/WorkerDock.Infrastructure/Dispatch/SqlProjectionStore.cs; src/WorkerDock.Quartz/Dispatch/{QuartzPublisher,DispatchReconciler,ProjectionJob}.cs; src/WorkerDock.Quartz/Hosting/{PublisherScheduler,QuartzTopology}.cs; src/WorkerDock.Scheduler/{Program.cs,WorkerDock.Scheduler.csproj,packages.lock.json}; tests/WorkerDock.IntegrationTests/{PublicationRecoveryTests,ProjectionSqlTests}.cs; this report.

## Self-review and concerns

Reviewed the complete scoped diff against the brief. Corrected the stored-trigger deadline/template-group gap using a concrete RED/GREEN test, reserved review capacity so pending work cannot hide published errors, used the pinned policy priority rather than a hardcoded projection priority, and normalized SQL bytes before final checksums. No separate work-table claim loop, Quartz table writes, secret/configuration payload or executable stand-in was introduced.

Memory proof covers API behavior and fault recovery only. Task08's transactional attempt-start gate is still required to suppress a business-state change racing a publication read and to authorize actual effects. SQL durability, runtime grants, migrations, isolation behavior, unloaded-type persistence/loading and multi-process/cluster fault/restart behavior remain unverified until the documented disposable SQL environment and production worker exist. Permanent trigger errors require visible investigation and are not automatically reset. The five new SQL tests are implemented and compile, but are explicitly among the blocked cases below.

## Full-run SQL blockers (not passes)

Every following case failed before database creation with:
`SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.`
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "* * * * *", zone: "America/New_York", start: "2026-11-01T05:59:00Z", first: "2026-11-01T05:59:00Z", second: "2026-11-01T07:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "*/15 * * * *", zone: "America/New_York", start: "2026-11-01T05:45:00Z", first: "2026-11-01T05:45:00Z", second: "2026-11-01T07:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "0 0 13 * 5", zone: "Etc/UTC", start: "2026-10-01T00:00:00Z", first: "2026-11-13T00:00:00Z", second: "2027-08-13T00:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "0,30 2 * * *", zone: "Australia/Lord_Howe", start: "2026-10-03T14:00:00Z", first: "2026-10-03T15:30:00Z", second: "2026-10-04T15:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "30 1 * * *", zone: "America/New_York", start: "2026-11-01T04:00:00Z", first: "2026-11-01T05:30:00Z", second: "2026-11-02T06:30:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "30 2 * * *", zone: "America/New_York", start: "2026-03-08T05:00:00Z", first: "2026-03-09T06:30:00Z", second: "2026-03-10T06:30:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "45 1 * * *", zone: "Australia/Lord_Howe", start: "2026-04-04T14:00:00Z", first: "2026-04-04T14:45:00Z", second: "2026-04-05T15:15:00Z")
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Audit_failure_rolls_back_sql_job_revision(snapshot: False)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Audit_failure_rolls_back_sql_job_revision(snapshot: True)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Empty_execution_schema_can_roll_back_and_reapply
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Manual_key_is_deduplicated_with_atomic_audit_and_outbox(snapshot: False)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Manual_key_is_deduplicated_with_atomic_audit_and_outbox(snapshot: True)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Outbox_failure_rolls_back_manual_execution_and_audit
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Revision_audit_records_bounded_transition_and_outcome
- WorkerDock.IntegrationTests.MigrationTests.Checksum_tamper_and_wrong_instance_fail_closed(snapshot: False)
- WorkerDock.IntegrationTests.MigrationTests.Checksum_tamper_and_wrong_instance_fail_closed(snapshot: True)
- WorkerDock.IntegrationTests.MigrationTests.Up_down_up_preserves_schema_contract(snapshot: False)
- WorkerDock.IntegrationTests.MigrationTests.Up_down_up_preserves_schema_contract(snapshot: True)
- WorkerDock.IntegrationTests.ProjectionSqlTests.Projection_migration_is_paired_and_guards_live_reconciliation_metadata
- WorkerDock.IntegrationTests.ProjectionSqlTests.Publication_survives_sql_handle_restart_and_concurrent_publishers(snapshot: False)
- WorkerDock.IntegrationTests.ProjectionSqlTests.Publication_survives_sql_handle_restart_and_concurrent_publishers(snapshot: True)
- WorkerDock.IntegrationTests.ProjectionSqlTests.Sql_projection_ack_integrity_and_retry_deadline_are_persisted(snapshot: False)
- WorkerDock.IntegrationTests.ProjectionSqlTests.Sql_projection_ack_integrity_and_retry_deadline_are_persisted(snapshot: True)
- WorkerDock.IntegrationTests.QuartzCapacityTests.Locked_acquisition_does_not_multiply_capacity(snapshot: False)
- WorkerDock.IntegrationTests.QuartzCapacityTests.Locked_acquisition_does_not_multiply_capacity(snapshot: True)
- WorkerDock.IntegrationTests.QuartzSqlTopologyTests.Publisher_restart_persists_one_delivery_for_two_worker_processes(snapshot: False)
- WorkerDock.IntegrationTests.QuartzSqlTopologyTests.Publisher_restart_persists_one_delivery_for_two_worker_processes(snapshot: True)
- WorkerDock.IntegrationTests.QuartzSqlTopologyTests.Schema_round_trip_and_runtime_scheduler_starts_without_DDL(snapshot: False)
- WorkerDock.IntegrationTests.QuartzSqlTopologyTests.Schema_round_trip_and_runtime_scheduler_starts_without_DDL(snapshot: True)
- WorkerDock.IntegrationTests.TenantSettingsTests.Audit_failure_rolls_back_setting(snapshot: False)
- WorkerDock.IntegrationTests.TenantSettingsTests.Audit_failure_rolls_back_setting(snapshot: True)
- WorkerDock.IntegrationTests.TenantSettingsTests.Update_is_audited_and_stale_version_is_rejected(snapshot: False)
- WorkerDock.IntegrationTests.TenantSettingsTests.Update_is_audited_and_stale_version_is_rejected(snapshot: True)
