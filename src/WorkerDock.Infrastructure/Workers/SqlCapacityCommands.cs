using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Workers;

public sealed class SqlCapacityCommands(TenantDatabaseOptions options) : ICapacityCommands
{
    public ValueTask<CommandReceipt> SetTemplateLimitAsync(string templateId, int limit, ActorContext actor, CancellationToken ct) =>
        ChangeAsync("TemplateLimit", SqlCapacityStore.PolicyId, templateId, limit, actor, ct);
    public ValueTask<CommandReceipt> SetJobLimitAsync(Guid jobId, int limit, ActorContext actor, CancellationToken ct) =>
        ChangeAsync("JobLimit", jobId, null, limit, actor, ct);
    private async ValueTask<CommandReceipt> ChangeAsync(string kind, Guid id, string? template, int limit, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        if (id == Guid.Empty || limit < 0 || kind == "TemplateLimit" && (string.IsNullOrWhiteSpace(template) || template.Length > 100))
            throw new WorkerDockException("ValidationFailed", "limit");
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (c, t, token) =>
            {
                await using var identity = Command(c, t, "SELECT InstanceId FROM [workerdock].TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                await using var update = Command(c, t, kind switch
                {
                    "TemplateLimit" => """
                        UPDATE [workerdock].TemplateCatalog SET ConcurrencyLimit=@limit WHERE TemplateId=@template AND Enabled=1;
                        IF @@ROWCOUNT=0 THROW 51010,'UnknownTemplate',1;
                        SELECT CAST(NULL AS bigint);
                        """,
                    "JobLimit" => "UPDATE [workerdock].Jobs SET ConcurrencyLimit=@limit WHERE JobId=@id AND Archived=0; IF @@ROWCOUNT=0 THROW 51011,'JobNotFound',1; SELECT CAST(NULL AS bigint);",
                    _ => throw new WorkerDockException("ValidationFailed", "limit")
                });
                Add(update, "@limit", SqlDbType.Int, limit); Add(update, "@id", SqlDbType.UniqueIdentifier, id); Add(update, "@template", SqlDbType.NVarChar, template, 100);
                var revision = await update.ExecuteScalarAsync(token);
                var receipt = new CommandReceipt(Guid.NewGuid(), id, "Applied");
                await using var command = Command(c, t, """
                    INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc,PolicyRevision) VALUES(@command,@id,@kind,@state,SYSUTCDATETIME(),@revision);
                    INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
                    VALUES(NEWID(),@kind,@actor,@mode,@correlation,SYSUTCDATETIME(),@details);
                    UPDATE [workerdock].Outbox SET ProjectionState=N'Pending',DispatchedAtUtc=NULL,ReviewAtUtc=NULL,RetryAtUtc=NULL WHERE ProjectionState=N'Held';
                    """);
                Add(command, "@command", SqlDbType.UniqueIdentifier, receipt.CommandId); Add(command, "@id", SqlDbType.UniqueIdentifier, id);
                Add(command, "@kind", SqlDbType.NVarChar, kind, 40); Add(command, "@state", SqlDbType.NVarChar, receipt.State, 20); Add(command, "@revision", SqlDbType.BigInt, revision);
                Add(command, "@actor", SqlDbType.NVarChar, actor.Subject, 300); Add(command, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40);
                Add(command, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId); Add(command, "@details", SqlDbType.NVarChar, JsonSerializer.Serialize(new { id, template, limit }), -1);
                await command.ExecuteNonQueryAsync(token);
                if (kind == "JobLimit") await JobAdmission.AdmitAsync(c, t, id, token);
                return receipt;
            }, ct);
        }
        catch (SqlException e) when (e.Number == 51010) { throw new WorkerDockException("UnknownTemplate"); }
        catch (SqlException e) when (e.Number == 51011) { throw new WorkerDockException("JobNotFound"); }
        catch (SqlException e) when (e.Number == 51012) { throw new WorkerDockException("ValidationFailed", "limit"); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
}
