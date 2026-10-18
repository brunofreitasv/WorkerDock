CREATE TABLE [workerdock].TemplateCatalog (
    TemplateId nvarchar(100) NOT NULL,
    TemplateVersion nvarchar(40) NOT NULL,
    MetadataDigest char(64) NOT NULL,
    Enabled bit NOT NULL CONSTRAINT DF_TemplateCatalog_Enabled DEFAULT 1,
    CONSTRAINT PK_TemplateCatalog PRIMARY KEY (TemplateId, TemplateVersion)
);
INSERT INTO [workerdock].TemplateCatalog(TemplateId,TemplateVersion,MetadataDigest) VALUES
    (N'workerdock.sql-server',N'1.0.0',CONVERT(char(64),HASHBYTES('SHA2_256',N'workerdock.sql-server|1.0.0'),2)),
    (N'workerdock.http',N'1.0.0',CONVERT(char(64),HASHBYTES('SHA2_256',N'workerdock.http|1.0.0'),2));

CREATE TABLE [workerdock].TemplatePolicies (
    TemplateId nvarchar(100) NOT NULL CONSTRAINT PK_TemplatePolicies PRIMARY KEY,
    DesiredLimit int NOT NULL CONSTRAINT DF_TemplatePolicies_Desired DEFAULT 0,
    EffectiveLimit int NOT NULL CONSTRAINT DF_TemplatePolicies_Effective DEFAULT 0,
    DesiredRevision bigint NOT NULL CONSTRAINT DF_TemplatePolicies_DesiredRevision DEFAULT 0,
    EffectiveRevision bigint NOT NULL CONSTRAINT DF_TemplatePolicies_EffectiveRevision DEFAULT 0,
    StartGateClosed bit NOT NULL CONSTRAINT DF_TemplatePolicies_Gate DEFAULT 1,
    CONSTRAINT CK_TemplatePolicies_Limits CHECK (DesiredLimit >= 0 AND EffectiveLimit >= 0)
);
INSERT INTO [workerdock].TemplatePolicies(TemplateId) VALUES (N'workerdock.sql-server'),(N'workerdock.http');

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

CREATE TABLE [workerdock].Workers (
    InstallationId uniqueidentifier NOT NULL CONSTRAINT PK_Workers PRIMARY KEY,
    BootId nvarchar(100) NOT NULL,
    CatalogDigest char(64) NOT NULL,
    CapacityCeiling int NOT NULL,
    LiveLimit int NOT NULL,
    LastHeartbeatUtc datetime2(7) NOT NULL,
    CONSTRAINT CK_Workers_Capacity CHECK (CapacityCeiling >= 0 AND LiveLimit BETWEEN 0 AND CapacityCeiling)
);
CREATE TABLE [workerdock].WorkerCapabilities (
    InstallationId uniqueidentifier NOT NULL,
    TemplateId nvarchar(100) NOT NULL,
    TemplateVersion nvarchar(40) NOT NULL,
    CONSTRAINT PK_WorkerCapabilities PRIMARY KEY (InstallationId,TemplateId,TemplateVersion),
    CONSTRAINT FK_WorkerCapabilities_Worker FOREIGN KEY (InstallationId) REFERENCES [workerdock].Workers(InstallationId),
    CONSTRAINT FK_WorkerCapabilities_Catalog FOREIGN KEY (TemplateId,TemplateVersion) REFERENCES [workerdock].TemplateCatalog(TemplateId,TemplateVersion)
);
CREATE TABLE [workerdock].ServiceHealth (
    ServiceId uniqueidentifier NOT NULL CONSTRAINT PK_ServiceHealth PRIMARY KEY,
    ServiceKind nvarchar(40) NOT NULL,
    BootId nvarchar(100) NOT NULL,
    LastHeartbeatUtc datetime2(7) NOT NULL,
    Ready bit NOT NULL
);
CREATE TABLE [workerdock].PolicyRevisions (
    PolicyId uniqueidentifier NOT NULL CONSTRAINT PK_PolicyRevisions PRIMARY KEY,
    PolicyKind nvarchar(40) NOT NULL,
    DesiredRevision bigint NOT NULL,
    EffectiveRevision bigint NOT NULL,
    State nvarchar(30) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL
);

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
    InstallationId uniqueidentifier NULL,
    WorkerBootId nvarchar(100) NULL,
    Generation bigint NOT NULL CONSTRAINT DF_Attempts_Generation DEFAULT 0,
    State nvarchar(30) NOT NULL,
    LeaseExpiresUtc datetime2(7) NULL,
    StartedAtUtc datetime2(7) NULL,
    DeadlineUtc datetime2(7) NULL,
    EndedAtUtc datetime2(7) NULL,
    SafeCode nvarchar(100) NULL,
    CONSTRAINT FK_Attempts_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId),
    CONSTRAINT FK_Attempts_Worker FOREIGN KEY (InstallationId) REFERENCES [workerdock].Workers(InstallationId),
    CONSTRAINT UQ_Attempts_Ordinal UNIQUE (ExecutionId,AttemptNumber),
    CONSTRAINT CK_Attempts_Ordinal CHECK (AttemptNumber > 0)
);
CREATE INDEX IX_Attempts_Lease ON [workerdock].Attempts(State,LeaseExpiresUtc) WHERE LeaseExpiresUtc IS NOT NULL;

CREATE TABLE [workerdock].JobPermits (
    PermitId uniqueidentifier NOT NULL CONSTRAINT PK_JobPermits PRIMARY KEY,
    JobId uniqueidentifier NOT NULL,
    ExecutionId uniqueidentifier NOT NULL,
    Mode nvarchar(20) NOT NULL,
    AcquiredAtUtc datetime2(7) NOT NULL,
    CONSTRAINT FK_JobPermits_Job FOREIGN KEY (JobId) REFERENCES [workerdock].Jobs(JobId),
    CONSTRAINT FK_JobPermits_Execution FOREIGN KEY (ExecutionId) REFERENCES [workerdock].Executions(ExecutionId),
    CONSTRAINT UQ_JobPermits_Execution UNIQUE (ExecutionId)
);
CREATE INDEX IX_JobPermits_Job ON [workerdock].JobPermits(JobId,AcquiredAtUtc);

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

ALTER TABLE [workerdock].Outbox ADD
    SchemaVersion int NOT NULL CONSTRAINT DF_Outbox_SchemaVersion DEFAULT 1,
    EntityId uniqueidentifier NULL,
    EntityRevision bigint NULL,
    PayloadDigest char(64) NULL,
    ProjectionState nvarchar(20) NOT NULL CONSTRAINT DF_Outbox_ProjectionState DEFAULT N'Pending',
    RetryAtUtc datetime2(7) NULL;
CREATE INDEX IX_Outbox_Projection ON [workerdock].Outbox(ProjectionState,AvailableAtUtc,CreatedAtUtc) INCLUDE(EntityId,EntityRevision);

GRANT SELECT ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
GRANT SELECT ON [workerdock].JobRevisions TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE ON [workerdock].Jobs TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].JobRevisions TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE ON [workerdock].Executions TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE, DELETE ON [workerdock].ManualRequestDedup TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE ON [workerdock].Commands TO WorkerDockRuntime;
GRANT SELECT, INSERT, UPDATE ON [workerdock].Outbox TO WorkerDockRuntime;
