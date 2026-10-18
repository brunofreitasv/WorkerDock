IF EXISTS(SELECT 1 FROM dbo.Workers) OR EXISTS(SELECT 1 FROM dbo.JobPermits)
    OR EXISTS(SELECT 1 FROM dbo.PolicyRevisions WHERE DesiredRevision<>0 OR EffectiveRevision<>0)
    OR EXISTS(SELECT 1 FROM dbo.Commands WHERE PolicyRevision IS NOT NULL)
    THROW 51002, 'UnsafeRollback', 1;
DELETE dbo.PolicyRevisions WHERE PolicyId='c9188146-bcab-43ec-95ee-c4713f46f90d';
REVOKE SELECT,INSERT,UPDATE ON dbo.PolicyRevisions FROM WorkerDockRuntime;
REVOKE INSERT,UPDATE ON dbo.TemplatePolicies FROM WorkerDockRuntime;
REVOKE INSERT,UPDATE ON dbo.Workers FROM WorkerDockRuntime;
REVOKE INSERT,DELETE ON dbo.WorkerCapabilities FROM WorkerDockRuntime;
REVOKE INSERT ON dbo.JobPermits FROM WorkerDockRuntime;
DROP INDEX UX_Workers_Boot ON dbo.Workers;
ALTER TABLE dbo.Workers DROP CONSTRAINT DF_Workers_Ack, DF_Workers_State, DF_Workers_Recovered;
ALTER TABLE dbo.Workers DROP COLUMN AcknowledgedRevision, State, ReservationsRecovered;
ALTER TABLE dbo.PolicyRevisions DROP COLUMN CatalogDigest;
ALTER TABLE dbo.Commands DROP COLUMN PolicyRevision;
DROP INDEX IX_Executions_Admission ON dbo.Executions;
ALTER TABLE dbo.Executions DROP COLUMN AdmissionSequence;
