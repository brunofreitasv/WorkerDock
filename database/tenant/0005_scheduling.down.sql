IF EXISTS(SELECT 1 FROM [workerdock].Schedules)
    OR EXISTS(SELECT 1 FROM [workerdock].Executions WHERE ScheduleId IS NOT NULL)
    OR EXISTS(SELECT 1 FROM [workerdock].ScheduleRecoveryEvents)
    THROW 51002,'UnsafeRollback',1;
REVOKE SELECT,INSERT,UPDATE,DELETE ON [workerdock].Schedules FROM WorkerDockRuntime;
REVOKE SELECT,INSERT ON [workerdock].ScheduleRecoveryEvents FROM WorkerDockRuntime;
REVOKE SELECT,UPDATE ON [workerdock].ScheduleRuleScanState FROM WorkerDockRuntime;
DROP INDEX IX_Schedules_Cursor ON [workerdock].Schedules;
DROP INDEX IX_ScheduleRecoveryEvents_Schedule ON [workerdock].ScheduleRecoveryEvents;
ALTER TABLE [workerdock].Executions DROP CONSTRAINT CK_Executions_Occurrence, FK_Executions_Schedules;
ALTER TABLE [workerdock].Schedules DROP CONSTRAINT CK_Schedules_CatchUpRecovery, DF_Schedules_CatchUpRecoveryCreated;
ALTER TABLE [workerdock].ScheduleRecoveryEvents DROP CONSTRAINT CK_ScheduleRecoveryEvents_Range, DF_ScheduleRecoveryEvents_Skipped;
DROP TABLE [workerdock].ScheduleRecoveryEvents;
DROP TABLE [workerdock].ScheduleRuleScanState;
DROP TABLE [workerdock].Schedules;
ALTER TABLE [workerdock].Jobs DROP COLUMN ActivationBoundaryUtc,DisabledAtUtc;
