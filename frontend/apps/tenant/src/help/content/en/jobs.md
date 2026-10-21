## Prerequisites

You need Manage access and at least one selectable installed template. The **Jobs** page opens a wizard with six stages: **Template**, **Configuration**, **Schedule**, **Policy**, **Review**, and **Activation**.

## Role

Creating, editing, enabling, disabling, duplicating, archiving, and stopping jobs require Manage access. Read users can review job and execution information where their tenant exposes it.

## Steps

1. Open [Jobs](#/jobs) and enter a name, then choose a template in **Template**.
2. In **Configuration**, fill the fields generated from the installed template and choose **Validate configuration**. Correct any reported issues before continuing.
3. In **Schedule**, leave **Use a schedule** off for a manual job, or configure a schedule if needed.
4. In **Policy**, choose retry, timeout, overlap, and missed-work behavior. These settings control execution behavior; they do not validate a target system.
5. Review the job and choose **Save disabled draft**. The saved draft is disabled and does not start work.
6. Review the saved configuration and choose **Enable job** in the final stage when it is ready. Use **Run now** to request a manual execution.
7. In job details, use **Pause schedule** or **Resume schedule**, **Disable job**, **Duplicate**, or **Archive** as appropriate. Duplicate creates a disabled copy that needs review. Use **Stop execution** to request stopping a running execution.

## Result

The job is saved with its selected template, configuration, schedule, and policy. Enabling is a separate action after the draft is saved; disabling or archiving a job does not cancel accepted or running work. See [schedules](#/help:schedules), [SQL Server jobs](#/help:sql-jobs), and [HTTP jobs](#/help:http-jobs) for configuration details.

## Worked example

Create a manual HTTP job named **Daily status check**, choose **HTTP request**, enter an allowlisted HTTPS endpoint, and validate its configuration. Leave **Use a schedule** off, review **Policy**, save the disabled draft, then enable it after verifying the destination and secret references.

## Verify

Confirm the job details show **Enabled** only after activation. Use **Run now** and record the returned execution ID; inspect its status and event timeline to verify the run reached a terminal state.

## Recover

Keep a draft disabled while correcting validation issues. If the revision is stale, load the current revision and review the retained edits before saving. A failed or uncertain run request can be retried with its same request key; do not create duplicate runs with a new key until checking execution history.

## Related guides

See [installed templates](#/help:templates), [job inputs](#/help:job-inputs), [manual runs](#/help:manual-runs), [schedules](#/help:schedules), and [execution policies](#/help:execution-policies).
