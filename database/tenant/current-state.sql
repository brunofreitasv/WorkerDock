-- WorkerDock tenant desired state for a fresh SQL Server 2022+ database.
-- Replace the tenant GUID token and execute all batches on one connection.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET IMPLICIT_TRANSACTIONS ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
DECLARE @InstanceText nvarchar(100)=N'__WORKERDOCK_INSTANCE_ID__';
DECLARE @InstanceId uniqueidentifier=TRY_CONVERT(uniqueidentifier,@InstanceText);
IF DATALENGTH(@InstanceText)<>72 OR @InstanceId IS NULL OR @InstanceId='00000000-0000-0000-0000-000000000000' THROW 51070,'SnapshotInstanceIdRequired',1;
IF CONVERT(int,SERVERPROPERTY(N'ProductMajorVersion'))<16 THROW 51072,'UnsupportedSqlServerVersion',1;
IF (SELECT compatibility_level FROM sys.databases WHERE name=DB_NAME())<160 THROW 51073,'UnsupportedCompatibilityLevel',1;
IF EXISTS(SELECT 1 FROM sys.objects WHERE is_ms_shipped=0)
 OR EXISTS(SELECT 1 FROM sys.schemas WHERE name NOT IN(N'dbo',N'guest',N'sys',N'INFORMATION_SCHEMA',N'db_owner',N'db_accessadmin',N'db_securityadmin',N'db_ddladmin',N'db_backupoperator',N'db_datareader',N'db_datawriter',N'db_denydatareader',N'db_denydatawriter'))
 OR EXISTS(SELECT 1 FROM sys.database_principals WHERE type=N'R' AND is_fixed_role=0 AND name<>N'public')
 OR EXISTS(SELECT 1 FROM sys.types WHERE is_user_defined=1) OR EXISTS(SELECT 1 FROM sys.assemblies WHERE is_user_defined=1)
 THROW 51071,'SnapshotRequiresEmptyDatabase',1;
SET NOCOUNT ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

-- 0001_bootstrap
CREATE SCHEMA [workerdock] AUTHORIZATION [dbo];
GO

CREATE TABLE [workerdock].TenantSettings (
    InstanceId uniqueidentifier NOT NULL CONSTRAINT PK_TenantSettings PRIMARY KEY,
    DisplayName nvarchar(200) NOT NULL,
    DisplayTimeZone nvarchar(100) NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_TenantSettings_Name CHECK (LEN(TRIM(DisplayName)) > 0),
    CONSTRAINT CK_TenantSettings_Zone CHECK (LEN(TRIM(DisplayTimeZone)) > 0)
);
INSERT INTO [workerdock].TenantSettings (InstanceId, DisplayName, DisplayTimeZone) VALUES (TRY_CONVERT(uniqueidentifier,N'__WORKERDOCK_INSTANCE_ID__'), N'WorkerDock', N'UTC');

CREATE TABLE [workerdock].SchemaHistory (
    MigrationId nvarchar(100) NOT NULL CONSTRAINT PK_SchemaHistory PRIMARY KEY,
    UpChecksum char(64) NOT NULL,
    DownChecksum char(64) NOT NULL,
    AppliedAtUtc datetime2(7) NOT NULL
);

CREATE TABLE [workerdock].AuditEvents (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_AuditEvents PRIMARY KEY,
    EventType nvarchar(100) NOT NULL,
    ActorSubject nvarchar(300) NOT NULL,
    AuthenticationMode nvarchar(40) NOT NULL,
    CorrelationId uniqueidentifier NOT NULL,
    OccurredAtUtc datetime2(7) NOT NULL,
    DetailsJson nvarchar(4000) NOT NULL,
    CONSTRAINT CK_AuditEvents_Json CHECK (ISJSON(DetailsJson) = 1)
);

GO
CREATE TRIGGER [workerdock].AuditEvents_AppendOnly ON [workerdock].AuditEvents AFTER UPDATE, DELETE AS
BEGIN
    THROW 51001, 'Audit events are append-only', 1;
END;
GO

CREATE ROLE WorkerDockRuntime;
GRANT SELECT, UPDATE ON [workerdock].TenantSettings TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].AuditEvents TO WorkerDockRuntime;

GO
-- 0003_execution
CREATE TABLE [workerdock].TemplateCatalog (
    TemplateId nvarchar(100) NOT NULL,
    TemplateVersion nvarchar(40) NOT NULL,
    MetadataDigest char(64) NOT NULL,
    Enabled bit NOT NULL CONSTRAINT DF_TemplateCatalog_Enabled DEFAULT 1,
    CONSTRAINT PK_TemplateCatalog PRIMARY KEY (TemplateId, TemplateVersion)
);
INSERT INTO [workerdock].TemplateCatalog(TemplateId,TemplateVersion,MetadataDigest) VALUES
    (N'workerdock.sql-server',N'1.1.0',CONVERT(char(64),HASHBYTES('SHA2_256',N'workerdock.sql-server|1.1.0'),2)),
    (N'workerdock.http',N'1.1.0',CONVERT(char(64),HASHBYTES('SHA2_256',N'workerdock.http|1.1.0'),2));


CREATE TABLE [workerdock].Jobs (
    JobId uniqueidentifier NOT NULL CONSTRAINT PK_Jobs PRIMARY KEY,
    CurrentRevision bigint NULL,
    Enabled bit NOT NULL CONSTRAINT DF_Jobs_Enabled DEFAULT 0,
    Archived bit NOT NULL CONSTRAINT DF_Jobs_Archived DEFAULT 0,
    ConcurrencyLimit int NOT NULL CONSTRAINT DF_Jobs_Concurrency DEFAULT 1,
    Version rowversion NOT NULL,
    CONSTRAINT CK_Jobs_Concurrency CHECK (ConcurrencyLimit >= 0)
);
CREATE TABLE [workerdock].JobRevisions (
    JobId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    Name nvarchar(200) NOT NULL,
    TemplateId nvarchar(100) NOT NULL,
    TemplateVersion nvarchar(40) NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    ProfileId uniqueidentifier NULL,
    PolicyJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_JobRevisions PRIMARY KEY (JobId, Revision),
    CONSTRAINT FK_JobRevisions_Jobs FOREIGN KEY (JobId) REFERENCES [workerdock].Jobs(JobId),
    CONSTRAINT FK_JobRevisions_Catalog FOREIGN KEY (TemplateId,TemplateVersion) REFERENCES [workerdock].TemplateCatalog(TemplateId,TemplateVersion),
    CONSTRAINT CK_JobRevisions_Revision CHECK (Revision > 0),
    CONSTRAINT CK_JobRevisions_Configuration CHECK (ISJSON(ConfigurationJson) = 1),
    CONSTRAINT CK_JobRevisions_Policy CHECK (ISJSON(PolicyJson) = 1)
);
ALTER TABLE [workerdock].Jobs ADD CONSTRAINT FK_Jobs_CurrentRevision FOREIGN KEY (JobId,CurrentRevision)
    REFERENCES [workerdock].JobRevisions(JobId,Revision);
