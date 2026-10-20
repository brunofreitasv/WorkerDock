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
