# Store and reference secrets

## Prerequisites

You need **Manage** access to configure profiles or webhook headers. A tenant administrator must configure a trusted secret provider. Vault settings show the selected provider and public options; they do not expose secret values.

## Role

Manage users enter logical secret references in SQL profiles and webhook sinks. The configured provider supplies and rotates the actual value outside these forms. Read users do not manage credentials.

## Steps

1. Ask the provider administrator to create the credential and give you its logical reference name. Agree on its spelling and rotation process.
2. In a SQL profile, choose **Vault password** and enter the SQL user plus reference. In a webhook sink, add a header name and enter its reference. See [SQL profiles](#/help:sql-profiles) and [webhook sinks](#/help:webhook-sinks).
3. Save the configuration with the reference only. Never paste a password, token, API key, or other secret value into WorkerDock, a guide, or a support report.
4. Confirm the tenant service identity can resolve the reference. The current development vault adapter is unavailable unless its development-only environment mapping is enabled; this release includes no production vault adapter.

## Example

The provider administrator creates a webhook credential and gives you `tenant/alerts/ops-token`. In the sink, add header `Authorization` and use that string as its secret reference. WorkerDock stores the reference; the provider resolves the value when delivery runs.

## Verify

Check the saved reference spelling and ask the provider administrator to confirm tenant access. Run a controlled test only after the provider confirms access. WorkerDock never displays resolved values; failures show a safe result code.

## Recover

For a resolution failure, verify reference spelling, provider configuration, service identity permissions, and rotation state with the administrator. If a credential value was pasted into a form, rotate it with the provider and remove it from exposed logs or notes.

## Related guides

See [SQL profiles](#/help:sql-profiles), [webhook sinks](#/help:webhook-sinks), [outbound policy](#/help:outbound-policy), and [tenant settings](#/help:settings).
