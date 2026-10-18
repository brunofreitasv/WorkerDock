ALTER TABLE dbo.Executions ADD AdmissionSequence bigint IDENTITY(1,1) NOT NULL;
CREATE INDEX IX_Executions_Admission ON dbo.Executions(JobId,AdmissionSequence) INCLUDE(State);
ALTER TABLE dbo.Workers ADD
    AcknowledgedRevision bigint NOT NULL CONSTRAINT DF_Workers_Ack DEFAULT -1,
    State nvarchar(30) NOT NULL CONSTRAINT DF_Workers_State DEFAULT N'Joining',
    ReservationsRecovered bit NOT NULL CONSTRAINT DF_Workers_Recovered DEFAULT 0;
CREATE UNIQUE INDEX UX_Workers_Boot ON dbo.Workers(BootId);
ALTER TABLE dbo.PolicyRevisions ADD CatalogDigest char(64) NULL;
ALTER TABLE dbo.Commands ADD PolicyRevision bigint NULL;
INSERT dbo.PolicyRevisions(PolicyId,PolicyKind,DesiredRevision,EffectiveRevision,State,CreatedAtUtc)
VALUES('c9188146-bcab-43ec-95ee-c4713f46f90d',N'Capacity',0,0,N'Pending',SYSUTCDATETIME());
UPDATE dbo.TemplatePolicies SET StartGateClosed=1;
GRANT SELECT,INSERT,UPDATE ON dbo.PolicyRevisions TO WorkerDockRuntime;
GRANT INSERT,UPDATE ON dbo.TemplatePolicies TO WorkerDockRuntime;
GRANT INSERT,UPDATE ON dbo.Workers TO WorkerDockRuntime;
GRANT INSERT,DELETE ON dbo.WorkerCapabilities TO WorkerDockRuntime;
GRANT INSERT ON dbo.JobPermits TO WorkerDockRuntime;
