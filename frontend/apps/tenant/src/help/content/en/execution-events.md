# Read execution events

## What you will do

Trace an execution through its attempts and status timeline using the safe details shown by the tenant UI.

## Before you start

Read access is sufficient. Start from a recent execution on the [Dashboard](#/dashboard). The panel exposes summaries, not raw error text, request bodies, SQL, configuration, or secrets.

## Steps

1. Open a recent execution from the dashboard. The details view shows the job and template, trigger kind, creation time, correlation ID, certainty, and current state.
2. Review **Attempts** in order. Each entry can show its state, safe result code, start time, and whether effects may have occurred.
3. Read **Timeline** chronologically to see recorded state changes. Compare its timestamps with the attempt details; absence of a timeline entry does not establish that an external system did nothing.
4. If the result is uncertain or stop was requested, use the correlation ID when asking an administrator to trace the operation.

## Example

A synthetic run may show `Queued → Running → Failed`, attempt 1 with safe code `Timeout`, and certainty `Unknown`. This means the UI cannot confirm the remote outcome; check the destination system before retrying work that could repeat side effects.

## Check the result

You can identify the recorded state transitions, attempt count, safe result code, and whether effects may have happened. An accepted run or successful UI response is not proof of an external SQL commit or webhook delivery.

## If it does not work

If details are unavailable or marked stale, check **Updated**, refresh, and try again. If they remain unavailable, ask an administrator to check the monitoring store. Memory-mode events do not prove SQL-backed history or live deployment behavior.

## Related guides

- [Understand the dashboard](#/help:dashboard)
- [Monitor jobs and executions](#/help:monitoring)
- [Review audit history](#/help:audit)
