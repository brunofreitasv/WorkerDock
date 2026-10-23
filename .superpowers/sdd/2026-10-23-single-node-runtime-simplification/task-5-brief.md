### Task 5: Direct alert evaluation and delivery

**Files:** Create `src/WorkerDock.Infrastructure/Alerts/AlertDeliveryRunner.cs`; modify `src/WorkerDock.Application/Alerts/IAlertDeliveryStore.cs`, `src/WorkerDock.Infrastructure/Alerts/SqlAlertDeliveryStore.cs`, `src/WorkerDock.Infrastructure/Alerts/IncidentEvaluator.cs`, `tests/WorkerDock.IntegrationTests/AlertDeliveryTests.cs`.

**Interfaces:** Add `DueAlertDelivery(Guid DeliveryId, int AttemptOrdinal)` and `IAlertDeliveryStore.ReadDueAsync(int limit, CancellationToken ct) : ValueTask<IReadOnlyList<DueAlertDelivery>>`; `AlertDeliveryRunner.RunAsync(DueAlertDelivery due, CancellationToken ct) : ValueTask` uses existing claim/renew/complete contracts.

- [ ] Add `LeaseLostAfterSend_DoesNotCompleteDelivery`: `Assert.NotEqual("Succeeded", await fixture.DeliveryStateAsync(deliveryId));`. Cover direct incident-to-delivery insertion, duplicate claims, retry due time, stable delivery ID, and pinned sink revision.
- [ ] Run `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~AlertDeliveryTests` with SQL; expect new tests to fail.
- [ ] Move bounded send/lease logic into `AlertDeliveryRunner`; read due rows in batches and remove alert outbox publication. Task 5 defines the runner and due-read contract. Task 6 intentionally supplies their production caller in the tenant runtime service.
- [ ] Run the alert delivery filter and the alert delivery/access regression filters; expect pass.
- [ ] Commit: `feat: deliver alerts from durable SQL rows`.
