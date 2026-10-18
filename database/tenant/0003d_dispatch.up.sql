CREATE TABLE [workerdock].ProjectionMaintenance (
    Id tinyint NOT NULL CONSTRAINT PK_ProjectionMaintenance PRIMARY KEY CONSTRAINT CK_ProjectionMaintenance_Id CHECK(Id=1),
    AfterJobId uniqueidentifier NULL
);
INSERT [workerdock].ProjectionMaintenance(Id,AfterJobId) VALUES(1,NULL);
CREATE INDEX IX_Executions_Maintenance ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State) WHERE State IN(N'Queued',N'RetryWaiting');
CREATE INDEX IX_Commands_PendingTarget ON [workerdock].Commands(TargetId,CommandKind) INCLUDE(CommandId) WHERE State=N'Pending';
GRANT SELECT,UPDATE ON [workerdock].ProjectionMaintenance TO WorkerDockRuntime;
CREATE INDEX IX_Executions_AdmissionActive ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State,JobRevision) WHERE State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview');
CREATE INDEX IX_Outbox_Execution ON [workerdock].Outbox(EntityId,EventType) INCLUDE(ProjectionState,DispatchedAtUtc,EntityRevision,PayloadDigest);
