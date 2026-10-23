ALTER TABLE [workerdock].TenantSettings ADD
    LogRetentionDays int NOT NULL CONSTRAINT DF_TenantSettings_LogRetentionDays DEFAULT(30),
    ExecutionRetentionDays int NOT NULL CONSTRAINT DF_TenantSettings_ExecutionRetentionDays DEFAULT(90),
    AuditRetentionDays int NOT NULL CONSTRAINT DF_TenantSettings_AuditRetentionDays DEFAULT(365),
    ArchiveBeforeDelete bit NOT NULL CONSTRAINT DF_TenantSettings_ArchiveBeforeDelete DEFAULT(0),
    ArchiveRetentionDays int NOT NULL CONSTRAINT DF_TenantSettings_ArchiveRetentionDays DEFAULT(0),
    RetentionVersion bigint NOT NULL CONSTRAINT DF_TenantSettings_RetentionVersion DEFAULT(1);
ALTER TABLE [workerdock].TenantSettings ADD CONSTRAINT CK_TenantSettings_Retention CHECK
    (LogRetentionDays BETWEEN 1 AND 3650 AND ExecutionRetentionDays BETWEEN 1 AND 3650 AND AuditRetentionDays BETWEEN 1 AND 3650
     AND ArchiveRetentionDays BETWEEN 0 AND 3650 AND RetentionVersion>0 AND (ArchiveBeforeDelete=0 OR ArchiveRetentionDays>0));
UPDATE s SET LogRetentionDays=p.LogDays,ExecutionRetentionDays=p.ExecutionDays,AuditRetentionDays=p.AuditDays,
    ArchiveBeforeDelete=p.ArchiveBeforeDelete,ArchiveRetentionDays=p.ArchiveDays
FROM [workerdock].TenantSettings s CROSS JOIN [workerdock].RetentionPolicy p WHERE p.PolicyId=1;

ALTER TABLE [workerdock].AlertRules ADD LastEvaluatedAtUtc datetime2(7) NULL,LastSubjectKey nvarchar(256) NULL;
UPDATE r SET LastEvaluatedAtUtc=s.LastEvaluatedAtUtc,LastSubjectKey=s.LastSubjectKey
FROM [workerdock].AlertRules r CROSS JOIN [workerdock].AlertEvaluationState s WHERE s.StateId=1 AND s.LastRuleId=r.RuleId;

CREATE TABLE [workerdock].OperationalEvents
(
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_OperationalEvents PRIMARY KEY,
    EventKind nvarchar(24) NOT NULL,
    ExecutionId uniqueidentifier NULL,
    AttemptId uniqueidentifier NULL,
    AttemptScopeId AS ISNULL(AttemptId,CONVERT(uniqueidentifier,'00000000-0000-0000-0000-000000000000')) PERSISTED,
    Source nvarchar(20) NULL,
    Sequence bigint NULL,
    AtUtc datetime2(7) NOT NULL,
    Code nvarchar(80) NOT NULL,
    SafeFieldsJson nvarchar(2048) NOT NULL CONSTRAINT DF_OperationalEvents_Fields DEFAULT(N'{}'),
    ScheduleId uniqueidentifier NULL,
    ScheduleRevision bigint NULL,
    FromUtc datetime2(7) NULL,
    ThroughUtc datetime2(7) NULL,
    Policy nvarchar(16) NULL,
    CreatedExecutions int NULL,
    ExactMissedCount bigint NULL,
    SkippedOccurrences bigint NULL,
    CONSTRAINT CK_OperationalEvents_Kind CHECK
    ((EventKind=N'Execution' AND ExecutionId IS NOT NULL AND Source IN(N'Lifecycle',N'Supervisor',N'Handler') AND Sequence>=0
       AND ScheduleId IS NULL AND ScheduleRevision IS NULL AND FromUtc IS NULL AND ThroughUtc IS NULL AND Policy IS NULL)
     OR (EventKind=N'ScheduleRecovery' AND ExecutionId IS NULL AND AttemptId IS NULL AND Source IS NULL AND Sequence IS NULL
       AND ScheduleId IS NOT NULL AND ScheduleRevision>0 AND FromUtc IS NOT NULL AND ThroughUtc>=FromUtc
       AND Policy IN(N'Skip',N'Coalesce',N'CatchUp') AND CreatedExecutions>=0 AND (ExactMissedCount IS NULL OR ExactMissedCount>=0) AND SkippedOccurrences>=0)),
    CONSTRAINT CK_OperationalEvents_Json CHECK(ISJSON(SafeFieldsJson)=1),
    CONSTRAINT CK_OperationalEvents_Code CHECK(LEN(Code)>0)
);
INSERT [workerdock].OperationalEvents(EventId,EventKind,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson)
SELECT EventId,N'Execution',ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson FROM [workerdock].ExecutionEvents;
INSERT [workerdock].OperationalEvents(EventId,EventKind,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson)
SELECT EventId,N'Execution',ExecutionId,AttemptId,N'Lifecycle',0,OccurredAtUtc,State,N'{}' FROM [workerdock].AttemptStatusEvents;
INSERT [workerdock].OperationalEvents(EventId,EventKind,AtUtc,Code,ScheduleId,ScheduleRevision,FromUtc,ThroughUtc,Policy,CreatedExecutions,ExactMissedCount,SkippedOccurrences)
SELECT RecoveryEventId,N'ScheduleRecovery',CreatedAtUtc,N'ScheduleRecovered',ScheduleId,ScheduleRevision,FromUtc,ThroughUtc,Policy,CreatedExecutions,ExactMissedCount,SkippedOccurrences
FROM [workerdock].ScheduleRecoveryEvents;
CREATE INDEX IX_OperationalEvents_ExecutionPage ON [workerdock].OperationalEvents(EventKind,ExecutionId,AtUtc,EventId)
    INCLUDE(AttemptId,Source,Sequence,Code,SafeFieldsJson);
