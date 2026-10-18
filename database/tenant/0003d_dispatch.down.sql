DROP INDEX IX_Outbox_Execution ON [workerdock].Outbox;
DROP INDEX IX_Executions_AdmissionActive ON [workerdock].Executions;
DROP INDEX IX_Commands_PendingTarget ON [workerdock].Commands;
DROP INDEX IX_Executions_Maintenance ON [workerdock].Executions;
DROP TABLE [workerdock].ProjectionMaintenance;
