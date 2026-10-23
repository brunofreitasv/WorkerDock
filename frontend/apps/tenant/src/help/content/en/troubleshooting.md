## Prerequisites

Read access is enough to inspect tenant dashboards, monitoring, workers, and audit history. You need Manage access for settings and job changes. For host services, database configuration, or deployment credentials, collect the time and safe error summary and contact your WorkerDock administrator.

## Role

Use the tenant UI to check current status and safe result codes. Do not copy passwords, connection strings, secret values, raw SQL, or sensitive request bodies into support messages. Deployment operators use the source runbooks for infrastructure changes.

## Steps

1. Open [Dashboard](#/dashboard) and note the last update time. A refresh warning means the displayed values may be stale; it does not mean the underlying data is empty.
2. Open [Monitoring](#/monitoring), find the job, and check its enabled or archived state, recent failures, and schedule status.
3. Open the affected execution and review its state, attempts, safe result code, certainty, and timeline. Record the execution ID and correlation ID if shown.
4. Check [Workers](#/workers) for availability and heartbeat freshness. Compare worker capability with the job's exact template ID and version.
5. For a settings or integration issue, open [Settings](#/settings) if you have Manage access and confirm the relevant SQL profile, secret reference, or outbound policy. Do not change a production policy only to make an error disappear.
6. If the problem persists, share the safe identifiers, timestamp with time zone, relevant status, and steps already tried with an administrator.

## Worked example

If an execution is still queued, check its timeline and current state, the runtime heartbeat, child ceiling, job/template limits, and exact template version. If the execution reports a safe SQL connection code, a manager can review the job's selected profile and an operator can validate target permissions and secret-provider setup. Avoid repeatedly starting the job until its external effect and current execution state are understood.

## Verify

After the responsible change, refresh the affected page and check its **Updated** time. Confirm that the runtime heartbeat or setting is current, and inspect a new or safely retried execution's result and timeline. A successful UI refresh proves only that the displayed API request succeeded; it does not prove an external side effect unless the target system confirms it.

## Recover

| Symptom | First checks | Safe next step |
| --- | --- | --- |
| Page shows stale or unavailable data | Updated time, refresh warning, configured store health | Refresh once. If it persists, ask an operator to check the SQL store and service health; unavailable is not the same as empty. |
| Settings save is denied | Manage role and identity-provider role claim | Ask an administrator to correct access. Do not bypass the UI with a direct API request. |
| Settings report a stale version | Whether another manager changed the setting | Reload, review the current value, and apply only the needed edit. |
| Job stays queued or fails before a child starts | Runtime readiness, SQL availability, execution state and template ID/version | Ask an operator to check the single runtime service, SQL access and installed template catalog. |
| SQL or HTTP job cannot reach its target | Selected profile or destination policy, secret reference, host/port, TLS and target availability | Review [SQL jobs](#/help:sql-jobs) or [HTTP jobs](#/help:http-jobs), then ask the operator to verify deployment access. |
| Alert delivery fails | Alert rule, sink, delivery state, egress policy and runtime health | Review [Alert rules](#/help:alert-rules) and [Alert deliveries](#/help:incidents-deliveries); do not paste sink credentials into the UI. |
| Archive maintenance fails | Archive root, runtime permissions, disk capacity and integrity status | Preserve source records and follow [retention guidance](#/help:retention) with an operator. |

## Related guides

- [Monitor executions](#/help:monitoring)
- [Read logs and events](#/help:execution-events)
- [Check worker health](#/help:workers)
- [Manage tenant settings](#/help:settings)
- Operator reference: `docs/how-to/troubleshooting.md`
