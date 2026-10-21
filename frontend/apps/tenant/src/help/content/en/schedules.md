## Prerequisites

You need Manage access and a job draft. Open [Jobs](#/jobs) and turn on **Use a schedule** in the **Schedule** stage.

## Role

Readers can view this guide. Creating or changing a schedule and previewing its occurrences require Manage access.

## Steps

1. Choose **Recurring (cron)** for a recurring schedule or **One time** for a single local date and time.
2. For a recurring schedule, enter the five cron fields shown in **Cron expression**. The day-of-month and day-of-week fields use AND semantics.
3. Choose **Time zone**. For a one-time schedule, enter **Local date and time** and the intended **UTC offset** when the local time is ambiguous.
4. Choose **Preview next runs** and review the returned UTC instants and offsets. The preview service resolves daylight-saving gaps and repeated times.
5. In **Policy**, choose how to handle missed work: **Skip**, **Coalesce**, or **Catch up**. Set retry and overlap behavior separately; overlap can queue, skip, or allow concurrent work, subject to the job policy.
6. Save the schedule while the job is still disabled, review it, then enable the job. Pause stops future scheduled occurrences for the paused interval; resume begins from the resume time rather than replaying that interval.

## Result

The schedule is saved with its time zone and execution policy. Deleting it requires confirmation and leaves the job available for manual runs. Accepted executions keep their accepted lifecycle after later schedule edits or removal. See [job lifecycle](#/help:jobs) and `docs/reference/scheduling.md` in the source repository.

## Worked example

To run at 09:00 in São Paulo, select **Recurring (cron)**, enter `0 9 * * *`, and choose `America/Sao_Paulo`. Review **Preview next runs** in UTC and compare the displayed offset with the intended local time before saving.

## Verify

Confirm the saved time zone, cron expression, and next UTC instants. After enabling the job, use execution history to verify occurrences; previewing predicts times but does not run the job.

## Recover

For an ambiguous one-time local time, choose the intended UTC offset. For a daylight-saving gap, choose a valid local time and preview again. If runs were missed, review the configured **Skip**, **Coalesce**, or **Catch up** policy; pausing does not replay the paused interval.

## Related guides

See [execution policies](#/help:execution-policies), [manual runs](#/help:manual-runs), [job creation](#/help:jobs), and `docs/reference/scheduling.md` in the source repository.
