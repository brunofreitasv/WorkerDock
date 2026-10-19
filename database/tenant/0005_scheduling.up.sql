ALTER TABLE [workerdock].Jobs ADD
    ActivationBoundaryUtc datetime2(7) NULL,
    DisabledAtUtc datetime2(7) NULL;
GO
CREATE TABLE [workerdock].Schedules
(
    ScheduleId uniqueidentifier NOT NULL CONSTRAINT PK_Schedules PRIMARY KEY,
    JobId uniqueidentifier NOT NULL CONSTRAINT UQ_Schedules_Job UNIQUE,
    Revision bigint NOT NULL,
    Kind nvarchar(16) NOT NULL,
    Cron nvarchar(120) NULL,
    ZoneId nvarchar(100) NOT NULL,
    OneTimeUtc datetime2(7) NULL,
    ActivatedUtc datetime2(7) NOT NULL,
    Paused bit NOT NULL CONSTRAINT DF_Schedules_Paused DEFAULT 0,
    Removed bit NOT NULL CONSTRAINT DF_Schedules_Removed DEFAULT 0,
    PausedAtUtc datetime2(7) NULL,
    CursorUtc datetime2(7) NOT NULL,
    MissedMode nvarchar(16) NOT NULL,
    LatenessToleranceMs bigint NOT NULL,
    CatchUpCount int NOT NULL,
    CatchUpAgeMs bigint NOT NULL,
    CatchUpRecoveryThroughUtc datetime2(7) NULL,
    CatchUpRecoveryCreated int NOT NULL CONSTRAINT DF_Schedules_CatchUpRecoveryCreated DEFAULT 0,
    ZoneRulesFingerprint char(64) NOT NULL,
    ProjectionRevision bigint NOT NULL CONSTRAINT DF_Schedules_ProjectionRevision DEFAULT 0,
    CONSTRAINT FK_Schedules_Jobs FOREIGN KEY(JobId) REFERENCES [workerdock].Jobs(JobId),
    CONSTRAINT CK_Schedules_Revision CHECK(Revision>0 AND ProjectionRevision>=0),
    CONSTRAINT CK_Schedules_Kind CHECK((Kind=N'Cron' AND Cron IS NOT NULL AND OneTimeUtc IS NULL) OR (Kind=N'Once' AND Cron IS NULL AND OneTimeUtc IS NOT NULL)),
    CONSTRAINT CK_Schedules_Pause CHECK((Paused=0 AND PausedAtUtc IS NULL) OR (Paused=1 AND PausedAtUtc IS NOT NULL)),
    CONSTRAINT CK_Schedules_Policy CHECK(MissedMode IN(N'Skip',N'Coalesce',N'CatchUp') AND LatenessToleranceMs>=0 AND
        ((MissedMode=N'CatchUp' AND CatchUpCount BETWEEN 1 AND 100 AND CatchUpAgeMs>0) OR (MissedMode<>N'CatchUp' AND CatchUpCount=0 AND CatchUpAgeMs=0))),
    CONSTRAINT CK_Schedules_CatchUpRecovery CHECK((CatchUpRecoveryThroughUtc IS NULL AND CatchUpRecoveryCreated=0) OR (CatchUpRecoveryThroughUtc IS NOT NULL AND CatchUpRecoveryCreated BETWEEN 0 AND 100)),
    CONSTRAINT CK_Schedules_Fingerprint CHECK(LEN(ZoneRulesFingerprint)=64 AND ZoneRulesFingerprint NOT LIKE '%[^0-9A-F]%')
);
GO
ALTER TABLE [workerdock].Executions ADD
    CONSTRAINT FK_Executions_Schedules FOREIGN KEY(ScheduleId) REFERENCES [workerdock].Schedules(ScheduleId),
    CONSTRAINT CK_Executions_Occurrence CHECK((TriggerKind=N'Scheduled' AND ScheduleId IS NOT NULL AND ScheduleRevision>0 AND ScheduledForUtc IS NOT NULL) OR
        (TriggerKind<>N'Scheduled' AND ScheduleId IS NULL AND ScheduleRevision IS NULL AND ScheduledForUtc IS NULL));
GO
CREATE INDEX IX_Schedules_Cursor ON [workerdock].Schedules(Paused,CursorUtc,ScheduleId) INCLUDE(JobId,Revision,ActivatedUtc,Kind,Cron,ZoneId,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs);
GO
CREATE TABLE [workerdock].ScheduleRecoveryEvents
(
    RecoveryEventId uniqueidentifier NOT NULL CONSTRAINT PK_ScheduleRecoveryEvents PRIMARY KEY,
    ScheduleId uniqueidentifier NOT NULL,
    ScheduleRevision bigint NOT NULL,
    FromUtc datetime2(7) NOT NULL,
    ThroughUtc datetime2(7) NOT NULL,
    Policy nvarchar(16) NOT NULL,
    CreatedExecutions int NOT NULL,
    ExactMissedCount bigint NULL,
    SkippedOccurrences bigint NOT NULL CONSTRAINT DF_ScheduleRecoveryEvents_Skipped DEFAULT 0,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ScheduleRecoveryEvents_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ScheduleRecoveryEvents_Range CHECK(ThroughUtc>=FromUtc AND CreatedExecutions>=0 AND SkippedOccurrences>=0 AND (ExactMissedCount IS NULL OR ExactMissedCount>=0)),
    CONSTRAINT CK_ScheduleRecoveryEvents_Policy CHECK(Policy IN(N'Skip',N'Coalesce',N'CatchUp'))
);
GO
CREATE INDEX IX_ScheduleRecoveryEvents_Schedule ON [workerdock].ScheduleRecoveryEvents(ScheduleId,ScheduleRevision,ThroughUtc);
GO
CREATE TABLE [workerdock].ScheduleRuleScanState
(
    ScanId tinyint NOT NULL CONSTRAINT PK_ScheduleRuleScanState PRIMARY KEY,
    LastScheduleId uniqueidentifier NULL,
    CONSTRAINT CK_ScheduleRuleScanState_Singleton CHECK(ScanId=1)
);
INSERT [workerdock].ScheduleRuleScanState(ScanId,LastScheduleId) VALUES(1,NULL);
GO
GRANT SELECT,INSERT,UPDATE,DELETE ON [workerdock].Schedules TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].ScheduleRecoveryEvents TO WorkerDockRuntime;
GRANT SELECT,UPDATE ON [workerdock].ScheduleRuleScanState TO WorkerDockRuntime;
