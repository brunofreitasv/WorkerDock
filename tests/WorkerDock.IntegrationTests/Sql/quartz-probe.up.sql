-- Disposable test database only. @RuntimeLogin is an existing server SQL login.
DECLARE @runtimeIdentifier nvarchar(258) = QUOTENAME(@RuntimeLogin);
IF @runtimeIdentifier IS NULL THROW 51020, 'Invalid runtime login', 1;
EXEC(N'CREATE USER ' + @runtimeIdentifier + N' FOR LOGIN ' + @runtimeIdentifier);
EXEC(N'ALTER ROLE WorkerDockRuntime ADD MEMBER ' + @runtimeIdentifier);

CREATE TABLE dbo.QuartzProbeState (
    Id tinyint NOT NULL CONSTRAINT PK_QuartzProbeState PRIMARY KEY,
    Active int NOT NULL,
    MaximumActive int NOT NULL,
    CONSTRAINT CK_QuartzProbeState_Id CHECK (Id = 1),
    CONSTRAINT CK_QuartzProbeState_Active CHECK (Active >= 0)
);
INSERT dbo.QuartzProbeState(Id, Active, MaximumActive) VALUES (1, 0, 0);
CREATE TABLE dbo.QuartzProbeEvents (
    EventId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_QuartzProbeEvents PRIMARY KEY,
    DeliveryId char(32) NOT NULL,
    BootId nvarchar(150) NOT NULL,
    EventKind char(1) NOT NULL,
    OccurredAtUtc datetime2(7) NOT NULL CONSTRAINT DF_QuartzProbeEvents_Time DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_QuartzProbeEvents_Kind CHECK (EventKind IN ('S', 'E'))
);
GRANT SELECT, UPDATE ON dbo.QuartzProbeState TO WorkerDockRuntime;
GRANT SELECT, INSERT ON dbo.QuartzProbeEvents TO WorkerDockRuntime;
