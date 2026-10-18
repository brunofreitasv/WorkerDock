using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Connections;
using WorkerDock.Application.Jobs;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Templates.SqlServer;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;
namespace WorkerDock.Infrastructure.Connections;
public sealed class SqlProfileDiagnostics(TenantDatabaseOptions options) : IProfileDiagnostics
{
    public async ValueTask<CommandReceipt> StartAsync(Guid profileId,string catalog,ActorContext actor,CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        try
        {
            return await new BusinessTransaction(options).RunAsync(async(c,t,token)=>
            {
                await using var identity=Command(c,t,"SELECT InstanceId FROM [workerdock].TenantSettings");
                if((Guid?)await identity.ExecuteScalarAsync(token)!=options.InstanceId)throw new WorkerDockException("TenantInstanceMismatch");
                await using var pending=Command(c,t,"SELECT COUNT(*) FROM [workerdock].Executions WHERE TriggerKind=N'Diagnostic' AND State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview')");
                if((int)(await pending.ExecuteScalarAsync(token))!>=32)throw new WorkerDockException("DiagnosticLimitReached");
                await using var read=Command(c,t,"SELECT r.ConfigurationJson FROM [workerdock].ConnectionProfiles p JOIN [workerdock].ConnectionProfileRevisions r ON r.ProfileRevisionId=p.CurrentRevisionId WHERE p.ProfileId=@id AND p.Retired=0");Add(read,"@id",SqlDbType.UniqueIdentifier,profileId);
                if(await read.ExecuteScalarAsync(token) is not string json)throw new WorkerDockException("ProfileUnavailable");
                var profile=JsonSerializer.Deserialize<WorkerDock.Contracts.Connections.ConnectionProfileRevision>(json)!;
                if(!profile.AllowedCatalogs.Contains(catalog,StringComparer.OrdinalIgnoreCase))throw new WorkerDockException("CatalogNotAllowed");
                await using var installed=Command(c,t,"SELECT COUNT(*) FROM [workerdock].TemplateCatalog WHERE TemplateId=N'workerdock.sql-server' AND TemplateVersion=N'1.0.0' AND Enabled=1 AND DescriptorJson IS NOT NULL");
                if((int)(await installed.ExecuteScalarAsync(token))!!=1)throw new WorkerDockException("TemplateVersionUnavailable");
                var job=Guid.NewGuid();var execution=Guid.NewGuid();
                var configuration=new SqlJobConfiguration(profileId,catalog,"Query","SELECT 1",[],null,false);
                var policy=new ExecutionPolicy(TimeSpan.FromSeconds(30),1,TimeSpan.Zero,TimeSpan.Zero,0,[],false,"Queue",1,5);
                await using var insert=Command(c,t,"""
                    INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES(@job,NULL,1,0,1);
                    INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
                    VALUES(@job,1,N'Profile connectivity diagnostic',N'workerdock.sql-server',N'1.0.0',@configuration,@profile,@policy,SYSUTCDATETIME());
                    UPDATE [workerdock].Jobs SET CurrentRevision=1 WHERE JobId=@job;
                    INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,ProfileRevisionId,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
                    VALUES(@execution,@job,1,@revision,N'Diagnostic',@key,N'Queued',N'Known',SYSUTCDATETIME(),@correlation,SYSUTCDATETIME());
                    INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState)
                    VALUES(NEWID(),N'ExecutionDispatch',@payload,SYSUTCDATETIME(),SYSUTCDATETIME(),1,@execution,1,@digest,N'Pending');
                    """);
                Add(insert,"@job",SqlDbType.UniqueIdentifier,job);Add(insert,"@execution",SqlDbType.UniqueIdentifier,execution);Add(insert,"@revision",SqlDbType.UniqueIdentifier,profile.ProfileRevisionId);Add(insert,"@profile",SqlDbType.UniqueIdentifier,profileId);Add(insert,"@key",SqlDbType.NVarChar,execution.ToString("N"),100);Add(insert,"@correlation",SqlDbType.UniqueIdentifier,actor.CorrelationId);
                Add(insert,"@configuration",SqlDbType.NVarChar,JsonSerializer.Serialize(configuration,SqlJobConfiguration.JsonOptions),-1);Add(insert,"@policy",SqlDbType.NVarChar,JsonSerializer.Serialize(policy),-1);
                Add(insert,"@payload",SqlDbType.NVarChar,JsonSerializer.Serialize(new { executionId=execution,jobRevision=1 }),-1);Add(insert,"@digest",SqlDbType.Char,WorkerDock.Application.Dispatch.ProjectionEnvelope.ExecutionDigest(execution,1),64);
                await insert.ExecuteNonQueryAsync(token);
                var receipt=await SqlConnectionProfiles.ReceiptAsync(c,t,execution,"ProfileDiagnostic",actor,token,"Pending");
                await WorkerDock.Infrastructure.Dispatch.JobAdmission.AdmitAsync(c,t,job,token);
                return receipt;
            },ct);
        }
        catch(SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
}
