# Manage SQL Server profiles

## Prerequisites

Manage permission is required. Have the SQL Server name and the catalog names that should be allowlisted. For Vault password authentication, configure a logical password secret reference in the tenant secret provider.

## Role

Manage. Profiles contain connection settings and revisions; Read users cannot open profile management.

## Steps

1. Open [Settings](#/settings), select the **Profiles** tab, then select **New profile**. Enter a profile name, server, and one allowed catalog per line.
2. Choose **Integrated** authentication or **Vault password**. For the latter, enter a user name and a logical secret reference; never paste the password itself. Keep **Encrypt connection** enabled and use **Trust server certificate** only when your environment requires it.
3. Save the profile. When editing, a save creates a new revision. Retiring a profile prevents new jobs from selecting it while preserving revisions already referenced by jobs or executions.
4. Select a saved, active profile and an allowed catalog, then use **Run safe diagnostic**. The diagnostic checks connectivity through a fixed internal query; it does not run a query or procedure that you provide. Check the command status and execution history for its outcome.

Memory mode supports profile management and allowlists but does not connect to SQL; a diagnostic can report persistence unavailable. This is not a successful database connectivity test. A real diagnostic requires the configured SQL-backed service and target database. Job SQL settings are documented in the job creation guide.

## Result

The tenant stores a versioned profile with an explicit catalog allowlist and secret reference. A diagnostic reports only whether its bounded connectivity check was accepted or failed safely.
