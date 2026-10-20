# Upgrade and restore

Tenant releases include versioned migrations and a package manifest. The application will not create or upgrade SQL objects at runtime. Tenant upgrades require a package whose tested schema compatibility window includes the database's current migration version. Center releases use persistent state format 1 and never migrate a database.

## Before an upgrade

1. Read the release notes and confirm the tenant or central package kind, target version, and supported schema window.
2. For a tenant, confirm that the target SQL Server database is backed up and that the SQL Server service account can write to and read from the chosen backup location. Choose a new `.bak` path for each attempt. For a center, choose a new `.zip` path for the verified file-state/configuration/release backup.
3. Review running executions in the dashboard. Disable new work and wait for finite executions to finish. Infinite-timeout jobs can continue indefinitely; an operator must decide whether to stop them. Stopping a Windows service can terminate its child process after the configured drain period, and external side effects may have happened even if WorkerDock cannot confirm completion.
4. Confirm that the package manifest hashes match the package files and that the existing installation metadata matches the expected instance GUID.

## Run an upgrade

Run `Upgrade-Instance.ps1` from an elevated PowerShell 7 session on the Windows host. For a tenant, supply the package, protected persistent config, install root, and a new SQL Server backup path. For example:

```powershell
pwsh -File .\deploy\Upgrade-Instance.ps1 `
  -InstallRoot 'C:\Program Files\WorkerDock\<TENANT>' `
  -ConfigPath 'C:\Program Files\WorkerDock\<TENANT>\config\tenant.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-tenant-1.1.0.zip' `
  -BackupPath '\\<SQL_BACKUP_SERVER>\WorkerDock\<TENANT>-before-1.1.0.bak'
```

The tenant script requires the corresponding IIS site, application pool, scheduler, and worker services to be stopped before switching files. Before stopping them, an operator must block admission, inspect current executions, allow finite work to finish, and decide how to handle infinite-timeout jobs. The script cannot drain jobs or infer whether an external side effect is safe to repeat. It creates and verifies a SQL Server `COPY_ONLY` backup and a checksum-verified application ZIP, checks package hashes and schema compatibility, applies the package's migrations, writes a recovery journal with both backup paths, and stages the new release. It leaves services and IIS stopped for operator review and readiness checks.

A center uses the same command with its center config/package and a new `.zip` backup path:

```powershell
pwsh -File .\deploy\Upgrade-Instance.ps1 `
  -InstallRoot 'C:\Program Files\WorkerDock\ControlCenter' `
  -ConfigPath 'C:\Program Files\WorkerDock\ControlCenter\config\center.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-center-1.1.0.zip' `
  -BackupPath 'C:\WorkerDockBackups\center-before-1.1.0.zip'
```

The center site and app pool must be stopped. The script verifies state format 1, archives and checks `registry.journal`, `health.json` when present, `center.json`, and the active release, then switches the web release. It does not connect to SQL or run the migrator. IIS remains stopped for operator review. Keep the state directory outside the release tree with Modify access limited to the center app pool identity and administrators. Uninstall must preserve this directory.

After reviewing the journal, backup, and migration result, start the services and site manually, then verify `/health/ready`, worker heartbeats, upcoming schedules, and recent execution results. Keep the previous release, configuration, and backup until the rollback window ends.

An interrupted upgrade may leave a journal and stopped services. The script does not automatically resume an interrupted upgrade. Do not delete the journal or rerun blindly. Compare its `step` and backup paths with SQL migration history, active release metadata, and both verified backups. Reconcile the installation manually or restore the database and previous compatible release; remove the journal only after the installation state is consistent. Use new backup paths if you start a new upgrade attempt. The script refuses to replace an existing release directory with different content.

## Restore

Application rollback is supported only when the previous application version is compatible with the current database migration level. Use the manifest's tested schema window and migration history to decide. Never run a down migration as a substitute for a verified restore.

For an incompatible tenant schema or damaged upgrade, stop the application and restore the verified SQL Server backup to the same isolated database identity, then point the previous compatible release at it. Preserve the tenant instance GUID and Data Protection keys. Confirm the target database identity and restore timestamp before starting any service; a restore replaces all later database changes.

For a center, restore the verified ZIP with the deployment module's `Restore-CenterBackupArchive` function to an empty staging directory. The archive verifies each file's SHA-256 before extraction and records the center instance ID and previous release version in its manifest. Restore `state/` to the configured persistent `Central.Storage.DataDirectory`, `config/center.json` to protected config storage, and `release/` under `releases/<releaseVersion>`. Keep the same `Central.InstanceId`, state directory and Data Protection keys, then point the stopped IIS site at the restored release and review readiness. The health snapshot is advisory and may become stale; the journal is authoritative. Do not restore the center archive over tenant files or a different center identity.

WorkerDock delivery is at least once. Restoring a backup or losing a process can cause already-started external work to be retried. Use idempotent job handlers and downstream idempotency keys where possible, then review execution history and external systems after recovery.
