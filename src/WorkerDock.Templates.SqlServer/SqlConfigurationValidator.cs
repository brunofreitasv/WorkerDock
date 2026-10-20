using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkerDock.Contracts.Templates;
using WorkerDock.Contracts.Values;

namespace WorkerDock.Templates.SqlServer;

public sealed class SqlConfigurationValidator(string version) : ITemplateAuthoringValidator
{
    public string TemplateId => "workerdock.sql-server";
    public string TemplateVersion => version;

    public IReadOnlyList<ValidationIssue> ValidateAuthoring(TemplateAuthoringContext context)
    {
        var issues = new List<ValidationIssue>();
        if (!context.Configuration.TryGetProperty("commandKind", out var kind) || kind.GetString() != "Query")
        {
            if (context.Configuration.TryGetProperty("commandText", out var text) && text.ValueKind == JsonValueKind.String &&
                !Regex.IsMatch(text.GetString()!, @"^(\[[A-Za-z_][A-Za-z0-9_]*\]|[A-Za-z_][A-Za-z0-9_]*)(\.(\[[A-Za-z_][A-Za-z0-9_]*\]|[A-Za-z_][A-Za-z0-9_]*))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                issues.Add(new("configuration.commandText", "SqlProcedureNameRequired"));
        }

        if (context.Configuration.TryGetProperty("commandTimeoutSeconds", out var timeout) && timeout.ValueKind == JsonValueKind.Number &&
            timeout.TryGetInt32(out var seconds) && seconds == 0 && context.JobTimeout is { } jobTimeout && jobTimeout > TimeSpan.Zero)
            issues.Add(new("configuration.commandTimeoutSeconds", "CommandTimeoutInvalid"));

        if (!context.Configuration.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array) return issues;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var row in parameters.EnumerateArray())
        {
            var path = $"configuration.parameters[{index}]";
            if (!row.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String || !names.Add(nameElement.GetString()!))
                issues.Add(new(path + ".name", "SqlParameterNameInvalid"));
            var dbType = row.TryGetProperty("dbType", out var typeElement) && Enum.TryParse<SqlDbType>(typeElement.GetString(), out var parsed) ? parsed : (SqlDbType?)null;
            var target = $"/parameters/{index}/value";
            var binding = context.DynamicValues?.Bindings.FirstOrDefault(candidate => candidate.Target == target);
            if (binding is not null && dbType is { } targetType && !Compatible(targetType, binding))
                issues.Add(new(path + ".value", "DynamicTypeMismatch"));
            var sources = (row.TryGetProperty("value", out var sourceValue) && sourceValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? 1 : 0)
                + (row.TryGetProperty("secret", out var sourceSecret) && sourceSecret.ValueKind == JsonValueKind.Object ? 1 : 0)
                + (row.TryGetProperty("useExecutionKey", out var executionKey) && executionKey.ValueKind == JsonValueKind.True ? 1 : 0)
                + (row.TryGetProperty("isNull", out var isNull) && isNull.ValueKind == JsonValueKind.True ? 1 : 0)
                + (binding is not null ? 1 : 0);
            if (sources != 1) issues.Add(new(path + ".value", sources == 0 ? "SqlValueSourceRequired" : "SqlValueSourceConflict"));
            if (row.TryGetProperty("value", out var fixedValue) && fixedValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && dbType is { } fixedType)
            {
                try { _ = SqlTemplate.ConvertAuthoringValue(fixedValue, fixedType, version); }
                catch { issues.Add(new(path + ".value", "SqlParameterInvalid")); }
            }
            var precision = row.TryGetProperty("precision", out var precisionElement) && precisionElement.ValueKind == JsonValueKind.Number && precisionElement.TryGetByte(out var p) ? p : (byte?)null;
            var scale = row.TryGetProperty("scale", out var scaleElement) && scaleElement.ValueKind == JsonValueKind.Number && scaleElement.TryGetByte(out var s) ? s : (byte?)null;
            if (dbType == SqlDbType.Decimal && precision is { } pr && scale is { } sc && sc > pr)
                issues.Add(new(path + ".scale", "SqlScaleExceedsPrecision"));
            if (dbType == SqlDbType.Decimal && (precision is null || scale is null))
                issues.Add(new(path + ".precision", "SqlDecimalMetadataRequired"));
            var sized = dbType is SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.Char or SqlDbType.NChar or SqlDbType.Binary or SqlDbType.VarBinary;
            var nSize = 0;
            var hasSize = row.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt32(out nSize);
            if (sized && !hasSize) issues.Add(new(path + ".size", "SqlSizeRequired"));
            if (hasSize && !sized) issues.Add(new(path + ".size", "SqlSizeNotApplicable"));
            if (hasSize && nSize == -1 && dbType is not (SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.VarBinary))
                issues.Add(new(path + ".size", "SqlMaxSizeNotSupported"));
            if (hasSize && (nSize == 0 || nSize < -1 || nSize > 1048576)) issues.Add(new(path + ".size", "SqlSizeInvalid"));
            index++;
        }
        return issues.Take(64).ToArray();
    }

    private static bool Compatible(SqlDbType type, DynamicValueBinding binding)
    {
        if (type is SqlDbType.NVarChar or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.Char)
            return binding.Source is not (DynamicValueSource.ReferenceDate or DynamicValueSource.ScheduledTimestamp or DynamicValueSource.FirstExecutionStart) || binding.Format is DynamicValueFormat.String or DynamicValueFormat.IsoDate or DynamicValueFormat.IsoOffsetTimestamp or DynamicValueFormat.IsoUtcTimestamp or DynamicValueFormat.UnixSeconds or DynamicValueFormat.UnixMilliseconds;
        if (type == SqlDbType.Date) return binding.Source == DynamicValueSource.ReferenceDate && binding.Format == DynamicValueFormat.IsoDate || binding.Source is DynamicValueSource.ScheduledTimestamp or DynamicValueSource.FirstExecutionStart && binding.Format == DynamicValueFormat.IsoDate;
        if (type is SqlDbType.DateTime or SqlDbType.DateTime2) return binding.Source is DynamicValueSource.ScheduledTimestamp or DynamicValueSource.FirstExecutionStart && binding.Format is DynamicValueFormat.IsoDate or DynamicValueFormat.IsoOffsetTimestamp or DynamicValueFormat.IsoUtcTimestamp;
        if (type == SqlDbType.DateTimeOffset) return binding.Source is DynamicValueSource.ScheduledTimestamp or DynamicValueSource.FirstExecutionStart && binding.Format is DynamicValueFormat.IsoOffsetTimestamp or DynamicValueFormat.IsoUtcTimestamp;
        if (type == SqlDbType.BigInt) return binding.Source is DynamicValueSource.ScheduledTimestamp or DynamicValueSource.FirstExecutionStart && binding.Format is DynamicValueFormat.UnixSeconds or DynamicValueFormat.UnixMilliseconds;
        if (type == SqlDbType.UniqueIdentifier) return binding.Source == DynamicValueSource.ExecutionId && binding.Format == DynamicValueFormat.String;
        return false;
    }
}
