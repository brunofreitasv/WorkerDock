using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
using WorkerDock.Templates.Http;
using WorkerDock.Templates.SqlServer;

namespace WorkerDock.IntegrationTests;

public sealed class FailureDiagnosticTemplateTests
{
    [Fact]
    public async Task Http_text_failure_captures_status_and_body_without_exception_stack()
    {
        var result = await RunHttp(new StringContent("intentional failure", Encoding.UTF8, "text/plain"));
        Assert.Equal("TransientFailure", result.Outcome);
        Assert.Equal("HttpRemoteTransient", result.SafeCode);
        Assert.Equal("Http", result.Diagnostic?.Kind);
        Assert.Equal("GET", result.Diagnostic?.HttpMethod);
        Assert.Equal(500, result.Diagnostic?.HttpStatus);
        Assert.Equal("text/plain", result.Diagnostic?.MediaType);
        Assert.Equal("intentional failure", result.Diagnostic?.ResponseContent);
        Assert.Null(result.Diagnostic?.StackTrace);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    public async Task Http_undecodable_or_binary_failure_keeps_only_metadata(string mediaType)
    {
        var result = await RunHttp(new ByteArrayContent([0xff, 0xfe, 0xfd]), mediaType);
        Assert.Equal(500, result.Diagnostic?.HttpStatus);
        Assert.Equal(mediaType, result.Diagnostic?.MediaType);
        Assert.Equal(3, result.Diagnostic?.ResponseBytes);
        Assert.Null(result.Diagnostic?.ResponseContent);
    }

    [Fact]
    public async Task Oversized_http_response_keeps_status_and_length()
    {
        var result = await RunHttp(new StringContent(new string('x', 1100), Encoding.UTF8, "text/plain"));
        Assert.Equal("ResponseTooLarge", result.SafeCode);
        Assert.Equal(500, result.Diagnostic?.HttpStatus);
        Assert.Equal(1100, result.Diagnostic?.ResponseBytes);
        Assert.Null(result.Diagnostic?.ResponseContent);
    }

    [Fact]
    public async Task Http_transport_exception_captures_available_exception_fields()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("transport failed")));
        var result = await new HttpTemplate(client).ExecuteAsync(HttpContext(), default);
        Assert.Equal("HttpTransportFailure", result.SafeCode);
        Assert.Contains("HttpRequestException", result.Diagnostic?.ExceptionType);
        Assert.Equal("transport failed", result.Diagnostic?.Message);
        Assert.NotNull(result.Diagnostic?.StackTrace);
    }

    [Fact]
    public void Ad_hoc_sql_diagnostic_does_not_include_command_text_or_fabricate_procedure()
    {
        var diagnostic = SqlTemplate.Diagnostic(new InvalidOperationException("failed"), "Query");
        Assert.Equal("Sql", diagnostic.Kind);
        Assert.Equal("Query", diagnostic.CommandKind);
        Assert.Null(diagnostic.SqlProcedure);
        Assert.Null(diagnostic.SqlNumber);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Sql_throw_captures_number_procedure_and_line()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL_TARGET_RUNTIME"))) return;
        await using var fixture = new SqlTemplateTargetFixture();
        await fixture.CreateAsync();
        var config = new SqlJobConfiguration(fixture.Profile.ProfileId, fixture.Profile.AllowedCatalogs[0], "Procedure",
            "[workerdock].FailSqlTemplateTarget",
            [new("@key", SqlDbType.NVarChar, null, null, true, false, 100, null, null),
             new("@amount", SqlDbType.Decimal, SqlTemplateTests.Json(1m), null, false, false, null, 10, 2),
             new("@text", SqlDbType.NVarChar, SqlTemplateTests.Json("test"), null, false, false, 400, null, null),
             new("@optional", SqlDbType.Int, null, null, false, true, null, null, null)], null, false);
        var context = new TemplateExecutionContext(
            new ExecutionDescriptor(Guid.NewGuid(), 1, "workerdock.sql-server", "1.1.0", "key",
                SqlTemplateTests.Json(config), fixture.Profile.ProfileRevisionId, SqlTemplateTests.Json(fixture.Profile),
                JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow.AddSeconds(30)),
            fixture.Vault, new NoLog());
        var result = await new SqlTemplate().ExecuteAsync(context, default);
        Assert.Equal("PermanentFailure", result.Outcome);
        Assert.Equal(51025, result.Diagnostic?.SqlNumber);
        Assert.NotNull(result.Diagnostic?.SqlProcedure);
        Assert.True(result.Diagnostic?.SqlLine > 0);
        Assert.Contains("Synthetic target failure", result.Diagnostic?.Message);
        Assert.NotNull(result.Diagnostic?.StackTrace);
    }

    private static async Task<TemplateResult> RunHttp(HttpContent content, string? mediaType = null)
    {
        if (mediaType is not null) content.Headers.ContentType = new(mediaType);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = content })));
        return await new HttpTemplate(client).ExecuteAsync(HttpContext(), default);
    }

    private static TemplateExecutionContext HttpContext() => new(
        new ExecutionDescriptor(Guid.NewGuid(), 1, "workerdock.http", "1.1.0", "key",
            JsonSerializer.SerializeToElement(new { method = "GET", url = "https://example.com", query = new { }, headers = new { }, secretHeaders = new { }, parameters = new { }, maxResponseBytes = 1024 }),
            null, null, JsonSerializer.SerializeToElement(new { }), null),
        new NoSecrets(), new NoLog());

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class NoSecrets : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct) => throw new SecretUnavailableException();
    }
    private sealed class NoLog : WorkerDock.Contracts.Logging.ISafeExecutionLog
    {
        public ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
