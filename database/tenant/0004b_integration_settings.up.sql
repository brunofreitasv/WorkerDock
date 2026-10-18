CREATE TABLE [workerdock].IntegrationSettings (
    SettingsId tinyint NOT NULL CONSTRAINT PK_IntegrationSettings PRIMARY KEY,
    VaultRevision bigint NOT NULL,
    ProviderId nvarchar(128) NOT NULL,
    PublicOptionsJson nvarchar(4000) NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_IntegrationSettings_Singleton CHECK(SettingsId=1),
    CONSTRAINT CK_IntegrationSettings_Revision CHECK(VaultRevision>0),
    CONSTRAINT CK_IntegrationSettings_Options CHECK(ISJSON(PublicOptionsJson)=1 AND DATALENGTH(PublicOptionsJson)<=8000)
);
INSERT [workerdock].IntegrationSettings(SettingsId,VaultRevision,ProviderId,PublicOptionsJson)
VALUES(1,1,N'workerdock.environment',N'{}');

CREATE TABLE [workerdock].TenantEgressPolicies (
    Name nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT PK_TenantEgressPolicies PRIMARY KEY,
    Revision bigint NOT NULL,
    HostsJson nvarchar(4000) NOT NULL,
    PortsJson nvarchar(4000) NOT NULL,
    PrivateCidrsJson nvarchar(4000) NOT NULL,
    AllowHttp bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT CK_TenantEgressPolicies_Revision CHECK(Revision>0),
    CONSTRAINT CK_TenantEgressPolicies_Name CHECK(Name IN(N'tenant-jobs',N'tenant-alerts')),
    CONSTRAINT CK_TenantEgressPolicies_Json CHECK(ISJSON(HostsJson)=1 AND ISJSON(PortsJson)=1 AND ISJSON(PrivateCidrsJson)=1)
);
INSERT [workerdock].TenantEgressPolicies(Name,Revision,HostsJson,PortsJson,PrivateCidrsJson,AllowHttp)
VALUES(N'tenant-jobs',1,N'[]',N'[443]',N'[]',0),(N'tenant-alerts',1,N'[]',N'[443]',N'[]',0);

CREATE TABLE [workerdock].IntegrationSettingsRevisions (
    RevisionId uniqueidentifier NOT NULL CONSTRAINT PK_IntegrationSettingsRevisions PRIMARY KEY,
    SettingsKind nvarchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SettingsName nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Revision bigint NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT UQ_IntegrationSettingsRevisions_Revision UNIQUE(SettingsKind,SettingsName,Revision),
    CONSTRAINT CK_IntegrationSettingsRevisions_Kind CHECK(SettingsKind IN(N'Vault',N'Egress')),
    CONSTRAINT CK_IntegrationSettingsRevisions_Name CHECK((SettingsKind=N'Vault' AND SettingsName=N'vault') OR (SettingsKind=N'Egress' AND SettingsName IN(N'tenant-jobs',N'tenant-alerts'))),
    CONSTRAINT CK_IntegrationSettingsRevisions_Json CHECK(ISJSON(ConfigurationJson)=1)
);
INSERT [workerdock].IntegrationSettingsRevisions(RevisionId,SettingsKind,SettingsName,Revision,ConfigurationJson,CreatedAtUtc)
VALUES(NEWID(),N'Vault',N'vault',1,N'{"providerId":"workerdock.environment","publicOptions":{}}',SYSUTCDATETIME()),
      (NEWID(),N'Egress',N'tenant-jobs',1,N'{"name":"tenant-jobs","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}',SYSUTCDATETIME()),
      (NEWID(),N'Egress',N'tenant-alerts',1,N'{"name":"tenant-alerts","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}',SYSUTCDATETIME());
GO
CREATE TRIGGER [workerdock].IntegrationSettingsRevisions_Immutable ON [workerdock].IntegrationSettingsRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51025,'IntegrationSettingsRevisionImmutable',1;
END;
GO
GRANT SELECT,INSERT,UPDATE ON [workerdock].IntegrationSettings TO WorkerDockRuntime;
GRANT SELECT,INSERT,UPDATE ON [workerdock].TenantEgressPolicies TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].IntegrationSettingsRevisions TO WorkerDockRuntime;
