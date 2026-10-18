IF EXISTS(SELECT 1 FROM dbo.AttemptStatusEvents) OR EXISTS(SELECT 1 FROM dbo.Attempts)
    OR EXISTS(SELECT 1 FROM dbo.Executions WHERE StopRequested=1 OR ForceStop=1)
    THROW 51002, 'UnsafeRollback', 1;
DROP TABLE dbo.AttemptStatusEvents;
REVOKE INSERT,UPDATE ON dbo.Attempts FROM WorkerDockRuntime;
REVOKE SELECT,DELETE ON dbo.JobPermits FROM WorkerDockRuntime;
REVOKE SELECT ON dbo.TemplatePolicies FROM WorkerDockRuntime;
REVOKE SELECT ON dbo.Workers FROM WorkerDockRuntime;
REVOKE SELECT ON dbo.WorkerCapabilities FROM WorkerDockRuntime;
ALTER TABLE dbo.Attempts DROP CONSTRAINT DF_Attempts_Exit, DF_Attempts_Effects;
ALTER TABLE dbo.Attempts DROP COLUMN LocalExitConfirmed, EffectsMayHaveOccurred;
ALTER TABLE dbo.Executions DROP CONSTRAINT DF_Executions_Stop, DF_Executions_Force;
ALTER TABLE dbo.Executions DROP COLUMN StopRequested, ForceStop;
