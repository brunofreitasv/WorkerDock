# Task 5 report: Direct alert evaluation and delivery

## Implementation

- Added `DueAlertDelivery` and `IAlertDeliveryStore.ReadDueAsync`, backed by a bounded SQL query over due `Pending` delivery rows ordered by availability and stable ID.
- Added `AlertDeliveryRunner` to claim, send, renew the lease, sanitize results, and complete or retry through the existing durable store contracts. When renewal reports ownership loss, the runner cancels the send and does not record a completion; the next owner can recover the expired attempt.
- Removed alert dispatch outbox writes from incident creation, lease-expiry retry, normal retry, and manual retry. Alert deliveries now remain the durable source of truth for due work.
- Added regressions for direct incident-to-delivery insertion without an alert dispatch outbox record, due retry timing, duplicate claim fencing, stable delivery IDs, pinned sink revisions, and lease loss after a sink send.
- Updated the unavailable web store implementation for the new interface contract. Its unrelated pre-existing `Program.cs` changes were left unstaged.
- This is an intentional transitional commit: Task 5 defines the due-read contract and runner but does not wire a production caller. Task 6 must compose them in `TenantRuntimeService` by polling `ReadDueAsync` and invoking `AlertDeliveryRunner.RunAsync` in its bounded alert loop; Task 5 alone is not deployment-ready.

## Verification

- TDD RED: the alert delivery filter initially failed to compile because `ReadDueAsync` and `DueAlertDelivery` did not exist.
- `dotnet build WorkerDock.slnx --no-restore`: passed, 0 warnings and 0 errors.
- `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter 'FullyQualifiedName~AlertDeliveryTests|FullyQualifiedName~AlertAccessTests' --no-restore`: compiled; 2 `AlertAccessTests` passed. All 3 `AlertDeliveryTests` were blocked at fixture startup because `WORKERDOCK_TEST_SQL` is unset (`SQL_TEST_BLOCKED`). SQL behavior remains unverified here.
- `git diff --cached --check`: passed before commit.

## Commit

`ecebff8 feat: deliver alerts from durable SQL rows`
