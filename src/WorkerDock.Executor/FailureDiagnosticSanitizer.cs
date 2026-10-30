using System.Text;
using System.Text.Json;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Executor;

public static class FailureDiagnosticSanitizer
{
    private const string Marker = "[truncated]";
    public const int MaximumResultBytes = 48 * 1024;
    private const int MaximumDiagnosticBytes = 28 * 1024;

    public static FailureDiagnostic? Sanitize(FailureDiagnostic? input, bool hide, Func<string, string> redact)
    {
        if (input is null) return null;
        try
        {
            var truncated = input.Truncated;
            var suppressDetails = hide || input.Hidden;
            string? Field(string? value, int limit)
            {
                if (value is null) return null;
                var safe = redact(value);
                var bounded = Bound(safe, limit, out var cut);
                truncated |= cut;
                return bounded;
            }

            var result = new FailureDiagnostic(
                Field(input.Kind, 80) ?? "",
                Field(input.ExceptionType, 512),
                suppressDetails ? null : Field(input.Message, 4 * 1024),
                suppressDetails ? null : Field(input.StackTrace, 8 * 1024),
                suppressDetails ? null : Field(input.ResponseContent, 16 * 1024),
                input.SqlNumber,
                input.SqlState,
                Field(input.SqlProcedure, 512),
                input.SqlLine,
                Field(input.CommandKind, 80),
                Field(input.HttpMethod, 32),
                input.HttpStatus,
                Field(input.MediaType, 128),
                input.ResponseBytes,
                truncated,
                suppressDetails);
            // JSON escaping can expand control characters; retain generous room for
            // the safe result envelope and drop optional text if this limit is hit.
            while (JsonSerializer.SerializeToUtf8Bytes(result).Length > MaximumDiagnosticBytes)
            {
                if (result.ResponseContent is { Length: > 8192 })
                    result = result with { ResponseContent = Bound(result.ResponseContent, 8192, out _), Truncated = true };
                else if (result.ResponseContent is not null) result = result with { ResponseContent = null, Truncated = true };
                else if (result.StackTrace is not null) result = result with { StackTrace = null, Truncated = true };
                else if (result.Message is not null) result = result with { Message = null, Truncated = true };
                else return null;
            }
            return Valid(result) ? result : null;
        }
        catch { return null; } // Optional diagnostic failures cannot affect execution.
    }

    private static string Bound(string value, int limit, out bool cut)
    {
        var content = Take(value, limit, out cut);
        return cut ? Take(value, limit - Marker.Length, out _) + Marker : content;
    }

    private static string Take(string value, int limit, out bool cut)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.ToString();
            var encoded = JsonSerializer.SerializeToUtf8Bytes(scalar).Length - 2;
            if (bytes + encoded > limit) { cut = true; return builder.ToString(); }
            builder.Append(scalar);
            bytes += encoded;
        }
        cut = false;
        return builder.ToString();
    }

    public static bool Valid(FailureDiagnostic value)
    {
        static bool Token(string? text, int max) => text is { Length: > 0 } && text.Length <= max &&
            text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        static bool Text(string? text, int max) => text is null || JsonSerializer.SerializeToUtf8Bytes(text).Length - 2 <= max;
        return Token(value.Kind, 80) &&
            Text(value.ExceptionType, 512) && Text(value.Message, 4096) &&
            Text(value.StackTrace, 8192) && Text(value.ResponseContent, 16384) &&
            Text(value.SqlProcedure, 512) && Text(value.CommandKind, 80) &&
            Text(value.HttpMethod, 32) && Text(value.MediaType, 128) &&
            value.SqlState is null or >= 0 and <= 255 &&
            value.SqlLine is null or >= 0 && value.HttpStatus is null or >= 100 and <= 599 &&
            value.ResponseBytes is null or >= 0 &&
            (!value.Hidden || value is { Message: null, StackTrace: null, ResponseContent: null }) &&
            JsonSerializer.SerializeToUtf8Bytes(value).Length <= MaximumDiagnosticBytes;
    }
}
