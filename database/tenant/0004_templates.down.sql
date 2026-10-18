IF EXISTS(SELECT 1 FROM [workerdock].SchemaHistory WHERE MigrationId NOT IN(N'0001_bootstrap',N'0002_quartz',N'0003_execution',N'0003a_projection',N'0003b_attempts',N'0003c_capacity',N'0003d_dispatch',N'0004_templates'))
    OR EXISTS(SELECT 1 FROM [workerdock].TemplateCatalog WHERE DescriptorJson IS NOT NULL OR Retired=1)
    THROW 51002,'UnsafeRollback',1;
DROP TRIGGER [workerdock].TemplateCatalog_Immutable;
REVOKE INSERT ON [workerdock].TemplateCatalog FROM WorkerDockRuntime;
REVOKE UPDATE (DescriptorJson,MetadataDigest,Enabled,Retired) ON [workerdock].TemplateCatalog FROM WorkerDockRuntime;
ALTER TABLE [workerdock].TemplateCatalog DROP CONSTRAINT CK_TemplateCatalog_Descriptor, CK_TemplateCatalog_Retired, DF_TemplateCatalog_Retired;
ALTER TABLE [workerdock].TemplateCatalog DROP COLUMN DescriptorJson,Retired;