GO
CREATE TRIGGER [workerdock].JobRevisions_Immutable ON [workerdock].JobRevisions AFTER UPDATE, DELETE AS
BEGIN
    THROW 51003, 'Job revisions are immutable', 1;
END;
GO


CREATE TABLE [workerdock].Executions (
    ExecutionId uniqueidentifier NOT NULL CONSTRAINT PK_Executions PRIMARY KEY,
    JobId uniqueidentifier NOT NULL,
    JobRevision bigint NOT NULL,
    ProfileRevisionId uniqueidentifier NULL,
    TriggerKind nvarchar(20) NOT NULL,
    ScheduleId uniqueidentifier NULL,
    ScheduleRevision bigint NULL,
    ScheduledForUtc datetime2(7) NULL,
    RequestKey nvarchar(200) NULL,
    IdempotencyKey nvarchar(100) NOT NULL,
    State nvarchar(30) NOT NULL,
    Certainty nvarchar(10) NOT NULL,
    AvailableAtUtc datetime2(7) NOT NULL,
    CorrelationId uniqueidentifier NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT FK_Executions_JobRevision FOREIGN KEY (JobId,JobRevision) REFERENCES [workerdock].JobRevisions(JobId,Revision),
    CONSTRAINT CK_Executions_State CHECK (State IN (N'Queued',N'Running',N'RetryWaiting',N'NeedsReview',N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped')),
    CONSTRAINT CK_Executions_Certainty CHECK (Certainty IN (N'Known',N'Unknown'))
);
CREATE UNIQUE INDEX UX_Executions_Occurrence ON [workerdock].Executions(ScheduleId,ScheduleRevision,ScheduledForUtc) WHERE ScheduleId IS NOT NULL;
CREATE INDEX IX_Executions_JobStateTime ON [workerdock].Executions(JobId,State,AvailableAtUtc);

CREATE TABLE [workerdock].Attempts (
    AttemptId uniqueidentifier NOT NULL CONSTRAINT PK_Attempts PRIMARY KEY,
    ExecutionId uniqueidentifier NOT NULL,
    AttemptNumber int NOT NULL,
    WorkerBootId nvarchar(100) NULL,
    Generation bigint NOT NULL CONSTRAINT DF_Attempts_Generation DEFAULT 0,
    State nvarchar(30) NOT NULL,
    LeaseExpiresUtc datetime2(7) NULL,
    StartedAtUtc datetime2(7) NULL,
    DeadlineUtc datetime2(7) NULL,
    EndedAtUtc datetime2(7) NULL,
    SafeCode nvarchar(100) NULL,
    CONSTRAINT FK_Attempts_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId),
    CONSTRAINT UQ_Attempts_Ordinal UNIQUE (ExecutionId,AttemptNumber),
    CONSTRAINT CK_Attempts_Ordinal CHECK (AttemptNumber > 0)
);
CREATE INDEX IX_Attempts_Lease ON [workerdock].Attempts(State,LeaseExpiresUtc) WHERE LeaseExpiresUtc IS NOT NULL;


CREATE TABLE [workerdock].Commands (
    CommandId uniqueidentifier NOT NULL CONSTRAINT PK_Commands PRIMARY KEY,
    TargetId uniqueidentifier NOT NULL,
    CommandKind nvarchar(40) NOT NULL,
    State nvarchar(20) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL
);
CREATE TABLE [workerdock].ManualRequestDedup (
    JobId uniqueidentifier NOT NULL,
    ActorScopeHash char(64) NOT NULL,
    RequestKeyHash char(64) NOT NULL,
    ActorMode nvarchar(40) NOT NULL,
    ActorSubject nvarchar(300) NOT NULL,
    RequestKey nvarchar(200) NOT NULL,
    RequestHash char(64) NOT NULL,
    ExecutionId uniqueidentifier NOT NULL,
    CommandId uniqueidentifier NOT NULL,
    ResponseState nvarchar(20) NOT NULL,
    AcceptedAtUtc datetime2(7) NOT NULL,
    ExpiresAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_ManualRequestDedup PRIMARY KEY (JobId,ActorScopeHash,RequestKeyHash),
    CONSTRAINT FK_ManualRequestDedup_Job FOREIGN KEY (JobId) REFERENCES [workerdock].Jobs(JobId),
    CONSTRAINT FK_ManualRequestDedup_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId),
    CONSTRAINT FK_ManualRequestDedup_Command FOREIGN KEY (CommandId) REFERENCES [workerdock].Commands(CommandId)
);
CREATE INDEX IX_ManualRequestDedup_Expiry ON [workerdock].ManualRequestDedup(ExpiresAtUtc);


GRANT SELECT ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].JobRevisions TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE ON [workerdock].Jobs TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].JobRevisions TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE ON [workerdock].Executions TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE, DELETE ON [workerdock].ManualRequestDedup TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE ON [workerdock].Commands TO WorkerDockRuntime;

GO
-- 0003a_projection
GRANT SELECT ON [workerdock].Attempts TO WorkerDockRuntime;

GO
-- 0003b_attempts
ALTER TABLE [workerdock].Executions ADD StopRequested bit NOT NULL CONSTRAINT DF_Executions_Stop DEFAULT 0,
    ForceStop bit NOT NULL CONSTRAINT DF_Executions_Force DEFAULT 0;
ALTER TABLE [workerdock].Attempts ADD LocalExitConfirmed bit NOT NULL CONSTRAINT DF_Attempts_Exit DEFAULT 0,
    EffectsMayHaveOccurred bit NOT NULL CONSTRAINT DF_Attempts_Effects DEFAULT 0;
GRANT SELECT,INSERT,UPDATE ON [workerdock].Attempts TO WorkerDockRuntime;

GO
-- 0003c_capacity
ALTER TABLE [workerdock].Executions ADD AdmissionSequence bigint IDENTITY(1,1) NOT NULL;
CREATE INDEX IX_Executions_Admission ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State);

GO
-- 0003d_dispatch
CREATE INDEX IX_Executions_Maintenance ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State) WHERE State IN(N'Queued',N'RetryWaiting');
CREATE INDEX IX_Commands_PendingTarget ON [workerdock].Commands(TargetId,CommandKind) INCLUDE(CommandId) WHERE State=N'Pending';
CREATE INDEX IX_Executions_AdmissionActive ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State,JobRevision) WHERE State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview');

