IF EXISTS(SELECT 1 FROM [workerdock].ArchiveBatches)
    OR EXISTS(SELECT 1 FROM [workerdock].ArchiveBatches WHERE ArchiveRemovedAtUtc IS NOT NULL)
    OR EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems)
    OR EXISTS(SELECT 1 FROM [workerdock].RetentionRequestTombstones)
    OR EXISTS(SELECT 1 FROM [workerdock].RetentionPurgeAudit)
    OR NOT EXISTS(SELECT 1 FROM [workerdock].RetentionPolicy WHERE PolicyId=1 AND LogDays=30 AND ExecutionDays=90 AND AuditDays=365 AND ArchiveBeforeDelete=0 AND ArchiveDays=0)
    OR (SELECT COUNT(*) FROM [workerdock].RetentionPolicy)<>1
    THROW 51032,'Cannot remove populated retention state',1;
GO
REVOKE EXECUTE ON [workerdock].PurgeAuditEvents FROM WorkerDockRuntime;
REVOKE EXECUTE ON [workerdock].PurgeRetentionItems FROM WorkerDockRuntime;
REVOKE EXECUTE ON [workerdock].PurgeRetentionRequestTombstones FROM WorkerDockRuntime;
REVOKE EXECUTE ON [workerdock].PurgeExpiredArchiveBatchItems FROM WorkerDockRuntime;
REVOKE SELECT ON [workerdock].RetentionPurgeAudit FROM WorkerDockRuntime;
REVOKE SELECT,INSERT,UPDATE ON [workerdock].RetentionRequestTombstones FROM WorkerDockRuntime;
REVOKE SELECT,INSERT,UPDATE ON [workerdock].ArchiveBatches FROM WorkerDockRuntime;
REVOKE SELECT,INSERT ON [workerdock].ArchiveBatchItems FROM WorkerDockRuntime;
REVOKE DELETE ON [workerdock].ArchiveBatchItems FROM WorkerDockRuntime;
REVOKE DELETE ON [workerdock].ArchiveBatches FROM WorkerDockRuntime;
REVOKE DELETE ON [workerdock].RetentionPurgeAudit FROM WorkerDockRuntime;
REVOKE SELECT,UPDATE ON [workerdock].RetentionPolicy FROM WorkerDockRuntime;
DROP PROCEDURE [workerdock].PurgeAuditEvents;
DROP PROCEDURE [workerdock].PurgeRetentionItems;
DROP PROCEDURE [workerdock].PurgeRetentionRequestTombstones;
DROP PROCEDURE [workerdock].PurgeExpiredArchiveBatchItems;
GO
DROP TRIGGER [workerdock].ArchiveBatchItems_Immutable;
DROP TRIGGER [workerdock].ArchiveBatches_Immutable;
ALTER TRIGGER [workerdock].AuditEvents_AppendOnly ON [workerdock].AuditEvents AFTER UPDATE, DELETE AS
BEGIN
    THROW 51001, 'Audit events are append-only', 1;
END;
GO
DROP TABLE [workerdock].RetentionPurgeAudit;
DROP TABLE [workerdock].RetentionRequestTombstones;
DROP TABLE [workerdock].ArchiveBatchItems;
DROP TABLE [workerdock].ArchiveBatches;
DROP TABLE [workerdock].RetentionPolicy;
