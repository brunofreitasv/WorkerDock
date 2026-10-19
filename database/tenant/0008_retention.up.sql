CREATE TABLE [workerdock].RetentionPolicy
(
    PolicyId tinyint NOT NULL CONSTRAINT PK_RetentionPolicy PRIMARY KEY,
    LogDays int NOT NULL,
    ExecutionDays int NOT NULL,
    AuditDays int NOT NULL,
    ArchiveBeforeDelete bit NOT NULL,
    ArchiveDays int NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_RetentionPolicy_Singleton CHECK(PolicyId=1),
    CONSTRAINT CK_RetentionPolicy_Days CHECK(LogDays BETWEEN 1 AND 3650 AND ExecutionDays BETWEEN 1 AND 3650 AND AuditDays BETWEEN 1 AND 3650 AND ArchiveDays BETWEEN 0 AND 3650 AND (ArchiveBeforeDelete=0 OR ArchiveDays>0))
);
INSERT [workerdock].RetentionPolicy(PolicyId,LogDays,ExecutionDays,AuditDays,ArchiveBeforeDelete,ArchiveDays)
VALUES(1,30,90,365,0,0);
GO
CREATE TABLE [workerdock].ArchiveBatches
(
    BatchId uniqueidentifier NOT NULL CONSTRAINT PK_ArchiveBatches PRIMARY KEY,
    RelativePath nvarchar(500) NOT NULL,
    Sha256 char(64) NOT NULL,
    RowCount bigint NOT NULL,
    EligibleThroughUtc datetime2(7) NOT NULL,
    LogCutoffUtc datetime2(7) NOT NULL,
    State nvarchar(16) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    FinalizedAtUtc datetime2(7) NULL,
    ArchiveRemovedAtUtc datetime2(7) NULL,
    CONSTRAINT UQ_ArchiveBatches_Path UNIQUE(RelativePath),
    CONSTRAINT CK_ArchiveBatches_Path CHECK(LEN(RelativePath)>0 AND LEFT(RelativePath,1)<>N'/' AND RelativePath NOT LIKE N'%..%'),
    CONSTRAINT CK_ArchiveBatches_Checksum CHECK(LEN(Sha256)=64 AND Sha256 NOT LIKE '%[^0-9A-Fa-f]%'),
    CONSTRAINT CK_ArchiveBatches_Rows CHECK(RowCount>=0),
    CONSTRAINT CK_ArchiveBatches_State CHECK((State=N'Writing' AND FinalizedAtUtc IS NULL) OR (State=N'Finalized' AND FinalizedAtUtc IS NOT NULL AND FinalizedAtUtc>=CreatedAtUtc) OR (State=N'Failed' AND FinalizedAtUtc IS NULL))
);
GO
CREATE INDEX IX_ArchiveBatches_State ON [workerdock].ArchiveBatches(State,EligibleThroughUtc,FinalizedAtUtc);
GO
CREATE TABLE [workerdock].ArchiveBatchItems
(
    BatchId uniqueidentifier NOT NULL,
    RecordKind nvarchar(32) NOT NULL,
    RecordId uniqueidentifier NOT NULL,
    CONSTRAINT PK_ArchiveBatchItems PRIMARY KEY(BatchId,RecordKind,RecordId),
    CONSTRAINT FK_ArchiveBatchItems_Batch FOREIGN KEY(BatchId) REFERENCES [workerdock].ArchiveBatches(BatchId),
    CONSTRAINT CK_ArchiveBatchItems_Kind CHECK(RecordKind IN(N'AuditEvent',N'Execution',N'ExecutionAttempt',N'ExecutionEvent',N'AttemptStatusEvent',N'JobPermit',N'ExecutionDispatchOutbox',N'ManualRequestDedup'))
);
GO
CREATE INDEX IX_ArchiveBatchItems_Record ON [workerdock].ArchiveBatchItems(RecordKind,RecordId,BatchId);
GO
CREATE TRIGGER [workerdock].ArchiveBatchItems_Immutable ON [workerdock].ArchiveBatchItems AFTER INSERT,UPDATE,DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1 FROM inserted i
        WHERE NOT
        (
            (i.RecordKind=N'AuditEvent' AND EXISTS(SELECT 1 FROM [workerdock].AuditEvents r WHERE r.EventId=i.RecordId)) OR
            (i.RecordKind=N'Execution' AND EXISTS(SELECT 1 FROM [workerdock].Executions r WHERE r.ExecutionId=i.RecordId)) OR
            (i.RecordKind=N'ExecutionAttempt' AND EXISTS(SELECT 1 FROM [workerdock].Attempts r WHERE r.AttemptId=i.RecordId)) OR
            (i.RecordKind=N'ExecutionEvent' AND EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents r WHERE r.EventId=i.RecordId)) OR
            (i.RecordKind=N'AttemptStatusEvent' AND EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents r WHERE r.EventId=i.RecordId)) OR
            (i.RecordKind=N'JobPermit' AND EXISTS(SELECT 1 FROM [workerdock].JobPermits r WHERE r.PermitId=i.RecordId)) OR
            (i.RecordKind=N'ExecutionDispatchOutbox' AND EXISTS(SELECT 1 FROM [workerdock].Outbox r WHERE r.EventId=i.RecordId AND r.EventType=N'ExecutionDispatch')) OR
            (i.RecordKind=N'ManualRequestDedup' AND EXISTS(SELECT 1 FROM [workerdock].ManualRequestDedup r WHERE r.CommandId=i.RecordId) AND (SELECT COUNT(*) FROM [workerdock].ManualRequestDedup r WHERE r.CommandId=i.RecordId)=1)
        )
    ) THROW 51044,'Archive batch item does not identify a source row',1;
    IF EXISTS
    (
        SELECT 1 FROM inserted i
        JOIN [workerdock].ArchiveBatches b ON b.BatchId=i.BatchId
        WHERE (i.RecordKind=N'ExecutionEvent' AND EXISTS
            (SELECT 1 FROM [workerdock].ExecutionEvents r WHERE r.EventId=i.RecordId AND r.AtUtc>b.LogCutoffUtc))
           OR (i.RecordKind=N'AttemptStatusEvent' AND EXISTS
            (SELECT 1 FROM [workerdock].AttemptStatusEvents r WHERE r.EventId=i.RecordId AND r.OccurredAtUtc>b.LogCutoffUtc))
           OR (i.RecordKind=N'Execution' AND
               (EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents r WHERE r.ExecutionId=i.RecordId AND r.AtUtc>b.LogCutoffUtc)
                OR EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents r WHERE r.ExecutionId=i.RecordId AND r.OccurredAtUtc>b.LogCutoffUtc)))
    ) THROW 51045,'Archive log item is newer than the batch log cutoff',1;
    IF EXISTS(SELECT 1 FROM inserted i JOIN [workerdock].ArchiveBatches b ON b.BatchId=i.BatchId WHERE b.State<>N'Writing')
        THROW 51034,'Archive batch items are immutable after finalization',1;
    IF EXISTS
    (
        SELECT 1 FROM deleted d JOIN [workerdock].ArchiveBatches b ON b.BatchId=d.BatchId
        WHERE b.State<>N'Writing'
          AND NOT (b.State=N'Finalized' AND b.ArchiveRemovedAtUtc IS NOT NULL AND USER_NAME()=N'dbo'
              AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=b.BatchId)
    ) THROW 51047,'Finalized archive items can be removed only by the archive expiry procedure',1;
