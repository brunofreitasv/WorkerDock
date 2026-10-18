IF EXISTS (SELECT 1 FROM [workerdock].Jobs) OR EXISTS (SELECT 1 FROM [workerdock].Executions) OR
   EXISTS (SELECT 1 FROM [workerdock].Attempts) OR EXISTS (SELECT 1 FROM [workerdock].JobPermits) OR
   EXISTS (SELECT 1 FROM [workerdock].ManualRequestDedup) OR EXISTS (SELECT 1 FROM [workerdock].Commands) OR
   EXISTS (SELECT 1 FROM [workerdock].Workers) OR EXISTS (SELECT 1 FROM [workerdock].ServiceHealth) OR
   EXISTS (SELECT 1 FROM [workerdock].PolicyRevisions) OR
   EXISTS (SELECT 1 FROM [workerdock].Outbox WHERE EventType = N'ExecutionDispatch')
    THROW 51002, 'Unsafe rollback: execution data requires backup and explicit removal', 1;
DROP INDEX IX_Outbox_Projection ON [workerdock].Outbox;
ALTER TABLE [workerdock].Outbox DROP CONSTRAINT DF_Outbox_SchemaVersion, DF_Outbox_ProjectionState;
ALTER TABLE [workerdock].Outbox DROP COLUMN SchemaVersion, EntityId, EntityRevision, PayloadDigest, ProjectionState, RetryAtUtc;
DROP TABLE [workerdock].ManualRequestDedup;
DROP TABLE [workerdock].Commands;
DROP TABLE [workerdock].JobPermits;
DROP TABLE [workerdock].Attempts;
DROP TABLE [workerdock].Executions;
DROP TABLE [workerdock].PolicyRevisions;
DROP TABLE [workerdock].ServiceHealth;
DROP TABLE [workerdock].WorkerCapabilities;
DROP TABLE [workerdock].Workers;
ALTER TABLE [workerdock].Jobs DROP CONSTRAINT FK_Jobs_CurrentRevision;
DROP TRIGGER [workerdock].JobRevisions_Immutable;
DROP TABLE [workerdock].JobRevisions;
DROP TABLE [workerdock].Jobs;
DROP TABLE [workerdock].TemplatePolicies;
DROP TABLE [workerdock].TemplateCatalog;
