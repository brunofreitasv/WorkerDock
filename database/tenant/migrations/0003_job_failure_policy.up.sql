ALTER TABLE [workerdock].JobRevisions ADD HideFailureDetails bit NOT NULL CONSTRAINT DF_JobRevisions_HideFailureDetails DEFAULT(0);
