using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
using WorkerDock.Templates.Http;
using WorkerDock.Application.Outbound;
using WorkerDock.Infrastructure.Outbound;

namespace WorkerDock.IntegrationTests;

public sealed class OutboundPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("168.63.129.16")]
    [InlineData("100.100.100.200")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Production_policy_rejects_nonpublic_addresses(string ip)
    {
        var policy = new OutboundPolicy(new FixedResolver(IPAddress.Parse(ip)));
        await Assert.ThrowsAsync<OutboundPolicyException>(async () => await policy.ResolveAsync(new(new Uri("https://example.com/"), "tenant-jobs"), default));
    }

    [Fact]
    public async Task Mapped_public_ipv4_is_normalized_before_pinning()
    {
        var policy = new OutboundPolicy(new FixedResolver(IPAddress.Parse("::ffff:93.184.216.34")),
            new Dictionary<string, OutboundPolicyRule>(StringComparer.Ordinal)
            { ["tenant-jobs"] = new(new HashSet<string> { "example.com" }, new HashSet<int> { 443 }) });
        var approved = await policy.ResolveAsync(new(new Uri("https://example.com/"), "tenant-jobs"), default);
        Assert.Equal(AddressFamily.InterNetwork, approved.Addresses.Single().AddressFamily);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), approved.Addresses.Single());
    }

    [Fact]
    public async Task Mixed_dns_answers_fail_closed()
    {
        var policy = new OutboundPolicy(new FixedResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.1.2.3")));
        await Assert.ThrowsAsync<OutboundPolicyException>(async () => await policy.ResolveAsync(new(new Uri("https://example.com/"), "tenant-jobs"), default));
    }

    [Fact]
    public async Task Private_cidr_requires_explicit_allowlist_but_loopback_stays_denied()
    {
        var rules = new Dictionary<string, OutboundPolicyRule>(StringComparer.Ordinal)
        {
            ["tenant-jobs"] = new(new HashSet<string> { "api.internal.test" }, new HashSet<int> { 443 }, PrivateCidrs: ["10.0.0.0/8"])
        };
        var allowed = new OutboundPolicy(new FixedResolver(IPAddress.Parse("10.2.3.4")), rules);
        var destination = await allowed.ResolveAsync(new(new Uri("https://api.internal.test/"), "tenant-jobs"), default);
        Assert.Equal(IPAddress.Parse("10.2.3.4"), destination.Addresses.Single());
        var loopback = new OutboundPolicy(new FixedResolver(IPAddress.Loopback), rules);
        await Assert.ThrowsAsync<OutboundPolicyException>(async () => await loopback.ResolveAsync(new(new Uri("https://api.internal.test/"), "tenant-jobs"), default));
    }

    [Fact]
    public async Task Private_destination_requires_both_explicit_host_and_cidr_allowlists()
    {
        var rules = new Dictionary<string, OutboundPolicyRule>(StringComparer.Ordinal)
        {
            ["tenant-jobs"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<int> { 443 }, PrivateCidrs: ["10.0.0.0/8"])
        };
        var policy = new OutboundPolicy(new FixedResolver(IPAddress.Parse("10.1.2.3")), rules);
        await Assert.ThrowsAsync<OutboundPolicyException>(async () => await policy.ResolveAsync(new(new Uri("https://internal.test/"), "tenant-jobs"), default));
    }

    [Fact]
    public async Task Hostnames_are_normalized_and_userinfo_is_rejected()
    {
        var resolver = new FixedResolver(IPAddress.Parse("93.184.216.34"));
        var policy = new OutboundPolicy(resolver);
        var approved = await policy.ResolveAsync(new(new Uri("https://EXAMPLE.COM./"), "tenant-jobs"), default);
        Assert.Equal("example.com", approved.Host);
        var unicode = await policy.ResolveAsync(new(new Uri("https://bücher.example./"), "tenant-jobs"), default);
        Assert.Equal("xn--bcher-kva.example", unicode.Host);
        await Assert.ThrowsAsync<OutboundPolicyException>(async () => await policy.ResolveAsync(new(new Uri("https://user:pass@example.com/"), "tenant-jobs"), default));
    }

    [Fact]
    public async Task Explicit_test_policy_pins_connection_to_approved_address()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IOutboundPolicy policy = new FixedPolicy(new("allowed.test", port, [IPAddress.Loopback]));
        var approved = await policy.ResolveAsync(new(new Uri($"http://allowed.test:{port}/"), "test"), default);
        Assert.Equal("allowed.test", approved.Host);
        Assert.Equal(new[] { IPAddress.Loopback }, approved.Addresses);
    }

    private sealed class FixedResolver(params IPAddress[] answers) : IOutboundAddressResolver
    {
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => ValueTask.FromResult(answers);
    }
    private sealed class FixedPolicy(ApprovedDestination destination) : IOutboundPolicy
    { public ValueTask<ApprovedDestination> ResolveAsync(OutboundTarget target, CancellationToken ct) => ValueTask.FromResult(destination); }
}

