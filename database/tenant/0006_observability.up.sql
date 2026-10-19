ALTER TABLE [workerdock].Executions ADD AggregatedAtUtc datetime2(7) NULL;
GO
CREATE INDEX IX_Executions_Aggregation ON [workerdock].Executions(AggregatedAtUtc,CreatedAtUtc,ExecutionId)
    INCLUDE(JobId,JobRevision,State,AvailableAtUtc,ScheduledForUtc) WHERE AggregatedAtUtc IS NULL;
GO
CREATE TABLE [workerdock].ExecutionEvents
(
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_ExecutionEvents PRIMARY KEY,
    ExecutionId uniqueidentifier NOT NULL,
    AttemptId uniqueidentifier NULL,
    AttemptScopeId AS ISNULL(AttemptId,CONVERT(uniqueidentifier,'00000000-0000-0000-0000-000000000000')) PERSISTED,
    Source nvarchar(20) NOT NULL,
    Sequence bigint NOT NULL,
    AtUtc datetime2(7) NOT NULL,
    Code nvarchar(80) NOT NULL,
    SafeFieldsJson nvarchar(2048) NOT NULL,
    CONSTRAINT FK_ExecutionEvents_Execution FOREIGN KEY(ExecutionId) REFERENCES [workerdock].Executions(ExecutionId),
    CONSTRAINT FK_ExecutionEvents_Attempt FOREIGN KEY(AttemptId) REFERENCES [workerdock].Attempts(AttemptId),
    CONSTRAINT CK_ExecutionEvents_Source CHECK(Source IN(N'Lifecycle',N'Supervisor',N'Handler')),
    CONSTRAINT CK_ExecutionEvents_Sequence CHECK(Sequence>0),
    CONSTRAINT CK_ExecutionEvents_Code CHECK(LEN(Code)>0),
    CONSTRAINT CK_ExecutionEvents_Json CHECK(ISJSON(SafeFieldsJson)=1),
    CONSTRAINT UQ_ExecutionEvents_Scope UNIQUE(ExecutionId,AttemptScopeId,Source,Sequence)
);
GO
CREATE INDEX IX_ExecutionEvents_Page ON [workerdock].ExecutionEvents(ExecutionId,AtUtc,EventId)
    INCLUDE(AttemptId,Source,Sequence,Code,SafeFieldsJson);
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
GRANT SELECT,INSERT ON [workerdock].ExecutionEvents TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].ExecutionMetricAggregates TO WorkerDockRuntime;
