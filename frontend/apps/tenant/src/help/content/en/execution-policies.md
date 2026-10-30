# Execution policies

## Prerequisites

Manage access and a job draft are required. Policy settings are reviewed during job creation and activation.

## Role

Read users can read this guide and execution history. Managers set and change policies.

## Steps

1. Open [Jobs](#/jobs), edit a draft, and go to **Policy**.
2. Set the job timeout. **No timeout** removes the outer job deadline; SQL or HTTP handlers can still have their own bounded timeout.
3. Set maximum attempts, retry base and maximum delay, jitter, and whether uncertain outcomes may retry. Retries can repeat external effects, so use destination idempotency where available.
4. Set **Job capacity** to limit how many runs may be running at once. Choose overlap behavior: **Queue**, **Skip**, or **Allow**, and set the backlog limit for queued work. For **Queue** and **Skip**, choose whether new runs wait for or skip around pending review. Capacity and review behavior can also be changed in job details; only running attempts count against capacity.
5. For scheduled jobs, choose missed-work handling: **Skip**, **Coalesce**, or **Catch up**. In the current tenant editor, **Catch up** accepts at most three occurrences inside the one-day catch-up age window; the editor exposes the mode selector, not controls for changing the count or age limits.
6. Review the policy, save the draft disabled, then enable the job. Change policy through a reviewed job revision.

## Worked example

For a report that must not run concurrently, choose **Queue**, set a small backlog limit, and use bounded retries with jitter. For a schedule where only the latest missed report matters, choose **Coalesce** and review the recovered executions in history.

## Verify

Confirm the saved policy in job review and inspect attempt history after a run. A retry policy controls how the executor handles outcomes; it does not prove the target supports deduplication or undo.

## Recover

If work accumulates, check execution history, backlog limits, overlap choice, timeout, and missed-work policy. Disable or pause future starts while diagnosing. Stopping a run requests cancellation and may not reverse an external effect already accepted by a destination.

## Result

The policy determines bounded retries, deadlines, overlap, and schedule recovery. Accepted executions retain their lifecycle across later schedule edits; pause does not replay the paused interval.

## Related guides

See [schedules](#/help:schedules), [manual runs](#/help:manual-runs), [job creation](#/help:jobs), and [monitoring](#/help:monitoring).
