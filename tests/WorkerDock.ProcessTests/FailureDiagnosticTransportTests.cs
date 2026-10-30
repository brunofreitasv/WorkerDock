using System.Reflection;
using System.Text.Json;
using WorkerDock.Contracts.Templates;
using WorkerDock.Executor;
using WorkerDock.Runtime.Ipc;

namespace WorkerDock.ProcessTests;

public sealed class FailureDiagnosticTransportTests
{
    [Fact]
    public void Signed_sql_timeout_number_survives_sanitizer_and_child_validation()
    {
        var input = new FailureDiagnostic("Sql", SqlNumber: -2, CommandKind: "Query");
        var diagnostic = FailureDiagnosticSanitizer.Sanitize(input, false, static value => value);
        Assert.Equal(-2, diagnostic?.SqlNumber);
        var result = new TemplateResult("TransientFailure", "SqlTimeout", true,
            JsonSerializer.SerializeToElement(new { }), null, diagnostic);
        var type = typeof(ExecutionProtocol).Assembly.GetType("WorkerDock.Runtime.Processes.ChildHandle")!;
        var method = type.GetMethod("ValidateFailureDiagnostic", BindingFlags.Static | BindingFlags.NonPublic)!;
        var validated = (TemplateResult)method.Invoke(null, [result])!;
        Assert.Equal(-2, validated.Diagnostic?.SqlNumber);
    }

    [Fact]
    public void Redacts_literal_and_json_escaped_secrets_in_every_string_field()
    {
        const string secret = "key\nvalue";
        var escaped = JsonSerializer.Serialize(secret)[1..^1];
        var input = new FailureDiagnostic("Sql", $"Type {secret}", $"Message {escaped}", $"Stack {secret}",
            $"Body {escaped}", SqlProcedure: $"Proc {secret}", CommandKind: $"Command {escaped}",
            HttpMethod: $"Method {secret}", MediaType: $"Media {escaped}");
        string Redact(string value) => value.Replace(secret, "[REDACTED]", StringComparison.Ordinal)
            .Replace(escaped, "[REDACTED]", StringComparison.Ordinal);

        var diagnostic = FailureDiagnosticSanitizer.Sanitize(input, false, Redact);
        var result = new TemplateResult("PermanentFailure", "CommandFailed", true,
            JsonSerializer.SerializeToElement(new { }), null, diagnostic);
        var frame = System.Text.Encoding.UTF8.GetString(ExecutionProtocol.Serialize(result));

        Assert.DoesNotContain(secret, frame);
        Assert.DoesNotContain(escaped, frame);
        Assert.Contains("[REDACTED]", frame);
    }

    [Fact]
    public void Hidden_output_discards_details_and_preserves_reproduction_metadata()
    {
        var input = new FailureDiagnostic("Sql", "SqlException", "private message", "private stack", "private body",
            SqlNumber: 51000, SqlState: 1, SqlProcedure: "test_proc", SqlLine: 9, CommandKind: "StoredProcedure");
        var output = FailureDiagnosticSanitizer.Sanitize(input, true, static x => x);

        Assert.NotNull(output);
        Assert.True(output.Hidden);
        Assert.Null(output.Message);
        Assert.Null(output.StackTrace);
        Assert.Null(output.ResponseContent);
        Assert.Equal(51000, output.SqlNumber);
        Assert.Equal("test_proc", output.SqlProcedure);
        Assert.Equal("StoredProcedure", output.CommandKind);
    }

    [Fact]
    public void Oversized_unicode_and_controls_remain_bounded_and_marked_truncated()
    {
        var huge = string.Concat(Enumerable.Repeat("😀\u0000\u0001", 20000));
        var input = new FailureDiagnostic("Http", huge, huge, huge, huge, MediaType: huge);
        var output = FailureDiagnosticSanitizer.Sanitize(input, false, static x => x);
        var result = new TemplateResult("PermanentFailure", "CommandFailed", true,
            JsonSerializer.SerializeToElement(new { }), null, output);
        var frame = ExecutionProtocol.Serialize(result);

        Assert.NotNull(output);
        Assert.True(output.Truncated);
        Assert.True(frame.Length < 48 * 1024);
        Assert.DoesNotContain('\uFFFD', output.Message!);
        Assert.Contains("[truncated]", output.Message!);
    }

    [Fact]
    public void Oversized_combined_diagnostic_is_reduced_and_retained()
    {
        var input = new FailureDiagnostic("Http", Message: new string('m', 4096),
            StackTrace: new string('s', 8192), ResponseContent: new string('r', 16384));

        var output = FailureDiagnosticSanitizer.Sanitize(input, false, static x => x);

        Assert.NotNull(output);
        Assert.True(output.Truncated);
        Assert.Equal("Http", output.Kind);
        Assert.Contains("[truncated]", output.ResponseContent);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(output).Length <= 28 * 1024);
    }

    [Fact]
    public void Malformed_child_diagnostic_is_dropped_without_discarding_safe_result()
    {
        var result = new TemplateResult("PermanentFailure", "CommandFailed", true,
            JsonSerializer.SerializeToElement(new { }), null,
            new FailureDiagnostic("Sql", Message: new string('x', 100000), SqlNumber: -1));
        var type = typeof(ExecutionProtocol).Assembly.GetType("WorkerDock.Runtime.Processes.ChildHandle")!;
        var method = type.GetMethod("ValidateFailureDiagnostic", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sanitized = (TemplateResult)method.Invoke(null, [result])!;

        Assert.Equal(result.SafeCode, sanitized.SafeCode);
        Assert.Equal(result.Outcome, sanitized.Outcome);
        Assert.Null(sanitized.Diagnostic);
    }

    [Fact]
    public void Wrongly_typed_child_diagnostic_preserves_safe_result()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("""
            {"Outcome":"PermanentFailure","SafeCode":"CommandFailed","EffectsMayHaveOccurred":true,"SafeSummary":{},"RetryAfter":null,"Diagnostic":{"Kind":"Sql","Message":42}}
            """);
        var type = typeof(ExecutionProtocol).Assembly.GetType("WorkerDock.Runtime.Processes.ChildHandle")!;
        var method = type.GetMethod("DeserializeResult", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = (TemplateResult)method.Invoke(null, [payload])!;

        Assert.Equal("CommandFailed", result.SafeCode);
        Assert.Equal("PermanentFailure", result.Outcome);
        Assert.Null(result.Diagnostic);
    }
}
