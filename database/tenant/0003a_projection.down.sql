IF EXISTS (SELECT 1 FROM [workerdock].Outbox WHERE LastErrorCode IS NOT NULL OR ProjectionState IN (N'Published',N'Retired') OR ReviewAtUtc IS NOT NULL)
    THROW 51002, 'Unsafe rollback: projection state requires backup and explicit removal', 1;
REVOKE SELECT ON [workerdock].Attempts FROM WorkerDockRuntime;
DROP INDEX IX_Outbox_Review ON [workerdock].Outbox;
ALTER TABLE [workerdock].Outbox DROP COLUMN LastErrorCode,ReviewAtUtc;