CREATE INDEX IX_OperationalEvents_Recovery ON [workerdock].OperationalEvents(EventKind,ScheduleId,AtUtc)
    INCLUDE(FromUtc,ThroughUtc,Policy,CreatedExecutions,ExactMissedCount,SkippedOccurrences);
GRANT SELECT,INSERT ON [workerdock].OperationalEvents TO WorkerDockRuntime;
GRANT UPDATE(LastEvaluatedAtUtc,LastSubjectKey) ON [workerdock].AlertRules TO WorkerDockRuntime;
ALTER TABLE [workerdock].ArchiveBatchItems DROP CONSTRAINT CK_ArchiveBatchItems_Kind;
ALTER TABLE [workerdock].ArchiveBatchItems ADD CONSTRAINT CK_ArchiveBatchItems_Kind CHECK
    (RecordKind IN(N'AuditEvent',N'Execution',N'ExecutionAttempt',N'ExecutionEvent',N'AttemptStatusEvent',N'OperationalEvent',N'JobPermit',N'ExecutionDispatchOutbox',N'ManualRequestDedup'));
CREATE PROCEDURE [workerdock].PurgeOperationalEvents
    @LogCutoffUtc datetime2(7),@MaxRows int,@BatchId uniqueidentifier=NULL,@PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @LogCutoffUtc IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000 THROW 51091,'Invalid operational event purge bounds',1;
    DECLARE @ArchiveEnabled bit;
    BEGIN TRANSACTION;
    SELECT @ArchiveEnabled=ArchiveBeforeDelete FROM [workerdock].TenantSettings WITH(UPDLOCK,HOLDLOCK);
    IF @ArchiveEnabled IS NULL THROW 51092,'Retention settings are missing',1;
    IF @BatchId IS NULL AND @ArchiveEnabled=1 THROW 51093,'Archive batch required by retention policy',1;
    IF @BatchId IS NOT NULL AND @ArchiveEnabled=0 THROW 51094,'Archive batch is not enabled by retention policy',1;
    IF @BatchId IS NOT NULL
    BEGIN
        IF NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
            WHERE BatchId=@BatchId AND State=N'Finalized' AND FinalizedAtUtc IS NOT NULL AND LogCutoffUtc=@LogCutoffUtc)
            THROW 51095,'A finalized archive manifest covering operational events is required',1;
    END;
    DELETE TOP(@MaxRows) FROM [workerdock].OperationalEvents
    WHERE AtUtc<=@LogCutoffUtc AND (@BatchId IS NULL OR EventId IN(SELECT RecordId FROM [workerdock].ArchiveBatchItems WHERE BatchId=@BatchId AND RecordKind=N'OperationalEvent'))
      AND (EventKind=N'ScheduleRecovery' OR
        (EventKind=N'Execution'
         AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=OperationalEvents.ExecutionId AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
         AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=OperationalEvents.ExecutionId AND a.State=N'Running')
         AND NOT EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=OperationalEvents.ExecutionId AND e.HoldsJobSlot=1)
         AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),OperationalEvents.ExecutionId) AND i.State IN(N'Open',N'Unknown'))));
    SET @PurgedRows=@@ROWCOUNT;
    COMMIT TRANSACTION;
END;
GO
GRANT EXECUTE ON [workerdock].PurgeOperationalEvents TO WorkerDockRuntime;
