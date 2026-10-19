IF EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents)
    THROW 51006, 'Cannot remove persisted execution diagnostics', 1;
IF EXISTS(SELECT 1 FROM [workerdock].ExecutionMetricAggregates) OR EXISTS(SELECT 1 FROM [workerdock].Executions WHERE AggregatedAtUtc IS NOT NULL)
    THROW 51006, 'Cannot remove persisted execution aggregates', 1;
GO
REVOKE SELECT ON [workerdock].AuditEvents FROM WorkerDockRuntime;
REVOKE SELECT,INSERT ON [workerdock].ExecutionEvents FROM WorkerDockRuntime;
REVOKE SELECT,INSERT,UPDATE ON [workerdock].ExecutionMetricAggregates FROM WorkerDockRuntime;
DROP TABLE [workerdock].ExecutionMetricAggregates;
DROP TABLE [workerdock].ExecutionEvents;
DROP INDEX IX_Executions_Aggregation ON [workerdock].Executions;
ALTER TABLE [workerdock].Executions DROP COLUMN AggregatedAtUtc;