GO
-- 0004_templates
ALTER TABLE [workerdock].TemplateCatalog ADD
    DescriptorJson nvarchar(max) NULL,
    Retired bit NOT NULL CONSTRAINT DF_TemplateCatalog_Retired DEFAULT 0,
    CONSTRAINT CK_TemplateCatalog_Descriptor CHECK (DescriptorJson IS NULL OR ISJSON(DescriptorJson)=1),
    CONSTRAINT CK_TemplateCatalog_Retired CHECK (Retired=0 OR Enabled=0);
GO
CREATE TRIGGER [workerdock].TemplateCatalog_Immutable ON [workerdock].TemplateCatalog AFTER UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.TemplateId=d.TemplateId AND i.TemplateVersion=d.TemplateVersion WHERE i.TemplateId IS NULL)
        THROW 51021,'MetadataVersionConflict',1;
    IF EXISTS(
        SELECT 1 FROM deleted d JOIN inserted i ON i.TemplateId=d.TemplateId AND i.TemplateVersion=d.TemplateVersion
        WHERE d.Retired=1 AND i.Retired=0
            OR (d.DescriptorJson IS NOT NULL AND (i.DescriptorJson IS NULL OR i.DescriptorJson COLLATE Latin1_General_100_BIN2<>d.DescriptorJson COLLATE Latin1_General_100_BIN2 OR i.MetadataDigest COLLATE Latin1_General_100_BIN2<>d.MetadataDigest COLLATE Latin1_General_100_BIN2))
            OR (d.DescriptorJson IS NULL AND i.DescriptorJson IS NULL AND i.MetadataDigest COLLATE Latin1_General_100_BIN2<>d.MetadataDigest COLLATE Latin1_General_100_BIN2)
            OR (d.DescriptorJson IS NULL AND i.DescriptorJson IS NOT NULL AND NOT (
                d.TemplateVersion COLLATE Latin1_General_100_BIN2=N'1.0.0'
                AND d.TemplateId COLLATE Latin1_General_100_BIN2 IN(N'workerdock.http',N'workerdock.sql-server')
                AND d.MetadataDigest COLLATE Latin1_General_100_BIN2=CONVERT(char(64),HASHBYTES('SHA2_256',d.TemplateId+N'|'+d.TemplateVersion),2)
            )))
        THROW 51021,'MetadataVersionConflict',1;
END;
GO
-- Runtime receives catalog data mutations only; the trigger prevents identity/metadata replacement and deletion.
GRANT INSERT ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
GRANT UPDATE (DescriptorJson,MetadataDigest,Enabled,Retired) ON [workerdock].TemplateCatalog TO WorkerDockRuntime;

GO
-- 0004a_profiles
CREATE TABLE [workerdock].ConnectionProfiles (
    ProfileId uniqueidentifier NOT NULL CONSTRAINT PK_ConnectionProfiles PRIMARY KEY,
    Name nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CurrentRevisionId uniqueidentifier NULL,
    Retired bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT UQ_ConnectionProfiles_Name UNIQUE(Name),
    CONSTRAINT CK_ConnectionProfiles_Name CHECK(LEN(TRIM(Name))>0)
);
CREATE TABLE [workerdock].ConnectionProfileRevisions (
    ProfileRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_ConnectionProfileRevisions PRIMARY KEY,
    ProfileId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT FK_ConnectionProfileRevisions_Profile FOREIGN KEY(ProfileId) REFERENCES [workerdock].ConnectionProfiles(ProfileId),
    CONSTRAINT UQ_ConnectionProfileRevisions_Revision UNIQUE(ProfileId,Revision),
    CONSTRAINT UQ_ConnectionProfileRevisions_Identity UNIQUE(ProfileId,ProfileRevisionId),
    CONSTRAINT CK_ConnectionProfileRevisions_Json CHECK(ISJSON(ConfigurationJson)=1 AND DATALENGTH(ConfigurationJson)<=8192),
    CONSTRAINT CK_ConnectionProfileRevisions_Revision CHECK(Revision>0)
);
ALTER TABLE [workerdock].ConnectionProfiles ADD CONSTRAINT FK_ConnectionProfiles_Current FOREIGN KEY(ProfileId,CurrentRevisionId) REFERENCES [workerdock].ConnectionProfileRevisions(ProfileId,ProfileRevisionId);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT FK_Executions_ProfileRevision FOREIGN KEY(ProfileRevisionId) REFERENCES [workerdock].ConnectionProfileRevisions(ProfileRevisionId);
GO
CREATE TRIGGER [workerdock].ConnectionProfileRevisions_Immutable ON [workerdock].ConnectionProfileRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51023,'ProfileRevisionImmutable',1;
END;
GO
GRANT SELECT,INSERT ON [workerdock].ConnectionProfileRevisions TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].ConnectionProfiles TO WorkerDockRuntime;
GRANT UPDATE(Name,CurrentRevisionId,Retired) ON [workerdock].ConnectionProfiles TO WorkerDockRuntime;

GO
-- 0004b_integration_settings
CREATE TABLE [workerdock].IntegrationSettings (
    SettingsId tinyint NOT NULL CONSTRAINT PK_IntegrationSettings PRIMARY KEY,
    VaultRevision bigint NOT NULL,
    ProviderId nvarchar(128) NOT NULL,
    PublicOptionsJson nvarchar(4000) NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_IntegrationSettings_Singleton CHECK(SettingsId=1),
    CONSTRAINT CK_IntegrationSettings_Revision CHECK(VaultRevision>0),
    CONSTRAINT CK_IntegrationSettings_Options CHECK(ISJSON(PublicOptionsJson)=1 AND DATALENGTH(PublicOptionsJson)<=8000)
);
INSERT [workerdock].IntegrationSettings(SettingsId,VaultRevision,ProviderId,PublicOptionsJson)
VALUES(1,1,N'workerdock.environment',N'{}');

CREATE TABLE [workerdock].TenantEgressPolicies (
    Name nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT PK_TenantEgressPolicies PRIMARY KEY,
    Revision bigint NOT NULL,
    HostsJson nvarchar(4000) NOT NULL,
    PortsJson nvarchar(4000) NOT NULL,
    PrivateCidrsJson nvarchar(4000) NOT NULL,
    AllowHttp bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_TenantEgressPolicies_Revision CHECK(Revision>0),
    CONSTRAINT CK_TenantEgressPolicies_Name CHECK(Name IN(N'tenant-jobs',N'tenant-alerts')),
    CONSTRAINT CK_TenantEgressPolicies_Json CHECK(ISJSON(HostsJson)=1 AND ISJSON(PortsJson)=1 AND ISJSON(PrivateCidrsJson)=1)
);
INSERT [workerdock].TenantEgressPolicies(Name,Revision,HostsJson,PortsJson,PrivateCidrsJson,AllowHttp)
VALUES(N'tenant-jobs',1,N'[]',N'[443]',N'[]',0),(N'tenant-alerts',1,N'[]',N'[443]',N'[]',0);