END;
GO
CREATE TRIGGER [workerdock].ArchiveBatches_Immutable ON [workerdock].ArchiveBatches AFTER UPDATE,DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1 FROM deleted d
        WHERE d.State=N'Finalized'
          AND NOT EXISTS
          (
              SELECT 1 FROM inserted i
              WHERE i.BatchId=d.BatchId AND d.ArchiveRemovedAtUtc IS NULL AND i.ArchiveRemovedAtUtc IS NOT NULL
                AND i.RelativePath=d.RelativePath AND i.Sha256=d.Sha256 AND i.RowCount=d.RowCount
                AND i.EligibleThroughUtc=d.EligibleThroughUtc AND i.LogCutoffUtc=d.LogCutoffUtc
                AND i.State=d.State AND i.CreatedAtUtc=d.CreatedAtUtc AND i.FinalizedAtUtc=d.FinalizedAtUtc
                AND USER_NAME()=N'dbo' AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=d.BatchId
          )
          AND NOT (d.ArchiveRemovedAtUtc IS NOT NULL AND USER_NAME()=N'dbo'
              AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=d.BatchId
              AND NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems x WHERE x.BatchId=d.BatchId)
              AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionPurgeAudit a WHERE a.ArchiveBatchId=d.BatchId)
              AND NOT EXISTS(SELECT 1 FROM inserted i WHERE i.BatchId=d.BatchId))
    ) THROW 51035,'Finalized archive manifests are immutable except for archive expiry cleanup',1;
    IF EXISTS
    (
        SELECT 1 FROM inserted i JOIN deleted d ON d.BatchId=i.BatchId
        WHERE d.State=N'Finalized'
          AND NOT (d.ArchiveRemovedAtUtc IS NULL AND i.ArchiveRemovedAtUtc IS NOT NULL
              AND i.RelativePath=d.RelativePath AND i.Sha256=d.Sha256 AND i.RowCount=d.RowCount
              AND i.EligibleThroughUtc=d.EligibleThroughUtc AND i.LogCutoffUtc=d.LogCutoffUtc
              AND i.State=d.State AND i.CreatedAtUtc=d.CreatedAtUtc AND i.FinalizedAtUtc=d.FinalizedAtUtc
              AND USER_NAME()=N'dbo' AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=d.BatchId)
    ) THROW 51048,'Finalized manifest fields are immutable',1;
    IF EXISTS
    (
        SELECT 1 FROM inserted i JOIN deleted d ON d.BatchId=i.BatchId
        WHERE d.ArchiveRemovedAtUtc IS NOT NULL AND (i.ArchiveRemovedAtUtc IS NULL OR i.ArchiveRemovedAtUtc<>d.ArchiveRemovedAtUtc)
    ) THROW 51046,'Archive expiry can be marked only once',1;
    IF EXISTS(SELECT 1 FROM inserted b WHERE b.State=N'Finalized' AND b.RowCount<>(SELECT COUNT_BIG(*) FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=b.BatchId))
        THROW 51036,'Archive row count must match its staged items',1;
