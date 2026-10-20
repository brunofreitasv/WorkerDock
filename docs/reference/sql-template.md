# SQL Server template

`workerdock.sql-server` version `1.0.0` is explicitly installed in Worker and Executor. Both use the same immutable descriptor/digest. SQL queries and procedures receive typed `SqlParameter` values; SQL text is never interpolated. Procedure identifiers accept an optional schema plus a regular identifier (brackets allowed). Use `[workerdock]` for WorkerDock-owned fixture objects. Production target objects belong to the target owner's schema and are never installed by runtime startup.

Profiles are nonsecret named revisions. The manager selects an allowlisted catalog, Integrated authentication or VaultPassword with a username/logical secret reference, and explicit Encrypt/TrustServerCertificate flags. The builder accepts no connection-string input, uses a 15-second connection bound, disables driver reconnect retries, pooling and ambient transaction enlistment, and never returns a connection-string DTO. Use certificate validation and encryption for production. SQL target credentials must be separate from WorkerDock persistence identities.

API routes (all Manage; browser mutations also require HTTPS, same-origin and CSRF):

- GET/POST `/api/connection-profiles`
- GET/PUT `/api/connection-profiles/{profileId}` (GET returns ETag; PUT requires If-Match)
- POST `/api/connection-profiles/{profileId}/retire` (If-Match)
- POST `/api/connection-profiles/{profileId}/diagnostics` with exactly `{ "catalog": "AllowedCatalog" }`

Create sends zero profile/revision IDs and revision 0; the server assigns identities. Updates create a new immutable revision. Maximum 200 profiles (including retired), 64 allowed catalogs/profile and a 4 KiB serialized profile budget (leaving space for integration settings in child IPC), unique nonempty Name <=200 characters. Retirement prevents new selection but preserves referenced revisions. Execution acceptance pins the current revision; profile rotation after acceptance cannot silently change an execution. Vault values resolve again on each attempt, so rotating a value behind the same logical reference takes effect on the next attempt.

Job configuration uses camelCase properties: profileId, catalog, commandKind (`Query`/`Procedure`), commandText, parameters, commandTimeoutSeconds (nullable), useTransaction. A parameter supplies name, dbType (SqlDbType enum name), exactly one source (value, secret object `{ "name": "logical-reference" }`, useExecutionKey, isNull), and optional size/precision/scale. Strings/binary require explicit size; decimal requires precision/scale. Values parse with invariant culture, binary values are base64 and date/time values should be ISO 8601. SQL text is bounded to 16384 characters, parameters to 128 and explicit sizes to 1 MiB (or -1 for MAX). No rows, parameters, command text, credentials or raw driver errors enter safe results/logs.

Null command timeout inherits the attempt deadline. A finite explicit timeout is capped by the remaining attempt deadline; zero is permitted only with an infinite outer policy. Connection setup remains bounded even for infinite commands. Optional local transactions commit after reading results. Confirmed rollback following a non-cancellation failure may establish no effects; cancellation remains conservative. Commit acknowledgement loss returns SqlCommitUnknown with EffectsMayHaveOccurred=true and never claims rollback. The handler never retries. Target idempotency requires the explicit execution-key parameter and target-side enforcement.

Diagnostics are durable audited executions, bounded to 32 outstanding diagnostics and a 30-second attempt deadline with one attempt. The server creates TriggerKind=Diagnostic; the supervisor derives the private integration marker from persisted state. Job configuration cannot create this marker. The child selects an internal connectivity-only handler issuing fixed `SELECT DB_ID()`; it ignores command text. Diagnostics share `workerdock.sql-server` capacity, host slots and the normal outbox/Quartz/supervisor path; there is no separately authorable diagnostic template or execute-SQL endpoint. Command receipts report acceptance/publication through `/api/commands/{id}`; safe attempt codes are stored with execution history. Profile revisions were introduced by `0004a_profiles`; tenant vault and egress settings by `0004b_integration_settings`.

## Mock instance

Development Tenant:Adapter=Memory supports profile CRUD, allowlists, revision pinning and retirement. It does not execute SQL or connectivity diagnostics. Diagnostics return PersistenceUnavailable; UI tests must not label mock acceptance as SQL target success. Database, locking, permission, TLS, transaction and real target claims remain pending until the tests below run against SQL Server.

## SQL verification

SQL template integration tests use a separate target database and restricted
target login in addition to the disposable tenant database identities. Follow
[Set up SQL Server](../how-to/setup-sql-server.md) for test prerequisites and the
full command. The target fixture scripts under `tests/sql` create only test
objects in generated databases. SQL results and memory-test results are separate:
memory adapter support does not execute SQL or establish target connectivity.
