SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Unsafe bit=0;
DECLARE @GuardSql nvarchar(max)=N'';
SELECT @GuardSql += N'IF EXISTS(SELECT 1 FROM '+QUOTENAME(s.name)+N'.'+QUOTENAME(t.name)+N') SET @Unsafe=1;'
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name=N'workerdock'
  AND t.name NOT IN(N'TenantSettings',N'SchemaHistory',N'TemplateCatalog',N'TemplatePolicies',N'PolicyRevisions',N'ProjectionMaintenance',N'IntegrationSettings',N'IntegrationSettingsRevisions',N'ScheduleRuleScanState',N'AlertEvaluationState',N'RetentionPolicy',N'RuntimeStatus');
EXEC sys.sp_executesql @GuardSql,N'@Unsafe bit OUTPUT',@Unsafe=@Unsafe OUTPUT;
IF @Unsafe=1 THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].TenantSettings)<>1 OR EXISTS(SELECT 1 FROM [workerdock].TenantSettings WHERE InstanceId<>@InstanceId OR DisplayName<>N'WorkerDock' OR DisplayTimeZone<>N'UTC' OR LogRetentionDays<>30 OR ExecutionRetentionDays<>90 OR AuditRetentionDays<>365 OR ArchiveBeforeDelete<>0 OR ArchiveRetentionDays<>0 OR RetentionVersion<>1) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].SchemaHistory)<>1 OR NOT EXISTS(SELECT 1 FROM [workerdock].SchemaHistory WHERE MigrationId=N'0001_initial') THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].TemplateCatalog)<>2 OR EXISTS(SELECT 1 FROM [workerdock].TemplateCatalog WHERE NOT (((TemplateId=N'workerdock.sql-server' AND TemplateVersion=N'1.1.0' AND MetadataDigest=CONVERT(char(64),HASHBYTES('SHA2_256',N'workerdock.sql-server|1.1.0'),2)) OR (TemplateId=N'workerdock.http' AND TemplateVersion=N'1.1.0' AND MetadataDigest=CONVERT(char(64),HASHBYTES('SHA2_256',N'workerdock.http|1.1.0'),2))) AND Enabled=1 AND DescriptorJson IS NULL AND Retired=0 AND ConcurrencyLimit=0) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].TemplatePolicies)<>2 OR EXISTS(SELECT 1 FROM [workerdock].TemplatePolicies WHERE TemplateId NOT IN(N'workerdock.sql-server',N'workerdock.http') OR DesiredLimit<>0 OR EffectiveLimit<>0 OR DesiredRevision<>0 OR EffectiveRevision<>0 OR StartGateClosed<>1) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].PolicyRevisions)<>1 OR EXISTS(SELECT 1 FROM [workerdock].PolicyRevisions WHERE PolicyId<>'c9188146-bcab-43ec-95ee-c4713f46f90d' OR PolicyKind<>N'Capacity' OR DesiredRevision<>0 OR EffectiveRevision<>0 OR State<>N'Pending' OR CatalogDigest IS NOT NULL) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].ProjectionMaintenance)<>1 OR EXISTS(SELECT 1 FROM [workerdock].ProjectionMaintenance WHERE Id<>1 OR AfterJobId IS NOT NULL) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].IntegrationSettings)<>1 OR EXISTS(SELECT 1 FROM [workerdock].IntegrationSettings WHERE SettingsId<>1 OR VaultRevision<>1 OR ProviderId<>N'workerdock.environment' OR PublicOptionsJson<>N'{}' OR EgressPoliciesJson<>N'[{"name":"tenant-alerts","revision":1,"hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false},{"name":"tenant-jobs","revision":1,"hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}]') THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].IntegrationSettingsRevisions)<>3 OR EXISTS(SELECT 1 FROM [workerdock].IntegrationSettingsRevisions WHERE NOT ((SettingsKind=N'Vault' AND SettingsName=N'vault' AND Revision=1 AND ConfigurationJson=N'{"providerId":"workerdock.environment","publicOptions":{}}') OR (SettingsKind=N'Egress' AND SettingsName=N'tenant-jobs' AND Revision=1 AND ConfigurationJson=N'{"name":"tenant-jobs","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}') OR (SettingsKind=N'Egress' AND SettingsName=N'tenant-alerts' AND Revision=1 AND ConfigurationJson=N'{"name":"tenant-alerts","hosts":[],"ports":[443],"privateCidrs":[],"allowHttp":false}'))) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].ScheduleRuleScanState)<>1 OR EXISTS(SELECT 1 FROM [workerdock].ScheduleRuleScanState WHERE ScanId<>1 OR LastScheduleId IS NOT NULL) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].AlertEvaluationState)<>1 OR EXISTS(SELECT 1 FROM [workerdock].AlertEvaluationState WHERE StateId<>1 OR LastEvaluatedAtUtc IS NOT NULL OR LastRuleId IS NOT NULL OR LastSubjectKey IS NOT NULL) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].RetentionPolicy)<>1 OR EXISTS(SELECT 1 FROM [workerdock].RetentionPolicy WHERE PolicyId<>1 OR LogDays<>30 OR ExecutionDays<>90 OR AuditDays<>365 OR ArchiveBeforeDelete<>0 OR ArchiveDays<>0) THROW 51002,'UnsafeRollback',1;
IF (SELECT COUNT(*) FROM [workerdock].RuntimeStatus)<>1 OR EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus WHERE StateId<>1 OR BootId IS NOT NULL OR Ready<>0 OR HostCeiling<>1 OR LastHeartbeatUtc IS NOT NULL) THROW 51002,'UnsafeRollback',1;
DECLARE @sql nvarchar(max)=N'';
SELECT @sql += N'ALTER TABLE '+QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+N'.'+QUOTENAME(OBJECT_NAME(parent_object_id))+N' DROP CONSTRAINT '+QUOTENAME(name)+N';'
FROM sys.foreign_keys WHERE schema_id=SCHEMA_ID(N'workerdock');
EXEC sys.sp_executesql @sql;
SET @sql=N'';
SELECT @sql += N'DROP TRIGGER '+QUOTENAME(SCHEMA_NAME(schema_id))+N'.'+QUOTENAME(name)+N';'
FROM sys.triggers WHERE schema_id=SCHEMA_ID(N'workerdock');
EXEC sys.sp_executesql @sql;
SET @sql=N'';
SELECT @sql += CASE type WHEN 'P' THEN N'DROP PROCEDURE ' WHEN 'V' THEN N'DROP VIEW ' WHEN 'FN' THEN N'DROP FUNCTION ' WHEN 'IF' THEN N'DROP FUNCTION ' WHEN 'TF' THEN N'DROP FUNCTION ' ELSE N'' END+QUOTENAME(SCHEMA_NAME(schema_id))+N'.'+QUOTENAME(name)+N';'
FROM sys.objects WHERE schema_id=SCHEMA_ID(N'workerdock') AND type IN('P','V','FN','IF','TF');
EXEC sys.sp_executesql @sql;
SET @sql=N'';
SELECT @sql += N'DROP TABLE '+QUOTENAME(SCHEMA_NAME(schema_id))+N'.'+QUOTENAME(name)+N';'
FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock');
EXEC sys.sp_executesql @sql;
DROP ROLE WorkerDockRuntime;
DROP SCHEMA [workerdock];