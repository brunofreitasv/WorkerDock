SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'[workerdock].Executions',N'U') IS NOT NULL AND EXISTS(SELECT 1 FROM [workerdock].Executions)
    THROW 51002,'UnsafeRollback',1;
IF OBJECT_ID(N'[workerdock].Jobs',N'U') IS NOT NULL AND EXISTS(SELECT 1 FROM [workerdock].Jobs)
    THROW 51002,'UnsafeRollback',1;
IF OBJECT_ID(N'[workerdock].Schedules',N'U') IS NOT NULL AND EXISTS(SELECT 1 FROM [workerdock].Schedules)
    THROW 51002,'UnsafeRollback',1;
IF OBJECT_ID(N'[workerdock].AuditEvents',N'U') IS NOT NULL AND EXISTS(SELECT 1 FROM [workerdock].AuditEvents)
    THROW 51002,'UnsafeRollback',1;
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