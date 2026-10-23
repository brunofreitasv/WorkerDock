# Create and run your first job

## What you will do

Use an installed template to save a draft, validate it, enable the job, and request one manual run.

![Five steps: choose a template, save a disabled draft, validate and review, enable when ready, then run and monitor.](./media/first-job.svg)

*Figure: A safe first-job path.* Keep the draft disabled until its configuration and possible external effects have been reviewed.

## Before you start

You need Manage access and a template that is selectable in this tenant. Choose a harmless example configuration and confirm that any external destination is approved before enabling work.

## Steps

1. Open [Jobs](#/jobs). The wizard opens at **Template**. Enter a recognizable name such as `guide-demo` in **Job name** and choose an installed template.
2. Continue to **Configuration** and fill the required fields. Use **Validate configuration**; fix every issue before proceeding. A successful configuration check does not prove an external SQL query or HTTP request will succeed.
3. At **Schedule**, leave scheduling off for a one-time manual example. Review **Policy** settings, then continue to **Review**.
4. Inspect the summary, acknowledge **I reviewed this current revision**, and choose **Save disabled draft**. Continue to **Activation**, then choose **Enable job** only after checking the reviewed configuration.
5. Choose **Run now**, confirm the dialog, and note the accepted execution identifier. Use the [Dashboard](#/dashboard) to locate the execution.

## Example

![The job wizard opened at the template selection step, with a numbered note identifying where to begin.](./media/wizard-job.png)

*Figure: Start in the template step.* The numbered marker identifies the first choice; the remaining wizard steps validate, review, and activate the draft.

Create `guide-demo` from a selectable sample template with a synthetic input such as `region = demo`. Save and validate the draft before enabling it. Run it once only if the selected template has no unintended external effects.

## Check the result

The wizard reports that the draft was saved, the job state becomes **Enabled**, and **Run now** returns an accepted execution identifier. Open the execution from the dashboard and check its recorded status and [events](#/help:execution-events); acceptance alone does not prove the external effect completed.

## If it does not work

If validation reports issues, correct the named fields and validate again. If the save reports a stale revision, reload the current revision and review it again before saving. Job and schedule changes are committed to SQL and appear in monitoring as the views refresh. If no template is selectable or a control is unavailable, ask an administrator to check template availability and your Manage role.

## Related guides

- [Browse installed templates](#/help:templates)
- [Read execution events](#/help:execution-events)
- [Monitor jobs and executions](#/help:monitoring)
