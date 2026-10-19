/*
WorkerDock runtime principal binding.

Run in each target database after its versioned migrations, as a database
owner/security administrator. Supply SQLCMD variables TenantRuntimePrincipal
and CentralRuntimePrincipal as existing database principal names (not server
login names); set the unused variable to an empty string. Create/map those
principals separately using the deployment's approved identity process.

This script only adds supplied SQL/Windows users or groups to the existing
WorkerDockRuntime (tenant) and/or WorkerDockCentralRuntime (central) roles.
Versioned migrations own all table and routine grants, including Quartz DML,
business data, audit access, and the narrow [workerdock] purge routines. This
script creates or changes no roles, schemas, tables, procedures, or Quartz data.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @TenantPrincipal sysname = N'$(TenantRuntimePrincipal)';
DECLARE @CentralPrincipal sysname = N'$(CentralRuntimePrincipal)';
DECLARE @PrincipalId int;
DECLARE @RoleId int;
DECLARE @Sql nvarchar(max);

IF @TenantPrincipal <> N''
BEGIN
    SET @PrincipalId = DATABASE_PRINCIPAL_ID(@TenantPrincipal);
    SET @RoleId = DATABASE_PRINCIPAL_ID(N'WorkerDockRuntime');
    IF @RoleId IS NULL
        THROW 51100, 'Required versioned role WorkerDockRuntime is missing; apply tenant migrations first.', 1;
    IF @PrincipalId IS NULL
        THROW 51101, 'Tenant runtime database principal does not exist; create/map it before running this script.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE principal_id = @PrincipalId AND type IN ('S','U','G'))
        THROW 51102, 'Tenant runtime principal must be a SQL or Windows database user/group.', 1;

    SET @Sql = N'ALTER ROLE [WorkerDockRuntime] ADD MEMBER ' + QUOTENAME(@TenantPrincipal) + N';';
    IF NOT EXISTS (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = @RoleId AND member_principal_id = @PrincipalId)
        EXEC sys.sp_executesql @Sql;
END;

IF @CentralPrincipal <> N''
BEGIN
    SET @PrincipalId = DATABASE_PRINCIPAL_ID(@CentralPrincipal);
    SET @RoleId = DATABASE_PRINCIPAL_ID(N'WorkerDockCentralRuntime');
    IF @RoleId IS NULL
        THROW 51103, 'Required versioned role WorkerDockCentralRuntime is missing; apply central migrations first.', 1;
    IF @PrincipalId IS NULL
        THROW 51104, 'Central runtime database principal does not exist; create/map it before running this script.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE principal_id = @PrincipalId AND type IN ('S','U','G'))
        THROW 51105, 'Central runtime principal must be a SQL or Windows database user/group.', 1;

    SET @Sql = N'ALTER ROLE [WorkerDockCentralRuntime] ADD MEMBER ' + QUOTENAME(@CentralPrincipal) + N';';
    IF NOT EXISTS (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = @RoleId AND member_principal_id = @PrincipalId)
        EXEC sys.sp_executesql @Sql;
END;
GO
