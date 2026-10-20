## Prerequisites

You need Manage access, the installed **SQL Server** template, and a SQL Server profile created under [Settings](#/settings). The profile must allow the catalog selected by the job.

## Role

Readers can read this guide. Creating the profile, running diagnostics, and creating or enabling a SQL job require Manage access.

## Steps

1. Open [Jobs](#/jobs), choose the SQL Server template, and continue to **Configuration**.
2. Enter the profile ID and an allowlisted catalog. Select **Query** or **Procedure**, then enter the command text.
3. Add each SQL parameter with its name and `SqlDbType`. Supply exactly one source for each parameter: a literal value, a logical secret reference, the execution key, or null. Add size, precision, or scale when the type requires it.
4. Choose whether to use a local transaction, then validate the configuration. SQL text is parameterized; values are not inserted into SQL text.
5. Review the job and save a disabled draft. Enable it only after the profile and configuration are ready.
6. If needed, open **Settings → SQL profiles** and run the profile diagnostic. It checks connectivity through a fixed connectivity operation; it does not run the job's SQL command.

## Result

The job stores its profile reference, catalog, command, and typed parameter metadata. A profile revision is pinned when an execution is accepted. Development memory mode can store profile settings but does not execute SQL or establish target connectivity; diagnostic acceptance in memory mode is not proof of a successful SQL connection. See [job creation](#/help:jobs) and `docs/reference/sql-template.md` in the source repository.
