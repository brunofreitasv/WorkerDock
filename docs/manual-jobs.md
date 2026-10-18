# Manual jobs

Managers can author a job from a registered template under **Jobs**. The form is generated from the installed template's validated JSON Schema and built-in UI hints; it does not execute user-supplied scripts, HTML, UI components, or packages. Unsupported schema controls are not rendered as executable content.

The authoring flow saves a disabled draft, validates its configuration with the server, and then requires a separate **Enable manual job** action. Enable is guarded by the current ETag and rejects stale drafts, retired profiles/templates, invalid schemas, and any schedule-bearing revision. **Run now** submits the existing job command with a fresh idempotency key and displays the accepted execution ID. A 412 response keeps the user's current edits in the form.

The SQL Server form accepts a profile ID, catalog, command kind, SQL text, and parameter metadata. Configure the manager-only profile separately under **Settings → SQL profiles**. The HTTP form exposes method, URL, bounded response handling, query/header/parameter fields, and secret references. Secret references are logical names; the browser never accepts or displays a password/token value. Profile diagnostics return a safe command status and do not display connection strings or response bodies.

Example values for UI walkthroughs should remain synthetic, such as catalog `WorkerDockTest`, command `SELECT 1`, and URL `https://api.example.test/health`. These values are not a live SQL or HTTP acceptance test. Real database-backed job execution requires the disposable SQL setup described in [database.md](database.md); it remains **SQL_TEST_BLOCKED** when `WORKERDOCK_TEST_SQL` is unavailable. Playwright browser tests require the Chromium version installed by the frontend package; discovery can be run without the browser using `npm.cmd --prefix frontend run e2e -- --list`.

Schema-generated forms cover only the closed subset described in [custom-templates.md](custom-templates.md#validated-catalog-and-schema-subset). Scheduling and complete execution monitoring are provided by Stage 04.
