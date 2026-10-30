# Monitor jobs and executions

## What you will do

Find jobs by state and inspect a job's activity and execution summary.

## Before you start

Sign in to the tenant panel. Read permission is sufficient.

## Access roles

Read. Job and execution monitoring is read-only.

## Steps

1. Open [Monitoring](#/monitoring) to search jobs, filter by state, and page through results. The table shows each job's template and version, enabled or archived state, active executions, recent failures, and schedule status.
2. Open an execution from the [Dashboard](#/dashboard) recent executions table to inspect its state, attempts, safe result codes, certainty, and **Timeline**. Choose **View all executions** to open [Execution history](#/executions), filter by period, status, template, or search, and page through results. Timeline entries show when the execution changed state.
3. Use **Refresh** where available. Views poll while visible, pause while the browser tab is hidden, and show an **Updated** time. When refresh fails, the warning identifies stale data; check the timestamp before acting on it.
4. Treat safe result codes as summaries. Expand an attempt's diagnostic for policy-appropriate, bounded failure output and reproduction metadata.

The application can show data held by its configured adapter. Memory-mode data is not evidence that SQL-backed monitoring, persistence, locking, or a live deployment works. Without a configured SQL store, database-backed monitoring may be unavailable rather than empty. See [Workers](#/help:workers) and [Audit](#/help:audit) for the other read-only operational views.

## Example

Search for `weekday-summary` and choose **Enabled**. A synthetic row might show template `sample-report · 1`, `Active 0`, and `Failed 24h 1`; inspect the execution before concluding why it failed.

## Check the result

Expand **Failure diagnostics** beneath an attempt to inspect its bounded, redacted message, stack, SQL metadata, or HTTP response content. **Details hidden by job setting** means the pinned job revision suppressed message, stack, and body before storage. **Unavailable** means no value was captured; HTTP status failures may have no stack. **Output truncated** marks bounded output. Older attempts may have no diagnostic. The execution shows its pinned job revision and template version. Known-secret redaction cannot detect arbitrary private response data; use **Hide detailed failure output** when authoring sensitive jobs.

You can inspect current job state and an execution's recorded attempts and timeline, while seeing when displayed data is stale or unavailable.

## If it does not work

If results are stale or unavailable, check **Updated**, refresh, and ask an administrator to inspect the configured store if the warning persists. Unavailable data is not an empty healthy result.

## Related guides

- [Understand the dashboard](#/help:dashboard)
- [Read execution events](#/help:execution-events)
- [Review worker health](#/help:workers)