END;
GO
CREATE TABLE [workerdock].RetentionRequestTombstones
(
    JobId uniqueidentifier NOT NULL,
    ActorScopeHash char(64) NOT NULL,
    RequestKeyHash char(64) NOT NULL,
    RequestHash char(64) NOT NULL,
    ExecutionId uniqueidentifier NOT NULL,
    CommandId uniqueidentifier NOT NULL,
    ResponseState nvarchar(20) NOT NULL,
    AcceptedAtUtc datetime2(7) NOT NULL,
    ExpiresAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_RetentionRequestTombstones PRIMARY KEY(JobId,ActorScopeHash,RequestKeyHash),
    CONSTRAINT CK_RetentionRequestTombstones_Hashes CHECK(ActorScopeHash NOT LIKE '%[^0-9A-Fa-f]%' AND RequestKeyHash NOT LIKE '%[^0-9A-Fa-f]%' AND RequestHash NOT LIKE '%[^0-9A-Fa-f]%'),
    CONSTRAINT CK_RetentionRequestTombstones_Times CHECK(ExpiresAtUtc>=AcceptedAtUtc)
);
GO
CREATE INDEX IX_RetentionRequestTombstones_Expiry ON [workerdock].RetentionRequestTombstones(ExpiresAtUtc);
GO
CREATE TABLE [workerdock].RetentionPurgeAudit
(
    PurgeId uniqueidentifier NOT NULL CONSTRAINT PK_RetentionPurgeAudit PRIMARY KEY,
    PurgeKind nvarchar(32) NOT NULL,
    CutoffUtc datetime2(7) NOT NULL,
    PurgedRowCount int NOT NULL,
    PurgedAtUtc datetime2(7) NOT NULL,
    ArchiveBatchId uniqueidentifier NULL,
    CONSTRAINT FK_RetentionPurgeAudit_ArchiveBatch FOREIGN KEY(ArchiveBatchId) REFERENCES [workerdock].ArchiveBatches(BatchId),
    CONSTRAINT CK_RetentionPurgeAudit_Kind CHECK(PurgeKind IN(N'AuditEvents',N'RequestTombstones',N'RetentionItems')),
    CONSTRAINT CK_RetentionPurgeAudit_Count CHECK(PurgedRowCount>=0)
);
GO
ALTER TRIGGER [workerdock].AuditEvents_AppendOnly ON [workerdock].AuditEvents AFTER UPDATE, DELETE AS
BEGIN
    IF USER_NAME() <> N'dbo'
        THROW 51001, 'Audit events are append-only', 1;
