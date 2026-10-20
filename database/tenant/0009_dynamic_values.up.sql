ALTER TABLE [workerdock].JobRevisions ADD DynamicValuesJson nvarchar(max) NULL;
ALTER TABLE [workerdock].JobRevisions ADD CONSTRAINT CK_JobRevisions_DynamicValuesJson CHECK (DynamicValuesJson IS NULL OR ISJSON(DynamicValuesJson)=1);

ALTER TABLE [workerdock].Executions ADD
    ValueContextJson nvarchar(max) NULL,
    FirstStartedAtUtc datetime2(7) NULL,
    ValueSnapshotJson nvarchar(max) NULL,
    ValueResolutionError nvarchar(80) NULL;
ALTER TABLE [workerdock].Executions ADD CONSTRAINT CK_Executions_ValueContextJson CHECK (ValueContextJson IS NULL OR ISJSON(ValueContextJson)=1);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT CK_Executions_ValueSnapshotJson CHECK (ValueSnapshotJson IS NULL OR ISJSON(ValueSnapshotJson)=1);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT CK_Executions_ValueSnapshotError CHECK (ValueSnapshotJson IS NULL OR ValueResolutionError IS NULL);

GO
CREATE TRIGGER [workerdock].Executions_ValueStateImmutable ON [workerdock].Executions AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted i JOIN deleted d ON d.ExecutionId=i.ExecutionId
        WHERE (d.ValueContextJson IS NOT NULL AND ISNULL(i.ValueContextJson,N'')<>ISNULL(d.ValueContextJson,N''))
           OR (d.FirstStartedAtUtc IS NOT NULL AND ISNULL(i.FirstStartedAtUtc,CONVERT(datetime2(7),'0001-01-01'))<>d.FirstStartedAtUtc)
           OR (d.ValueSnapshotJson IS NOT NULL AND ISNULL(i.ValueSnapshotJson,N'')<>ISNULL(d.ValueSnapshotJson,N''))
           OR (d.ValueResolutionError IS NOT NULL AND ISNULL(i.ValueResolutionError,N'')<>ISNULL(d.ValueResolutionError,N''))
    ) THROW 51061, 'Execution value state is immutable once populated', 1;
END;
GO
