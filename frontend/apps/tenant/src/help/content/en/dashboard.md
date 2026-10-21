## What you will do

Use the dashboard summary to decide what needs attention and which screen to inspect next.

## Before you start

Sign in with Read access to the tenant.

## Access roles

Read access is enough to view dashboard metrics. Manage access is not required.

## Explore the screen

1. Open the [dashboard](#/dashboard).
2. Review job and execution counts to understand the current workload.
3. Check capacity and the refresh timestamp together. The timestamp helps you judge how current the displayed data is.
4. Review upcoming work and recent executions for items that may need attention.
5. Open a recent execution item on the Dashboard for its details, or follow [Read execution events](#/help:execution-events) to understand its attempts and timeline.

## Example

Suppose the cards show `Running 2`, `Failed 1`, and `Ready workers 1/2`, with a recent execution listed. Open it to inspect the recorded timeline, then check Workers for heartbeat details. These are synthetic values.

## Check the result

You can see a summary of tenant activity and decide which job or execution to inspect next. Dashboard values describe data returned by the current tenant connection.

## If it does not work

If a warning says monitoring data is unavailable or stale, treat displayed values as provisional. Refresh and check the configured store or service health. Memory-mode results do not verify SQL-backed monitoring or a live deployment; unavailable data is not zero activity.

## Related guides

- [Monitor jobs and executions](#/help:monitoring)
- [Read execution events](#/help:execution-events)
- [Review worker health](#/help:workers)
