IF EXISTS(SELECT 1 FROM [workerdock].IntegrationSettingsRevisions WHERE Revision>1)
    THROW 51026,'IntegrationSettingsRollbackRequiresBackupRestore',1;
DROP TRIGGER [workerdock].IntegrationSettingsRevisions_Immutable;
DROP TABLE [workerdock].IntegrationSettingsRevisions;
DROP TABLE [workerdock].TenantEgressPolicies;
DROP TABLE [workerdock].IntegrationSettings;