END;
GO
CREATE PROCEDURE [workerdock].PurgeAuditEvents
    @CutoffUtc datetime2(7),
    @MaxRows int,
    @ArchiveBatchId uniqueidentifier = NULL,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @CutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51030, 'Invalid audit purge bounds', 1;

    DECLARE @ArchiveEnabled bit;
    BEGIN TRANSACTION;
    SELECT @ArchiveEnabled=ArchiveBeforeDelete FROM [workerdock].RetentionPolicy WITH(UPDLOCK,HOLDLOCK) WHERE PolicyId=1;
    IF @ArchiveEnabled IS NULL THROW 51043,'Retention policy is missing',1;
    IF @ArchiveEnabled=1
    BEGIN
        IF @ArchiveBatchId IS NULL OR NOT EXISTS
        (
            SELECT 1 FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
            WHERE BatchId=@ArchiveBatchId AND State=N'Finalized' AND FinalizedAtUtc IS NOT NULL AND EligibleThroughUtc>=@CutoffUtc
        )
            THROW 51031, 'A finalized archive covering the purge cutoff is required', 1;
    END;
    ELSE IF @ArchiveBatchId IS NOT NULL AND NOT EXISTS
    (
        SELECT 1 FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
        WHERE BatchId=@ArchiveBatchId AND State=N'Finalized' AND FinalizedAtUtc IS NOT NULL AND EligibleThroughUtc>=@CutoffUtc
    ) THROW 51039,'The supplied archive manifest does not cover the purge cutoff',1;

    DECLARE @Purged TABLE(EventId uniqueidentifier NOT NULL PRIMARY KEY);
    IF @ArchiveEnabled=1
        DELETE TOP (@MaxRows) FROM [workerdock].AuditEvents
            OUTPUT DELETED.EventId INTO @Purged(EventId)
            WHERE OccurredAtUtc<@CutoffUtc
              AND EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@ArchiveBatchId AND i.RecordKind=N'AuditEvent');
    ELSE
        DELETE TOP (@MaxRows) FROM [workerdock].AuditEvents
            OUTPUT DELETED.EventId INTO @Purged(EventId)
            WHERE OccurredAtUtc<@CutoffUtc;
    SELECT @PurgedRows=COUNT(*) FROM @Purged;
    INSERT [workerdock].RetentionPurgeAudit(PurgeId,PurgeKind,CutoffUtc,PurgedRowCount,PurgedAtUtc,ArchiveBatchId)
        VALUES(NEWID(),N'AuditEvents',@CutoffUtc,@PurgedRows,SYSUTCDATETIME(),@ArchiveBatchId);
    COMMIT TRANSACTION;
