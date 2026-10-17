-- Teardown only for the disposable test database after all TestHosts exit.
DROP TABLE dbo.QuartzProbeEvents;
DROP TABLE dbo.QuartzProbeState;
DECLARE @runtimeIdentifier nvarchar(258) = QUOTENAME(@RuntimeLogin);
IF @runtimeIdentifier IS NULL THROW 51020, 'Invalid runtime login', 1;
EXEC(N'ALTER ROLE WorkerDockRuntime DROP MEMBER ' + @runtimeIdentifier);
EXEC(N'DROP USER ' + @runtimeIdentifier);
