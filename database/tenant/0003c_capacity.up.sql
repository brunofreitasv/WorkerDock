ALTER TABLE [workerdock].Executions ADD AdmissionSequence bigint IDENTITY(1,1) NOT NULL;
CREATE INDEX IX_Executions_Admission ON [workerdock].Executions(JobId,AdmissionSequence) INCLUDE(State);
ALTER TABLE [workerdock].Workers ADD
    AcknowledgedRevision bigint NOT NULL CONSTRAINT DF_Workers_Ack DEFAULT -1,
    State nvarchar(30) NOT NULL CONSTRAINT DF_Workers_State DEFAULT N'Joining',
    ReservationsRecovered bit NOT NULL CONSTRAINT DF_Workers_Recovered DEFAULT 0;
CREATE UNIQUE INDEX UX_Workers_Boot ON [workerdock].Workers(BootId);
ALTER TABLE [workerdock].PolicyRevisions ADD CatalogDigest char(64) NULL;
ALTER TABLE [workerdock].Commands ADD PolicyRevision bigint NULL;
INSERT [workerdock].PolicyRevisions(PolicyId,PolicyKind,DesiredRevision,EffectiveRevision,State,CreatedAtUtc)
VALUES('c9188146-bcab-43ec-95ee-c4713f46f90d',N'Capacity',0,0,N'Pending',SYSUTCDATETIME());
UPDATE [workerdock].TemplatePolicies SET StartGateClosed=1;
GRANT SELECT,INSERT,UPDATE ON [workerdock].PolicyRevisions TO WorkerDockRuntime;
GRANT INSERT,UPDATE ON [workerdock].TemplatePolicies TO WorkerDockRuntime;
GRANT INSERT,UPDATE ON [workerdock].Workers TO WorkerDockRuntime;
GRANT INSERT,DELETE ON [workerdock].WorkerCapabilities TO WorkerDockRuntime;
GRANT INSERT ON [workerdock].JobPermits TO WorkerDockRuntime;