END;
GO
CREATE PROCEDURE [workerdock].PurgeRetentionItems
    @LogCutoffUtc datetime2(7),
    @ExecutionCutoffUtc datetime2(7),
    @MaxRows int,
    @BatchId uniqueidentifier = NULL,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @LogCutoffUtc IS NULL OR @ExecutionCutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51037,'Invalid retention purge bounds',1;
    DECLARE @ArchiveEnabled bit,@Remaining int,@Count int,@CoverCutoff datetime2(7);
    DECLARE @Purged TABLE(RecordId uniqueidentifier NOT NULL);
    BEGIN TRANSACTION;
    SELECT @ArchiveEnabled=ArchiveBeforeDelete FROM [workerdock].RetentionPolicy WITH(UPDLOCK,HOLDLOCK) WHERE PolicyId=1;
    IF @ArchiveEnabled IS NULL THROW 51040,'Retention policy is missing',1;
    IF @BatchId IS NULL AND @ArchiveEnabled=1 THROW 51041,'Archive batch required by retention policy',1;
    IF @BatchId IS NOT NULL AND @ArchiveEnabled=0 THROW 51042,'Archive batch is not enabled by retention policy',1;
    IF @BatchId IS NOT NULL
    BEGIN
        SET @CoverCutoff=CASE WHEN @LogCutoffUtc>@ExecutionCutoffUtc THEN @LogCutoffUtc ELSE @ExecutionCutoffUtc END;
        IF NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
            WHERE BatchId=@BatchId AND State=N'Finalized' AND FinalizedAtUtc IS NOT NULL AND EligibleThroughUtc>=@CoverCutoff AND LogCutoffUtc=@LogCutoffUtc)
            THROW 51038,'A finalized archive manifest covering both retention cutoffs is required',1;
    END
    SET @Remaining=@MaxRows;
    SET @PurgedRows=0;

    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].JobPermits
        OUTPUT DELETED.PermitId INTO @Purged(RecordId)
        WHERE (@BatchId IS NULL OR PermitId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'JobPermit'))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=JobPermits.ExecutionId AND e.CreatedAtUtc<=@ExecutionCutoffUtc AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
          AND (@BatchId IS NULL OR JobPermits.ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=JobPermits.ExecutionId AND a.State=N'Running')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Outbox o WHERE o.EntityId=JobPermits.ExecutionId AND o.EventType=N'ExecutionDispatch' AND o.ProjectionState<>N'Retired')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),JobPermits.ExecutionId) AND i.State IN(N'Open',N'Unknown'));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;

    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].AttemptStatusEvents
        OUTPUT DELETED.EventId INTO @Purged(RecordId)
        WHERE OccurredAtUtc<=@LogCutoffUtc
          AND (@BatchId IS NULL OR EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'AttemptStatusEvent'))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=AttemptStatusEvents.ExecutionId AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=AttemptStatusEvents.ExecutionId AND a.State=N'Running')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=AttemptStatusEvents.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),AttemptStatusEvents.ExecutionId) AND i.State IN(N'Open',N'Unknown'));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;

    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].ExecutionEvents
        OUTPUT DELETED.EventId INTO @Purged(RecordId)
        WHERE AtUtc<=@LogCutoffUtc
          AND (@BatchId IS NULL OR EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'ExecutionEvent'))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=ExecutionEvents.ExecutionId AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=ExecutionEvents.ExecutionId AND a.State=N'Running')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=ExecutionEvents.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),ExecutionEvents.ExecutionId) AND i.State IN(N'Open',N'Unknown'));
    SELECT @Count=COUNT(*) FROM @Purged;
    SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Attempts
        OUTPUT DELETED.AttemptId INTO @Purged(RecordId)
        WHERE (@BatchId IS NULL OR AttemptId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'ExecutionAttempt'))
          AND (@BatchId IS NULL OR Attempts.ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution'))
          -- Abandoned attempts are retained diagnostic history; purge them only with an old, aggregated terminal execution.
          AND State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped',N'Abandoned')
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=Attempts.ExecutionId AND e.CreatedAtUtc<=@ExecutionCutoffUtc AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=Attempts.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),Attempts.ExecutionId) AND i.State IN(N'Open',N'Unknown'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents ev WHERE ev.AttemptId=Attempts.AttemptId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents ev WHERE ev.AttemptId=Attempts.AttemptId);
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].ManualRequestDedup
        OUTPUT DELETED.CommandId INTO @Purged(RecordId)
        WHERE ExpiresAtUtc<SYSUTCDATETIME()
          AND (@BatchId IS NULL OR (CommandId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'ManualRequestDedup') AND ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution')));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Outbox
        OUTPUT DELETED.EventId INTO @Purged(RecordId)
        WHERE EventType=N'ExecutionDispatch' AND ProjectionState=N'Retired'
          AND (@BatchId IS NULL OR EventId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'ExecutionDispatchOutbox'))
          AND (@BatchId IS NULL OR Outbox.EntityId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution'))
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=Outbox.EntityId AND e.CreatedAtUtc<=@ExecutionCutoffUtc AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=Outbox.EntityId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),Outbox.EntityId) AND i.State IN(N'Open',N'Unknown'));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Executions
        OUTPUT DELETED.ExecutionId INTO @Purged(RecordId)
        WHERE CreatedAtUtc<=@ExecutionCutoffUtc AND AggregatedAtUtc IS NOT NULL AND State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped')
          AND (@BatchId IS NULL OR ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents ev WHERE ev.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents ev WHERE ev.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].JobPermits p WHERE p.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].ManualRequestDedup d WHERE d.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionRequestTombstones t WHERE t.ExecutionId=Executions.ExecutionId AND t.ExpiresAtUtc>=SYSUTCDATETIME())
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Outbox o WHERE o.EntityId=Executions.ExecutionId AND o.EventType=N'ExecutionDispatch' AND o.ProjectionState<>N'Retired')
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),Executions.ExecutionId) AND i.State IN(N'Open',N'Unknown'));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    INSERT [workerdock].RetentionPurgeAudit(PurgeId,PurgeKind,CutoffUtc,PurgedRowCount,PurgedAtUtc,ArchiveBatchId)
        VALUES(NEWID(),N'RetentionItems',@ExecutionCutoffUtc,@PurgedRows,SYSUTCDATETIME(),@BatchId);
    COMMIT TRANSACTION;
