CREATE TABLE [workerdock].AttemptFailureDiagnostics (
    AttemptId uniqueidentifier NOT NULL CONSTRAINT PK_AttemptFailureDiagnostics PRIMARY KEY,
    DiagnosticJson nvarchar(max) NOT NULL,
    CONSTRAINT FK_AttemptFailureDiagnostics_Attempt FOREIGN KEY(AttemptId) REFERENCES [workerdock].Attempts(AttemptId) ON DELETE CASCADE,
    CONSTRAINT CK_AttemptFailureDiagnostics_Json CHECK(ISJSON(DiagnosticJson)=1)
);
DECLARE @DiagnosticGrant nvarchar(max)=N'GRANT SELECT,INSERT ON [workerdock].AttemptFailureDiagnostics TO '+QUOTENAME(@RuntimePrincipal)+N';';
EXEC sys.sp_executesql @DiagnosticGrant;
