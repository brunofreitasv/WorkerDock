using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Templates;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;
namespace WorkerDock.Infrastructure.Templates;

/// <summary>Tenant metadata only; registration is invoked by trusted installed package composition.</summary>
public sealed class SqlTemplateCatalog(TenantDatabaseOptions options,IReadOnlyList<TemplateDescriptor> trustedInstalled) : ITemplateCatalog
{
    private readonly TemplateCatalog admission=new(trustedInstalled);
    private static SqlCommand Query(DbConnection connection,DbTransaction? transaction,string sql)
    { var command=(SqlCommand)connection.CreateCommand(); command.Transaction=(SqlTransaction?)transaction; command.CommandText=sql; return command; }
    private static async ValueTask VerifyIdentityAsync(DbConnection c,DbTransaction? t,Guid instanceId,CancellationToken ct)
    {
        await using var identity=Query(c,t,"SELECT InstanceId FROM [workerdock].TenantSettings");
        if((Guid?)await identity.ExecuteScalarAsync(ct)!=instanceId) throw new WorkerDockException("TenantInstanceMismatch");
    }
    public async ValueTask<string> RegisterAsync(IReadOnlyList<TemplateDescriptor> descriptors,CancellationToken ct)
    {
        var prepared=admission.Prepare(descriptors);
        try
        {
            return await new BusinessTransaction(options).RunAsync(async(c,t,token)=>
            {
                await VerifyIdentityAsync(c,t,options.InstanceId,token);
                foreach(var d in prepared)
                {
                    await using var read=Query(c,t,"SELECT MetadataDigest,DescriptorJson FROM [workerdock].TemplateCatalog WHERE TemplateId=@id AND TemplateVersion=@version");
                    Add(read,"@id",SqlDbType.NVarChar,d.Id,100); Add(read,"@version",SqlDbType.NVarChar,d.Version,40);
                    await using var reader=await read.ExecuteReaderAsync(token);
                    var exists=await reader.ReadAsync(token);
                    var priorDigest=exists?reader.GetString(0):null;
                    var missingDescriptor=exists && reader.IsDBNull(1);
                    await reader.CloseAsync();
                    var bootstrapDigest=BootstrapDigest(d.Id,d.Version);
                    var promotePlaceholder=exists && missingDescriptor && bootstrapDigest is not null && priorDigest==bootstrapDigest;
                    if(exists && !missingDescriptor)
                    {
                        if(priorDigest!=d.MetadataDigest) throw new WorkerDockException("MetadataVersionConflict");
                        continue;
                    }
                    if(exists && !promotePlaceholder) throw new WorkerDockException("MetadataVersionConflict");
                    await using var write=Query(c,t,exists?"""
                        UPDATE [workerdock].TemplateCatalog SET MetadataDigest=@digest,DescriptorJson=@json
                        WHERE TemplateId=@id AND TemplateVersion=@version AND DescriptorJson IS NULL AND MetadataDigest=@placeholderDigest;
                        IF @@ROWCOUNT<>1 THROW 51021,'MetadataVersionConflict',1;
                        """:"""
                        INSERT [workerdock].TemplateCatalog(TemplateId,TemplateVersion,MetadataDigest,DescriptorJson,Enabled,Retired)
                        VALUES(@id,@version,@digest,@json,1,0);
                        """);
                    Add(write,"@id",SqlDbType.NVarChar,d.Id,100); Add(write,"@version",SqlDbType.NVarChar,d.Version,40);
                    Add(write,"@digest",SqlDbType.Char,d.MetadataDigest,64); Add(write,"@json",SqlDbType.NVarChar,JsonSerializer.Serialize(d));
                    if(exists) Add(write,"@placeholderDigest",SqlDbType.Char,bootstrapDigest!,64);
                    await write.ExecuteNonQueryAsync(token);
                }
                return TemplateMetadata.AggregateDigest(prepared);
            },ct);
        }
        catch(SqlException e) when(e.Number==51021) { throw new WorkerDockException("MetadataVersionConflict"); }
        catch(SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
    private static string? BootstrapDigest(string id,string version)
    {
        if(version is not ("1.0.0" or "1.1.0") || id is not ("workerdock.http" or "workerdock.sql-server")) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(id+"|"+version)));
    }
    public async ValueTask<IReadOnlyList<TemplateDescriptor>> ListAsync(CancellationToken ct) => await ReadAsync(null,null,ct);
    public async ValueTask<TemplateDescriptor?> GetAsync(string id,string version,CancellationToken ct) => (await ReadAsync(id,version,ct)).SingleOrDefault();
    private async ValueTask<IReadOnlyList<TemplateDescriptor>> ReadAsync(string? id,string? version,CancellationToken ct)
    {
        try
        {
            await using var connection=new SqlConnection(options.ConnectionString); await connection.OpenAsync(ct);
            await VerifyIdentityAsync(connection,null,options.InstanceId,ct);
            await using var read=Query(connection,null,"""
                SELECT TemplateId,TemplateVersion,MetadataDigest,DescriptorJson FROM [workerdock].TemplateCatalog
                WHERE DescriptorJson IS NOT NULL AND (@id IS NULL OR TemplateId=@id AND TemplateVersion=@version)
                ORDER BY TemplateId COLLATE Latin1_General_100_BIN2,TemplateVersion COLLATE Latin1_General_100_BIN2
                """);
            Add(read,"@id",SqlDbType.NVarChar,(object?)id??DBNull.Value,100); Add(read,"@version",SqlDbType.NVarChar,(object?)version??DBNull.Value,40);
            await using var reader=await read.ExecuteReaderAsync(ct); var result=new List<TemplateDescriptor>();
            while(await reader.ReadAsync(ct))
            {
                var d=JsonSerializer.Deserialize<TemplateDescriptor>(reader.GetString(3));
                if(d is null || d.Id!=reader.GetString(0) || d.Version!=reader.GetString(1) || d.MetadataDigest!=reader.GetString(2) || TemplateSchemaValidator.ValidateDescriptor(d).Count!=0 || TemplateMetadata.ComputeDigest(d)!=d.MetadataDigest)
                    throw new WorkerDockException("TemplateMetadataMismatch");
                result.Add(d);
            }
            return result;
        }
        catch(SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
        catch(JsonException) { throw new WorkerDockException("TemplateMetadataMismatch"); }
    }
    public async ValueTask RetireAsync(string id,string version,CancellationToken ct)
    {
        try
        {
            await new BusinessTransaction(options).RunAsync(async(c,t,token)=>
            {
                await VerifyIdentityAsync(c,t,options.InstanceId,token);
                await using var command=Query(c,t,"""
                    IF NOT EXISTS(SELECT 1 FROM [workerdock].TemplateCatalog WHERE TemplateId=@id AND TemplateVersion=@version) THROW 51022,'TemplateVersionUnavailable',1;
                    UPDATE [workerdock].TemplateCatalog SET Enabled=0,Retired=1 WHERE TemplateId=@id AND TemplateVersion=@version AND Retired=0;
                    SELECT @@ROWCOUNT;
                    """);
                Add(command,"@id",SqlDbType.NVarChar,id,100); Add(command,"@version",SqlDbType.NVarChar,version,40);
                await command.ExecuteScalarAsync(token);
                return true;
            },ct);
        }
        catch(SqlException e) when(e.Number==51022) { throw new WorkerDockException("TemplateVersionUnavailable"); }
        catch(SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
    public async ValueTask<bool> IsSelectableAsync(string id,string version,CancellationToken ct)
    {
        try
        {
            await using var connection=new SqlConnection(options.ConnectionString); await connection.OpenAsync(ct);
            await VerifyIdentityAsync(connection,null,options.InstanceId,ct);
            await using var command=Query(connection,null,"""
                SELECT COUNT(*) FROM [workerdock].TemplateCatalog
                WHERE TemplateId=@id AND TemplateVersion=@version AND Enabled=1 AND Retired=0 AND DescriptorJson IS NOT NULL
                """);
            Add(command,"@id",SqlDbType.NVarChar,id,100); Add(command,"@version",SqlDbType.NVarChar,version,40);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct))==1;
        }
        catch(SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
}
