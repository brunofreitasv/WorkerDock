# Task 8 report

Implemented one-time dynamic resolution under the attempt ownership fence. Accepted starts assign `FirstStartedAtUtc` once. SQL persists first start, snapshot, or safe error in the transaction even when no lifecycle event is emitted. Memory stores the immutable original configuration and frozen plan; SQL rereads them from the pinned revision. Retries reapply saved values after checking the SHA-256 input digest. Integration settings stay outside the digest and snapshot.

`ExecutionValuePreparation` checks the actual serialized snapshot at 16,384 bytes and the complete prepared execution envelope plus existing reserves at 65,536 bytes. Both stores check the final descriptor before handoff. `ValueResolutionError` is carried for Task 9 to convert to `PermanentFailure`; Task 9 must also enforce the wire boundary and reject inconsistent snapshot/configuration data.

Verification: `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~ExecutionValueSnapshotTests|FullyQualifiedName~AttemptLifecycleTests' --no-restore` passed 20/20. The new SQL restart test and all three `AttemptSqlTests` cases compiled but stopped at `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` The initial sandbox run could not write `obj`; an escalated run exposed missing Task 7 namespace imports in `InMemoryJobCommands.cs` and `DynamicValueAdmissionTests.cs`, which were fixed. An intermediate envelope boundary test failed because its fixture was below the cap; the corrected fixture passes.

Limitation: SQL transaction, competing boot, rollback, and restart behavior remains unverified without the SQL fixture. No handler or process transport test was run in this task; Task 9 owns that boundary.
