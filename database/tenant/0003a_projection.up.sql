ALTER TABLE [workerdock].Outbox ADD LastErrorCode nvarchar(40) NULL, ReviewAtUtc datetime2(7) NULL;
CREATE INDEX IX_Outbox_Review ON [workerdock].Outbox(ProjectionState,ReviewAtUtc) INCLUDE(EventId,DispatchedAtUtc);
GRANT SELECT ON [workerdock].Attempts TO WorkerDockRuntime;