CREATE TABLE [workerdock].IntegrationSettingsRevisions (
    RevisionId uniqueidentifier NOT NULL CONSTRAINT PK_IntegrationSettingsRevisions PRIMARY KEY,
    SettingsKind nvarchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SettingsName nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Revision bigint NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT UQ_IntegrationSettingsRevisions_Revision UNIQUE(SettingsKind,SettingsName,Revision),
    CONSTRAINT CK_IntegrationSettingsRevisions_Kind CHECK(SettingsKind IN(N'Vault',N'Egress')),
    CONSTRAINT CK_IntegrationSettingsRevisions_Name CHECK((SettingsKind=N'Vault' AND SettingsName=N'vault') OR (SettingsKind=N'Egress' AND SettingsName IN(N'tenant-jobs',N'tenant-alerts'))),
    CONSTRAINT CK_IntegrationSettingsRevisions_Json CHECK(ISJSON(ConfigurationJson)=1)
);
INSERT [workerdock].IntegrationSettingsRevisions(RevisionId,SettingsKind,SettingsName,Revision,ConfigurationJson,CreatedAtUtc)
VALUES(NEWID(),N'Vault',N'vault',1,N'{"providerId":"workerdock.environment","publicOptions":{}}',SYSUTCDATETIME()),
      (NEWID(),N'Egress',N'tenant-jobs',1,N'{"name":"tenant-jobs","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}',SYSUTCDATETIME()),
      (NEWID(),N'Egress',N'tenant-alerts',1,N'{"name":"tenant-alerts","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}',SYSUTCDATETIME());
GO
CREATE TRIGGER [workerdock].IntegrationSettingsRevisions_Immutable ON [workerdock].IntegrationSettingsRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51025,'IntegrationSettingsRevisionImmutable',1;
END;
GO
GRANT SELECT,INSERT,UPDATE ON [workerdock].IntegrationSettings TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].TenantEgressPolicies TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].IntegrationSettingsRevisions TO WorkerDockRuntime;

GO
-- 0005_scheduling
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

GO
-- 0006_observability
ALTER TABLE [workerdock].Executions ADD AggregatedAtUtc datetime2(7) NULL;
GO
CREATE INDEX IX_Executions_Aggregation ON [workerdock].Executions(AggregatedAtUtc,CreatedAtUtc,ExecutionId)
    INCLUDE(JobId,JobRevision,State,AvailableAtUtc,ScheduledForUtc) WHERE AggregatedAtUtc IS NULL;
GO
CREATE TABLE [workerdock].ExecutionMetricAggregates
(
    WindowStartUtc datetime2(7) NOT NULL,
    TemplateId nvarchar(100) NOT NULL,
    Outcome nvarchar(30) NOT NULL,
    ExecutionCount bigint NOT NULL,
    RetryCount bigint NOT NULL,
    TimeoutCount bigint NOT NULL,
    DurationMsTotal bigint NOT NULL,
    TriggerDelayMsTotal bigint NOT NULL,
    QueueDelayMsTotal bigint NOT NULL,
    UpdatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_ExecutionMetricAggregates PRIMARY KEY(WindowStartUtc,TemplateId,Outcome),
    CONSTRAINT CK_ExecutionMetricAggregates_Counts CHECK(ExecutionCount>=0 AND RetryCount>=0 AND TimeoutCount>=0 AND DurationMsTotal>=0 AND TriggerDelayMsTotal>=0 AND QueueDelayMsTotal>=0)
);
GO
GRANT SELECT ON [workerdock].AuditEvents TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].ExecutionMetricAggregates TO WorkerDockRuntime;

