# Dynamic template values and SQL parameter authoring

Date: 2026-10-20
Status: Initial draft; brainstorming remains open. Not approved for implementation.
Workspace: C:\Users\bruno\.codex\worktrees\template-max-properties\WorkerDock 2

## Purpose and agreed outcomes

Users must be able to configure recurring SQL, HTTP, and trusted custom-template jobs whose inputs depend on the execution being processed. A reference date must not require manually editing a fixed value before every run.

The user approved the following direction for this initial spec:

- Fix the SQL parameter form's missing defaults and unclear validation messages.
- Explain that Procedure mode accepts a procedure name and binds parameters separately.
- Provide a shared, typed dynamic-value mechanism and a form preview.
- Use the original scheduled occurrence as the default reference-date anchor, including delayed work and retries.
- Allow Run now to override the reference date for reprocessing earlier periods.
- Preserve existing fixed configurations and version changed template metadata.

The technical details below are the initial proposed design. The user has requested further brainstorming before final spec review and implementation planning.

## Current behavior and evidence

`frontend/packages/ui/src/forms/TemplateForm.tsx` adds an object array item as `{}`. Child controls display fallback values without persisting those defaults in form state. The SQL schema requires `name`, `dbType`, `useExecutionKey`, and `isNull`; entering name and type while leaving the two displayed false values untouched therefore leaves two required members absent.

Both `SchemaValidation.ts` and `TemplateSchemaValidator.cs` report a missing required member at its containing object path. Array items share the `[]` path, producing duplicate `configuration.parameters[]: Required` messages without identifying the member or row.

`SqlTemplate` validates Procedure command text as a procedure identifier and executes it with `CommandType.StoredProcedure`. The screenshot's `Exec Job @Dt_Ref = @Dt_Ref` is not valid for that mode. The displayed parameter also has no selected value source: its value and secret are empty, and both execution-key and null flags are false.

The HTTP template already substitutes named scalar parameters and the execution idempotency key. Its substitution behavior is template-specific. `ExecutionDescriptor` does not currently expose the original scheduled timestamp, a reference date, or a stable first-start timestamp. Dispatch due times change during retries and cannot serve as the original schedule anchor.

The template-validation endpoint currently performs schema validation. Semantic checks such as procedure syntax and exactly one SQL value source also need an authoring-time path that does not execute a job.

## Scope

One shared resolution subsystem supports the built-in SQL and HTTP templates and declarative opt-in by installed custom templates. It includes authoring, validation, execution context, snapshot persistence, manual-run inputs, compatibility, and targeted documentation.

The first release supports dates, timestamps, execution ID, and idempotency key. Arbitrary scripts, database lookups to compute values, holiday calendars, previous-run output dependencies, and user-uploaded template code are outside this initial scope. These are scope boundaries for discussion, not additional requested features.

## User experience

### SQL command entry

Procedure mode labels the field **Stored procedure name**, uses `dbo.Job` as the example, and explains: “Enter only the procedure name. Add its parameters below.” An EXEC statement produces a field-specific message explaining how to correct it.

Query mode labels the field **SQL command text** and explains that named parameters must be configured below. Switching modes preserves the user's text and reports any incompatibility rather than silently rewriting it.

### Parameter values

Each SQL parameter has one **Value source** selector: Fixed value, Dynamic value, Secret, or Null. Show only the selected source's controls. The existing execution-key capability appears under Dynamic value. Parameter type, size, precision, and scale remain available where applicable.

New rows initialize their actual stored values consistently with their displayed controls. Required controls are marked. Clearing an optional source removes its payload; it must not create an empty secret object or select multiple sources.

Supported fields in HTTP and custom templates expose the same fixed/dynamic selection. Secret and null choices appear only where the target's contract permits them. Dynamic support does not implicitly apply to every configuration field.

### Preview and manual runs

