## Prerequisites

Open [Settings](#/settings) to review your access and set your personal **Language** and **Time zone** preferences. Read access is enough for Personal settings. You need **Manage** access to edit tenant configuration, secret provider, outbound policy, SQL profiles, or retention.

## Role

Read users can open Personal settings and review tenant configuration guidance. Only Manage users can edit tenant configuration, secret provider, outbound policy, SQL profiles, and retention. Deployment administrators own provider credentials and archive storage configuration.

## Steps

1. In **Personal settings**, review your access mode and role, and choose your personal language and time zone. These preferences are saved on this device.
2. In **Tenant configuration**, set the tenant's display name and default display time zone, then save. The saved display name appears beside the logo and in the browser tab.
3. In **Secret provider**, review the installed provider and public options. The page never displays resolved credentials. Provider implementation, credential mappings, and service identity are configured by deployment administrators.
4. In **Outbound policy**, select **tenant-jobs** or **tenant-alerts**. Review allowed hosts, ports, private CIDRs, and whether HTTP is permitted. Add only destinations the corresponding workflow needs. Hard-denied loopback, metadata, and link-local addresses remain blocked.
5. In **SQL profiles**, review named profiles and allowed catalogs. Configure profile credentials through logical secret references; use the diagnostic to check connectivity when the installed adapter supports it. See [SQL Server profiles](#/help:sql-profiles).
6. In **Retention**, set record and archive retention. The archive path and service identity are deployment configuration, not browser settings. See [Retention and archives](#/help:retention) before enabling archive-before-delete.
7. Save in the section you changed. Check for a success message before leaving the page.

## Worked example

Suppose a job calls `reports.example.test` over HTTPS and sends alerts to `notify.example.test`. In **Outbound policy**, select **tenant-jobs**, add only the reports host and required port, then save. Select **tenant-alerts** separately and allow only the notification host. This keeps job execution and alert delivery policies independently reviewable. Configure each job's secret reference in its own integration workflow; do not enter secret values into public settings.

## Verify

Return to each settings section and confirm the saved value is displayed. The tenant display name should appear beside the logo and in the browser tab; policy and profile pages reload the server's saved revision. A successful form save confirms that the API accepted the setting, but it does not prove a downstream service or production secret provider is reachable.

## Recover

- If saving reports that settings changed elsewhere or a stale version, reload the section, compare the latest values, reapply only your intended changes, and save again. The API uses version checks to prevent silently overwriting another user's update.
- If a settings page shows a permission error, ask a tenant administrator to grant the appropriate Manage role through the identity provider. Documentation access does not grant configuration access.
- If a provider or SQL diagnostic is unavailable, check whether the required adapter and deployment configuration are installed. Development memory mode is not evidence of production database, vault, egress, or archive readiness.
- If a destination is rejected, review the selected policy and exact host/port. Private destinations require explicit policy configuration; hard-denied addresses cannot be enabled in the tenant UI.

## Related guides

- [Manage SQL Server profiles](#/help:sql-profiles)
- [Configure outbound destinations](#/help:outbound-policy)
- [Manage retention and archives](#/help:retention)
- [Review audit history](#/help:audit)
