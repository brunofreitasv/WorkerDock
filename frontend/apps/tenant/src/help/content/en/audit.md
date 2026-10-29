# Review audit history

## What you will do

Search the tenant's audit summaries to relate recorded actions to an actor and time.

## Before you start

Read access is sufficient. Audit is a read-only view. The table contains summary fields; detail payloads and secret values are not exposed.

## Steps

1. Open [Audit](#/audit). Search by actor, correlation ID, or action summary, and select an exact **Event type**. Use **Next** and **Previous** to move through pages.
2. Choose a preset period or enter a custom range. The page starts with the last seven days; custom searches can span up to 365 days. Dates are interpreted in your selected time zone, with the end date included.
3. Review **Time**, **Action**, **Actor**, **Mode**, and **Correlation**. Use the correlation ID to connect records from the same operation; displayed times follow your selected time zone.
4. Check **Updated** and any warning after refreshing. Treat the last successful response as stale if the view says the refresh failed.
5. For execution state transitions and attempts, open the [execution events](#/help:execution-events) guide.

## Example

A synthetic row might read `2026-06-15 14:05 UTC · JobEnabled · user-demo · Session · corr-demo-17`. It identifies an action summary and correlation key; it does not include the full payload or prove that a later execution succeeded.

## Check the result

You can find matching action summaries, their UTC times, actors, authentication modes, and correlation IDs. Search results are bounded and paged; older records may no longer be available under the tenant's retention settings.

## If it does not work

If audit data is unavailable, refresh and check **Updated**. An unavailable store is not an empty audit history. Memory-mode records do not prove SQL-backed audit persistence. If older events are missing, ask an administrator to check retention and store configuration.

## Related guides

- [Review worker health](#/help:workers)
- [Read execution events](#/help:execution-events)
- [Manage retention and archives](#/help:retention)
