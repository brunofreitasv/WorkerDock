# Browse installed templates

## Prerequisites

Sign in to the tenant panel. Read access is enough to browse. Creating a job requires Manage permission.

## Role

Read to browse template descriptions and versions. Manage to select a template while creating a job.

## Steps

1. Open [Templates](#/templates) to review the installed template catalog. The job wizard's template picker shows each template's display name and version.
2. Choose a selectable template when creating a job. A retired version remains visible for existing jobs but is disabled for new selection.
3. Template packages are installed by the application deployment. The tenant UI does not create, upload, or install template packages. Developers adding a custom template should follow `docs/how-to/add-custom-template.md` in the source repository.

## Result

You can identify the installed template and version available to a job and understand why a retired version cannot be selected for a new job.
