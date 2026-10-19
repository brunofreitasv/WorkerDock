SET XACT_ABORT ON;

IF SCHEMA_ID(N'workerdock') IS NULL EXEC(N'CREATE SCHEMA [workerdock] AUTHORIZATION [dbo]');
GO

CREATE TABLE [workerdock].[SchemaHistory]
(
    [MigrationId] nvarchar(100) NOT NULL CONSTRAINT [PK_CentralSchemaHistory] PRIMARY KEY,
    [UpChecksum] char(64) NOT NULL,
    [DownChecksum] char(64) NOT NULL,
    [AppliedAtUtc] datetime2(7) NOT NULL
);
GO

CREATE TABLE [workerdock].[CentralSettings]
(
    [Singleton] tinyint NOT NULL CONSTRAINT [PK_CentralSettings] PRIMARY KEY,
    [CentralInstanceId] uniqueidentifier NOT NULL,
    [CreatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_CentralSettings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    CONSTRAINT [CK_CentralSettings_Singleton] CHECK ([Singleton] = 1),
    CONSTRAINT [CK_CentralSettings_CentralInstanceId] CHECK ([CentralInstanceId] <> '00000000-0000-0000-0000-000000000000')
);
GO

CREATE TABLE [workerdock].[TenantRegistrations]
(
    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_CentralTenantRegistrations] PRIMARY KEY,
    [DisplayName] nvarchar(120) NOT NULL,
    [ExpectedInstanceId] uniqueidentifier NOT NULL,
    [DashboardUrl] nvarchar(2048) NOT NULL,
    [HealthCredentialReference] nvarchar(256) NOT NULL,
    [CreatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_CentralTenantRegistrations_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_CentralTenantRegistrations_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [CK_CentralTenantRegistrations_DisplayName] CHECK (LEN(LTRIM(RTRIM([DisplayName]))) > 0),
    CONSTRAINT [CK_CentralTenantRegistrations_ExpectedInstanceId] CHECK ([ExpectedInstanceId] <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT [CK_CentralTenantRegistrations_HealthCredentialReference] CHECK (LEN(LTRIM(RTRIM([HealthCredentialReference]))) > 0)
);
GO

CREATE TABLE [workerdock].[TenantHealthObservations]
(
    [RegistrationId] uniqueidentifier NOT NULL CONSTRAINT [PK_CentralTenantHealthObservations] PRIMARY KEY,
    [Health] varchar(32) NOT NULL,
    [LastAttemptUtc] datetime2(7) NULL,
    [LastSuccessUtc] datetime2(7) NULL,
    [SafeCode] varchar(64) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_CentralTenantHealthObservations_Registration] FOREIGN KEY ([RegistrationId]) REFERENCES [workerdock].[TenantRegistrations]([Id]) ON DELETE CASCADE,
    CONSTRAINT [CK_CentralTenantHealthObservations_Health] CHECK ([Health] IN ('Unknown','Healthy','Unhealthy','IdentityMismatch','Stale'))
);
GO

CREATE TABLE [workerdock].[AuditEvents]
(
    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_CentralAuditEvents] PRIMARY KEY,
    [AtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_CentralAuditEvents_AtUtc] DEFAULT SYSUTCDATETIME(),
    [ActorId] nvarchar(256) NOT NULL,
    [AuthenticationMode] varchar(32) NOT NULL,
    [Action] varchar(64) NOT NULL,
    [EntityType] varchar(64) NULL,
    [EntityId] nvarchar(128) NULL,
    [CorrelationId] uniqueidentifier NOT NULL,
    [SafeDetails] nvarchar(2000) NULL
);
GO

CREATE TRIGGER [workerdock].[CentralAuditEvents_AppendOnly] ON [workerdock].[AuditEvents] AFTER UPDATE, DELETE AS
BEGIN
    THROW 51001, 'Central audit events are append-only', 1;
END;
GO

CREATE INDEX [IX_CentralTenantRegistrations_DisplayName] ON [workerdock].[TenantRegistrations]([DisplayName], [Id]);
CREATE INDEX [IX_CentralTenantHealthObservations_LastAttemptUtc] ON [workerdock].[TenantHealthObservations]([LastAttemptUtc]);
GO

IF DATABASE_PRINCIPAL_ID(N'WorkerDockCentralRuntime') IS NULL CREATE ROLE [WorkerDockCentralRuntime] AUTHORIZATION [dbo];
GRANT SELECT ON OBJECT::[workerdock].[CentralSettings] TO [WorkerDockCentralRuntime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::[workerdock].[TenantRegistrations] TO [WorkerDockCentralRuntime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::[workerdock].[TenantHealthObservations] TO [WorkerDockCentralRuntime];
GRANT SELECT, INSERT ON OBJECT::[workerdock].[AuditEvents] TO [WorkerDockCentralRuntime];
GO
