IF EXISTS (SELECT 1 FROM dbo.AuditEvents) OR EXISTS (SELECT 1 FROM dbo.Outbox)
    THROW 51002, 'Unsafe rollback: restore prerequisites require empty audit and outbox', 1;
IF EXISTS (SELECT 1 FROM dbo.TenantSettings WHERE DisplayName <> N'WorkerDock' OR DisplayTimeZone <> N'UTC')
    THROW 51002, 'Unsafe rollback: restore prerequisites require default settings', 1;
IF EXISTS (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'WorkerDockRuntime'))
    THROW 51002, 'Unsafe rollback: remove runtime role members first', 1;
DROP TRIGGER dbo.AuditEvents_AppendOnly;
DROP ROLE WorkerDockRuntime;
DROP TABLE dbo.Outbox;
DROP TABLE dbo.AuditEvents;
DROP TABLE dbo.TenantSettings;
DROP TABLE dbo.SchemaHistory;
