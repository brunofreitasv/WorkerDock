# Direct runtime-user grants design

## Intent

Make both supported fresh-database setup paths work with an existing database user chosen by the operator. The scripts grant that principal the runtime permissions already defined by the baseline. Setup must not create a `WorkerDockRuntime` role, a database user, or a server login. This removes an unnecessary database object and avoids requiring elevated identity provisioning from WorkerDock scripts.

## Goals

- Accept an existing target-database user in the migration and snapshot paths.
- Support an explicit initial-setup mode that generates a tenant GUID and displays it only after successful setup; reuse mode requires the operator to pass the tenant's existing GUID.
- Apply the same least-privilege object, column, procedure, and explicit deny permissions that the runtime currently receives through `WorkerDockRuntime`.
- Validate the supplied principal before creating tenant objects; fail with one clear error if it is missing or is not a supported database user.
- Keep migration operations and snapshot execution free of `CREATE ROLE`, `CREATE USER`, and `CREATE LOGIN` statements.
- Keep the rollback fresh-install-only and remove only grants made by this baseline; never drop the supplied principal.
- Ensure the current-state snapshot records one satisfied `SchemaHistory` row with the same migration checksums so the migration runner accepts it unchanged.
- Update tests and setup documentation so the approved workflow is unambiguous.

## Non-goals

- Creating, mapping, or managing SQL Server logins and database users.
- Changing runtime data access or broadening its permissions.
- Supporting database upgrades or preserving an already provisioned `WorkerDockRuntime` role.
- Introducing another role, schema, helper table, or permanent permission-management database object.

## Constraints and current state

- The fresh baseline has no deployed tenant databases, so its checksummed SQL and rollback can be revised without an upgrade migration.
- `0001_initial.up.sql` currently creates `WorkerDockRuntime` and grants it permissions throughout the file. `0001_initial.down.sql` drops that role.
- `current-state.sql` also creates the role and grants it the same runtime permissions; its snapshot guard permits an existing user principal but requires the rest of the database to be fresh.
- `permissions.sql` and `permissions.rollback.sql` currently add/remove a supplied user as a member of `WorkerDockRuntime` rather than granting object permissions directly.
- `MigrationRunner` currently binds only `@InstanceId`; the tenant migration entry point will need to pass the selected runtime database principal as well.
- SQL Server treats a principal name in `GRANT` as an identifier, so generated grant statements must safely delimit it with `QUOTENAME`; no user-supplied text may be concatenated as an unquoted identifier. Accept exact existing principal names from 1 to 128 characters, reject null/empty/whitespace-only names, and resolve the exact name against `sys.database_principals` before setup. Support SQL and Windows user principal types; reject roles and non-user principals.
- Runtime and migration identities remain separate. The migration identity needs authority to create tenant objects and grant the permissions; the supplied runtime user needs only runtime permissions.

## Approaches

1. **Grant directly to the supplied existing database user (selected).** Pass its name into the migration runner and snapshot setup; validate it, then apply the existing per-object grants and denies directly. This removes the role and all membership scripts. It matches the request and minimizes database principals, at the cost of targeting every grant at the supplied principal.
2. **Grant through a pre-existing database role.** The scripts would accept a role name and grant permissions to it, while an administrator manages user membership outside WorkerDock. This keeps the grant target reusable but retains a role object and does not meet the selected direct-user requirement.
3. **Keep `WorkerDockRuntime` and only stop creating it.** An administrator would have to provision the role in advance. This preserves the current permission layout but keeps the unwanted role and makes setup fail unless an extra object is provisioned.

Direct grants are selected because they satisfy the request with the fewest database objects and preserve the current permission boundaries.

## Design

- Add a required runtime-principal input to `MigrationRunner` and `Invoke-TenantMigration.ps1` for apply and rollback. Validate the input as a database principal name, check that it exists in the target database as a user principal before applying or rolling back the baseline, and bind it as a SQL parameter in each migration batch. The repository currently has only the fresh-install baseline; future migration work may refine this contract as part of its separately reviewed upgrade design.
- Add mutually exclusive `-InitialSetup` and `-InstanceId` modes to `Invoke-TenantMigration.ps1`. With `-InitialSetup`, require a fresh target database, generate a new GUID before apply, and print it only after the migration succeeds so the same value can be copied into tenant configuration. Without `-InitialSetup`, require `-InstanceId` and pass that existing tenant GUID through unchanged; if the baseline is already applied, it must match the recorded `TenantSettings.InstanceId`. Rollback always requires the existing `-InstanceId`; it never generates a new value.
- For manual `current-state.sql` setup, document generating one GUID for initial provisioning and replacing every instance token with it. For repeat setup/configuration, reuse the tenant's existing GUID. The SQL snapshot itself remains a manually rendered script and does not generate IDs.
- Keep the per-object runtime permission inventory and its explicit `DENY` statements unchanged in meaning. Target each grant/deny at the supplied database user using safely quoted dynamic SQL. Do not grant permissions to `public`, a broad schema, or the migration identity.
- Update the snapshot to accept a runtime-principal placeholder alongside the tenant GUID. Validate both inputs and the existing user before changing the database. Snapshot remains fresh-database-only and creates no user or role.
- Remove role creation, membership setup, and role deletion from the baseline, snapshot, permission scripts, rollback, tests, and documentation. If the standalone permission scripts remain useful, make them apply/revoke the same direct permission set for the supplied user and document their exact prerequisite; otherwise remove them in favor of the baseline paths.
- Ensure rollback receives the same existing database user name used by apply, revokes this baseline permissions from that user, and never deletes or otherwise changes the user. Keep the existing business-data rollback guard.
- Update `manifest.json` for the revised baseline checksum. Update tests to provision a disposable database user outside the production scripts, verify every expected permission and deny is assigned to that user, verify no `WorkerDockRuntime` role exists, verify absent/invalid users fail before partial setup, and verify rollback leaves the user in place.
- Correct setup docs: the database user must already be mapped in the target database; scripts only validate it and grant runtime access. Document passing the same principal name to either setup path. Correct stale snapshot guidance that refers to editing `CREATE USER` or role-membership statements.

## Implementation calls

- Accept SQL-authenticated and Windows-authenticated database users; reject roles and non-user principals. State this policy in docs/tests.
- Keep the grant inventory matching between the migration and snapshot, with tests that detect drift; do not add a persistent SQL object.
- Keep the current explicit permission granularity, including column-level grants and archive delete denials.
- Require the same runtime database user for apply and rollback; rollback revokes its baseline permissions and leaves the user intact.
- Use `-InitialSetup` only for first-time apply to a fresh database; require the existing `-InstanceId` for reuse and all rollbacks. Print the generated GUID only after successful initial apply.
- Validate exact principal names as 1-128 characters, reject empty/whitespace-only input, and use `QUOTENAME`; accept existing SQL and Windows database users only.

## Acceptance checks

- Both fresh-install paths accept an existing database user and grant it the required runtime permissions.
- Initial setup generates and reports one GUID only after success; reuse mode requires and preserves the supplied existing tenant GUID.
- Neither path or permission helper creates a role, database user, or server login.
- The migration rejects a missing or unsupported principal before any partial schema is committed.
- Initial-setup mode rejects a non-fresh database; reuse mode retains the supplied GUID and verifies it against an already applied baseline.
- The snapshot rejects an unrendered/invalid principal or a non-fresh database before later batches produce cascaded object errors.
- Migration and snapshot permission inventories match; rollback removes only baseline grants and retains the principal.
- Focused integration tests, manifest checksum checks, and whitespace checks pass. Live SQL validation is reported separately if no SQL Server test instance is available.