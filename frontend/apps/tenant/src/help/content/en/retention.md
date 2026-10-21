## Prerequisites

Sign in to the tenant panel with **Manage** access. Open [Settings](#/settings) and choose **Retention**. For archive storage configuration or a failed maintenance run, contact the deployment administrator; the tenant page does not expose filesystem paths.

## Role

Read users can review this guide. Only Manage users can change retention settings. Deployment administrators configure the archive root and scheduler service identity; database migration and recovery procedures belong to the operators documented in `docs/how-to/manage-retention-and-archives.md`.

## Steps

1. Review the three independent periods: **Execution logs**, **Execution history**, and **Audit history**. Initial defaults are 30, 90, and 365 days. Each accepts a whole number from 1 through 3650.
2. Decide whether the deployment is ready to archive before deletion. Keep **Archive eligible records before deleting them** off until an administrator has configured and tested the archive storage path and scheduler permissions.
3. Set **Archive retention (days)** separately. When archiving is enabled, use 1–3650 days. When it is off, 0 means retain archive files indefinitely; a positive value can expire files that were created earlier.
4. Select **Save changes**. The page validates whole numbers and requires a current settings version before it sends the update.

## Worked example

For a tenant that keeps execution logs for 30 days, execution history for 90 days, and audit history for 365 days, leave the initial values unchanged. If operators have verified the archive root and scheduler access, enable archive-before-delete and set archive retention to 365 days. If the archive location is not ready, keep archiving disabled; the setting alone cannot configure storage.

## Verify

After saving, confirm **Retention settings saved** appears and the fields show the selected values. A policy change does not purge data immediately: the scheduler processes bounded batches, and only records eligible under active-work and replay safeguards can be removed. Operators should monitor retention job health and archive capacity separately.

## Recover

- If the page says the settings version is missing, reload so it can read the current server version, then review the values before saving.
- If another user changed the policy, the page reports a stale version (HTTP 412). Reload, compare the current policy, and reapply your change; do not retry against an old version.
- If a value is rejected, use a whole number in the allowed range. Archive retention must be at least 1 day while archive-before-delete is enabled.
- If archive storage, permissions, disk space, finalization, or integrity verification fails, WorkerDock preserves the corresponding source records. Ask an operator to resolve the storage issue and retry the same logical batch. Never delete source rows or partial files manually to force a successful status.
- The browser never chooses an archive path. If archiving is unavailable, ask an administrator to check protected deployment configuration, scheduler identity permissions, free space, and retention job health.

## Related guides

- [Manage tenant settings](#/help:settings)
- [Review audit history](#/help:audit)
- [Troubleshoot common problems](#/help:troubleshooting)
- Operator reference: `docs/how-to/manage-retention-and-archives.md`