A preview shows source, timezone, transformation, and an example result, such as “Reference date minus 1 day: 2026-10-19.” Preview values never become saved fixed values. Preview-only execution IDs and first-start times are clearly identified as examples.

Run now offers an optional reference-date override with a date picker. Leaving it empty uses the request's date in the pinned execution timezone. The override belongs to that execution and does not edit the job or schedule. The confirmation shows the selected reference date and timezone.

## Shared architecture

### Binding declarations

Proposed representation: a bounded collection of dynamic bindings stored alongside the job revision's template configuration. Each binding identifies a declared target using a JSON Pointer and supplies its source, supported transformation, and output representation.

The template descriptor declares eligible targets and their accepted scalar types through a validated metadata extension. An array declaration applies to eligible item fields; a saved binding identifies a concrete item. Arbitrary JSON paths, structural modifications, unknown targets, overlapping bindings, and bindings into secret/profile configuration are rejected.

The authoring representation may omit a dynamically supplied leaf value. Validation treats that leaf as supplied by its binding and checks all remaining static constraints. Resolution produces an ordinary configuration conforming to the template schema. Templates receive typed resolved values, not binding expressions.

A shared contract and resolver keep SQL, HTTP, and custom templates consistent. Existing templates without dynamic metadata continue to accept their existing configurations. Template-specific adapters handle SQL parameter conversion and HTTP serialization/encoding.

### Execution context and persistence

Pin the job revision, bindings, resolver semantics version, and timezone when an execution is admitted. Persist:

- The original scheduled occurrence for scheduled executions.
- The accepted request timestamp for manual executions.
- The optional manual reference-date override.
- The first accepted attempt-start timestamp, assigned once.
- The execution ID and stable idempotency key already associated with the execution.

Resolve dynamic bindings after the first-start timestamp exists and before external actions. Persist the resolved nonsecret binding values as one execution snapshot before invoking the template. All attempts reuse that snapshot, including recovery on another worker. Concurrent attempts cannot replace it with a different snapshot.

Use equivalent semantics in SQL-backed and in-memory execution stores. Memory-mode durability remains limited to that process's lifetime. Existing secret resolution remains separate; secret values must not enter the dynamic-value snapshot.

## Value semantics

### Sources

- **Reference date:** the original occurrence's local date for scheduled work; the override or accepted request's local date for manual work.
- **Scheduled timestamp:** the original occurrence instant. It is unavailable for manual work; report an explicit missing-source error rather than fabricate a timestamp.
- **First execution start:** the first accepted attempt-start instant, preserved on retries. Label it accordingly; do not call it a continuously changing “now.”
- **Execution ID** and **idempotency key:** stable execution identifiers.

A schedule's timezone is the initial authoring default. A job without a schedule defaults to UTC unless the user chooses another zone. Persist the chosen timezone; browser display preferences and later schedule edits cannot reinterpret an existing execution. An explicit, pinned binding timezone may change the presentation or calendar transformation of a timestamp source. A reference date, including a manual override, remains a date in the execution timezone and is not shifted between zones.

### Date transformations and formats

The initial proposal supports signed calendar month and day offsets plus start/end of day, month, and year. Apply timezone conversion first, month offset second, day offset third, and the selected boundary last. Month shifts clamp to the last valid day of the target month. Preview displays this order.

A date-only result remains a calendar date. It must not acquire an implicit UTC midnight. Daylight-saving gaps or ambiguous local times resulting from timestamp transformations produce an explicit validation/resolution error; the engine must not silently choose an offset.

Use a bounded set of output formats: ISO date, ISO timestamp with offset, UTC ISO timestamp, Unix seconds, and Unix milliseconds where the source supports them. The wizard offers only combinations compatible with the target type. End-of-period dates are inclusive; timestamp ranges use the next period's start as an exclusive endpoint, avoiding precision-dependent “last millisecond” values.

