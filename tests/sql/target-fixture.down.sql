DROP PROCEDURE [workerdock].FailSqlTemplateTarget;
DROP PROCEDURE [workerdock].InsertSqlTemplateTarget;
DROP TABLE [workerdock].SqlTemplateTarget;
DECLARE @sql nvarchar(max)=N'ALTER ROLE WorkerDockSqlTarget DROP MEMBER '+QUOTENAME(@TargetLogin)+N'; DROP USER '+QUOTENAME(@TargetLogin)+N';';
EXEC sys.sp_executesql @sql;
DROP ROLE WorkerDockSqlTarget;
DROP SCHEMA [workerdock];
