# Manage SQL Server profiles

## Prerequisites

You need **Manage** access and the SQL Server host name plus the catalog names jobs may use. Confirm the tenant SQL service can reach the server. For Vault password authentication, request the logical password reference from the configured secret provider; the profile form never accepts the password itself.

## Role

Manage users can create, revise, diagnose, and retire profiles. Read users cannot open profile management. Revisions remain available to jobs and executions that already reference them.

## Steps

1. Open [Settings](#/settings), choose **Profiles**, then **New profile**. Enter a descriptive name, server, and one allowed catalog per line. Jobs can select only catalogs in this list.
2. Choose **Integrated** or **Vault password** authentication. For Vault password, enter the SQL user name and logical secret reference. Keep **Encrypt connection** enabled. Enable **Trust server certificate** only when required by your environment and its certificate risk is understood.
3. Choose **Save profile**. Editing creates a new revision. Retiring blocks selection by new jobs but preserves revisions already referenced by jobs or executions.
4. Select an active profile and allowed catalog, then choose **Run safe diagnostic**. It uses a fixed internal connectivity query; it does not run your job query or a procedure you enter. Follow the command status and execution record for the result.

## Example

For a reporting database, create **Reporting SQL**, use the tenant-approved SQL host, and list `Reporting` as its allowed catalog. Choose Integrated authentication if approved; otherwise select Vault password and enter the reference supplied by the secret administrator. Run the safe diagnostic against `Reporting` before choosing the profile in a job.

## Verify

Confirm the profile appears with the expected server and active status. A successful diagnostic proves connectivity only for that profile and catalog at that time. Memory mode can store profiles but cannot connect to SQL; persistence unavailable is not a connectivity success.

## Recover

If saving reports a stale revision, reload the current profile and review your edits. If the diagnostic cannot start, check server, catalog allowlist, authentication, reference, and SQL service configuration. A retired profile cannot be selected by a new job; choose an active profile.

## Related guides

See [SQL Server jobs](#/help:sql-jobs), [store and reference secrets](#/help:secrets), and [tenant settings](#/help:settings).
