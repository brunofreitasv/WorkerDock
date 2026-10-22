# Task 2 report

## Implementation

- Added TenantSqlStartup with legacy rejection in every adapter mode, eager SQL settings validation and vault resolution, and one shared TenantDatabaseOptions.
- Registered the environment-sensitive fallback vault with TryAddSingleton so a real provider registration can replace it.
- Await initialization after Build and before RunAsync; every web SQL factory consumes initialized Options.
- Preserved Development Memory, Production Memory unavailable behavior, health routes and authorization.
- Removed legacy connection string keys from the specified memory fixtures and retained separate memory liveness/readiness coverage.
- Added startup tests in Development/Production plus a Production custom-vault SQL-service construction test.

## TDD and validation

1. Red command: dotnet test tests/WorkerDock.IntegrationTests -c Release --filter "FullyQualifiedName~TenantSqlWebStartupTests|FullyQualifiedName~WebHostTests"
   Result: five expected failures (missing eager validation, missing legacy rejection, custom vault SQL services unavailable), three existing WebHost tests passed.
   The first trial needed a valid Production DataProtection fixture; corrected before the final red run.
2. Green command: dotnet test tests/WorkerDock.IntegrationTests -c Release --filter "FullyQualifiedName~TenantSqlWebStartupTests|FullyQualifiedName~WebHostTests|FullyQualifiedName~AccessPolicyTests|FullyQualifiedName~MonitoringAccessTests|FullyQualifiedName~ExecutionControlsEndpointTests"
   Result: 44 passed, zero failed, zero skipped. Build produced no warnings.
3. Full command: dotnet test tests/WorkerDock.IntegrationTests -c Release --no-build
   Result: 336 passed, 134 failed, zero skipped, 470 total.
4. Repeated the full no-build run with a TRX logger because console output was truncated and a complete failure inventory was needed. Identical totals: 336 passed, 134 failed.
   Evidence: test-results/task-2-integration.trx and task-2-full-test-output.log beside this report.
5. git diff --check on the changed existing Task 2 files passed.

## Failure assessment

112 failures stop in the SQL fixture before exercising persistence because WORKERDOCK_TEST_SQL is absent. First blocker: SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.

The other 22 fail with UnknownTemplate (19 exceptions, two expected PersistenceUnavailable assertions, one HTTP 400 response). Existing dirty tests changed their job versions from 1.0.0 to 1.1.0. JobCommandTestCatalog.Create still inherits descriptor version 1.0.0 from TemplateCatalogTests.Descriptor. Those mismatched fixtures cause TemplateJobValidation.RequireRegisteredAsync to reject the jobs. Tests directly constructing InMemoryJobCommands fail before any web host or Task 2 code; the endpoint test injects the same mismatched custom catalog. The Task 2 commit does not change that catalog or those job fixture versions. No failure was traced to Task 2.

## Scope isolation

Program.cs user changes removing trusted 1.0.0 templates and permitting settings reads are excluded from this commit. AccessPolicyTests user changes to settings permissions and the added reader test are also excluded. No unrelated dirty files or hunks are staged. A temporary Git index is used to stage the scoped content while preserving the working tree.

## Non-SQL failures (each assessed as existing catalog/job-version mismatch)
- WorkerDock.IntegrationTests.AttemptLifecycleTests.Memory_job_commands_and_execution_controls_share_the_same_execution_and_receipt — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.CommandOutcomeTests.Memory_failed_audit_rolls_back_acknowledgement_and_command_outcome — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.CommandOutcomeTests.Memory_publication_receipts_survive_failure_and_lost_ack(rejected: False) — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.CommandOutcomeTests.Memory_publication_receipts_survive_failure_and_lost_ack(rejected: True) — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.IntegrationSettingsTests.Manager_settings_are_versioned_validated_and_snapshotted_without_secret_values — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.IntegrationSettingsTests.Retry_attempt_uses_latest_settings_revision_while_first_attempt_keeps_its_snapshot — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Disabled_job_rejects_run_and_actor_scope_isolated — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Execution_pins_immutable_job_revision — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Failed_audit_or_outbox_prevents_job_mutation(phase: "Audit") — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Failed_audit_or_outbox_prevents_job_mutation(phase: "Outbox") — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Management_routes_return_safe_receipts_and_stale_edit_problem — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Manual_and_stop_receipts_follow_authoritative_attempt_commits(stopFirst: False) — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Manual_and_stop_receipts_follow_authoritative_attempt_commits(stopFirst: True) — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Receipt_kind_survives_request_key_expiry_while_execution_is_still_queued — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Repeated_key_returns_original_after_job_edit — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Repeated_manual_key_returns_original_for_24_hours — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Revision_audit_identifies_transition_and_outcome_without_configuration — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Save_bounds_the_serialized_execution_envelope(length: 54000, accepted: True) — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Stale_edit_returns_412_without_revision_or_audit — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.JobCommandTests.Unconfirmed_stop_failure_and_unpublished_cancellation_settle_receipts — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.SecretProjectionTests.Rotation_preserves_nonsecret_application_Quartz_and_API_records — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.
- WorkerDock.IntegrationTests.SqlProfileEndpointTests.Execution_pins_revision_after_profile_rotation — existing catalog 1.0.0 / job 1.1.0 mismatch; no Task 2 defect.

