# Task 9 report

## Result

- Replaced the draft tenant migration chain with the `0001_initial` fresh-install baseline and guarded rollback; updated `manifest.json`, `current-state.sql`, runtime permissions, migration fixtures, and migration checks.
- Folded the pre-existing `0010_remove_builtin_v1` desired state into the baseline: built-in v1.0 catalog entries are removed and v1.1 entries remain. Fresh-install SQL coverage asserts those catalog counts. The old rollback/preflight logic is migration-history-specific and is excluded from the one-baseline design; no upgrade converter remains because no tenant databases are deployed.
- Removed Quartz source/project/upstream schema, scheduler and TestHost projects, Quartz-only tests/fixtures, references, package entries, and superseded migrations. The endpoint schedule evaluator now uses `FiveFieldScheduleEvaluator`; retained in-memory capacity policy coverage with a local test store.
- Added fresh-install schema inventory, manifest/checksum, tenant identity, runtime permission, and guarded rollback coverage, including the required `FreshInstall_HasNoQuartzObjects` assertion.

## Plan scan conflict

The plan asks for a broad `rg -n 'Quartz|QRTZ|WDS_|WDE_' src database/tenant tests --glob '!*.md'` with no matches. This conflicts with the required `FreshInstall_HasNoQuartzObjects` test and its literal assertions. Per the ruling, the exact scan returns only four expected lines in `tests/WorkerDock.IntegrationTests/CustomSqlSchemaTests.cs`; the production, schema, project, and lockfile scan returns no matches.

## Verification

- `dotnet build WorkerDock.slnx --no-restore --artifacts-path <writable artifacts directory>`: pass, 0 warnings and 0 errors.
- Database-independent fresh baseline inventory and runtime permission tests: pass, 2/2.
- Planned SQL filters (`CustomSqlSchemaTests|MigrationTests|TenantSnapshotTests`): SQL execution blocked because `WORKERDOCK_TEST_SQL` is unset; tests that construct SQL fixtures report `SQL_TEST_BLOCKED`.
- Full schema parity, SQL rollback execution, and `FreshInstall_HasNoQuartzObjects` database execution remain unverified pending `WORKERDOCK_TEST_SQL`.

## Preserved pre-existing work

The prior settings/template UI and help changes remain in the worktree. The original untracked 0010 migration/preflight sources were absent from the other checkout and worktrees; generated build-output copies of the SQL scripts were recovered and checked against the baseline. Per the ruling, the original migration-history-specific rollback/preflight code is intentionally superseded, while its desired catalog state is represented by the fresh-install baseline. Task 10 documentation remains outside this change.

## Rollback guard review fix (round 1)

- Replaced the four-table rollback check with a guard that dynamically checks every table in the `workerdock` schema before any drop. It permits only the known baseline seed tables, then verifies their expected seed counts and values, including tenant settings, integration configuration, template catalog/policies, default policy revision, retention, and singleton state rows.
- Added rollback refusal cases for alert sinks, archive batches, changed tenant settings, connection profiles, and changed integration settings. Added a database-independent check that the dynamic guard precedes table drops and that its seed allowlist matches the reviewed set. Existing migration round-trip coverage verifies the default seeded baseline still rolls back cleanly.
- Updated `manifest.json` with the recalculated `0001_initial.down.sql` checksum: `998F1688C568B39554A4E72C3ED62AEF22CED62970D003B7385F1E15E16643A5`.

### Verification

- `dotnet build WorkerDock.slnx --no-restore --artifacts-path <writable artifacts directory>`: pass, 0 warnings and 0 errors.
- Schema inventory, permission, and rollback-guard inventory tests: pass, 3/3.
- Rollback SQL tests for business rows, alert/archive rows, settings, and profile rows were attempted. Execution is blocked because `WORKERDOCK_TEST_SQL` is unset; each fixture reports `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.`
Executed commands:

```powershell
dotnet build WorkerDock.slnx --no-restore --artifacts-path 'C:\Users\bruno\OneDrive\Documentos\ChatGPT\WorkerDock 2\.codex-task9-artifacts'
# Compilação com êxito; 0 Aviso(s); 0 Erro(s).

dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-build --artifacts-path 'C:\Users\bruno\OneDrive\Documentos\ChatGPT\WorkerDock 2\.codex-task9-artifacts' --filter "FullyQualifiedName~Fresh_baseline_has_one_target_and_a_reviewed_table_inventory|FullyQualifiedName~Fresh_baseline_permissions_keep_audit_and_archive_protection|FullyQualifiedName~Fresh_baseline_rollback_guard_covers_every_table_except_known_seeds"
# Aprovado: 3; Com falha: 0.

dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-build --artifacts-path 'C:\Users\bruno\OneDrive\Documentos\ChatGPT\WorkerDock 2\.codex-task9-artifacts' --filter "FullyQualifiedName~Fresh_baseline_rollback_refuses_user_state_outside_execution_tables|FullyQualifiedName~Fresh_baseline_rollback_is_guarded_by_business_data"
# Blocked: WORKERDOCK_TEST_SQL is unset; SQL_TEST_BLOCKED was reported by fixture construction.
```