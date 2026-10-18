ALTER TABLE dbo.Outbox ADD LastErrorCode nvarchar(40) NULL, ReviewAtUtc datetime2(7) NULL;
CREATE INDEX IX_Outbox_Review ON dbo.Outbox(ProjectionState,ReviewAtUtc) INCLUDE(EventId,DispatchedAtUtc);
GRANT SELECT ON dbo.Attempts TO WorkerDockRuntime;
