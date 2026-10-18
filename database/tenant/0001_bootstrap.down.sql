IF EXISTS (SELECT 1 FROM [workerdock].SchemaHistory WHERE MigrationId <> N'0001_bootstrap')
    THROW 51002, 'Unsafe rollback: remove dependent migrations first', 1;
IF EXISTS (SELECT 1 FROM [workerdock].AuditEvents) OR EXISTS (SELECT 1 FROM [workerdock].Outbox)
    THROW 51002, 'Unsafe rollback: restore prerequisites require empty audit and outbox', 1;
IF EXISTS (SELECT 1 FROM [workerdock].TenantSettings WHERE DisplayName <> N'WorkerDock' OR DisplayTimeZone <> N'UTC')
    THROW 51002, 'Unsafe rollback: restore prerequisites require default settings', 1;
IF EXISTS (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'WorkerDockRuntime'))
    THROW 51002, 'Unsafe rollback: remove runtime role members first', 1;
DROP TRIGGER [workerdock].AuditEvents_AppendOnly;
DROP ROLE WorkerDockRuntime;
DROP TABLE [workerdock].Outbox;
DROP TABLE [workerdock].AuditEvents;
DROP TABLE [workerdock].TenantSettings;
DROP TABLE [workerdock].SchemaHistory;
DROP SCHEMA [workerdock];
