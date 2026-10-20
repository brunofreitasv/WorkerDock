# Review workers and audit events

## Prerequisites

Sign in to the tenant panel. Read permission is sufficient.

## Role

Read. These operational views do not change worker configuration or audit records.

## Steps

1. Open [Workers](#/workers) to review each worker's state, installation ID, live limit and capacity ceiling, last heartbeat, and advertised capabilities. The heartbeat time helps you assess whether the worker is reporting recently.
2. Open [Audit](#/audit) to search and page through recorded events. Review the UTC time, action, actor, authentication mode, and correlation ID to relate an event to an operation.
3. Use **Refresh** and the **Updated** time to judge freshness. A refresh warning may say that displayed worker or audit data is stale; unavailable data is not an empty result.
4. Audit summaries omit detail payloads and secret values. Use the correlation ID when tracing related activity.

Memory-mode examples can show adapter-held records but do not verify SQL-backed audit persistence or live worker coordination. For execution attempts and their [timeline](#/help:monitoring), open the monitoring guide.

## Result

You can check worker heartbeats and capacity, and search audit summaries without changing tenant state.