GO
-- 0007_alerts
CREATE TABLE [workerdock].AlertSinks
(
    SinkId uniqueidentifier NOT NULL CONSTRAINT PK_AlertSinks PRIMARY KEY,
    Name nvarchar(150) NOT NULL,
    CurrentRevision bigint NOT NULL,
    Enabled bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_AlertSinks_Name CHECK(LEN(Name)>0 AND DATALENGTH(Name)<=300),
    CONSTRAINT CK_AlertSinks_Revision CHECK(CurrentRevision>0)
);
GO
CREATE TABLE [workerdock].AlertSinkRevisions
(
    SinkRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_AlertSinkRevisions PRIMARY KEY,
    SinkId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    SinkType nvarchar(80) NOT NULL,
    ConfigurationJson nvarchar(4000) NOT NULL,
    SecretReferencesJson nvarchar(2000) NOT NULL,
    MaxAttempts int NOT NULL,
    RetryMinSeconds int NOT NULL,
    RetryMaxSeconds int NOT NULL,
    TimeoutSeconds int NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT FK_AlertSinkRevisions_Sink FOREIGN KEY(SinkId) REFERENCES [workerdock].AlertSinks(SinkId),
    CONSTRAINT UQ_AlertSinkRevisions_Revision UNIQUE(SinkId,Revision),
    CONSTRAINT UQ_AlertSinkRevisions_IdSink UNIQUE(SinkRevisionId,SinkId),
    CONSTRAINT CK_AlertSinkRevisions_Revision CHECK(Revision>0),
    CONSTRAINT CK_AlertSinkRevisions_Type CHECK(LEN(SinkType)>0 AND DATALENGTH(SinkType)<=160),
    CONSTRAINT CK_AlertSinkRevisions_Json CHECK(ISJSON(ConfigurationJson)=1 AND ISJSON(SecretReferencesJson)=1 AND LEFT(LTRIM(SecretReferencesJson),1)=N'['),
    CONSTRAINT CK_AlertSinkRevisions_Retry CHECK(MaxAttempts BETWEEN 1 AND 20 AND RetryMinSeconds BETWEEN 0 AND 86400 AND RetryMaxSeconds BETWEEN RetryMinSeconds AND 86400 AND TimeoutSeconds BETWEEN 1 AND 120)
);
GO
CREATE TRIGGER [workerdock].AlertSinkRevisions_Immutable ON [workerdock].AlertSinkRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51027,'AlertSinkRevisionImmutable',1;
END;
GO
CREATE TABLE [workerdock].AlertRules
(
    RuleId uniqueidentifier NOT NULL CONSTRAINT PK_AlertRules PRIMARY KEY,
    Name nvarchar(150) NOT NULL,
    Condition nvarchar(16) NOT NULL,
    Threshold int NOT NULL,
    WindowSeconds int NOT NULL,
    GraceSeconds int NOT NULL,
    ReminderSeconds int NULL,
    SinkId uniqueidentifier NOT NULL,
    Enabled bit NOT NULL,
    CurrentRevision bigint NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT FK_AlertRules_Sink FOREIGN KEY(SinkId) REFERENCES [workerdock].AlertSinks(SinkId),
    CONSTRAINT CK_AlertRules_Name CHECK(LEN(Name)>0 AND DATALENGTH(Name)<=300),
    CONSTRAINT CK_AlertRules_Condition CHECK(Condition IN(N'RepeatedFailure',N'RetryExhausted',N'Timeout',N'LongRunning',N'MissedExecution',N'WorkerUnavailable') AND (Condition NOT IN(N'LongRunning',N'WorkerUnavailable') OR Threshold=1)),
    CONSTRAINT CK_AlertRules_Threshold CHECK(Threshold BETWEEN 1 AND 1000),
    CONSTRAINT CK_AlertRules_Window CHECK(WindowSeconds BETWEEN 1 AND 2592000 AND GraceSeconds BETWEEN 0 AND 2592000 AND (ReminderSeconds IS NULL OR ReminderSeconds BETWEEN 0 AND 2592000)),
    CONSTRAINT CK_AlertRules_Revision CHECK(CurrentRevision>0)
);
GO
CREATE TABLE [workerdock].AlertRuleRevisions
(
    RuleRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_AlertRuleRevisions PRIMARY KEY,
    RuleId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    SnapshotJson nvarchar(4000) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT FK_AlertRuleRevisions_Rule FOREIGN KEY(RuleId) REFERENCES [workerdock].AlertRules(RuleId),
    CONSTRAINT UQ_AlertRuleRevisions_Revision UNIQUE(RuleId,Revision),
    CONSTRAINT CK_AlertRuleRevisions_Revision CHECK(Revision>0),
    CONSTRAINT CK_AlertRuleRevisions_Json CHECK(ISJSON(SnapshotJson)=1 AND DATALENGTH(SnapshotJson)<=8000)
);
GO
CREATE TRIGGER [workerdock].AlertRuleRevisions_Immutable ON [workerdock].AlertRuleRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51028,'AlertRuleRevisionImmutable',1;
END;
GO
CREATE TABLE [workerdock].AlertIncidents
(
    IncidentId uniqueidentifier NOT NULL CONSTRAINT PK_AlertIncidents PRIMARY KEY,
    RuleId uniqueidentifier NOT NULL,
    SubjectKey nvarchar(256) NOT NULL,
    Condition nvarchar(16) NOT NULL,
    State nvarchar(16) NOT NULL,
    OpenedAtUtc datetime2(7) NOT NULL,
    LastObservedAtUtc datetime2(7) NOT NULL,
    ResolvedAtUtc datetime2(7) NULL,
    NextReminderAtUtc datetime2(7) NULL,
    CONSTRAINT FK_AlertIncidents_Rule FOREIGN KEY(RuleId) REFERENCES [workerdock].AlertRules(RuleId),
    CONSTRAINT CK_AlertIncidents_Subject CHECK(LEN(SubjectKey)>0 AND DATALENGTH(SubjectKey)<=512),
    CONSTRAINT CK_AlertIncidents_Condition CHECK(Condition IN(N'RepeatedFailure',N'RetryExhausted',N'Timeout',N'LongRunning',N'MissedExecution',N'WorkerUnavailable',N'Unknown')),
    CONSTRAINT CK_AlertIncidents_State CHECK(State IN(N'Open',N'Unknown',N'Resolved')),
    CONSTRAINT CK_AlertIncidents_Times CHECK(LastObservedAtUtc>=OpenedAtUtc AND ((State=N'Resolved' AND ResolvedAtUtc IS NOT NULL AND ResolvedAtUtc>=OpenedAtUtc AND NextReminderAtUtc IS NULL) OR (State IN(N'Open',N'Unknown') AND ResolvedAtUtc IS NULL)))
);
GO
CREATE UNIQUE INDEX UX_AlertIncidents_OpenSubject ON [workerdock].AlertIncidents(RuleId,Condition,SubjectKey) WHERE State IN(N'Open',N'Unknown');
GO
CREATE INDEX IX_AlertIncidents_RuleObserved ON [workerdock].AlertIncidents(RuleId,LastObservedAtUtc DESC,IncidentId) INCLUDE(SubjectKey,Condition,State,OpenedAtUtc,ResolvedAtUtc,NextReminderAtUtc);
GO
CREATE INDEX IX_AlertIncidents_Reminders ON [workerdock].AlertIncidents(NextReminderAtUtc,IncidentId) INCLUDE(RuleId,SubjectKey,Condition,OpenedAtUtc,LastObservedAtUtc) WHERE State=N'Open' AND NextReminderAtUtc IS NOT NULL;
GO
CREATE TABLE [workerdock].AlertDeliveries
(
    DeliveryId uniqueidentifier NOT NULL CONSTRAINT PK_AlertDeliveries PRIMARY KEY,
    IncidentId uniqueidentifier NOT NULL,
    SinkRevisionId uniqueidentifier NOT NULL,
    Kind nvarchar(16) NOT NULL,
    Status nvarchar(16) NOT NULL,
    AttemptOrdinal int NOT NULL,
    AttemptCount int NOT NULL,
    AttemptBudget int NOT NULL,
    ManualRetryCount int NOT NULL CONSTRAINT DF_AlertDeliveries_ManualRetries DEFAULT 0,
    NextAvailableAtUtc datetime2(7) NOT NULL,
    LeaseOwner nvarchar(128) NULL,
    LeaseGeneration bigint NOT NULL,
    LeaseStartedAtUtc datetime2(7) NULL,
    LeaseExpiresAtUtc datetime2(7) NULL,
    SafeCode nvarchar(80) NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CompletedAtUtc datetime2(7) NULL,
    CONSTRAINT FK_AlertDeliveries_Incident FOREIGN KEY(IncidentId) REFERENCES [workerdock].AlertIncidents(IncidentId),
    CONSTRAINT FK_AlertDeliveries_SinkRevision FOREIGN KEY(SinkRevisionId) REFERENCES [workerdock].AlertSinkRevisions(SinkRevisionId),
    CONSTRAINT CK_AlertDeliveries_Kind CHECK(Kind IN(N'Open',N'Reminder',N'Resolve')),
    CONSTRAINT CK_AlertDeliveries_Status CHECK(Status IN(N'Pending',N'Leased',N'Delivered',N'Failed',N'Cancelled')),
    CONSTRAINT CK_AlertDeliveries_Attempts CHECK(AttemptCount>=0 AND AttemptBudget BETWEEN 1 AND 200 AND AttemptCount<=AttemptBudget AND ManualRetryCount BETWEEN 0 AND 10 AND AttemptOrdinal>=1 AND ((Status=N'Pending' AND AttemptOrdinal=AttemptCount+1) OR (Status=N'Leased' AND AttemptOrdinal=AttemptCount) OR (Status IN(N'Delivered',N'Failed',N'Cancelled') AND AttemptOrdinal=AttemptCount))),
    CONSTRAINT CK_AlertDeliveries_Lease CHECK(LeaseGeneration>=0 AND ((Status=N'Leased' AND LeaseOwner IS NOT NULL AND LEN(LeaseOwner)>0 AND LeaseStartedAtUtc IS NOT NULL AND LeaseExpiresAtUtc IS NOT NULL) OR (Status<>N'Leased' AND LeaseOwner IS NULL AND LeaseStartedAtUtc IS NULL AND LeaseExpiresAtUtc IS NULL))),
    CONSTRAINT CK_AlertDeliveries_SafeCode CHECK(SafeCode IS NULL OR (LEN(SafeCode)>0 AND DATALENGTH(SafeCode)<=160)),
    CONSTRAINT CK_AlertDeliveries_Completed CHECK((Status IN(N'Delivered',N'Failed',N'Cancelled') AND CompletedAtUtc IS NOT NULL) OR (Status IN(N'Pending',N'Leased') AND CompletedAtUtc IS NULL))
);
GO
CREATE INDEX IX_AlertDeliveries_Due ON [workerdock].AlertDeliveries(Status,NextAvailableAtUtc,DeliveryId) INCLUDE(IncidentId,SinkRevisionId,Kind,AttemptOrdinal,AttemptCount,LeaseGeneration) WHERE Status IN(N'Pending',N'Leased');
GO
CREATE INDEX IX_AlertDeliveries_Incident ON [workerdock].AlertDeliveries(IncidentId,CreatedAtUtc,DeliveryId) INCLUDE(SinkRevisionId,Kind,Status,AttemptCount,NextAvailableAtUtc,SafeCode);
GO
CREATE TABLE [workerdock].AlertDeliveryAttempts
(
    DeliveryId uniqueidentifier NOT NULL,
    AttemptOrdinal int NOT NULL,
    LeaseGeneration bigint NOT NULL,
    Result nvarchar(16) NOT NULL,
    SafeCode nvarchar(80) NULL,
    StartedAtUtc datetime2(7) NOT NULL,
    CompletedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_AlertDeliveryAttempts PRIMARY KEY(DeliveryId,AttemptOrdinal),
    CONSTRAINT FK_AlertDeliveryAttempts_Delivery FOREIGN KEY(DeliveryId) REFERENCES [workerdock].AlertDeliveries(DeliveryId),
    CONSTRAINT CK_AlertDeliveryAttempts_Ordinal CHECK(AttemptOrdinal>0 AND LeaseGeneration>0),
    CONSTRAINT CK_AlertDeliveryAttempts_Result CHECK(Result IN(N'Succeeded',N'Retry',N'PermanentFailure',N'TimedOut')),
    CONSTRAINT CK_AlertDeliveryAttempts_Times CHECK(CompletedAtUtc>=StartedAtUtc),
    CONSTRAINT CK_AlertDeliveryAttempts_SafeCode CHECK(SafeCode IS NULL OR (LEN(SafeCode)>0 AND DATALENGTH(SafeCode)<=160))
);
GO
CREATE INDEX IX_AlertDeliveryAttempts_Timing ON [workerdock].AlertDeliveryAttempts(DeliveryId,CompletedAtUtc,AttemptOrdinal) INCLUDE(LeaseGeneration,Result,SafeCode);
GO
CREATE TABLE [workerdock].AlertEvaluationState
(
    StateId tinyint NOT NULL CONSTRAINT PK_AlertEvaluationState PRIMARY KEY,
    LastEvaluatedAtUtc datetime2(7) NULL,
    LastRuleId uniqueidentifier NULL,
    LastSubjectKey nvarchar(256) NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_AlertEvaluationState_Singleton CHECK(StateId=1 AND (LastSubjectKey IS NULL OR LastRuleId IS NOT NULL)),
    CONSTRAINT FK_AlertEvaluationState_Rule FOREIGN KEY(LastRuleId) REFERENCES [workerdock].AlertRules(RuleId)
);
INSERT [workerdock].AlertEvaluationState(StateId,LastEvaluatedAtUtc,LastRuleId) VALUES(1,NULL,NULL);
GO
GRANT SELECT,INSERT,UPDATE ON [workerdock].AlertSinks TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].AlertSinkRevisions TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].AlertRules TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].AlertRuleRevisions TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].AlertIncidents TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].AlertDeliveries TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].AlertDeliveryAttempts TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].AlertEvaluationState TO WorkerDockRuntime;

