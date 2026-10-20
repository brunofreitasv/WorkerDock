IF EXISTS (SELECT 1 FROM [workerdock].Executions WHERE ValueContextJson IS NOT NULL OR FirstStartedAtUtc IS NOT NULL OR ValueSnapshotJson IS NOT NULL OR ValueResolutionError IS NOT NULL)
    THROW 51002, 'Cannot roll back populated execution value state', 1;
IF EXISTS (SELECT 1 FROM [workerdock].JobRevisions WHERE DynamicValuesJson IS NOT NULL)
    THROW 51002, 'Cannot roll back populated dynamic job plans', 1;
GO
DROP TRIGGER [workerdock].Executions_ValueStateImmutable;
ALTER TABLE [workerdock].Executions DROP CONSTRAINT CK_Executions_ValueSnapshotError, CK_Executions_ValueSnapshotJson, CK_Executions_ValueContextJson;
ALTER TABLE [workerdock].Executions DROP COLUMN ValueResolutionError, ValueSnapshotJson, FirstStartedAtUtc, ValueContextJson;
ALTER TABLE [workerdock].JobRevisions DROP CONSTRAINT CK_JobRevisions_DynamicValuesJson;
ALTER TABLE [workerdock].JobRevisions DROP COLUMN DynamicValuesJson;