A job scheduled for October 20 and executed on October 21 resolves its default reference date to October 20 in the configured timezone. A retry after midnight retains that date. A new manual execution with an October 19 override resolves its reference date to October 19 while retaining its actual request and first-start timestamps.

Catch-up executions use their individual original occurrences. Coalesced work follows the scheduler's existing selected occurrence; this feature does not redefine missed-work policy.

## Template integration

### SQL Server

Resolve the selected source into a scalar, convert it using the declared database type, and bind it with `SqlParameter`. Never interpolate dynamic values into SQL command text or procedure names. Preserve existing profile pinning, transaction, deadline, and secret handling.

### HTTP

Resolve named parameter values using the shared resolver. Preserve JSON scalar types in bodies. Apply URL encoding to substituted URL/query components and appropriate handling to headers and text bodies. Prevent double encoding and reject invalid header characters. Validate the final URL and apply the existing outbound destination policy after resolution.

### Custom templates

Installed templates opt in by publishing eligible fields and scalar constraints in a new descriptor version. The `IWorkerTemplate` interface continues to receive ordinary resolved configuration through its execution context; templates do not interpret binding expressions. The shared execution descriptor gains the context and snapshot data needed by the resolver. Cross-template conformance tests demonstrate that equivalent declarations produce equivalent values.

## Validation and errors

Authoring validation checks binding structure, source availability, target compatibility, transformations, timezone, and fixed values without resolving secrets or invoking external services. Built-in SQL semantic validation is available in this side-effect-free flow.

Preview uses the same resolver semantics with an explicit sample context. At execution, validate the complete resolved configuration and template-specific rules before any external side effects. Invalid or unavailable dynamic inputs produce a permanent configuration failure with a bounded safe code.

Errors identify an indexed row and declared member, for example `configuration.parameters[0].isNull`. Report actionable messages for missing sources, invalid procedure names, incompatible types, and unavailable scheduled timestamps. Never echo secret values or arbitrary unknown property names. Frontend and backend agree on paths and codes.

## Compatibility and rollout

Existing fixed-value jobs preserve their behavior. Existing SQL source flags remain readable and map to the new authoring selector. Editing an existing job must not silently migrate its pinned template version.

Publish changed built-in metadata under new versions while retaining required older handlers/descriptors. Update catalog digests through the existing deployment process. Extend persistence and execution transport together, including payload limits and compatibility checks. Unsupported workers must not receive executions requiring new resolver semantics.

New dynamic contexts are required only for jobs using the new capability. Previously queued fixed-value executions remain executable without inventing historical timestamps. The implementation plan must enumerate the migration and coordinated Web, Worker, and Executor deployment steps.

## Verification and acceptance

- Adding a SQL parameter persists displayed defaults and removes the reported duplicate missing-field failure.
- Missing or conflicting sources identify the exact row and control.
- Procedure mode explains accepted input and rejects EXEC syntax before activation.
- Existing fixed SQL, HTTP, and custom-template configurations remain valid.
- Equivalent dynamic declarations resolve identically across all three template types.
- Delayed jobs and catch-up jobs use their original occurrence dates.
- Retries across midnight, worker restarts, and competing resolution attempts reuse one snapshot.
- Manual override affects only the new execution and survives its retries.
- Timezone boundaries, daylight-saving transitions, leap days, month ends, and period endpoints follow the specified rules.
- SQL date parameters retain their intended type; HTTP preserves JSON types and correct component encoding.
- Missing sources and invalid resolved values stop before external effects.
- Secret contents never appear in previews, snapshots, errors, or logs.

Use focused frontend tests, shared resolver fixtures, execution-store tests, and template integration tests. SQL persistence checks require an available test SQL database. Documentation-only drafting does not require running application tests.

## Brainstorming continuation

This document captures the initial agreed direction and proposed technical details. Additional user requests will be incorporated through further brainstorming. Final written-spec review and approval, implementation planning, and execution-method selection remain separate later stages. No product implementation is authorized by the creation of this initial draft alone.
