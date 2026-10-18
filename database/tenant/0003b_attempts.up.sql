ALTER TABLE dbo.Executions ADD StopRequested bit NOT NULL CONSTRAINT DF_Executions_Stop DEFAULT 0,
    ForceStop bit NOT NULL CONSTRAINT DF_Executions_Force DEFAULT 0;
ALTER TABLE dbo.Attempts ADD LocalExitConfirmed bit NOT NULL CONSTRAINT DF_Attempts_Exit DEFAULT 0,
    EffectsMayHaveOccurred bit NOT NULL CONSTRAINT DF_Attempts_Effects DEFAULT 0;
CREATE TABLE dbo.AttemptStatusEvents (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_AttemptStatusEvents PRIMARY KEY,
    ExecutionId uniqueidentifier NOT NULL,
    AttemptId uniqueidentifier NULL,
    State nvarchar(40) NOT NULL,
    OccurredAtUtc datetime2(7) NOT NULL,
    CONSTRAINT FK_AttemptStatusEvents_Execution FOREIGN KEY (ExecutionId) REFERENCES dbo.Executions(ExecutionId),
    CONSTRAINT FK_AttemptStatusEvents_Attempt FOREIGN KEY (AttemptId) REFERENCES dbo.Attempts(AttemptId)
);
CREATE INDEX IX_AttemptStatusEvents_Execution ON dbo.AttemptStatusEvents(ExecutionId,OccurredAtUtc,EventId);
GRANT SELECT,INSERT,UPDATE ON dbo.Attempts TO WorkerDockRuntime;
GRANT SELECT,DELETE ON dbo.JobPermits TO WorkerDockRuntime;
GRANT SELECT ON dbo.TemplatePolicies TO WorkerDockRuntime;
GRANT SELECT ON dbo.Workers TO WorkerDockRuntime;
GRANT SELECT ON dbo.WorkerCapabilities TO WorkerDockRuntime;
GRANT SELECT,INSERT ON dbo.AttemptStatusEvents TO WorkerDockRuntime;
