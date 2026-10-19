SET XACT_ABORT ON;

IF EXISTS (SELECT 1 FROM [workerdock].[TenantRegistrations])
    THROW 51002, 'Central registry contains registrations; export or remove them before rollback.', 1;
IF EXISTS (SELECT 1 FROM [workerdock].[AuditEvents])
    THROW 51002, 'Central audit contains events; preserve them before rollback.', 1;
IF EXISTS (SELECT 1 FROM [workerdock].[SchemaHistory] WHERE [MigrationId] <> N'0001_registry')
    THROW 51002, 'Rollback later central migrations first.', 1;
GO

DROP TRIGGER [workerdock].[CentralAuditEvents_AppendOnly];
DROP TABLE [workerdock].[TenantHealthObservations];
DROP TABLE [workerdock].[AuditEvents];
DROP TABLE [workerdock].[TenantRegistrations];
DROP TABLE [workerdock].[CentralSettings];
DROP TABLE [workerdock].[SchemaHistory];
DROP SCHEMA [workerdock];
GO

IF DATABASE_PRINCIPAL_ID(N'WorkerDockCentralRuntime') IS NOT NULL DROP ROLE [WorkerDockCentralRuntime];
GO
