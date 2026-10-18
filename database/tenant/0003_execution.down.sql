IF EXISTS (SELECT 1 FROM dbo.Jobs) OR EXISTS (SELECT 1 FROM dbo.Executions) OR
   EXISTS (SELECT 1 FROM dbo.Attempts) OR EXISTS (SELECT 1 FROM dbo.JobPermits) OR
   EXISTS (SELECT 1 FROM dbo.ManualRequestDedup) OR EXISTS (SELECT 1 FROM dbo.Commands) OR
   EXISTS (SELECT 1 FROM dbo.Workers) OR EXISTS (SELECT 1 FROM dbo.ServiceHealth) OR
   EXISTS (SELECT 1 FROM dbo.PolicyRevisions) OR
   EXISTS (SELECT 1 FROM dbo.Outbox WHERE EventType = N'ExecutionDispatch')
    THROW 51002, 'Unsafe rollback: execution data requires backup and explicit removal', 1;
DROP INDEX IX_Outbox_Projection ON dbo.Outbox;
ALTER TABLE dbo.Outbox DROP CONSTRAINT DF_Outbox_SchemaVersion, DF_Outbox_ProjectionState;
ALTER TABLE dbo.Outbox DROP COLUMN SchemaVersion, EntityId, EntityRevision, PayloadDigest, ProjectionState, RetryAtUtc;
DROP TABLE dbo.ManualRequestDedup;
DROP TABLE dbo.Commands;
DROP TABLE dbo.JobPermits;
DROP TABLE dbo.Attempts;
DROP TABLE dbo.Executions;
DROP TABLE dbo.PolicyRevisions;
DROP TABLE dbo.ServiceHealth;
DROP TABLE dbo.WorkerCapabilities;
DROP TABLE dbo.Workers;
ALTER TABLE dbo.Jobs DROP CONSTRAINT FK_Jobs_CurrentRevision;
DROP TRIGGER dbo.JobRevisions_Immutable;
DROP TABLE dbo.JobRevisions;
DROP TABLE dbo.Jobs;
DROP TABLE dbo.TemplatePolicies;
DROP TABLE dbo.TemplateCatalog;
