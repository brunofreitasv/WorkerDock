# Create alert rules

## Prerequisites

You need **Manage** access, a webhook sink, and an approved destination. Review [webhook sinks](#/help:webhook-sinks) and [outbound policy](#/help:outbound-policy). Rules use the SQL-backed tenant service and outbox worker; saving in memory mode does not prove evaluation is running.

## Role

Manage users create, edit, and enable rules. Read users can review incidents and deliveries. A new rule is saved disabled so it can be reviewed before opening incidents.

## Steps

1. Open [Alerts](#/alerts) and choose **New rule**. Enter a name, select an enabled sink, and choose repeated failure, retry exhausted, timeout, long running, missed execution, or worker unavailable.
2. Set threshold and lookback window. Count conditions use the threshold within the window. Long-running and worker-unavailable conditions use it as an age limit and require threshold `1`. Grace delays opening; a positive reminder interval schedules reminders while an incident remains open. Zero disables reminders.
3. Save the rule. The first save creates it **disabled**. Review its condition, threshold, window, sink, grace, and reminder values.
4. Choose **Enable rule** only when ready. Activation is a separate explicit action. Incident processing depends on the SQL service and outbox worker.

## Example

To detect three repeated failures within 15 minutes, choose **Repeated failure**, threshold `3`, window `900` seconds, an enabled operations sink, and a grace period suited to your response process. Save, inspect the disabled rule, then enable it. For **Long running**, use threshold `1` and choose the window as the age limit.

## Verify

Confirm the rule shows enabled only after activation. Review [incidents and deliveries](#/help:incidents-deliveries) after the evaluator observes matching data. Incidents are deduplicated by rule, condition, and subject. Missing worker telemetry can produce Unknown; absence of telemetry does not prove recovery.

## Recover

Creation requires an enabled sink. If validation fails, check integer limits and use threshold `1` for long-running or unavailable-worker conditions. If an incident is absent, confirm SQL evaluation is running and the lookback/grace period elapsed. Disable a rule while investigating unwanted matches.

## Related guides

See [webhook sinks](#/help:webhook-sinks), [incidents and deliveries](#/help:incidents-deliveries), [monitoring](#/help:monitoring), and [tenant settings](#/help:settings).
