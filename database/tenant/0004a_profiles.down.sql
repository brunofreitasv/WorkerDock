IF EXISTS(SELECT 1 FROM [workerdock].Executions WHERE ProfileRevisionId IS NOT NULL)
    THROW 51024,'ProfileRollbackRequiresBackupRestore',1;
ALTER TABLE [workerdock].Executions DROP CONSTRAINT FK_Executions_ProfileRevision;
ALTER TABLE [workerdock].ConnectionProfiles DROP CONSTRAINT FK_ConnectionProfiles_Current;
DROP TRIGGER [workerdock].ConnectionProfileRevisions_Immutable;
DROP TABLE [workerdock].ConnectionProfileRevisions;
DROP TABLE [workerdock].ConnectionProfiles;