END;
GO
CREATE PROCEDURE [workerdock].PurgeRetentionRequestTombstones
    @CutoffUtc datetime2(7),
    @MaxRows int,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @CutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51033, 'Invalid tombstone purge bounds', 1;
    DECLARE @Purged TABLE(JobId uniqueidentifier NOT NULL,ActorScopeHash char(64) NOT NULL,RequestKeyHash char(64) NOT NULL);
    BEGIN TRANSACTION;
    DELETE TOP (@MaxRows) FROM [workerdock].RetentionRequestTombstones
        OUTPUT DELETED.JobId,DELETED.ActorScopeHash,DELETED.RequestKeyHash INTO @Purged(JobId,ActorScopeHash,RequestKeyHash)
        WHERE ExpiresAtUtc<@CutoffUtc;
    SELECT @PurgedRows=COUNT(*) FROM @Purged;
    INSERT [workerdock].RetentionPurgeAudit(PurgeId,PurgeKind,CutoffUtc,PurgedRowCount,PurgedAtUtc,ArchiveBatchId)
        VALUES(NEWID(),N'RequestTombstones',@CutoffUtc,@PurgedRows,SYSUTCDATETIME(),NULL);
    COMMIT TRANSACTION;
END;
GO
CREATE PROCEDURE [workerdock].PurgeExpiredArchiveBatchItems
    @BatchId uniqueidentifier,
    @FinalizedBeforeUtc datetime2(7),
    @AuditCutoffUtc datetime2(7),
    @MaxRows int,
    @PurgedRows int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @BatchId IS NULL OR @FinalizedBeforeUtc IS NULL OR @AuditCutoffUtc IS NULL OR @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 1000
        THROW 51049,'Invalid archive metadata purge bounds',1;
    DECLARE @ArchiveDays int,@AuditDays int,@FinalizedAtUtc datetime2(7),@RowCount bigint,@ArchiveRemovedAtUtc datetime2(7);
    DECLARE @Remaining int=@MaxRows,@Count int;
    BEGIN TRANSACTION;
    SELECT @ArchiveDays=ArchiveDays,@AuditDays=AuditDays FROM [workerdock].RetentionPolicy WITH(UPDLOCK,HOLDLOCK) WHERE PolicyId=1;
    IF @ArchiveDays IS NULL OR @AuditDays IS NULL THROW 51040,'Retention policy is missing',1;
    IF @FinalizedBeforeUtc>SYSUTCDATETIME() OR @AuditCutoffUtc>DATEADD(day,-@AuditDays,SYSUTCDATETIME())
        THROW 51050,'Archive or audit cutoff is newer than configured retention permits',1;
    SELECT @FinalizedAtUtc=FinalizedAtUtc,@RowCount=RowCount,@ArchiveRemovedAtUtc=ArchiveRemovedAtUtc
      FROM [workerdock].ArchiveBatches WITH(UPDLOCK,HOLDLOCK)
      WHERE BatchId=@BatchId AND State=N'Finalized' AND FinalizedAtUtc IS NOT NULL;
    IF @FinalizedAtUtc IS NULL OR @FinalizedAtUtc>=@FinalizedBeforeUtc OR @FinalizedAtUtc>DATEADD(day,-@ArchiveDays,SYSUTCDATETIME())
        THROW 51051,'Archive batch has not reached configured file retention age',1;
    EXEC sys.sp_set_session_context @key=N'WDArchivePurgeBatchId',@value=@BatchId;
    IF @ArchiveRemovedAtUtc IS NULL
    BEGIN
        IF @RowCount>COALESCE((SELECT SUM(CONVERT(bigint,PurgedRowCount)) FROM [workerdock].RetentionPurgeAudit WITH(HOLDLOCK)
            WHERE ArchiveBatchId=@BatchId AND PurgeKind IN(N'AuditEvents',N'RetentionItems')),0)
            THROW 51052,'Archive batch purge summaries do not cover its manifest row count',1;
        UPDATE [workerdock].ArchiveBatches SET ArchiveRemovedAtUtc=SYSUTCDATETIME() WHERE BatchId=@BatchId AND ArchiveRemovedAtUtc IS NULL;
    END;

    DELETE TOP (@Remaining) FROM [workerdock].ArchiveBatchItems WHERE BatchId=@BatchId;
    SET @Count=@@ROWCOUNT; SET @Remaining-=@Count; SET @PurgedRows=@Count;

    IF @Remaining>0
       AND NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems WHERE BatchId=@BatchId)
       AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionPurgeAudit WHERE ArchiveBatchId=@BatchId AND PurgedAtUtc>=@AuditCutoffUtc)
       AND @FinalizedAtUtc<@AuditCutoffUtc
    BEGIN
        DELETE TOP (@Remaining) FROM [workerdock].RetentionPurgeAudit WHERE ArchiveBatchId=@BatchId AND PurgedAtUtc<@AuditCutoffUtc;
        SET @Count=@@ROWCOUNT; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    END;
    IF @Remaining>0
       AND NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems WHERE BatchId=@BatchId)
       AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionPurgeAudit WHERE ArchiveBatchId=@BatchId)
       AND @FinalizedAtUtc<@AuditCutoffUtc
    BEGIN
        DELETE FROM [workerdock].ArchiveBatches WHERE BatchId=@BatchId AND ArchiveRemovedAtUtc IS NOT NULL;
        SET @PurgedRows+=@@ROWCOUNT;
    END;
    EXEC sys.sp_set_session_context @key=N'WDArchivePurgeBatchId',@value=NULL;
    COMMIT TRANSACTION;
END;
GO
GRANT SELECT,UPDATE ON [workerdock].RetentionPolicy TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].ArchiveBatches TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].ArchiveBatchItems TO WorkerDockRuntime;
DENY DELETE ON [workerdock].ArchiveBatchItems TO WorkerDockRuntime;
DENY DELETE ON [workerdock].ArchiveBatches TO WorkerDockRuntime;
DENY DELETE ON [workerdock].RetentionPurgeAudit TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].RetentionRequestTombstones TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].RetentionPurgeAudit TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeAuditEvents TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeRetentionItems TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeRetentionRequestTombstones TO WorkerDockRuntime;
GRANT EXECUTE ON [workerdock].PurgeExpiredArchiveBatchItems TO WorkerDockRuntime;
