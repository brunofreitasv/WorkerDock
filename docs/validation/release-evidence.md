# Release evidence

**Current status:** No complete release candidate evidence is recorded here. Earlier test runs were for earlier source revisions; they do not certify this checkout. Use the [release checklist](release-checklist.md) and [fault matrix](fault-matrix.md) for each candidate.

## Record one candidate at a time

Copy this table for each reviewed candidate. Use **PASS**, **FAIL**, or **NOT RUN** for every check. Include a link or path to the actual output; a command without its result is not evidence.

| Item | Record |
| --- | --- |
| Source commit and branch | |
| Build date, operator, and tool versions | |
| Package names and SHA-256 values | |
| Tenant migration target and checksums | |
| Windows, .NET, IIS, SQL Server, and compatibility level | |
| Frontend tests and build | |
| .NET unit, process, and integration tests | |
| Disposable SQL integration and migration tests | |
| Live tenant and Control Center installation | |
| Entra sign-in, role enforcement, and anonymous-mode check | |
| Secret-vault integration and rotation | |
| Backup, restore, and interrupted-deployment drill | |
| Fault and security matrix results | |
| Workload report and hardware | |
| Open blockers and decision | |

## What counts as a result

- For a local code check, record the exact command, exit code, test count, and output artifact.
- For SQL or IIS, record the disposable environment and observed behavior. Memory-mode or script-parsing results do not count as live SQL or deployment results.
- For each failure or skipped check, write **FAIL** or **NOT RUN** and why. Keep secrets, customer data, and connection strings out of evidence.
- For workload measurements, attach the output described in [workload validation](workload-results.md). Do not infer an SLA from a test run.

Historical verification notes from October 2026 remain available in Git history. Re-run the checks against the candidate before using them in a release decision.
