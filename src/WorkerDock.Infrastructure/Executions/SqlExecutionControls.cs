using System.Data;
using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Executions;

public sealed class SqlExecutionControls(TenantDatabaseOptions options) : IExecutionControls
{
    public ValueTask<CommandReceipt> StopAsync(Guid executionId, bool force, string reason, ActorContext actor, CancellationToken ct) =>
        ApplyAsync(executionId, force ? "ForceStop" : "Stop", reason, actor, (data, _) => data.Stop(force), ct);
    public ValueTask<CommandReceipt> ResolveAsync(Guid executionId, string decision, string reason, ActorContext actor, CancellationToken ct) =>
        ApplyAsync(executionId, decision, reason, actor, (data, now) => data.Resolve(decision, now), ct);

    private ValueTask<CommandReceipt> ApplyAsync(Guid id, string kind, string reason, ActorContext actor,
        Action<AttemptLifecycle, DateTimeOffset> action, CancellationToken ct)
    {
        InMemoryAttemptStore.ValidateControl(id, reason, actor);
        return new SqlAttemptStore(options).TransactAsync(id, null, (data, now) =>
        {
            action(data, now);
            return new CommandReceipt(Guid.NewGuid(), id, data.State == "Running" ? "Pending" : "Applied");
        }, ct, async (connection, transaction, receipt, token) =>
        {
            await using var command = Command(connection, transaction, """
                INSERT INTO dbo.Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@command,@target,@kind,@state,SYSUTCDATETIME());
                INSERT INTO dbo.AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
                    VALUES(NEWID(),@kind,@subject,@mode,@correlation,SYSUTCDATETIME(),@details);
                """);
            Add(command, "@command", SqlDbType.UniqueIdentifier, receipt.CommandId); Add(command, "@target", SqlDbType.UniqueIdentifier, id);
            Add(command, "@kind", SqlDbType.NVarChar, kind, 40); Add(command, "@state", SqlDbType.NVarChar, receipt.State, 20);
            Add(command, "@subject", SqlDbType.NVarChar, actor.Subject, 300); Add(command, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40);
            Add(command, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
            // Operator-provided reason is audit-only and is never returned as a handler error/status code.
            Add(command, "@details", SqlDbType.NVarChar, JsonSerializer.Serialize(new { executionId = id, decision = kind, reason }), 4000);
            await command.ExecuteNonQueryAsync(token);
        });
    }
}
