IF EXISTS(SELECT 1 FROM [workerdock].AttemptFailureDiagnostics)
    THROW 51002,'UnsafeRollback',1;
DROP TABLE [workerdock].AttemptFailureDiagnostics;
