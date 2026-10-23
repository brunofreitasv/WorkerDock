CREATE TABLE [workerdock].RuntimeStatus (
    StateId tinyint NOT NULL CONSTRAINT PK_RuntimeStatus PRIMARY KEY,
    BootId uniqueidentifier NULL,
    Ready bit NOT NULL,
    HostCeiling int NOT NULL,
    LastHeartbeatUtc datetime2(7) NULL,
    CONSTRAINT CK_RuntimeStatus_SingleRow CHECK (StateId=1),
    CONSTRAINT CK_RuntimeStatus_HostCeiling CHECK (HostCeiling BETWEEN 1 AND 1000)
);
INSERT [workerdock].RuntimeStatus(StateId,BootId,Ready,HostCeiling,LastHeartbeatUtc) VALUES(1,NULL,0,1,NULL);
GRANT SELECT ON [workerdock].RuntimeStatus TO WorkerDockRuntime;
GRANT UPDATE ON [workerdock].RuntimeStatus TO WorkerDockRuntime;
