# Task 9 report — dynamic execution transport and registration

Base commit: `2e477c0` (`fix: validate and persist frozen dynamic values`). Branch: `codex/workerdock-final`.

## Implemented

- Set the shared process protocol to version 2. Supervisor and executor now use it for handshake and execute requests; version 1 peers are rejected before handler entry.
- Check the actual prepared descriptor and serialized execute envelope, including the snapshot and reserved bytes, before child startup and again at the child boundary. The snapshot cap is 16,384 bytes and the complete budget is 65,536 bytes.
- Map bounded `ValueResolutionError` codes to `PermanentFailure` with `EffectsMayHaveOccurred = false` before template composition, adapters, secrets, or handler execution. Reject missing snapshots for context-bearing descriptors and invalid context/snapshot/error combinations.
- Validate the saved snapshot's resolver version, digest shape, context shape, target eligibility against installed descriptor metadata, and exact value match against the materialized configuration. Run full schema and template validation before adapters or `ExecuteAsync`. Legacy context-free fixed descriptors retain their execution path.
- Register SQL Server and HTTP versions 1.0.0 and 1.1.0 in Web, Worker, and Executor; explicitly register built-in authoring validators in Web. Preserve the worker's aggregate digest readiness fence and update the four-descriptor worker configuration example to `8A719E8D8B3F5A5D5046ED8299D5D555F924F05B5E6AA64AE53B7F3A91AC2765`.
- Add process fixtures for legacy fixed execution, dynamic rejection, error mapping, protocol rejection, and actual envelope/snapshot caps. Add SQL catalog coverage for both versions and unchanged legacy metadata digests.

## Verification

- RED: `dotnet test tests/WorkerDock.ProcessTests --filter FullyQualifiedName~DynamicExecutionTests --no-restore` failed to compile because `ExecutionProtocol.CurrentVersion` was absent.
- GREEN: focused dynamic process tests passed, 9/9; combined dynamic, `ExecutionControlTests`, and `ExecutionActivityTests` filters passed, 22/22. One earlier combined run had a 2-second startup timing failure in `Finite_timeout_allows_thirty_second_grace_then_contains_the_process_tree`; the isolated test passed, and the combined rerun passed.
- `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~TemplateCatalogTests --no-restore`: passed, 37/37.
- `dotnet build src/WorkerDock.Worker --no-restore`: passed, 0 warnings, 0 errors. The integration filter also built Web and Executor successfully.
- `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~TemplateCatalogSqlTests|FullyQualifiedName~TemplateCatalogTests' --no-restore`: 37 passed and 6 SQL tests blocked. First blocker: `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` NuGet restore was available from existing assets; no NuGet blocker occurred.

## Scope

Only Task 9 runtime, registration, fixture, catalog-test, digest-example, and this report files were staged. Concurrent Task 6 and frontend help/alert changes were left untouched.