GO
-- 0008_retention
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
    PurgedRowCount bigint NOT NULL CONSTRAINT DF_ArchiveBatches_PurgedRowCount DEFAULT(0),
    CONSTRAINT UQ_ArchiveBatches_Path UNIQUE(RelativePath),
    CONSTRAINT CK_ArchiveBatches_Path CHECK(LEN(RelativePath)>0 AND LEFT(RelativePath,1)<>N'/' AND RelativePath NOT LIKE N'%..%'),
    CONSTRAINT CK_ArchiveBatches_Checksum CHECK(LEN(Sha256)=64 AND Sha256 NOT LIKE '%[^0-9A-Fa-f]%'),
    CONSTRAINT CK_ArchiveBatches_Rows CHECK(RowCount>=0 AND PurgedRowCount BETWEEN 0 AND RowCount),
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
    CONSTRAINT CK_ArchiveBatchItems_Kind CHECK(RecordKind IN(N'AuditEvent',N'Execution',N'ExecutionAttempt',N'OperationalEvent',N'ManualRequestDedup'))
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
            (i.RecordKind=N'OperationalEvent' AND EXISTS(SELECT 1 FROM [workerdock].OperationalEvents r WHERE r.EventId=i.RecordId)) OR
            (i.RecordKind=N'ManualRequestDedup' AND EXISTS(SELECT 1 FROM [workerdock].ManualRequestDedup r WHERE r.CommandId=i.RecordId) AND (SELECT COUNT(*) FROM [workerdock].ManualRequestDedup r WHERE r.CommandId=i.RecordId)=1)
        )
    ) THROW 51044,'Archive batch item does not identify a source row',1;
    IF EXISTS
    (
        SELECT 1 FROM inserted i
        JOIN [workerdock].ArchiveBatches b ON b.BatchId=i.BatchId
        WHERE (i.RecordKind=N'OperationalEvent' AND EXISTS
            (SELECT 1 FROM [workerdock].OperationalEvents r WHERE r.EventId=i.RecordId AND r.AtUtc>b.LogCutoffUtc))
           OR (i.RecordKind=N'Execution' AND
               EXISTS(SELECT 1 FROM [workerdock].OperationalEvents r WHERE r.ExecutionId=i.RecordId AND r.AtUtc>b.LogCutoffUtc))
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
                AND i.PurgedRowCount>=d.PurgedRowCount AND i.PurgedRowCount<=i.RowCount
                AND USER_NAME()=N'dbo' AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=d.BatchId
          )
          AND NOT EXISTS
          (
              SELECT 1 FROM inserted i
              WHERE i.BatchId=d.BatchId AND d.PurgedRowCount<i.PurgedRowCount AND i.PurgedRowCount<=i.RowCount
                AND i.RelativePath=d.RelativePath AND i.Sha256=d.Sha256 AND i.RowCount=d.RowCount
                AND i.EligibleThroughUtc=d.EligibleThroughUtc AND i.LogCutoffUtc=d.LogCutoffUtc
                AND i.State=d.State AND i.CreatedAtUtc=d.CreatedAtUtc AND i.FinalizedAtUtc=d.FinalizedAtUtc
                AND i.ArchiveRemovedAtUtc=d.ArchiveRemovedAtUtc
                AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=d.BatchId
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
          AND NOT ((d.ArchiveRemovedAtUtc IS NULL AND i.ArchiveRemovedAtUtc IS NOT NULL
              AND i.RelativePath=d.RelativePath AND i.Sha256=d.Sha256 AND i.RowCount=d.RowCount
              AND i.EligibleThroughUtc=d.EligibleThroughUtc AND i.LogCutoffUtc=d.LogCutoffUtc
              AND i.State=d.State AND i.CreatedAtUtc=d.CreatedAtUtc AND i.FinalizedAtUtc=d.FinalizedAtUtc
              AND i.PurgedRowCount>=d.PurgedRowCount AND i.PurgedRowCount<=i.RowCount
              AND USER_NAME()=N'dbo' AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=d.BatchId)
            OR (d.ArchiveRemovedAtUtc=i.ArchiveRemovedAtUtc AND d.PurgedRowCount<i.PurgedRowCount AND i.PurgedRowCount<=i.RowCount
              AND i.RelativePath=d.RelativePath AND i.Sha256=d.Sha256 AND i.RowCount=d.RowCount
              AND i.EligibleThroughUtc=d.EligibleThroughUtc AND i.LogCutoffUtc=d.LogCutoffUtc
              AND i.State=d.State AND i.CreatedAtUtc=d.CreatedAtUtc AND i.FinalizedAtUtc=d.FinalizedAtUtc
              AND TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'WDArchivePurgeBatchId'))=d.BatchId))
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
    SELECT @ArchiveEnabled=ArchiveBeforeDelete FROM [workerdock].TenantSettings WITH(UPDLOCK,HOLDLOCK);
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
    SELECT @ArchiveEnabled=ArchiveBeforeDelete FROM [workerdock].TenantSettings WITH(UPDLOCK,HOLDLOCK);
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
    -- Lifecycle rows share OperationalEvents and are purged by PurgeOperationalEvents.

    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Attempts
        OUTPUT DELETED.AttemptId INTO @Purged(RecordId)
        WHERE (@BatchId IS NULL OR AttemptId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'ExecutionAttempt'))
          AND (@BatchId IS NULL OR Attempts.ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution'))
          -- Abandoned attempts are retained diagnostic history; purge them only with an old, aggregated terminal execution.
          AND State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped',N'Abandoned')
          AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=Attempts.ExecutionId AND e.CreatedAtUtc<=@ExecutionCutoffUtc AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),Attempts.ExecutionId) AND i.State IN(N'Open',N'Unknown'))
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].ManualRequestDedup
        OUTPUT DELETED.CommandId INTO @Purged(RecordId)
        WHERE ExpiresAtUtc<SYSUTCDATETIME()
          AND (@BatchId IS NULL OR (CommandId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'ManualRequestDedup') AND ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution')));
    SELECT @Count=COUNT(*) FROM @Purged; SET @Remaining-=@Count; SET @PurgedRows+=@Count;
    DELETE FROM @Purged;
    DELETE TOP (@Remaining) FROM [workerdock].Executions
        OUTPUT DELETED.ExecutionId INTO @Purged(RecordId)
        WHERE CreatedAtUtc<=@ExecutionCutoffUtc AND AggregatedAtUtc IS NOT NULL AND State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped')
          AND (@BatchId IS NULL OR ExecutionId IN(SELECT i.RecordId FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@BatchId AND i.RecordKind=N'Execution'))
          AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].ManualRequestDedup d WHERE d.ExecutionId=Executions.ExecutionId)
          AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionRequestTombstones t WHERE t.ExecutionId=Executions.ExecutionId AND t.ExpiresAtUtc>=SYSUTCDATETIME())
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
    SELECT @ArchiveDays=ArchiveRetentionDays,@AuditDays=AuditRetentionDays FROM [workerdock].TenantSettings WITH(UPDLOCK,HOLDLOCK);
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

