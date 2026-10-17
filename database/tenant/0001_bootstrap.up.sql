CREATE TABLE dbo.TenantSettings (
    InstanceId uniqueidentifier NOT NULL CONSTRAINT PK_TenantSettings PRIMARY KEY,
    DisplayName nvarchar(200) NOT NULL,
    DisplayTimeZone nvarchar(100) NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_TenantSettings_Name CHECK (LEN(TRIM(DisplayName)) > 0),
    CONSTRAINT CK_TenantSettings_Zone CHECK (LEN(TRIM(DisplayTimeZone)) > 0)
);
INSERT INTO dbo.TenantSettings (InstanceId, DisplayName, DisplayTimeZone) VALUES (@InstanceId, N'WorkerDock', N'UTC');

CREATE TABLE dbo.SchemaHistory (
    MigrationId nvarchar(100) NOT NULL CONSTRAINT PK_SchemaHistory PRIMARY KEY,
    UpChecksum char(64) NOT NULL,
    DownChecksum char(64) NOT NULL,
    AppliedAtUtc datetime2(7) NOT NULL
);

CREATE TABLE dbo.AuditEvents (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_AuditEvents PRIMARY KEY,
    EventType nvarchar(100) NOT NULL,
    ActorSubject nvarchar(300) NOT NULL,
    AuthenticationMode nvarchar(40) NOT NULL,
    CorrelationId uniqueidentifier NOT NULL,
    OccurredAtUtc datetime2(7) NOT NULL,
    DetailsJson nvarchar(4000) NOT NULL,
    CONSTRAINT CK_AuditEvents_Json CHECK (ISJSON(DetailsJson) = 1)
);

GO
CREATE TRIGGER dbo.AuditEvents_AppendOnly ON dbo.AuditEvents AFTER UPDATE, DELETE AS
BEGIN
    THROW 51001, 'Audit events are append-only', 1;
END;
GO

CREATE TABLE dbo.Outbox (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_Outbox PRIMARY KEY,
    EventType nvarchar(100) NOT NULL,
    PayloadJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    AvailableAtUtc datetime2(7) NOT NULL,
    DispatchedAtUtc datetime2(7) NULL,
    CONSTRAINT CK_Outbox_Json CHECK (ISJSON(PayloadJson) = 1)
);
CREATE INDEX IX_Outbox_Pending ON dbo.Outbox(AvailableAtUtc, CreatedAtUtc) WHERE DispatchedAtUtc IS NULL;

CREATE ROLE WorkerDockRuntime;
GRANT SELECT, UPDATE ON dbo.TenantSettings TO WorkerDockRuntime;
GRANT INSERT ON dbo.AuditEvents TO WorkerDockRuntime;
