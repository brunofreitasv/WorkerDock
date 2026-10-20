## Prerequisites

You need Manage access, the installed **HTTP request** template, and an outbound host allowlist that permits the destination host and port. Open [Settings](#/settings) to review the tenant job policy.

## Role

Readers can read this guide. Creating the job, changing outbound policy, and enabling the job require Manage access.

## Steps

1. Open [Jobs](#/jobs), choose **HTTP request**, and continue to **Configuration**.
2. Choose the method and destination URL. Configure query values, ordinary headers, parameters, and a bounded response mode as needed.
3. Add credentials only as logical references under **Secret headers**. Do not put tokens in a URL or ordinary header value; secret values are resolved by the executor and are not shown in the job form.
4. Set success statuses and timeout as needed, then validate the configuration. Placeholders use the configured parameter map; the execution key is available as `{execution.idempotencyKey}` when the destination supports idempotency.
5. Ask a manager to check **Settings → Outbound policy** if the host, port, or HTTP protocol is not allowed. Private destinations need an explicit private CIDR entry; hard-denied destinations remain blocked.
6. Review the job, save a disabled draft, and enable it when the destination policy and configuration are ready.

## Result

The job contains request configuration and logical secret references, not secret values. The executor checks outbound policy and does not follow redirects. A response summary omits response text. See [job creation](#/help:jobs) and `docs/reference/http-template.md` in the source repository.
