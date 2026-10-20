## Prerequisites

You need Manage access to change tenant settings. Open [Settings](#/settings); its tabs are **Settings**, **Secret provider**, **Outbound policy**, **SQL profiles**, and **Retention**. The app header also has **Language** and **Time zone** preferences for your own display.

## Role

Readers can read this guide. Saving tenant settings, changing outbound policy, managing SQL profiles, and editing retention require Manage access. Deployment administrators configure the vault provider and archive storage location.

## Steps

1. In the **Settings** tab, update **Display name** or **Default display time zone**, then save. Use the app header's **Language** and **Time zone** preferences for your own display.
2. In **Secret provider**, review the configured provider and its public settings. This page does not reveal or accept secret values; configure credentials through the deployment's secret provider.
3. In **Outbound policy**, select **tenant-jobs** or **tenant-alerts**, then review allowed hosts, ports, private CIDRs, and whether HTTP is allowed. Save only the destinations required by your workflows. Loopback, metadata, and link-local destinations remain blocked.
4. In **SQL profiles**, create or update a named profile, select allowed catalogs and authentication settings, and use the diagnostic when needed. Profile credentials use logical secret references.
5. In **Retention and archive**, set the number of days to keep execution logs, execution history, and audit history. You may enable archiving before deletion and set archive retention. The storage location is administrator-configured and is not available in this page.
6. Save changes in the relevant area. If the page reports a stale version, reload and review the current values before editing again.

## Result

Tenant preferences and operational policies are saved as versioned settings. Changing them does not turn development memory-mode behavior into proof of live database, secret-provider, outbound delivery, or archive storage readiness. See [SQL Server jobs](#/help:sql-jobs), [HTTP jobs](#/help:http-jobs), and [alert setup](#/help:alerts) for related workflows.
