CREATE TABLE [workerdock].ConnectionProfiles (
    ProfileId uniqueidentifier NOT NULL CONSTRAINT PK_ConnectionProfiles PRIMARY KEY,
    Name nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL,
    CurrentRevisionId uniqueidentifier NULL,
    Retired bit NOT NULL,
    Version rowversion NOT NULL,
    CONSTRAINT UQ_ConnectionProfiles_Name UNIQUE(Name),
    CONSTRAINT CK_ConnectionProfiles_Name CHECK(LEN(TRIM(Name))>0)
);
CREATE TABLE [workerdock].ConnectionProfileRevisions (
    ProfileRevisionId uniqueidentifier NOT NULL CONSTRAINT PK_ConnectionProfileRevisions PRIMARY KEY,
    ProfileId uniqueidentifier NOT NULL,
    Revision bigint NOT NULL,
    ConfigurationJson nvarchar(max) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT FK_ConnectionProfileRevisions_Profile FOREIGN KEY(ProfileId) REFERENCES [workerdock].ConnectionProfiles(ProfileId),
    CONSTRAINT UQ_ConnectionProfileRevisions_Revision UNIQUE(ProfileId,Revision),
    CONSTRAINT UQ_ConnectionProfileRevisions_Identity UNIQUE(ProfileId,ProfileRevisionId),
    CONSTRAINT CK_ConnectionProfileRevisions_Json CHECK(ISJSON(ConfigurationJson)=1 AND DATALENGTH(ConfigurationJson)<=8192),
    CONSTRAINT CK_ConnectionProfileRevisions_Revision CHECK(Revision>0)
);
ALTER TABLE [workerdock].ConnectionProfiles ADD CONSTRAINT FK_ConnectionProfiles_Current FOREIGN KEY(ProfileId,CurrentRevisionId) REFERENCES [workerdock].ConnectionProfileRevisions(ProfileId,ProfileRevisionId);
ALTER TABLE [workerdock].Executions ADD CONSTRAINT FK_Executions_ProfileRevision FOREIGN KEY(ProfileRevisionId) REFERENCES [workerdock].ConnectionProfileRevisions(ProfileRevisionId);
GO
CREATE TRIGGER [workerdock].ConnectionProfileRevisions_Immutable ON [workerdock].ConnectionProfileRevisions AFTER UPDATE,DELETE AS
BEGIN
    THROW 51023,'ProfileRevisionImmutable',1;
END;
GO
GRANT SELECT,INSERT ON [workerdock].ConnectionProfileRevisions TO WorkerDockRuntime;
GRANT SELECT,INSERT ON [workerdock].ConnectionProfiles TO WorkerDockRuntime;
GRANT UPDATE(Name,CurrentRevisionId,Retired) ON [workerdock].ConnectionProfiles TO WorkerDockRuntime;
