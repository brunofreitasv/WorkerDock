# Manual runs

## Prerequisites

The job must be enabled and valid. Manage access is required to request a run; the job needs a saved revision and available execution capacity.

## Role

Read users can inspect execution history. Managers can start or stop executions.

## Steps

1. Open [Jobs](#/jobs), select an enabled job, and review its current configuration and status.
2. Choose **Run now**. If the job has a dynamic **Reference date** binding, optionally supply a date override in the dialog.
3. Confirm the request and keep the returned execution ID. For an uncertain network response, retry the same request with the same request key.
4. Open [Monitoring](#/monitoring) or the job's execution details to follow the execution. Use **Stop execution** to request cooperative cancellation of a running job.

## Worked example

For an enabled daily report with a `ReferenceDate` binding, choose **Run now** and enter `2026-10-19` to test that date once. The override applies to this execution only; it does not edit the job or schedule.

## Verify

Use the execution ID to find the accepted run and review its status, attempts, and events. A successful request confirms admission, not successful completion or an external side effect.

## Recover

If admission is rejected, verify the job is enabled, its current revision passes validation, and capacity is available. An override must be a valid date for the configured time zone. A retry with the same key and same override returns the accepted request; a different override needs a new request key.

## Result

Manual runs enter the normal execution lifecycle without waiting for a schedule. Their dynamic **Scheduled timestamp** source is unavailable; the reference date uses the explicit override or the accepted request date.

## Related guides

See [job creation](#/help:jobs), [job inputs](#/help:job-inputs), [execution policies](#/help:execution-policies), and [monitoring](#/help:monitoring).
