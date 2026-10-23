IF EXISTS(SELECT 1 FROM [workerdock].OperationalEvents)
    THROW 51090,'Operational event consolidation cannot be rolled back after new events are written',1;
REVOKE EXECUTE ON [workerdock].PurgeOperationalEvents FROM WorkerDockRuntime;
DROP PROCEDURE [workerdock].PurgeOperationalEvents;
ALTER TABLE [workerdock].ArchiveBatchItems DROP CONSTRAINT CK_ArchiveBatchItems_Kind;
ALTER TABLE [workerdock].ArchiveBatchItems ADD CONSTRAINT CK_ArchiveBatchItems_Kind CHECK
    (RecordKind IN(N'AuditEvent',N'Execution',N'ExecutionAttempt',N'ExecutionEvent',N'AttemptStatusEvent',N'JobPermit',N'ExecutionDispatchOutbox',N'ManualRequestDedup'));
UPDATE s SET LastEvaluatedAtUtc=r.LastEvaluatedAtUtc,LastRuleId=r.RuleId,LastSubjectKey=r.LastSubjectKey
FROM [workerdock].AlertEvaluationState s CROSS APPLY
(SELECT TOP(1) RuleId,LastEvaluatedAtUtc,LastSubjectKey FROM [workerdock].AlertRules WHERE LastEvaluatedAtUtc IS NOT NULL ORDER BY LastEvaluatedAtUtc DESC,RuleId DESC) r
WHERE s.StateId=1;
REVOKE SELECT,INSERT ON [workerdock].OperationalEvents FROM WorkerDockRuntime;
REVOKE UPDATE(LastEvaluatedAtUtc,LastSubjectKey) ON [workerdock].AlertRules FROM WorkerDockRuntime;
DROP INDEX IX_OperationalEvents_ExecutionPage ON [workerdock].OperationalEvents;
DROP INDEX IX_OperationalEvents_Recovery ON [workerdock].OperationalEvents;
DROP TABLE [workerdock].OperationalEvents;
ALTER TABLE [workerdock].AlertRules DROP COLUMN LastEvaluatedAtUtc,LastSubjectKey;
ALTER TABLE [workerdock].TenantSettings DROP CONSTRAINT CK_TenantSettings_Retention;
ALTER TABLE [workerdock].TenantSettings DROP CONSTRAINT DF_TenantSettings_LogRetentionDays,DF_TenantSettings_ExecutionRetentionDays,
    DF_TenantSettings_AuditRetentionDays,DF_TenantSettings_ArchiveBeforeDelete,DF_TenantSettings_ArchiveRetentionDays,DF_TenantSettings_RetentionVersion;
ALTER TABLE [workerdock].TenantSettings DROP COLUMN LogRetentionDays,ExecutionRetentionDays,AuditRetentionDays,ArchiveBeforeDelete,ArchiveRetentionDays,RetentionVersion;
