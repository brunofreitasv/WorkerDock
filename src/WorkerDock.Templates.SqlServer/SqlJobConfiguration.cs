using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkerDock.Contracts.Secrets;
namespace WorkerDock.Templates.SqlServer;
public sealed record SqlJobConfiguration(Guid ProfileId, string Catalog, string CommandKind, string CommandText,
    IReadOnlyList<SqlParameterConfiguration> Parameters, int? CommandTimeoutSeconds, bool UseTransaction)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, Converters={ new JsonStringEnumConverter() } };
}
public sealed record SqlParameterConfiguration(string Name, SqlDbType DbType, JsonElement? Value, SecretReference? Secret,
    bool UseExecutionKey, bool IsNull, int? Size, byte? Precision, byte? Scale);
public sealed class SqlTemplateException(string code) : Exception(code) { public string SafeCode => Message; }
