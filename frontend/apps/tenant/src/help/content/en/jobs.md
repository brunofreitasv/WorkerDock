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