public sealed class HttpTemplateTests
{
    [Fact]
    public async Task Direct_handler_rejects_http_when_policy_disallows_it()
    {
        await using var server = new ControlledHttpServer("200 OK", "", "no-request-expected");
        IOutboundPolicy policy = new HttpsOnlyTestPolicy(new("allowed.test", server.Port, [IPAddress.Loopback]));
        using var client = new HttpClient(new ValidatedConnectionFactory(policy).CreateHandler("tenant-jobs"));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"http://allowed.test:{server.Port}/"));
        Assert.Equal(0, server.AcceptCount);
    }

    [Fact]
    public async Task Unbounded_execution_still_bounds_noncooperative_destination_preflight()
    {
        var resolverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preflightToken = CancellationToken.None;
        using var client = new HttpClient(new DelegateHandler((_, _) => throw new InvalidOperationException("request must not be sent")));
        var template = new HttpTemplate(client, (_, ct) =>
        {
            preflightToken = ct;
            resolverStarted.TrySetResult();
            return new ValueTask(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        });
        var resultTask = template.ExecuteAsync(Context(Config("https://allowed.test", timeoutSeconds: 0), null), default).AsTask();
        await resolverStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("HttpTimeout", result.SafeCode);
        Assert.False(result.EffectsMayHaveOccurred);
        Assert.True(preflightToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Bodyless_post_is_not_retried_after_server_closes_before_response()
    {
        await using var server = new DropAfterRequestServer();
        IOutboundPolicy policy = new FixedPolicy(new("allowed.test", server.Port, [IPAddress.Loopback]));
        using var client = new ValidatedConnectionFactory(policy).CreateClient("tenant-jobs");
        var template = new HttpTemplate(client, async (uri, ct) => { await policy.ResolveAsync(new(uri, "tenant-jobs"), ct); });
        var result = await template.ExecuteAsync(Context(Config($"http://allowed.test:{server.Port}/", timeoutSeconds: 3, method: "POST"), DateTimeOffset.UtcNow.AddSeconds(5)), default);
        Assert.Equal(1, await server.RequestCount.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("HttpTransportFailure", result.SafeCode);
        Assert.True(result.EffectsMayHaveOccurred);
    }

    [Fact]
    public void Descriptor_is_stable_and_defaults_to_safe_summary()
    {
        var template = new HttpTemplate(new HttpClient(new HttpClientHandler()));
        Assert.Equal("workerdock.http", template.Descriptor.Id);
        Assert.Equal("1.0.0", template.Descriptor.Version);
        Assert.Contains("SafeSummary", template.Descriptor.ConfigurationSchema.GetRawText());
    }

    [Fact]
    public void Header_values_reject_injection_and_conflicting_sensitive_headers()
    {
        var template = new HttpTemplate(new HttpClient(new HttpClientHandler()));
        using var config = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","headers":{"X-Test":"ok\r\nInjected: yes"}}""");
        Assert.NotEmpty(template.Validate(config.RootElement));
        using var forbidden = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","headers":{"Host":"evil.test"}}""");
        Assert.NotEmpty(template.Validate(forbidden.RootElement));
        using var sensitive = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","headers":{"Authorization":"ordinary"},"secretHeaders":{"authorization":{"name":"token"}}}""");
        Assert.NotEmpty(template.Validate(sensitive.RootElement));
    }

    [Fact]
    public void Substitution_is_limited_to_declared_values_and_execution_key()
    {
        var result = ParameterSubstitution.Apply("/items/{id}?k={execution.idempotencyKey}", new Dictionary<string, JsonElement> { ["id"] = JsonDocument.Parse("\"a/b\"").RootElement.Clone() }, "stable-key");
        Assert.Equal("/items/a%2Fb?k=stable-key", result);
        Assert.Throws<InvalidOperationException>(() => ParameterSubstitution.Apply("/{missing}", new Dictionary<string, JsonElement>(), "key"));
        using var parameter = JsonDocument.Parse("""{"value":"a\"b\\c"}""");
        var body = ParameterSubstitution.ApplyJson("""{"value":"{value}"}""", new Dictionary<string, JsonElement> { ["value"] = parameter.RootElement.GetProperty("value").Clone() }, "key");
        using var parsed = JsonDocument.Parse(body);
        Assert.Equal("a\"b\\c", parsed.RootElement.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Retry_after_is_returned_without_handler_retry(int status)
    {
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((req, ct) => { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3)) } }); }));
        var template = new HttpTemplate(client);
        var configuration = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","query":{},"headers":{},"secretHeaders":{},"parameters":{},"successStatuses":[],"maxResponseBytes":1024,"responseMode":"SafeSummary"}""").RootElement;
        var result = await template.ExecuteAsync(Context(configuration, DateTimeOffset.UtcNow.AddSeconds(5)), default);
        Assert.Equal(1, requests);
        Assert.Equal(TimeSpan.FromSeconds(3), result.RetryAfter);
    }

    [Fact]
    public async Task Response_body_is_never_in_safe_summary_and_size_is_bounded()
    {
        const string secretBody = "response-body-sentinel";
        using var client = new HttpClient(new DelegateHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(secretBody) })));
        var template = new HttpTemplate(client);
        var configuration = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","query":{},"headers":{},"secretHeaders":{},"parameters":{},"successStatuses":[],"maxResponseBytes":4,"responseMode":"SafeSummary"}""").RootElement;
        var result = await template.ExecuteAsync(Context(configuration, DateTimeOffset.UtcNow.AddSeconds(5)), default);
        Assert.Equal("ResponseTooLarge", result.SafeCode);
        Assert.DoesNotContain(secretBody, result.SafeSummary.GetRawText());
    }

    [Fact]
    public async Task Redirect_is_not_followed_and_connection_peer_is_pinned()
    {
        await using var server = new ControlledHttpServer("302 Found", "Location: http://forbidden.test/\r\n", "redirect-body");
        IOutboundPolicy policy = new FixedPolicy(new("allowed.test", server.Port, [IPAddress.Loopback]));
        var factory = new ValidatedConnectionFactory(policy);
        using var client = factory.CreateClient("tenant-jobs");
        var template = new HttpTemplate(client, async (uri, ct) => { await policy.ResolveAsync(new(uri, "tenant-jobs"), ct); });
        var config = Config($"http://allowed.test:{server.Port}/");
        var result = await template.ExecuteAsync(Context(config, DateTimeOffset.UtcNow.AddSeconds(5)), default);
        Assert.Equal("HttpRemoteRejected", result.SafeCode);
        Assert.True(result.EffectsMayHaveOccurred);
        Assert.Equal(1, await server.RequestCount.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(IPAddress.Loopback, server.AcceptedPeer?.Address);
        using var handler = factory.CreateHandler("tenant-jobs");
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Dns_rebinding_does_not_open_a_socket_to_late_forbidden_answer()
    {
        await using var server = new ControlledHttpServer("200 OK", "", "never-connect");
        var rules = new Dictionary<string, OutboundPolicyRule>(StringComparer.Ordinal)
        {
            ["tenant-jobs"] = new(new HashSet<string> { "allowed.test" }, new HashSet<int> { server.Port }, AllowHttp: true)
        };
        var resolver = new SequenceResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.34"), IPAddress.Loopback);
        var policy = new OutboundPolicy(resolver, rules);
        using var client = new ValidatedConnectionFactory(policy).CreateClient("tenant-jobs");
        var template = new HttpTemplate(client, async (uri, ct) => { await policy.ResolveAsync(new(uri, "tenant-jobs"), ct); });
        var config = Config($"http://allowed.test:{server.Port}/");
        var result = await template.ExecuteAsync(Context(config, DateTimeOffset.UtcNow.AddSeconds(5)), default);
        Assert.False(result.EffectsMayHaveOccurred);
        Assert.Equal(0, server.AcceptCount);
    }

    [Fact]
    public async Task Tls_keeps_original_hostname_for_sni_and_rejects_certificate_mismatch()
    {
        await using var server = new TlsControlledServer();
        IOutboundPolicy policy = new FixedPolicy(new("allowed.test", server.Port, [IPAddress.Loopback]));
        using var client = new ValidatedConnectionFactory(policy).CreateClient("tenant-jobs");
        var template = new HttpTemplate(client, async (uri, ct) => { await policy.ResolveAsync(new(uri, "tenant-jobs"), ct); });
        var result = await template.ExecuteAsync(Context(Config($"https://allowed.test:{server.Port}/"), DateTimeOffset.UtcNow.AddSeconds(5)), default);
        Assert.Equal("HttpTlsValidationFailed", result.SafeCode);
        Assert.False(result.EffectsMayHaveOccurred);
        Assert.Equal("allowed.test", await server.Sni.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(server.ReceivedRequest);
    }

    [Fact]
    public async Task Secret_headers_rotate_per_attempt_and_never_enter_summary()
    {
        const string first = "token-sentinel-one";
        const string second = "token-sentinel-two";
        var secrets = new RotatingVault(first, second);
        var captured = new List<string>();
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            captured.Add(request.Headers.GetValues("Authorization").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var template = new HttpTemplate(client);
        var config = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","secretHeaders":{"Authorization":{"name":"api-token"}},"maxResponseBytes":100,"responseMode":"SafeSummary"}""").RootElement;
        var firstResult = await template.ExecuteAsync(Context(config, null, secrets), default);
        var secondResult = await template.ExecuteAsync(Context(config, null, secrets), default);
        Assert.Equal(new[] { first, second }, captured);
        Assert.DoesNotContain(first, firstResult.SafeSummary.GetRawText());
        Assert.DoesNotContain(second, secondResult.SafeSummary.GetRawText());
    }

    [Fact]
    public async Task Zero_timeout_requires_unbounded_outer_deadline_and_negative_is_invalid()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var template = new HttpTemplate(client);
        var config = Config("https://example.com", timeoutSeconds: 0);
        var denied = await template.ExecuteAsync(Context(config, DateTimeOffset.UtcNow.AddSeconds(5)), default);
        Assert.Equal("HttpTimeoutInvalid", denied.SafeCode);
        var allowed = await template.ExecuteAsync(Context(config, null), default);
        Assert.Equal("Success", allowed.Outcome);
        using var invalid = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","timeoutSeconds":-1,"maxResponseBytes":1024}""");
        Assert.NotEmpty(template.Validate(invalid.RootElement));
    }

    [Fact]
    public async Task Retry_after_is_bounded_and_5xx_is_uncertain_without_hidden_retry()
    {
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(4));
            return Task.FromResult(response);
        }));
        var result = await new HttpTemplate(client).ExecuteAsync(Context(Config("https://example.com"), DateTimeOffset.UtcNow.AddSeconds(10)), default);
        Assert.Equal(1, requests);
        Assert.Equal("TransientFailure", result.Outcome);
        Assert.True(result.EffectsMayHaveOccurred);
        Assert.Equal(TimeSpan.FromDays(1), result.RetryAfter);
    }

    [Fact]
    public async Task Query_body_values_are_encoded_and_execution_key_is_explicit()
    {
        string? actualQuery = null; string? actualKey = null;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            actualQuery = request.RequestUri!.Query;
            actualKey = request.Headers.GetValues("Idempotency-Key").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var template = new HttpTemplate(client);
        using var document = JsonDocument.Parse("""{"method":"GET","url":"https://example.com/items","query":{"q":"{query}"},"parameters":{"query":"a/b &"},"idempotencyHeader":"Idempotency-Key","maxResponseBytes":100}""");
        var result = await template.ExecuteAsync(Context(document.RootElement, null), default);
        Assert.Equal("?q=a%2Fb%20%26", actualQuery);
        Assert.Equal("stable-key", actualKey);
        Assert.Equal("Success", result.Outcome);
    }

    [Fact]
    public async Task Finite_handler_timeout_cancels_a_slow_remote_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var document = JsonDocument.Parse("""{"method":"GET","url":"https://example.com","timeoutSeconds":1,"maxResponseBytes":100}""");
        var result = await new HttpTemplate(client).ExecuteAsync(Context(document.RootElement, null), default);
        Assert.True(started.Task.IsCompleted);
        Assert.Equal("HttpTimeout", result.SafeCode);
        Assert.True(result.EffectsMayHaveOccurred);
    }

    private static JsonElement Config(string url, int? timeoutSeconds = null, string method = "GET") => JsonSerializer.SerializeToElement(new { method, url, query = new { }, headers = new { }, secretHeaders = new { }, parameters = new { }, timeoutSeconds, successStatuses = Array.Empty<int>(), maxResponseBytes = 1024, responseMode = "SafeSummary" });
    private static TemplateExecutionContext Context(JsonElement configuration, DateTimeOffset? deadline, ISecretVault? secrets = null) => new(new(Guid.NewGuid(), 1, "workerdock.http", "1.0.0", "stable-key", configuration, null, null, JsonDocument.Parse("{}").RootElement, deadline), secrets ?? new NoSecrets(), new NoLog());
    private sealed class NoSecrets : ISecretVault { public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct) => throw new SecretUnavailableException(); }
    private sealed class FixedPolicy(ApprovedDestination destination) : IOutboundPolicy { public ValueTask<ApprovedDestination> ResolveAsync(OutboundTarget target, CancellationToken ct) => ValueTask.FromResult(destination); }
    private sealed class HttpsOnlyTestPolicy(ApprovedDestination destination) : IOutboundPolicy
    {
        public ValueTask<ApprovedDestination> ResolveAsync(OutboundTarget target, CancellationToken ct) =>
            target.Uri.Scheme == "https" ? ValueTask.FromResult(destination) : ValueTask.FromException<ApprovedDestination>(new OutboundPolicyException("OutboundPortDenied"));
    }
    private sealed class NoLog : WorkerDock.Contracts.Logging.ISafeExecutionLog { public ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct) => ValueTask.CompletedTask; }
    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class RotatingVault(params string[] values) : ISecretVault { private int _index; public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct) => ValueTask.FromResult(new SecretValue(values[Math.Min(Interlocked.Increment(ref _index) - 1, values.Length - 1)])); }
    private sealed class SequenceResolver(params IPAddress[] values) : IOutboundAddressResolver { private int _index; public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => ValueTask.FromResult(new[] { values[Math.Min(Interlocked.Increment(ref _index) - 1, values.Length - 1)] }); }
    private sealed class ControlledHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _response;
        private readonly Task _serverTask;
        private int _acceptCount;
        public ControlledHttpServer(string status, string headers, string body)
        {
            _response = $"HTTP/1.1 {status}\r\n{headers}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _serverTask = ServeAsync();
        }
        public int Port { get; }
        public int AcceptCount => Volatile.Read(ref _acceptCount);
        public IPEndPoint? AcceptedPeer { get; private set; }
        public Task<int> RequestCount => _serverTask.ContinueWith(_ => AcceptCount, TaskScheduler.Default);
        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(); Interlocked.Increment(ref _acceptCount); AcceptedPeer = (IPEndPoint?)client.Client.RemoteEndPoint;
                var stream = client.GetStream(); var buffer = new byte[1]; var matched = 0; var marker = new byte[] { 13, 10, 13, 10 };
                while (matched < marker.Length)
                {
                    var n = await stream.ReadAsync(buffer); if (n == 0) break;
                    matched = buffer[0] == marker[matched] ? matched + 1 : buffer[0] == 13 ? 1 : 0;
                }
                await stream.WriteAsync(Encoding.UTF8.GetBytes(_response));
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
        public async ValueTask DisposeAsync() { _listener.Stop(); try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
    }

    private sealed class DropAfterRequestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serverTask;
        private int _requestCount;
        public DropAfterRequestServer()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _serverTask = ServeAsync();
        }
        public int Port { get; }
        public Task<int> RequestCount => _serverTask.ContinueWith(_ => Volatile.Read(ref _requestCount), TaskScheduler.Default);
        private async Task ServeAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream(); var one = new byte[1]; var matched = 0; var marker = new byte[] { 13, 10, 13, 10 };
                    while (matched < marker.Length)
                    {
                        var read = await stream.ReadAsync(one, stop.Token); if (read == 0) break;
                        matched = one[0] == marker[matched] ? matched + 1 : one[0] == 13 ? 1 : 0;
                    }
                    if (matched == marker.Length) Interlocked.Increment(ref _requestCount);
                    client.Client.LingerState = new LingerOption(true, 0);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
        public async ValueTask DisposeAsync() { _listener.Stop(); try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }

    private sealed class TlsControlledServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly TaskCompletionSource<string> _sni = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;
        private int _receivedRequest;
        public TlsControlledServer()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _serverTask = ServeAsync();
        }
        public int Port { get; }
        public Task<string> Sni => _sni.Task;
        public bool ReceivedRequest => Volatile.Read(ref _receivedRequest) != 0;
        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12,
                    ServerCertificateSelectionCallback = (_, name) => { _sni.TrySetResult(name ?? ""); return _certificate; }
                });
                var buffer = new byte[1]; var matched = 0; var marker = new byte[] { 13, 10, 13, 10 };
                while (matched < marker.Length)
                {
                    var n = await ssl.ReadAsync(buffer); if (n == 0) return; Interlocked.Exchange(ref _receivedRequest, 1);
                    matched = buffer[0] == marker[matched] ? matched + 1 : buffer[0] == 13 ? 1 : 0;
                }
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
        public async ValueTask DisposeAsync() { _listener.Stop(); _certificate.Dispose(); try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
    }
}