GO
-- 0009_dynamic_values
ALTER TABLE [workerdock].JobRevisions ADD DynamicValuesJson nvarchar(max) NULL;
ALTER TABLE [workerdock].JobRevisions ADD CONSTRAINT CK_JobRevisions_DynamicValuesJson CHECK (DynamicValuesJson IS NULL OR ISJSON(DynamicValuesJson)=1);

ALTER TABLE [workerdock].Executions ADD
    ValueContextJson nvarchar(max) NULL,
    FirstStartedAtUtc datetime2(7) NULL,
    ValueSnapshotJson nvarchar(max) NULL,
    ValueResolutionError nvarchar(80) NULL;
ALTER TABLE [workerdock].Executions ADD CONSTRAINT CK_Executions_ValueContextJson CHECK (ValueContextJson IS NULL OR ISJSON(ValueContextJson)=1);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT CK_Executions_ValueSnapshotJson CHECK (ValueSnapshotJson IS NULL OR ISJSON(ValueSnapshotJson)=1);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT CK_Executions_ValueSnapshotError CHECK (ValueSnapshotJson IS NULL OR ValueResolutionError IS NULL);

GO
CREATE TRIGGER [workerdock].Executions_ValueStateImmutable ON [workerdock].Executions AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted i JOIN deleted d ON d.ExecutionId=i.ExecutionId
        WHERE (d.ValueContextJson IS NOT NULL AND (i.ValueContextJson IS NULL OR CONVERT(varbinary(max),i.ValueContextJson)<>CONVERT(varbinary(max),d.ValueContextJson)))
           OR (d.FirstStartedAtUtc IS NOT NULL AND (i.FirstStartedAtUtc IS NULL OR i.FirstStartedAtUtc<>d.FirstStartedAtUtc))
           OR (d.ValueSnapshotJson IS NOT NULL AND (i.ValueSnapshotJson IS NULL OR CONVERT(varbinary(max),i.ValueSnapshotJson)<>CONVERT(varbinary(max),d.ValueSnapshotJson)))
           OR (d.ValueResolutionError IS NOT NULL AND (i.ValueResolutionError IS NULL OR CONVERT(varbinary(max),i.ValueResolutionError)<>CONVERT(varbinary(max),d.ValueResolutionError)))
    ) THROW 51061, 'Execution value state is immutable once populated', 1;
