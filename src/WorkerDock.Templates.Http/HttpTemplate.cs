using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Templates.Http;

public sealed class HttpTemplate(HttpClient client, Func<Uri, CancellationToken, ValueTask>? approveDestination = null, string version = "1.1.0") : IWorkerTemplate
{
    private const int MaxUrlLength = 2048;
    private const int MaxBodyLength = 65_536;
    private const int MaxResponseLimit = 2_097_152;
    private const int MaxRetryAfterSeconds = 86_400;
    private static readonly TimeSpan DestinationPreflightTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Proxy-Authorization", "Proxy-Connection", "Connection", "Keep-Alive", "Transfer-Encoding",
        "Content-Length", "TE", "Trailer", "Upgrade", "Expect"
    };
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" };
    public TemplateDescriptor Descriptor => HttpTemplateDescriptors.Get(version);

    public IReadOnlyList<ValidationIssue> Validate(JsonElement configuration)
    {
        try
        {
            var c = Normalize(configuration.Deserialize<HttpJobConfiguration>(HttpJobConfigurationJson.Options));
            if (c is null || !Valid(c)) return [new("configuration", "HttpConfigurationInvalid")];
            return [];
        }
        catch { return [new("configuration", "HttpConfigurationInvalid")]; }
    }

    public async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var sendStarted = false;
        string? method = null;
        FailureDiagnostic ExceptionDiagnostic(Exception error) => new("Http", error.GetType().FullName, error.Message, error.StackTrace, HttpMethod: method);
        TemplateResult ExceptionResult(string outcome, string code, bool effects, Exception error) =>
            Result(outcome, code, effects) with { Diagnostic = ExceptionDiagnostic(error) };
        try
        {
            if (Validate(context.Execution.Configuration).Count != 0) return Result("PermanentFailure", "HttpConfigurationInvalid", false);
            var config = Normalize(context.Execution.Configuration.Deserialize<HttpJobConfiguration>(HttpJobConfigurationJson.Options))!;
            method = config.Method.ToUpperInvariant();
            var uri = BuildUri(config, context.Execution.IdempotencyKey);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var scheduledDeadline = new CancellationSchedule(deadline, context.Execution.DeadlineUtc);
            var timeoutAt = context.Execution.DeadlineUtc;
            if (config.TimeoutSeconds is { } seconds && seconds > 0)
            {
                var configured = DateTimeOffset.UtcNow.AddSeconds(seconds);
                timeoutAt = timeoutAt is null || configured < timeoutAt ? configured : timeoutAt;
            }
            if (timeoutAt is { } until)
            {
                if (until <= DateTimeOffset.UtcNow) return Result("TransientFailure", "HttpTimeout", false);
                scheduledDeadline.Set(until);
            }
            if (config.TimeoutSeconds == 0 && context.Execution.DeadlineUtc is not null)
                return Result("PermanentFailure", "HttpTimeoutInvalid", false);
            if (approveDestination is not null)
            {
                using var preflight = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                preflight.CancelAfter(DestinationPreflightTimeout);
                await approveDestination(uri, preflight.Token).AsTask().WaitAsync(preflight.Token);
            }

            using var request = new HttpRequestMessage(new HttpMethod(config.Method), uri);
            if (config.Body is not null)
            {
                var body = ApplyBody(config, context.Execution.IdempotencyKey);
                if (Encoding.UTF8.GetByteCount(body) > MaxBodyLength) return Result("PermanentFailure", "HttpConfigurationInvalid", false);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (config.ContentType is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Substitute(config.ContentType, config, context.Execution.IdempotencyKey));
            }
            else if (config.ContentType is not null) return Result("PermanentFailure", "HttpConfigurationInvalid", false);
            else request.Content = new ByteArrayContent([]);

            foreach (var header in config.Headers)
            {
                var value = ParameterSubstitution.ApplyHeader(header.Value, config.Parameters, context.Execution.IdempotencyKey);
                if (value.Length > 8192 || HasControl(value) || !request.Headers.TryAddWithoutValidation(header.Key, value))
                    return Result("PermanentFailure", "HttpHeaderInvalid", false);
            }
            if (!string.IsNullOrWhiteSpace(config.IdempotencyHeader))
                request.Headers.TryAddWithoutValidation(config.IdempotencyHeader, context.Execution.IdempotencyKey);
            foreach (var header in config.SecretHeaders)
            {
                using var secret = await context.Secrets.ResolveAsync(header.Value, deadline.Token);
                var value = secret.Reveal().ToArray();
                try
                {
                    if (value.Length is 0 or > 8192 || value.Any(c => c is '\r' or '\n' or '\0' || c < 0x20 && c != '\t'))
                        return Result("PermanentFailure", "HttpHeaderInvalid", false);
                    if (!request.Headers.TryAddWithoutValidation(header.Key, new string(value)))
                        return Result("PermanentFailure", "HttpHeaderInvalid", false);
                }
                finally { Array.Clear(value); }
            }

            sendStarted = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var bodyBytes = await ReadBoundedAsync(response.Content, config.MaxResponseBytes, deadline.Token);
            if (bodyBytes is null) return Result("PermanentFailure", "ResponseTooLarge", true) with
            {
                Diagnostic = new FailureDiagnostic("Http", HttpMethod: method, HttpStatus: (int)response.StatusCode,
                    MediaType: response.Content.Headers.ContentType?.MediaType,
                    ResponseBytes: response.Content.Headers.ContentLength is >= 0 and <= int.MaxValue ? (int)response.Content.Headers.ContentLength.Value : null)
            };
            var status = (int)response.StatusCode;
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var retryAfter = ParseRetryAfter(response.Headers.RetryAfter, DateTimeOffset.UtcNow);
            var summary = config.ResponseMode == "SafeSummary"
                ? JsonSerializer.SerializeToElement(new { status, durationMilliseconds = Math.Max(0, (long)elapsed), bytes = bodyBytes.Length })
                : JsonSerializer.SerializeToElement(new { });
            var success = IsSuccess(config, status);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var diagnostic = success ? null : new FailureDiagnostic("Http", ResponseContent: DecodeErrorBody(bodyBytes, mediaType),
                HttpMethod: method, HttpStatus: status, MediaType: mediaType, ResponseBytes: bodyBytes.Length);
            Array.Clear(bodyBytes);
            if (success) return new("Success", "HttpCompleted", true, summary, retryAfter);
            if (status is 408 or 425 or 429 || status >= 500)
                return new("TransientFailure", "HttpRemoteTransient", true, summary, retryAfter, diagnostic);
            return new("PermanentFailure", "HttpRemoteRejected", true, summary, retryAfter, diagnostic);
        }
        catch (SecretUnavailableException error) { return ExceptionResult("PermanentFailure", "SecretUnavailable", false, error); }
        catch (HttpTemplateException error) { return ExceptionResult("PermanentFailure", error.SafeCode, false, error); }
        catch (WorkerDock.Contracts.Templates.OutboundDestinationDeniedException error) { return ExceptionResult("PermanentFailure", error.SafeCode, false, error); }
        catch (OperationCanceledException error)
        {
            return ExceptionResult("TransientFailure", ct.IsCancellationRequested ? "HttpCancelled" : "HttpTimeout", sendStarted, error);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Http", StringComparison.Ordinal))
        { return ExceptionResult("PermanentFailure", error.Message, false, error); }
        catch (HttpRequestException error) when (HasDestinationDenial(error)) { return ExceptionResult("PermanentFailure", "OutboundAddressDenied", false, error); }
        catch (HttpRequestException error) when (HasTlsFailure(error)) { return ExceptionResult("PermanentFailure", "HttpTlsValidationFailed", false, error); }
        catch (HttpRequestException error) { return ExceptionResult("TransientFailure", "HttpTransportFailure", sendStarted, error); }
        catch (Exception error) { return ExceptionResult("PermanentFailure", "HttpExecutionFailed", sendStarted, error); }
    }

    private static string? DecodeErrorBody(byte[] bytes, string? mediaType)
    {
        if (bytes.Length == 0 || mediaType is null ||
            !(mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
              mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
              mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
              mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.IndexOf('\0') >= 0 ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private bool Valid(HttpJobConfiguration c)
    {
        if (c.Method is not { Length: > 0 and <= 16 } || !Methods.Contains(c.Method) ||
            c.Url is not { Length: > 0 and <= MaxUrlLength } || c.Query is null || c.Query.Count > 128 ||
            c.Headers is null || c.Headers.Count > 64 || c.SecretHeaders is null || c.SecretHeaders.Count > 32 ||
            c.Parameters is null || c.Parameters.Count > 128 || c.Body?.Length > MaxBodyLength ||
            c.TimeoutSeconds < 0 || c.TimeoutSeconds > 31_536_000 || c.MaxResponseBytes is < 1 or > MaxResponseLimit ||
            c.ResponseMode is not ("Discard" or "SafeSummary") || c.SuccessStatuses is null || c.SuccessStatuses.Count > 100 ||
            c.SuccessStatuses.Any(x => x is < 100 or > 599)) return false;
        if (c.ContentType is { Length: > 256 } || c.ContentType is not null && !MediaTypeHeaderValue.TryParse(c.ContentType, out _)) return false;
        if (c.IdempotencyHeader is not null && (!ValidHeaderName(c.IdempotencyHeader) || ForbiddenHeaders.Contains(c.IdempotencyHeader))) return false;
        foreach (var (key, value) in c.Query)
            if (key is null || key.Length is 0 or > 2048 || value is null || value.Length > 8192) return false;
        foreach (var (name, value) in c.Headers)
            if (!ValidHeaderName(name) || ForbiddenHeaders.Contains(name) || value is null || value.Length > 8192 || HasControl(value)) return false;
        foreach (var (name, reference) in c.SecretHeaders)
            if (!ValidHeaderName(name) || ForbiddenHeaders.Contains(name) || reference is null || string.IsNullOrWhiteSpace(reference.Name) || reference.Name.Length > 128) return false;
        var allNames = c.Headers.Keys.Concat(c.SecretHeaders.Keys).ToArray();
        if (allNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allNames.Length ||
            (c.IdempotencyHeader is not null && allNames.Contains(c.IdempotencyHeader, StringComparer.OrdinalIgnoreCase))) return false;
        if (c.Parameters.Any(p => p.Key.Length is < 1 or > 64 || !Regex.IsMatch(p.Key, "^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
                                  p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined ||
                                  p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length > 8192 || p.Value.GetRawText().Length > 8192)) return false;
        try
        {
            _ = BuildUri(c, "validation-key");
            if (c.Body is not null) _ = ApplyBody(c, "validation-key");
            return true;
        }
        catch { return false; }
    }

    private Uri BuildUri(HttpJobConfiguration c, string key)
    {
        var expanded = ApplyUrl(c.Url, c.Parameters, key);
        if (!Uri.TryCreate(expanded, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            expanded.Length > MaxUrlLength || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("HttpConfigurationInvalid");
        if (c.Query.Count == 0) return uri;
        var builder = new UriBuilder(uri);
        var parts = c.Query.Select(x => $"{ApplyUrl(x.Key, c.Parameters, key)}={ApplyUrl(x.Value, c.Parameters, key)}");
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.Join("&", new[] { existing }.Concat(parts).Where(x => x.Length != 0));
        var result = builder.Uri;
        if (result.AbsoluteUri.Length > MaxUrlLength) throw new InvalidOperationException("HttpConfigurationInvalid");
        return result;
    }

    private static HttpJobConfiguration? Normalize(HttpJobConfiguration? c) => c is null ? null : c with
    {
        Query = c.Query ?? new Dictionary<string, string>(),
        Headers = c.Headers ?? new Dictionary<string, string>(),
        SecretHeaders = c.SecretHeaders ?? new Dictionary<string, SecretReference>(),
        Parameters = c.Parameters ?? new Dictionary<string, JsonElement>(),
        SuccessStatuses = c.SuccessStatuses ?? [],
        ResponseMode = string.IsNullOrWhiteSpace(c.ResponseMode) ? "SafeSummary" : c.ResponseMode
    };

    private string Substitute(string text, HttpJobConfiguration config, string key) => ParameterSubstitution.ApplyText(text, config.Parameters, key);
    private string ApplyUrl(string text, IReadOnlyDictionary<string, JsonElement> parameters, string key) => ParameterSubstitution.ApplyUrl(text, parameters, key);
    private string ApplyBody(HttpJobConfiguration config, string key)
    {
        var result = config.ContentType is null || IsJsonMediaType(config.ContentType)
            ? ParameterSubstitution.ApplyJson(config.Body!, config.Parameters, key)
            : ParameterSubstitution.ApplyText(config.Body!, config.Parameters, key);
        if (result.Length > MaxBodyLength || Encoding.UTF8.GetByteCount(result) > MaxBodyLength) throw new InvalidOperationException("HttpConfigurationInvalid");
        return result;
    }
    private static bool IsJsonMediaType(string value)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out var media)) return false;
        return media.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true || media.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true;
    }
    private static bool IsSuccess(HttpJobConfiguration c, int status) => c.SuccessStatuses.Count == 0 ? status is >= 200 and <= 299 : c.SuccessStatuses.Contains(status);
    private static bool ValidHeaderName(string value) => value.Length is > 0 and <= 128 && Regex.IsMatch(value, "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static bool HasControl(string value) => value.Any(c => c is '\r' or '\n' or '\0' || c < 0x20 && c != '\t');

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? value, DateTimeOffset now)
    {
        if (value is null) return null;
        var delay = value.Delta ?? (value.Date is { } date ? date - now : null);
        if (delay is null || delay < TimeSpan.Zero) return null;
        return delay > TimeSpan.FromSeconds(MaxRetryAfterSeconds) ? TimeSpan.FromSeconds(MaxRetryAfterSeconds) : delay;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } length && length > maxBytes) return null;
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(Math.Min(maxBytes, 16_384));
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var remaining = maxBytes + 1 - (int)output.Length;
                if (remaining <= 0) return null;
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
                if (read == 0) return output.ToArray();
                output.Write(buffer, 0, read);
                if (output.Length > maxBytes) return null;
            }
        }
        finally { Array.Clear(buffer); }
    }

    private static TemplateResult Result(string outcome, string code, bool effects) =>
        new(outcome, code, effects, JsonSerializer.SerializeToElement(new { }), null);
    private static bool HasDestinationDenial(Exception error) => error is WorkerDock.Contracts.Templates.OutboundDestinationDeniedException || error.InnerException is not null && HasDestinationDenial(error.InnerException);
    private static bool HasTlsFailure(Exception error) => error is AuthenticationException || error is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } || error.InnerException is not null && HasTlsFailure(error.InnerException);

    public sealed class HttpTemplateException(string safeCode) : Exception(safeCode) { public string SafeCode { get; } = safeCode; }

    private sealed class CancellationSchedule : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private CancellationTokenSource? _stop;
        public CancellationSchedule(CancellationTokenSource source, DateTimeOffset? deadline) { _source = source; if (deadline is not null) Set(deadline.Value); }
        public void Set(DateTimeOffset until)
        {
            _stop?.Cancel(); _stop?.Dispose(); _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var remaining = until - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero) { _source.Cancel(); return; }
                        await Task.Delay(remaining > TimeSpan.FromDays(24) ? TimeSpan.FromDays(24) : remaining, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }, token);
        }
        public void Dispose() { _stop?.Cancel(); _stop?.Dispose(); }
    }
}
