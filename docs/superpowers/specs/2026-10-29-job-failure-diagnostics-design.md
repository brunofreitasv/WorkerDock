# Job failure diagnostics

## Intent

Make failed executions useful to debug from the tenant web app. Every failed attempt should show the information available for its failure type. Job authors choose whether messages, response content, and stack traces may be retained and displayed. Known resolved secrets are redacted for every job.

## Goals and behavior

- Add a **Hide detailed failure output** choice to job creation and editing. The unchecked default shows details. Store the choice on each immutable job revision and copy it when duplicating a job. Existing revisions default to unchecked. An execution uses the choice from its pinned job revision, so later edits do not change historical visibility.
- For an unchecked job, show the bounded, redacted exception type, message, stack trace, and returned error content on each failed attempt when those fields exist. SQL failures also show error number, state, procedure, and line. HTTP failures show status and a bounded text response body. A failed HTTP status has no exception stack; the UI labels that field unavailable instead of inventing one.
- For a checked job, do not retain or return message, stack trace, or response body. Show safe failure code, attempt/execution IDs, template ID/version, job revision, time, and failure-specific reproduction metadata: SQL error number/state/procedure/line and command kind; HTTP method/status. For an ad hoc SQL query, show the job name and command kind, not SQL text or a fabricated query name. Process failures show available safe process code and state.
- Keep diagnostics on the existing execution detail view, grouped by attempt. Display text as text, never HTML. Readers already authorized for execution details can see the policy-appropriate fields. A clear label explains when the job setting hid detail or when no exception stack exists.
- Apply known-secret redaction inside Executor before diagnostics cross IPC, including values resolved through its secret facade and their JSON-escaped forms. Bound each field and the final serialized payload; truncate with an explicit marker. Redaction is best effort for known secrets and cannot detect arbitrary private values returned by SQL or HTTP. The job setting is the control for those values.
- Preserve the current execution certainty, retry, and effects semantics. Diagnostics are best effort and must not block completion or turn a successful execution into a failure.

## Approaches

1. Capture full output for every job: shortest path to visibility, but ignores the requested job-level hiding choice and retains unexpected private content. Rejected.
2. Store a capture policy with each job revision and project diagnostics according to the pinned revision: chosen. It makes historical behavior predictable and avoids retaining raw detail for hidden jobs.
3. Put full exceptions only in host logs: avoids schema/API work, but the web page cannot show per-attempt details or keep them with execution history. Rejected.

## Design

Extend the job revision contract, validation, persistence, create/edit forms, and duplication path with the boolean policy. Extend the execution descriptor sent to Executor with the pinned policy. Each template and the Executor's outer catch produce a typed failure diagnostic when a failure exists. For an HTTP non-success response, capture bounded text content before clearing the response bytes; binary or undecodable content is represented by status, media type, and byte count. For SQL exceptions, capture provider metadata and message/stack according to policy. Other template exceptions and process failures use the same envelope where information is available.

Use a dedicated bounded failure-diagnostic envelope in the result/IPC path rather than putting arbitrary text into `SafeCode` or `SafeSummary`. Redact and enforce IPC limits in Executor; the Worker validates the envelope again and stores it by attempt in SQL. Add a schema migration for the job-revision policy and attempt diagnostics. Keep failure diagnostics with the execution through the existing retention/archive lifecycle, including purge. Existing attempts have no retroactive details.

Extend the execution-details query, DTO, and read endpoint with policy-appropriate diagnostics. The UI shows a compact summary for each attempt and expandable fields for message, stack, response content, and reproduction metadata. It clearly distinguishes hidden, unavailable, and truncated values. Do not render exception text as HTML or include it in broad job lists, alerts, audit summaries, or logs.

## Constraints and limits

- The current IPC frame limit is 65,536 bytes. The complete diagnostic frame must remain below that limit after JSON encoding. Set conservative per-field limits and trim further after serialization if needed.
- Raw provider messages and response bodies can contain data that is unknown to the secret facade. For unchecked jobs the author accepts that those redacted-but-not-provably-safe fields become visible to existing Read-authorized users. The form explains this beside the checkbox.
- Hidden jobs must suppress message, stack, and response body before persistence, not merely hide them in the browser. Do not persist raw child stdout/stderr, SQL result rows, request bodies, headers, credentials, or connection strings.
- SQL Server exposes procedure and line for many errors; other failure types may have no equivalent. Missing fields are marked unavailable.

## Verification criteria

An unchecked job running a procedure that executes `THROW 51000, 'WorkerDock intentional failure test.', 1` shows that message, SQL number/procedure/line, and an available client stack on its failed attempt. A checked copy shows only the safe code and reproduction metadata, with no message or stack in storage or API. An HTTP 500 with a text body shows status and bounded body when unchecked, and status only when checked. Known resolved secret values are absent from persisted and returned diagnostics in both modes. Older attempts remain readable with their existing safe codes.
