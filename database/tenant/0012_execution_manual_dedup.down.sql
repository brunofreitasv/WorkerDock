INSERT [workerdock].ManualRequestDedup(JobId,ActorScopeHash,RequestKeyHash,ActorMode,ActorSubject,RequestKey,RequestHash,ExecutionId,CommandId,ResponseState,AcceptedAtUtc,ExpiresAtUtc)
SELECT e.JobId,e.ManualActorScopeHash,e.ManualRequestKeyHash,e.ManualActorMode,e.ManualActorSubject,e.RequestKey,e.ManualRequestHash,e.ExecutionId,e.ManualCommandId,e.ManualResponseState,e.CreatedAtUtc,e.ManualRequestExpiresAtUtc
FROM [workerdock].Executions e WHERE e.ManualActorScopeHash IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM [workerdock].ManualRequestDedup d WHERE d.JobId=e.JobId AND d.ActorScopeHash=e.ManualActorScopeHash AND d.RequestKeyHash=e.ManualRequestKeyHash);
DROP INDEX UX_Executions_ManualRequest ON [workerdock].Executions;
ALTER TABLE [workerdock].Executions DROP CONSTRAINT DF_Executions_HoldsJobSlot;
ALTER TABLE [workerdock].Executions DROP COLUMN ManualActorScopeHash,ManualRequestKeyHash,ManualActorMode,ManualActorSubject,ManualRequestHash,ManualCommandId,ManualResponseState,ManualRequestExpiresAtUtc,HoldsJobSlot;
ALTER TABLE [workerdock].TemplateCatalog DROP CONSTRAINT DF_TemplateCatalog_ConcurrencyLimit;
ALTER TABLE [workerdock].TemplateCatalog DROP COLUMN ConcurrencyLimit;
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
GRANT SELECT,INSERT,UPDATE ON [workerdock].TenantEgressPolicies TO WorkerDockRuntime;
INSERT [workerdock].TenantEgressPolicies(Name,Revision,HostsJson,PortsJson,PrivateCidrsJson,AllowHttp)
SELECT Name,Revision,JSON_QUERY(Hosts),JSON_QUERY(Ports),JSON_QUERY(PrivateCidrs),AllowHttp
FROM [workerdock].IntegrationSettings settings CROSS APPLY OPENJSON(settings.EgressPoliciesJson)
WITH(Name nvarchar(32),Revision bigint,Hosts nvarchar(max) '$.hosts' AS JSON,Ports nvarchar(max) '$.ports' AS JSON,PrivateCidrs nvarchar(max) '$.privateCidrs' AS JSON,AllowHttp bit '$.allowHttp');
ALTER TABLE [workerdock].IntegrationSettings DROP CONSTRAINT DF_IntegrationSettings_EgressPolicies;
ALTER TABLE [workerdock].IntegrationSettings DROP COLUMN EgressPoliciesJson;
