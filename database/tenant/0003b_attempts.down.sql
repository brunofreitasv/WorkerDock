IF EXISTS(SELECT 1 FROM [workerdock].AttemptStatusEvents) OR EXISTS(SELECT 1 FROM [workerdock].Attempts)
    OR EXISTS(SELECT 1 FROM [workerdock].Executions WHERE StopRequested=1 OR ForceStop=1)
    THROW 51002, 'UnsafeRollback', 1;
DROP TABLE [workerdock].AttemptStatusEvents;
REVOKE INSERT,UPDATE ON [workerdock].Attempts FROM WorkerDockRuntime;
REVOKE SELECT,DELETE ON [workerdock].JobPermits FROM WorkerDockRuntime;
REVOKE SELECT ON [workerdock].TemplatePolicies FROM WorkerDockRuntime;
REVOKE SELECT ON [workerdock].Workers FROM WorkerDockRuntime;
REVOKE SELECT ON [workerdock].WorkerCapabilities FROM WorkerDockRuntime;
ALTER TABLE [workerdock].Attempts DROP CONSTRAINT DF_Attempts_Exit, DF_Attempts_Effects;
ALTER TABLE [workerdock].Attempts DROP COLUMN LocalExitConfirmed, EffectsMayHaveOccurred;
ALTER TABLE [workerdock].Executions DROP CONSTRAINT DF_Executions_Stop, DF_Executions_Force;
ALTER TABLE [workerdock].Executions DROP COLUMN StopRequested, ForceStop;
