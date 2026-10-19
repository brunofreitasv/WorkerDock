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
