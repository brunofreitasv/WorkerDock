using System.Text.Json;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Infrastructure.Executions;

internal static class PersistedFailureDiagnostic
{
    public static FailureDiagnostic? Validate(TemplateResult? result, bool hide)
    {
        if (result?.Outcome == "Success" || result?.Diagnostic is not { } diagnostic) return null;
        try
        {
            if (hide || diagnostic.Hidden)
                diagnostic = diagnostic with { Message = null, StackTrace = null, ResponseContent = null, Hidden = true };
            static bool Text(string? value, int limit) => value is null || JsonSerializer.SerializeToUtf8Bytes(value).Length - 2 <= limit;
            return diagnostic.Kind is { Length: > 0 and <= 80 } && diagnostic.Kind.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
                Text(diagnostic.ExceptionType, 512) && Text(diagnostic.Message, 4096) && Text(diagnostic.StackTrace, 8192) &&
                Text(diagnostic.ResponseContent, 16384) && Text(diagnostic.SqlProcedure, 512) && Text(diagnostic.CommandKind, 80) &&
                Text(diagnostic.HttpMethod, 32) && Text(diagnostic.MediaType, 128) && diagnostic.SqlState is null or >= 0 and <= 255 &&
                diagnostic.SqlLine is null or >= 0 && diagnostic.HttpStatus is null or >= 100 and <= 599 && diagnostic.ResponseBytes is null or >= 0 &&
                JsonSerializer.SerializeToUtf8Bytes(diagnostic).Length <= 32 * 1024 ? diagnostic : null;
        }
        catch { return null; }
    }
}
