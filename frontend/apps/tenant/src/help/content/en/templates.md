# Installed templates

Use the catalog to choose a supported job type. A tenant can select installed templates, but cannot create or upload one. New templates require a developer change and deployment; authors can follow `docs/how-to/add-custom-template.md` in the source repository.

## Prerequisites

Anyone with tenant access can browse the catalog. Manage access is required to create or activate a job from a template.

## Role

Read users can inspect installed templates. Manage users can select one in the job wizard and save a job.

## Steps

1. Open [Templates](#/templates) to review the installed catalog, display names, versions, and descriptions.
2. Open [Jobs](#/jobs), start a job, and choose a template in **Template**. The picker shows templates available to this tenant.
3. Review the generated fields in **Configuration**. Select a template version intentionally; existing jobs stay pinned to their saved version when the catalog changes.
4. Validate the configuration, review the draft, and activate only when its settings and prerequisites are ready.

## Worked example

For a scheduled SQL task, select **SQL Server**, then provide an approved profile, catalog, command, and typed parameter rows. Selecting the template only starts configuration; it does not connect to SQL Server.

## Verify

Confirm the selected template name and version in the job review or details. After activation, check the job status and execution history separately to confirm runtime behavior.

## Recover

If the template is missing, ask a manager to confirm it is installed for the tenant. If a field is unavailable, choose a compatible template version or ask the template author to update and deploy the schema. A tenant user cannot add a template through the catalog.

## Result

The picker uses an installed, versioned template to generate configuration fields. For developer authoring, see the source reference `docs/how-to/add-custom-template.md`.

## Related guides

See [job creation](#/help:jobs), [SQL Server jobs](#/help:sql-jobs), and [HTTP jobs](#/help:http-jobs).
