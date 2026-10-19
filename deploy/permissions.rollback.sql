/*
WorkerDock permission rollback guard.

Run in the target database with the same SQLCMD variables as permissions.sql.
This paired script removes only the explicitly supplied principal-to-role
memberships when both named principals and roles exist and the membership is
present. Empty variables, absent principals/roles, and absent memberships are
no-ops. It does not revoke role grants or drop roles or business/Quartz objects.
*/
SET NOCOUNT ON;

DECLARE @TenantPrincipal sysname = N'$(TenantRuntimePrincipal)';
DECLARE @CentralPrincipal sysname = N'$(CentralRuntimePrincipal)';
DECLARE @TenantRoleId int = DATABASE_PRINCIPAL_ID(N'WorkerDockRuntime');
DECLARE @CentralRoleId int = DATABASE_PRINCIPAL_ID(N'WorkerDockCentralRuntime');
DECLARE @PrincipalId int;
DECLARE @Sql nvarchar(max);

IF @TenantPrincipal <> N''
BEGIN
    SET @PrincipalId = DATABASE_PRINCIPAL_ID(@TenantPrincipal);
    IF @TenantRoleId IS NOT NULL AND @PrincipalId IS NOT NULL AND EXISTS
       (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = @TenantRoleId AND member_principal_id = @PrincipalId)
    BEGIN
        SET @Sql = N'ALTER ROLE [WorkerDockRuntime] DROP MEMBER ' + QUOTENAME(@TenantPrincipal) + N';';
        EXEC sys.sp_executesql @Sql;
    END;
END;

IF @CentralPrincipal <> N''
BEGIN
    SET @PrincipalId = DATABASE_PRINCIPAL_ID(@CentralPrincipal);
    IF @CentralRoleId IS NOT NULL AND @PrincipalId IS NOT NULL AND EXISTS
       (SELECT 1 FROM sys.database_role_members WHERE role_principal_id = @CentralRoleId AND member_principal_id = @PrincipalId)
    BEGIN
        SET @Sql = N'ALTER ROLE [WorkerDockCentralRuntime] DROP MEMBER ' + QUOTENAME(@CentralPrincipal) + N';';
        EXEC sys.sp_executesql @Sql;
    END;
END;
GO