## SQL fixture failures (each blocked before Task 2 code by missing SQL test configuration)

- WorkerDock.IntegrationTests.AlertDeliveryTests.Expired_lease_is_recorded_and_recovered_as_a_new_bounded_attempt
- WorkerDock.IntegrationTests.AttemptSqlTests.Competing_boots_stop_ordering_and_sql_fences_are_durable(snapshot: False)
- WorkerDock.IntegrationTests.AttemptSqlTests.Competing_boots_stop_ordering_and_sql_fences_are_durable(snapshot: True)
- WorkerDock.IntegrationTests.AttemptSqlTests.Empty_attempt_migration_round_trip_restores_schema
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "* * * * *", zone: "America/New_York", start: "2026-11-01T05:59:00Z", first: "2026-11-01T05:59:00Z", second: "2026-11-01T07:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "*/15 * * * *", zone: "America/New_York", start: "2026-11-01T05:45:00Z", first: "2026-11-01T05:45:00Z", second: "2026-11-01T07:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "0 0 13 * 5", zone: "Etc/UTC", start: "2026-10-01T00:00:00Z", first: "2026-11-13T00:00:00Z", second: "2027-08-13T00:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "0,30 2 * * *", zone: "Australia/Lord_Howe", start: "2026-10-03T14:00:00Z", first: "2026-10-03T15:30:00Z", second: "2026-10-04T15:00:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "30 1 * * *", zone: "America/New_York", start: "2026-11-01T04:00:00Z", first: "2026-11-01T05:30:00Z", second: "2026-11-02T06:30:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "30 2 * * *", zone: "America/New_York", start: "2026-03-08T05:00:00Z", first: "2026-03-09T06:30:00Z", second: "2026-03-10T06:30:00Z")
- WorkerDock.IntegrationTests.CalendarPersistenceTests.Restart_preserves_filtered_occurrences(expression: "45 1 * * *", zone: "Australia/Lord_Howe", start: "2026-04-04T14:00:00Z", first: "2026-04-04T14:45:00Z", second: "2026-04-05T15:15:00Z")
- WorkerDock.IntegrationTests.CapacityPolicySqlTests.Empty_capacity_migration_round_trip_preserves_paired_schema
- WorkerDock.IntegrationTests.CapacityPolicySqlTests.Sql_epoch_fences_acquired_delivery_and_preserves_exclusive_order(snapshot: False)
- WorkerDock.IntegrationTests.CapacityPolicySqlTests.Sql_epoch_fences_acquired_delivery_and_preserves_exclusive_order(snapshot: True)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_ack_audit_failure_rolls_back_publication_and_command_outcome(snapshot: False)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_ack_audit_failure_rolls_back_publication_and_command_outcome(snapshot: True)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_publication_receipts_match_memory_and_survive_lost_ack(snapshot: False, rejected: False)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_publication_receipts_match_memory_and_survive_lost_ack(snapshot: False, rejected: True)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_publication_receipts_match_memory_and_survive_lost_ack(snapshot: True, rejected: False)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_publication_receipts_match_memory_and_survive_lost_ack(snapshot: True, rejected: True)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_stop_completion_ordering_settles_confirmed_stop_only(snapshot: False, stopFirst: False)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_stop_completion_ordering_settles_confirmed_stop_only(snapshot: False, stopFirst: True)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_stop_completion_ordering_settles_confirmed_stop_only(snapshot: True, stopFirst: False)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_stop_completion_ordering_settles_confirmed_stop_only(snapshot: True, stopFirst: True)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_unconfirmed_running_stop_fails_and_stale_completion_cannot_apply_it(snapshot: False)
- WorkerDock.IntegrationTests.CommandOutcomeSqlTests.Sql_unconfirmed_running_stop_fails_and_stale_completion_cannot_apply_it(snapshot: True)
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Archive_json_retains_value_context_and_snapshot_without_secrets
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "context-case")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "context-trailing-space")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "error-case")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "error-trailing-space")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "error")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "first-start")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "snapshot-case")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Execution_value_state_trigger_rejects_rewriting_populated_values(stateKind: "snapshot-trailing-space")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Migration_keeps_legacy_rows_null
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Rollback_refuses_populated_dynamic_state(stateKind: "context")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Rollback_refuses_populated_dynamic_state(stateKind: "plan")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Rollback_refuses_populated_dynamic_state(stateKind: "snapshot")
- WorkerDock.IntegrationTests.DynamicValuePersistenceTests.Save_load_duplicate_freeze_dynamic_plan
- WorkerDock.IntegrationTests.ExecutionValueSnapshotSqlTests.Crash_after_snapshot_commit_reuses_values_without_a_lifecycle_event
- WorkerDock.IntegrationTests.IntegrationSettingsTests.Sql_revision_history_rowversion_audit_and_rollback_guard_are_durable
- WorkerDock.IntegrationTests.JobLifecycleTests.Archive_tombstones_future_schedule_but_preserves_running_execution
- WorkerDock.IntegrationTests.JobLifecycleTests.Catch_up_count_is_shared_across_bounded_occurrence_pages
- WorkerDock.IntegrationTests.JobLifecycleTests.Changed_timezone_rules_advance_revision_and_enqueue_future_projection
- WorkerDock.IntegrationTests.JobLifecycleTests.Competing_dispatchers_materialize_each_logical_occurrence_once
- WorkerDock.IntegrationTests.JobLifecycleTests.Concurrent_schedule_edits_with_one_etag_reject_the_loser
- WorkerDock.IntegrationTests.JobLifecycleTests.Disable_wins_over_stale_Quartz_trigger
- WorkerDock.IntegrationTests.JobLifecycleTests.Enable_rejects_scheduled_timestamp_binding_when_job_has_no_schedule
- WorkerDock.IntegrationTests.JobLifecycleTests.Enable_releases_an_accepted_held_delivery
- WorkerDock.IntegrationTests.JobLifecycleTests.Manual_run_remains_available_while_schedule_is_paused
- WorkerDock.IntegrationTests.JobLifecycleTests.Materialized_execution_pins_the_current_profile_revision
- WorkerDock.IntegrationTests.JobLifecycleTests.Older_occurrence_horizon_does_not_move_cursor_back_or_duplicate_work
- WorkerDock.IntegrationTests.JobLifecycleTests.Pause_resume_advances_cursor_without_replaying_paused_interval
- WorkerDock.IntegrationTests.JobLifecycleTests.Repeated_enable_returns_a_persisted_command_receipt
- WorkerDock.IntegrationTests.JobLifecycleTests.Saving_an_edited_paused_schedule_keeps_it_paused
- WorkerDock.IntegrationTests.JobLifecycleTests.Timezone_reconciliation_rotates_past_the_first_hundred_schedules
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Audit_failure_rolls_back_sql_job_revision(snapshot: False)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Audit_failure_rolls_back_sql_job_revision(snapshot: True)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Empty_execution_schema_can_roll_back_and_reapply
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Manual_key_is_deduplicated_with_atomic_audit_and_outbox(snapshot: False)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Manual_key_is_deduplicated_with_atomic_audit_and_outbox(snapshot: True)
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Outbox_failure_rolls_back_manual_execution_and_audit
- WorkerDock.IntegrationTests.JobSqlIntegrationTests.Revision_audit_records_bounded_transition_and_outcome
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.All_legacy_revisions_migrate_and_unrelated_templates_remain
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Execution_reference_blocks_removal_without_mutating_rows(state: "Queued")
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Execution_reference_blocks_removal_without_mutating_rows(state: "Succeeded")
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Fresh_install_removes_bootstrap_v1_without_v11_catalog
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Invalid_legacy_configuration_blocks_removal_without_leakage
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Missing_or_mismatched_v11_catalog_blocks_removal(defect: "malformed")
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Missing_or_mismatched_v11_catalog_blocks_removal(defect: "mismatch")
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Missing_or_mismatched_v11_catalog_blocks_removal(defect: "missing")
- WorkerDock.IntegrationTests.LegacyTemplateRemovalTests.Missing_or_mismatched_v11_catalog_blocks_removal(defect: "retired")
- WorkerDock.IntegrationTests.MigrationTests.Checksum_tamper_and_wrong_instance_fail_closed(snapshot: False)
- WorkerDock.IntegrationTests.MigrationTests.Checksum_tamper_and_wrong_instance_fail_closed(snapshot: True)
- WorkerDock.IntegrationTests.MigrationTests.Fresh_custom_schema_installs_and_rolls_back_all_pairs(snapshot: False)
- WorkerDock.IntegrationTests.MigrationTests.Fresh_custom_schema_installs_and_rolls_back_all_pairs(snapshot: True)
- WorkerDock.IntegrationTests.MigrationTests.Up_down_up_preserves_schema_contract(snapshot: False)
- WorkerDock.IntegrationTests.MigrationTests.Up_down_up_preserves_schema_contract(snapshot: True)
- WorkerDock.IntegrationTests.ProjectionSqlTests.Maintenance_cursor_survives_restart_and_bounds_large_job_backlog(snapshot: False)
- WorkerDock.IntegrationTests.ProjectionSqlTests.Maintenance_cursor_survives_restart_and_bounds_large_job_backlog(snapshot: True)
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
- WorkerDock.IntegrationTests.SqlProfileTests.Migration_round_trip_and_audit_failure_are_atomic
- WorkerDock.IntegrationTests.SqlProfileTests.Restricted_profile_revisions_pin_and_diagnostics_are_durable(snapshot: False)
- WorkerDock.IntegrationTests.SqlProfileTests.Restricted_profile_revisions_pin_and_diagnostics_are_durable(snapshot: True)
- WorkerDock.IntegrationTests.SqlTemplateTests.Commit_ack_loss_is_unknown_not_safe_retry
- WorkerDock.IntegrationTests.SqlTemplateTests.Confirmed_failure_rolls_back_but_cancellation_remains_conservative
- WorkerDock.IntegrationTests.SqlTemplateTests.Diagnostic_auth_catalog_failures_and_secret_rotation_remain_safe
- WorkerDock.IntegrationTests.SqlTemplateTests.Diagnostic_ignores_command_text_and_returns_safe_connectivity_code
- WorkerDock.IntegrationTests.SqlTemplateTests.Dynamic_reference_date_binds_as_sql_date
- WorkerDock.IntegrationTests.SqlTemplateTests.Typed_parameters_do_not_interpolate_sql(mode: "Procedure")
- WorkerDock.IntegrationTests.SqlTemplateTests.Typed_parameters_do_not_interpolate_sql(mode: "Query")
- WorkerDock.IntegrationTests.TemplateCatalogSqlTests.Built_in_catalog_registers_only_v11_descriptors_after_migration
- WorkerDock.IntegrationTests.TemplateCatalogSqlTests.Custom_v1_catalog_metadata_is_immutable_and_preserved(snapshot: False)
- WorkerDock.IntegrationTests.TemplateCatalogSqlTests.Custom_v1_catalog_metadata_is_immutable_and_preserved(snapshot: True)
- WorkerDock.IntegrationTests.TemplateCatalogSqlTests.Empty_templates_migration_supports_up_down_up(snapshot: False)
- WorkerDock.IntegrationTests.TemplateCatalogSqlTests.Empty_templates_migration_supports_up_down_up(snapshot: True)
- WorkerDock.IntegrationTests.TemplateCatalogSqlTests.Unrecognized_bootstrap_digest_cannot_be_promoted
- WorkerDock.IntegrationTests.TenantSettingsTests.Audit_failure_rolls_back_setting(snapshot: False)
- WorkerDock.IntegrationTests.TenantSettingsTests.Audit_failure_rolls_back_setting(snapshot: True)
- WorkerDock.IntegrationTests.TenantSettingsTests.Update_is_audited_and_stale_version_is_rejected(snapshot: False)
- WorkerDock.IntegrationTests.TenantSettingsTests.Update_is_audited_and_stale_version_is_rejected(snapshot: True)
