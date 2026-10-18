CREATE SCHEMA [workerdock] AUTHORIZATION [dbo];
GO
CREATE TABLE [workerdock].SqlTemplateTarget (
    ExecutionKey nvarchar(100) NOT NULL CONSTRAINT PK_SqlTemplateTarget PRIMARY KEY,
    Amount decimal(10,2) NOT NULL,
    TextValue nvarchar(400) NOT NULL,
    OptionalValue int NULL
);
GO
CREATE PROCEDURE [workerdock].InsertSqlTemplateTarget @key nvarchar(100),@amount decimal(10,2),@text nvarchar(400),@optional int AS
BEGIN
    SET NOCOUNT ON;
    INSERT [workerdock].SqlTemplateTarget(ExecutionKey,Amount,TextValue,OptionalValue) VALUES(@key,@amount,@text,@optional);
END;
GO
CREATE PROCEDURE [workerdock].FailSqlTemplateTarget @key nvarchar(100),@amount decimal(10,2),@text nvarchar(400),@optional int AS
BEGIN
    SET NOCOUNT ON;
    INSERT [workerdock].SqlTemplateTarget(ExecutionKey,Amount,TextValue,OptionalValue) VALUES(@key,@amount,@text,@optional);
    THROW 51025,'Synthetic target failure',1;
END;
GO
IF SUSER_ID(@TargetLogin) IS NULL THROW 51026,'TargetTestLoginMissing',1;
DECLARE @sql nvarchar(max)=N'CREATE USER '+QUOTENAME(@TargetLogin)+N' FOR LOGIN '+QUOTENAME(@TargetLogin)+N';';
EXEC sys.sp_executesql @sql;
CREATE ROLE WorkerDockSqlTarget;
SET @sql=N'ALTER ROLE WorkerDockSqlTarget ADD MEMBER '+QUOTENAME(@TargetLogin)+N';';
EXEC sys.sp_executesql @sql;
GRANT SELECT,INSERT ON [workerdock].SqlTemplateTarget TO WorkerDockSqlTarget;
GRANT EXECUTE ON [workerdock].InsertSqlTemplateTarget TO WorkerDockSqlTarget;
GRANT EXECUTE ON [workerdock].FailSqlTemplateTarget TO WorkerDockSqlTarget;
