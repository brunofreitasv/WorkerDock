using System.Data;
using System.Data.Common;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;
namespace WorkerDock.Infrastructure.Executions;

internal static class SqlCommandOutcomes
{
    internal static async ValueTask SettleAsync(DbConnection connection, DbTransaction transaction, Guid executionId, CancellationToken ct, bool rejected = false)
    {
        await using var command = Command(connection, transaction, """
            DECLARE @changed TABLE(CommandId uniqueidentifier,TargetId uniqueidentifier,State nvarchar(20));
            UPDATE c SET State=CASE WHEN c.CommandKind IN(N'ManualRun',N'ProfileDiagnostic') THEN
                CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId) THEN N'Applied'
                    WHEN @rejected=1 OR e.State IN(N'Stopped',N'Skipped',N'Failed',N'TimedOut') THEN N'Failed' ELSE N'Pending' END
                ELSE CASE WHEN e.State=N'Running' THEN N'Pending' WHEN e.State=N'NeedsReview' THEN N'Failed' ELSE N'Applied' END END
            OUTPUT inserted.CommandId,inserted.TargetId,inserted.State INTO @changed
            FROM [workerdock].Commands c JOIN [workerdock].Executions e ON e.ExecutionId=c.TargetId
            WHERE c.TargetId=@execution AND c.State=N'Pending' AND c.CommandKind IN(N'ManualRun',N'ProfileDiagnostic',N'Stop',N'ForceStop')
                AND (c.CommandKind IN(N'ManualRun',N'ProfileDiagnostic') AND (@rejected=1 OR e.State NOT IN(N'Queued',N'RetryWaiting')
                    OR EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId))
                    OR c.CommandKind NOT IN(N'ManualRun',N'ProfileDiagnostic') AND e.State<>N'Running');
            UPDATE e SET ManualResponseState=c.State FROM [workerdock].Executions e JOIN @changed c ON c.TargetId=e.ExecutionId WHERE e.ManualCommandId=c.CommandId;
            INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
                SELECT NEWID(),N'CommandSettled',N'workerdock',N'System',CommandId,SYSUTCDATETIME(),
                    (SELECT changed.CommandId AS commandId,changed.TargetId AS targetId,changed.State AS outcome FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
                FROM @changed changed WHERE State<>N'Pending';
            """);
        Add(command, "@execution", SqlDbType.UniqueIdentifier, executionId);
        Add(command, "@rejected", SqlDbType.Bit, rejected);
        await command.ExecuteNonQueryAsync(ct);
    }
}
