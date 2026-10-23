ALTER TABLE [workerdock].Executions ADD
    ManualActorScopeHash char(64) NULL,
    ManualRequestKeyHash char(64) NULL,
    ManualActorMode nvarchar(40) NULL,
    ManualActorSubject nvarchar(300) NULL,
    ManualRequestHash char(64) NULL,
    ManualCommandId uniqueidentifier NULL,
    ManualResponseState nvarchar(20) NULL,
    ManualRequestExpiresAtUtc datetime2(7) NULL,
    HoldsJobSlot bit NOT NULL CONSTRAINT DF_Executions_HoldsJobSlot DEFAULT(0);
ALTER TABLE [workerdock].TemplateCatalog ADD ConcurrencyLimit int NOT NULL CONSTRAINT DF_TemplateCatalog_ConcurrencyLimit DEFAULT(1);
GRANT UPDATE (ConcurrencyLimit) ON [workerdock].TemplateCatalog TO WorkerDockRuntime;
UPDATE cat SET ConcurrencyLimit=p.EffectiveLimit FROM [workerdock].TemplateCatalog cat JOIN [workerdock].TemplatePolicies p ON p.TemplateId=cat.TemplateId;
ALTER TABLE [workerdock].IntegrationSettings ADD EgressPoliciesJson nvarchar(max) NOT NULL CONSTRAINT DF_IntegrationSettings_EgressPolicies DEFAULT(N'[]');
UPDATE settings SET EgressPoliciesJson=(SELECT p.Name AS [name],p.Revision AS [revision],JSON_QUERY(p.HostsJson) AS [hosts],JSON_QUERY(p.PortsJson) AS [ports],
    JSON_QUERY(p.PrivateCidrsJson) AS [privateCidrs],p.AllowHttp AS [allowHttp] FROM [workerdock].TenantEgressPolicies p ORDER BY p.Name FOR JSON PATH)
FROM [workerdock].IntegrationSettings settings WHERE settings.SettingsId=1;

UPDATE e SET ManualActorScopeHash=d.ActorScopeHash,ManualRequestKeyHash=d.RequestKeyHash,
    ManualActorMode=d.ActorMode,ManualActorSubject=d.ActorSubject,ManualRequestHash=d.RequestHash,
    ManualCommandId=d.CommandId,ManualResponseState=d.ResponseState,ManualRequestExpiresAtUtc=d.ExpiresAtUtc
FROM [workerdock].Executions e JOIN [workerdock].ManualRequestDedup d ON d.ExecutionId=e.ExecutionId;
UPDATE e SET HoldsJobSlot=1 FROM [workerdock].Executions e JOIN [workerdock].JobPermits p ON p.ExecutionId=e.ExecutionId;

CREATE UNIQUE INDEX UX_Executions_ManualRequest ON [workerdock].Executions(JobId,ManualActorScopeHash,ManualRequestKeyHash)
    WHERE ManualActorScopeHash IS NOT NULL AND ManualRequestKeyHash IS NOT NULL;
DROP TABLE [workerdock].TenantEgressPolicies;