END;
GO

GO
-- 0010_remove_builtin_v1
DISABLE TRIGGER [workerdock].JobRevisions_Immutable ON [workerdock].JobRevisions;
DISABLE TRIGGER [workerdock].TemplateCatalog_Immutable ON [workerdock].TemplateCatalog;

UPDATE [workerdock].JobRevisions
SET TemplateVersion=N'1.1.0'
WHERE TemplateVersion=N'1.0.0' AND TemplateId IN(N'workerdock.http',N'workerdock.sql-server');


DELETE FROM [workerdock].TemplateCatalog
WHERE TemplateVersion=N'1.0.0' AND TemplateId IN(N'workerdock.http',N'workerdock.sql-server');

ENABLE TRIGGER [workerdock].JobRevisions_Immutable ON [workerdock].JobRevisions;
ENABLE TRIGGER [workerdock].TemplateCatalog_Immutable ON [workerdock].TemplateCatalog;

IF EXISTS(SELECT 1 FROM [workerdock].JobRevisions WHERE TemplateVersion=N'1.0.0' AND TemplateId IN(N'workerdock.http',N'workerdock.sql-server'))
    THROW 51003,'LegacyTemplateRevisionRemains',1;
IF EXISTS(SELECT 1 FROM [workerdock].TemplateCatalog WHERE TemplateVersion=N'1.0.0' AND TemplateId IN(N'workerdock.http',N'workerdock.sql-server'))
    THROW 51003,'LegacyTemplateCatalogRemains',1;

GO
-- 0011_single_node_transition
CREATE TABLE [workerdock].RuntimeStatus (
    StateId tinyint NOT NULL CONSTRAINT PK_RuntimeStatus PRIMARY KEY,
    BootId uniqueidentifier NULL,
    Ready bit NOT NULL,
    HostCeiling int NOT NULL,
    LastHeartbeatUtc datetime2(7) NULL,
    CONSTRAINT CK_RuntimeStatus_SingleRow CHECK (StateId=1),
    CONSTRAINT CK_RuntimeStatus_HostCeiling CHECK (HostCeiling BETWEEN 1 AND 1000)
);
INSERT [workerdock].RuntimeStatus(StateId,BootId,Ready,HostCeiling,LastHeartbeatUtc) VALUES(1,NULL,0,1,NULL);
GRANT SELECT ON [workerdock].RuntimeStatus TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].RuntimeStatus TO WorkerDockRuntime;

GO
-- 0012_execution_manual_dedup
ALTER TABLE [workerdock].Executions ADD
    ManualActorScopeHash char(64) NULL,
    ManualRequestKeyHash char(64) NULL,
    ManualActorMode nvarchar(40) NULL,
    ManualActorSubject nvarchar(300) NULL,
    ManualRequestHash char(64) NULL,
    ManualCommandId uniqueidentifier NULL,
    ManualResponseState nvarchar(20) NULL,
    ManualRequestExpiresAtUtc datetime2(7) NULL,
    HoldsJobSlot bit NOT NULL CONSTRAINT DF_Executions_HoldsJobSlot DEFAULT(0);
ALTER TABLE [workerdock].TemplateCatalog ADD ConcurrencyLimit int NOT NULL CONSTRAINT DF_TemplateCatalog_ConcurrencyLimit DEFAULT(1);
GRANT UPDATE (ConcurrencyLimit) ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
ALTER TABLE [workerdock].IntegrationSettings ADD EgressPoliciesJson nvarchar(max) NOT NULL CONSTRAINT DF_IntegrationSettings_EgressPolicies DEFAULT(N'[]');
UPDATE settings SET EgressPoliciesJson=(SELECT p.Name AS [name],p.Revision AS [revision],JSON_QUERY(p.HostsJson) AS [hosts],JSON_QUERY(p.PortsJson) AS [ports],
    JSON_QUERY(p.PrivateCidrsJson) AS [privateCidrs],p.AllowHttp AS [allowHttp] FROM [workerdock].TenantEgressPolicies p ORDER BY p.Name FOR JSON PATH)
FROM [workerdock].IntegrationSettings settings WHERE settings.SettingsId=1;

UPDATE e SET ManualActorScopeHash=d.ActorScopeHash,ManualRequestKeyHash=d.RequestKeyHash,
    ManualActorMode=d.ActorMode,ManualActorSubject=d.ActorSubject,ManualRequestHash=d.RequestHash,
    ManualCommandId=d.CommandId,ManualResponseState=d.ResponseState,ManualRequestExpiresAtUtc=d.ExpiresAtUtc
FROM [workerdock].Executions e JOIN [workerdock].ManualRequestDedup d ON d.ExecutionId=e.ExecutionId;

CREATE UNIQUE INDEX UX_Executions_ManualRequest ON [workerdock].Executions(JobId,ManualActorScopeHash,ManualRequestKeyHash)
    WHERE ManualActorScopeHash IS NOT NULL AND ManualRequestKeyHash IS NOT NULL;
DROP TABLE [workerdock].TenantEgressPolicies;

GO
-- 0013_operational_retention
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
INSERT [workerdock].OperationalEvents(EventId,EventKind,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson)
INSERT [workerdock].OperationalEvents(EventId,EventKind,AtUtc,Code,ScheduleId,ScheduleRevision,FromUtc,ThroughUtc,Policy,CreatedExecutions,ExactMissedCount,SkippedOccurrences)
SELECT RecoveryEventId,N'ScheduleRecovery',CreatedAtUtc,N'ScheduleRecovered',ScheduleId,ScheduleRevision,FromUtc,ThroughUtc,Policy,CreatedExecutions,ExactMissedCount,SkippedOccurrences
FROM [workerdock].ScheduleRecoveryEvents;
CREATE INDEX IX_OperationalEvents_ExecutionPage ON [workerdock].OperationalEvents(EventKind,ExecutionId,AtUtc,EventId)
    INCLUDE(AttemptId,Source,Sequence,Code,SafeFieldsJson);
CREATE INDEX IX_OperationalEvents_Recovery ON [workerdock].OperationalEvents(EventKind,ScheduleId,AtUtc)
    INCLUDE(FromUtc,ThroughUtc,Policy,CreatedExecutions,ExactMissedCount,SkippedOccurrences);
GRANT SELECT,INSERT ON [workerdock].OperationalEvents TO WorkerDockRuntime;
GRANT UPDATE(LastEvaluatedAtUtc,LastSubjectKey) ON [workerdock].AlertRules TO WorkerDockRuntime;
GO
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

COMMIT;
