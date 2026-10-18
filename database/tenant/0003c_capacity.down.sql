IF EXISTS(SELECT 1 FROM [workerdock].Workers) OR EXISTS(SELECT 1 FROM [workerdock].JobPermits)
    OR EXISTS(SELECT 1 FROM [workerdock].PolicyRevisions WHERE DesiredRevision<>0 OR EffectiveRevision<>0)
    OR EXISTS(SELECT 1 FROM [workerdock].Commands WHERE PolicyRevision IS NOT NULL)
    THROW 51002, 'UnsafeRollback', 1;
DELETE [workerdock].PolicyRevisions WHERE PolicyId='c9188146-bcab-43ec-95ee-c4713f46f90d';
REVOKE SELECT,INSERT,UPDATE ON [workerdock].PolicyRevisions FROM WorkerDockRuntime;
REVOKE INSERT,UPDATE ON [workerdock].TemplatePolicies FROM WorkerDockRuntime;
REVOKE INSERT,UPDATE ON [workerdock].Workers FROM WorkerDockRuntime;
REVOKE INSERT,DELETE ON [workerdock].WorkerCapabilities FROM WorkerDockRuntime;
REVOKE INSERT ON [workerdock].JobPermits FROM WorkerDockRuntime;
DROP INDEX UX_Workers_Boot ON [workerdock].Workers;
ALTER TABLE [workerdock].Workers DROP CONSTRAINT DF_Workers_Ack, DF_Workers_State, DF_Workers_Recovered;
ALTER TABLE [workerdock].Workers DROP COLUMN AcknowledgedRevision, State, ReservationsRecovered;
ALTER TABLE [workerdock].PolicyRevisions DROP COLUMN CatalogDigest;
ALTER TABLE [workerdock].Commands DROP COLUMN PolicyRevision;
DROP INDEX IX_Executions_Admission ON [workerdock].Executions;
ALTER TABLE [workerdock].Executions DROP COLUMN AdmissionSequence;
