using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkerDock.Contracts.Connections;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.Templates.SqlServer;
public class SqlTemplate : IWorkerTemplate
{
    public TemplateDescriptor Descriptor => Metadata;
    private static readonly TemplateDescriptor Metadata=CreateDescriptor();
    private static bool Identifier(string? text) => text is { Length: >0 and <=128 } && Regex.IsMatch(text,@"^@[A-Za-z_][A-Za-z0-9_]*$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static bool Procedure(string text) => text.Length<=257 && Regex.IsMatch(text,@"^(\[[A-Za-z_][A-Za-z0-9_]*\]|[A-Za-z_][A-Za-z0-9_]*)(\.(\[[A-Za-z_][A-Za-z0-9_]*\]|[A-Za-z_][A-Za-z0-9_]*))?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public IReadOnlyList<ValidationIssue> Validate(JsonElement configuration)
    {
        try { var config=configuration.Deserialize<SqlJobConfiguration>(SqlJobConfiguration.JsonOptions); return config is not null && Valid(config) ? [] : [new("configuration","SqlConfigurationInvalid")]; }
        catch { return [new("configuration","SqlConfigurationInvalid")]; }
    }
    private static bool Valid(SqlJobConfiguration c) => c.ProfileId!=Guid.Empty && c.Catalog is { Length: >0 and <=128 } && !string.IsNullOrWhiteSpace(c.Catalog) &&
        c.CommandKind is "Query" or "Procedure" && c.CommandText is { Length: >0 and <=16384 } && !string.IsNullOrWhiteSpace(c.CommandText) &&
        (c.CommandKind!="Procedure" || Procedure(c.CommandText)) && c.CommandTimeoutSeconds is not <0 && c.Parameters is { Count: <=128 } &&
        c.Parameters.All(p=>p is not null && Identifier(p.Name) && Supported(p.DbType) &&
            (p.Value.HasValue?1:0)+(p.Secret is not null?1:0)+(p.UseExecutionKey?1:0)+(p.IsNull?1:0)==1 &&
            (p.Value is not {} v || v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) &&
            (p.Secret is null || p.Secret.Name is { Length: >0 and <=200 }) && p.Size is not < -1 and not 0 && p.Size is not >1048576 &&
            p.Precision is not 0 and not >38 && p.Scale is not >38 && (p.Scale is null || p.Scale<=(p.Precision ?? 38)) &&
            (!Sized(p.DbType) || p.Size is not null) && (p.DbType!=SqlDbType.Decimal || p.Precision is not null && p.Scale is not null)) &&
        c.Parameters.Select(p=>p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()==c.Parameters.Count;
    private static bool Sized(SqlDbType type)=>type is SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.Char or SqlDbType.NChar or SqlDbType.Binary or SqlDbType.VarBinary;
    private static bool Supported(SqlDbType type)=>type is SqlDbType.BigInt or SqlDbType.Int or SqlDbType.SmallInt or SqlDbType.TinyInt or SqlDbType.Bit or SqlDbType.Decimal or SqlDbType.Float or SqlDbType.Real or SqlDbType.Money or SqlDbType.SmallMoney or SqlDbType.NVarChar or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.Char or SqlDbType.UniqueIdentifier or SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset or SqlDbType.Time or SqlDbType.Binary or SqlDbType.VarBinary;
    public static async ValueTask<SqlCommand> BuildCommandAsync(SqlJobConfiguration config,SqlConnection connection,ISecretVault vault,string executionKey,DateTimeOffset? deadline,CancellationToken ct)
    {
        if(!Valid(config)) throw new SqlTemplateException("SqlConfigurationInvalid");
        if(config.CommandTimeoutSeconds==0 && deadline is not null) throw new SqlTemplateException("CommandTimeoutInvalid");
        var remaining=deadline is {} until ? (int)Math.Clamp(Math.Ceiling((until-DateTimeOffset.UtcNow).TotalSeconds),1,int.MaxValue) : 0;
        if(deadline<=DateTimeOffset.UtcNow) throw new SqlTemplateException("SqlDeadlineExceeded");
        var command=connection.CreateCommand();
        try
        {
            command.CommandText=config.CommandText; command.CommandType=config.CommandKind=="Procedure" ? CommandType.StoredProcedure : CommandType.Text;
            var configured=config.CommandTimeoutSeconds ?? remaining;
            command.CommandTimeout=remaining>0 ? Math.Min(configured,remaining) : configured;
            foreach(var p in config.Parameters)
            {
                ct.ThrowIfCancellationRequested();
                object value;
                if(p.IsNull) value=DBNull.Value;
                else if(p.UseExecutionKey) value=ConvertValue(JsonSerializer.SerializeToElement(executionKey),p.DbType);
                else if(p.Secret is {} reference)
                {
                    using var secret=await vault.ResolveAsync(reference,ct);
                    value=ConvertValue(JsonSerializer.SerializeToElement(new string(secret.Reveal().Span)),p.DbType);
                }
                else value=ConvertValue(p.Value!.Value,p.DbType);
                if(value is string text && p.Size is >0 && text.Length>p.Size || value is byte[] bytes && p.Size is >0 && bytes.Length>p.Size) throw new SqlTemplateException("SqlParameterInvalid");
                var parameter=new SqlParameter(p.Name,p.DbType) { Value=value };
                if(p.Size is {} size) parameter.Size=size; if(p.Precision is {} precision) parameter.Precision=precision; if(p.Scale is {} scale) parameter.Scale=scale;
                command.Parameters.Add(parameter);
            }
            return command;
        }
        catch { command.Dispose(); throw; }
    }
    private static object ConvertValue(JsonElement value,SqlDbType type)
    {
        var text=value.ValueKind==JsonValueKind.String ? value.GetString()! : value.GetRawText();
        try
        {
            return type switch
            {
                SqlDbType.BigInt=>long.Parse(text,CultureInfo.InvariantCulture), SqlDbType.Int=>int.Parse(text,CultureInfo.InvariantCulture),
                SqlDbType.SmallInt=>short.Parse(text,CultureInfo.InvariantCulture),SqlDbType.TinyInt=>byte.Parse(text,CultureInfo.InvariantCulture),
                SqlDbType.Bit=>bool.Parse(text),SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney=>decimal.Parse(text,NumberStyles.Float,CultureInfo.InvariantCulture),
                SqlDbType.Float=>double.Parse(text,CultureInfo.InvariantCulture),SqlDbType.Real=>float.Parse(text,CultureInfo.InvariantCulture),
                SqlDbType.UniqueIdentifier=>Guid.Parse(text),SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2=>DateTime.Parse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
                SqlDbType.DateTimeOffset=>DateTimeOffset.Parse(text,CultureInfo.InvariantCulture),SqlDbType.Time=>TimeSpan.Parse(text,CultureInfo.InvariantCulture),
                SqlDbType.Binary or SqlDbType.VarBinary=>Convert.FromBase64String(text),_=>text
            };
        }
        catch { throw new SqlTemplateException("SqlParameterInvalid"); }
    }
    public async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context,CancellationToken ct)
    {
        var effects=false; var commitStarted=false; SqlTransaction? transaction=null; SqlConnection? connection=null;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        SqlDeadlineCancellation? deadlineSchedule=null;
        try
        {
            if(Validate(context.Execution.Configuration).Count!=0) throw new SqlTemplateException("SqlConfigurationInvalid");
            var config=context.Execution.Configuration.Deserialize<SqlJobConfiguration>(SqlJobConfiguration.JsonOptions)!;
            var profile=context.Execution.ProfileConfiguration?.Deserialize<ConnectionProfileRevision>(SqlJobConfiguration.JsonOptions);
            if(profile is null || context.Execution.ProfileRevisionId!=profile.ProfileRevisionId || config.ProfileId!=profile.ProfileId) throw new SqlTemplateException("ProfileRevisionMismatch");
            if(context.Execution.DeadlineUtc is {} until)
            {
                if(until<=DateTimeOffset.UtcNow) throw new SqlTemplateException("SqlDeadlineExceeded");
                deadlineSchedule=SqlDeadlineCancellation.Schedule(deadline,until,TimeProvider.System);
            }
            if(context.Execution.IntegrationConfiguration.TryGetProperty("sqlProfileDiagnostic",out var diagnostic) && diagnostic.ValueKind==JsonValueKind.True)
                return await SqlProfileDiagnostic.ExecuteAsync(profile,config.Catalog,context.Secrets,deadline.Token);
            connection=await SqlConnectionBuilder.BuildAsync(profile,config.Catalog,context.Secrets,deadline.Token);
            // Resolve and validate parameters before opening the target or issuing any command.
            await using var command=await BuildCommandAsync(config,connection,context.Secrets,context.Execution.IdempotencyKey,context.Execution.DeadlineUtc,deadline.Token);
            await connection.OpenAsync(deadline.Token);
            if(config.UseTransaction) { transaction=(SqlTransaction)await connection.BeginTransactionAsync(deadline.Token); command.Transaction=transaction; }
            effects=true;
            long rows=0; int affected;
            await using(var reader=await command.ExecuteReaderAsync(deadline.Token))
            {
                do { while(await reader.ReadAsync(deadline.Token)) rows++; } while(await reader.NextResultAsync(deadline.Token));
                affected=reader.RecordsAffected;
            }
            if(transaction is not null) { commitStarted=true; await CommitAsync(transaction,deadline.Token); }
            return new("Success","SqlCompleted",true,JsonSerializer.SerializeToElement(new { affectedRows=affected,rowCount=rows }),null);
        }
        catch(Exception error)
        {
            if(transaction is not null && !commitStarted)
            {
                using var rollback=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await transaction.RollbackAsync(rollback.Token); if(error is not OperationCanceledException) effects=false; } catch { }
            }
            if(commitStarted) return Result("TransientFailure","SqlCommitUnknown",true);
            if(error is SqlTemplateException safe) return Result("PermanentFailure",safe.SafeCode,effects);
            if(error is SecretUnavailableException) return Result("PermanentFailure","SecretUnavailable",effects);
            if(error is OperationCanceledException) return Result("TransientFailure","SqlCancelled",effects);
            if(error is SqlException sql) return Result(sql.Number is -2 or 1205 or 4060 or 40197 or 40501 or 40613 ? "TransientFailure" : "PermanentFailure",SqlProfileDiagnostic.SafeCode(sql.Number),effects);
            return Result("PermanentFailure","SqlExecutionFailed",effects);
        }
        finally { deadlineSchedule?.Dispose(); if(transaction is not null) await transaction.DisposeAsync(); if(connection is not null) await connection.DisposeAsync(); }
    }
    protected virtual Task CommitAsync(SqlTransaction transaction,CancellationToken ct)=>transaction.CommitAsync(ct);
    internal static TemplateResult Result(string outcome,string code,bool effects)=>new(outcome,code,effects,JsonSerializer.SerializeToElement(new {}),null);
    private static TemplateDescriptor CreateDescriptor()
    {
        using var schema=JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["profileId","catalog","commandKind","commandText","parameters","useTransaction"],"properties":{
        "profileId":{"type":"string","format":"uuid"},"catalog":{"type":"string","minLength":1,"maxLength":128},"commandKind":{"type":"string","enum":["Query","Procedure"]},"commandText":{"type":"string","minLength":1,"maxLength":16384},"commandTimeoutSeconds":{"type":["integer","null"],"minimum":0},"useTransaction":{"type":"boolean"},
        "parameters":{"type":"array","maxItems":128,"items":{"type":"object","additionalProperties":false,"required":["name","dbType","useExecutionKey","isNull"],"properties":{
        "name":{"type":"string","maxLength":128},"dbType":{"type":"string","enum":["BigInt","Int","SmallInt","TinyInt","Bit","Decimal","Float","Real","Money","SmallMoney","NVarChar","VarChar","NChar","Char","UniqueIdentifier","Date","DateTime","DateTime2","DateTimeOffset","Time","Binary","VarBinary"]},"value":{"type":["string","number","boolean","null"]},"secret":{"type":["object","null"],"additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string","minLength":1,"maxLength":128,"x-workerDock-secretReference":true}}},"useExecutionKey":{"type":"boolean"},"isNull":{"type":"boolean"},"size":{"type":["integer","null"],"minimum":-1,"maximum":1048576},"precision":{"type":["integer","null"],"minimum":1,"maximum":38},"scale":{"type":["integer","null"],"minimum":0,"maximum":38}}}}}}
        """);
        var descriptor=new TemplateDescriptor("workerdock.sql-server","1.0.0","SQL Server","Execute a parameterized query or stored procedure using a named connection profile.",schema.RootElement.Clone(),JsonSerializer.SerializeToElement(new {}),"");
        using var stream=new MemoryStream(); using(var writer=new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); writer.WriteStringValue(descriptor.Id);writer.WriteStringValue(descriptor.Version);writer.WriteStringValue(descriptor.DisplayName);writer.WriteStringValue(descriptor.Description);
            Canonical(writer,descriptor.ConfigurationSchema);Canonical(writer,descriptor.UiHints);writer.WriteEndArray();
        }
        return descriptor with { MetadataDigest=Convert.ToHexString(SHA256.HashData(stream.ToArray())) };
    }
    private static void Canonical(Utf8JsonWriter writer,JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object) { writer.WriteStartObject();foreach(var p in element.EnumerateObject().OrderBy(x=>x.Name,StringComparer.Ordinal)){writer.WritePropertyName(p.Name);Canonical(writer,p.Value);}writer.WriteEndObject(); }
        else if(element.ValueKind==JsonValueKind.Array){writer.WriteStartArray();foreach(var v in element.EnumerateArray())Canonical(writer,v);writer.WriteEndArray();}
        else element.WriteTo(writer);
    }
}
