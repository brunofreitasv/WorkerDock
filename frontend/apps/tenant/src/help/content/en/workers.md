# Review worker health

## What you will do

Check whether workers are reporting recently and understand their displayed capacity and capabilities.

## Before you start

Read access is sufficient. Worker status is a read-only operational view; it does not let you change worker configuration or capacity.

## Steps

1. Open [Workers](#/workers). Review **State**, **ID**, **Capacity**, **Heartbeat**, and **Capabilities** for each listed worker.
2. Compare **Heartbeat** timestamps with the current time. Review `live limit / capacity ceiling` together with worker state; an advertised capability describes what the worker can accept, not whether a job completed successfully.
3. Check **Updated** and any warning after a refresh failure. A stale result is the last successful response, not a live status.
4. Open [Audit](#/audit) if you need to search recorded operational events, or inspect [execution events](#/help:execution-events) for a specific run.

## Example

A synthetic row may show `Ready · worker-demo · 2/4 · heartbeat 08:55 UTC · sql,http`. If the page was last updated at 09:00 UTC, the heartbeat is five minutes old; follow your tenant's operational threshold rather than treating this example as a universal limit.

## Check the result

You can tell what state and heartbeat the last response reported, and whether the values may be stale or unavailable. No workers may mean no records were returned; it does not by itself diagnose why.

## If it does not work

If the view says data is unavailable, refresh and check the **Updated** time. A persistent unavailable state can indicate that SQL is unavailable or the runtime heartbeat is stale. A memory-mode response does not prove SQL persistence or live child supervision. Ask an administrator to inspect the runtime service and deployment configuration.

## Related guides

- [Understand the dashboard](#/help:dashboard)
- [Monitor jobs and executions](#/help:monitoring)
- [Review audit history](#/help:audit)
