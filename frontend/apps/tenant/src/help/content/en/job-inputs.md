# Job inputs and dynamic values

## Prerequisites

Manage access and a template with declared inputs are required to configure a job. Dynamic authoring is available only for fields whose installed template schema explicitly supports it.

## Role

Read users can review this guide. Managers configure inputs, preview dynamic values, and save the job.

![Select a SQL or HTTP template, declare an input, choose one supported source, validate and preview, then save a draft.](./media/input-choice.svg)

*Figure: Bind one supported source to each declared input.* The template schema determines which choices are available, and SQL parameters remain separate from query source text.

## Steps

1. Open [Jobs](#/jobs), select a template, and inspect **Configuration** for declared fields.
2. Built-in SQL jobs use template version **1.1.0**. Add a typed parameter and choose exactly one source: **Fixed value**, **Dynamic value**, **Secret**, or **Send SQL NULL**. In Query mode, bind each `@name` placeholder with a matching parameter row.
3. Built-in HTTP jobs use template version **1.1.0**. Define a parameter and use `{name}` in supported URL, query, or body fields. Query and URL substitutions are encoded for their component. Use logical secret references for credentials.
4. For dynamic inputs, choose a declared source such as **Reference date**, **Scheduled timestamp**, **First execution start**, **Execution ID**, or **Idempotency key**, then select a compatible format and time zone.
5. Use the wizard preview to inspect sample resolved values before saving. Save the draft disabled, review it, and activate when ready.

## Worked example

Bind a SQL `Date` parameter named `Dt_Ref` to **Reference date** with ISO date format. For an HTTP job, a declared `referenceDate` parameter can fill a query value using `{referenceDate}`. Both examples bind data as inputs; neither inserts user data into SQL source text.

## Verify

Confirm every binding points to a declared field and has a compatible type and format. Preview output is an example only: it does not save a fixed value, run template code, resolve secrets, or contact a target.

## Recover

For a binding error, check the target path, parameter name, type, format, and selected time zone. Dynamic values cannot target secrets, sensitive fields, undeclared fields, or structural objects. If a preview source is unavailable for the selected trigger, use a supported source or keep the job disabled while revising its inputs. Editing a job does not silently upgrade its pinned template version; use the reviewed revision flow to change versions.

## Result

Inputs and dynamic bindings are validated with the selected template version. Scheduled reference dates use the original occurrence in the plan's time zone, including on late runs and retries.

## Related guides

See [SQL Server jobs](#/help:sql-jobs), [HTTP jobs](#/help:http-jobs), [manual runs](#/help:manual-runs), and [schedules](#/help:schedules).
