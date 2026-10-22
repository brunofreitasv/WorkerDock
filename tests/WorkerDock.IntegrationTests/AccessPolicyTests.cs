using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using WorkerDock.Application.Common;
using WorkerDock.Application.Tenants;
using WorkerDock.Domain.Tenants;
using WorkerDock.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace WorkerDock.IntegrationTests;

// These tests catch endpoint policy bypasses, invalid configuration fallback, cookie/token leakage,
// cross-origin mutations and invalid issuer/audience acceptance through the real OIDC handler.
public sealed class AccessPolicyTests
{
    [Fact]
    public async Task Browser_api_requires_https_even_in_anonymous_mode()
    {
        await using var host = new AccessHost();
        using var client = host.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_mutation_without_antiforgery_is_rejected()
    {
        await using var host = new AccessHost(); using var client = host.Client();
        var session = await client.GetFromJsonAsync<SessionView>("/api/session");
        Assert.Equal(new(false, true, true, "Anonymous", "en", "UTC"), session);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings", new { displayName = "Team", displayTimeZone = "UTC" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/signout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/auth/signout")).StatusCode);
    }

    [Theory]
    [InlineData(null, null, "roles", true)]
    [InlineData("Manager", null, "roles", false)]
    [InlineData("Manager", "Viewer", "roles", false)]
    [InlineData("Manager", "CentralAdmin", "roles", false)]
    [InlineData("Manager", "Manager", "roles", true)]
    [InlineData("Manager", "Manager", "app_role", true)]
    [InlineData("Manager", "manager", "roles", false)]
    public async Task Configured_identity_and_role_matrix(string? requiredRole, string? userRole, string claimType, bool canManage)
    {
        using var issuer = new LocalOidcIssuer(userRole, claimType);
        await using var host = new AccessHost(issuer, requiredRole, claimType); using var client = host.Client();
        var before = await client.GetFromJsonAsync<SessionView>("/api/session");
        Assert.False(before!.CanRead); Assert.False(before.CanManage);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
        await issuer.SignIn(client);
        var session = await client.GetFromJsonAsync<SessionView>("/api/session");
        Assert.True(session!.Authenticated); Assert.True(session.CanRead); Assert.Equal(canManage, session.CanManage);
        Assert.Equal(requiredRole is null ? "Authenticated" : "RoleProtected", session.Mode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/monitoring")).StatusCode);
        Assert.Equal(canManage ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/api/settings")).StatusCode);
        var text = await client.GetStringAsync("/api/session");
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Viewer_reads_but_cannot_manage_or_read_protected_configuration()
    {
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var host = new AccessHost(issuer, "Manager"); using var client = host.Client();
        await issuer.SignIn(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/settings")).StatusCode);
        var csrf = await client.GetFromJsonAsync<CsrfView>("/api/antiforgery");
        using var update = Mutation(csrf!.RequestToken, "https://localhost");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(update)).StatusCode);
    }

    [Theory]
    [InlineData("https://other-directory.example", "tenant-client")]
    [InlineData("https://issuer.example", "another-client")]
    public async Task Wrong_issuer_or_audience_does_not_create_session(string tokenIssuer, string tokenAudience)
    {
        using var issuer = new LocalOidcIssuer(tokenIssuer: tokenIssuer, tokenAudience: tokenAudience);
        await using var host = new AccessHost(issuer); using var client = host.Client();
        await issuer.SignIn(client, succeeds: false);
        Assert.False((await client.GetFromJsonAsync<SessionView>("/api/session"))!.Authenticated);
    }

    [Theory]
    [InlineData("Access:Authority", "https://issuer.example")]
    [InlineData("Access:ClientId", "partial-client")]
    [InlineData("Access:RequiredManagementRole", "Manager")]
    [InlineData("Access:ClientSecretReference", "missing")]
    public void Invalid_partial_configuration_fails_closed(string key, string value)
    {
        using var host = new AccessHost(extra: new() { [key] = value });
        var error = Assert.ThrowsAny<Exception>(() => host.Client());
        Assert.Contains("AccessConfigurationInvalid", error.ToString());
        Assert.DoesNotContain(value, error.Message);
    }

    [Theory]
    [InlineData("Access:Authority", "http://issuer.example")]
    [InlineData("Access:Authority", "https://login.microsoftonline.com/common/v2.0")]
    [InlineData("Access:Authority", "https://login.microsoftonline.com/organizations/v2.0")]
    [InlineData("Access:ClientSecretReference", "../secret")]
    [InlineData("Access:RoleClaimType", " ")]
    public void Invalid_configured_options_fail_with_safe_code(string key, string value)
    {
        using var issuer = new LocalOidcIssuer();
        using var host = new AccessHost(issuer, extra: new() { [key] = value });
        var error = Assert.ThrowsAny<Exception>(() => host.Client());
        Assert.Contains("AccessConfigurationInvalid", error.ToString());
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public async Task Anonymous_mutation_requires_session_bound_token_origin_and_version()
    {
        await using var host = new AccessHost(); using var client = host.Client(); using var other = host.Client();
        var token = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        using var wrongOrigin = Mutation(token, "https://attacker.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(wrongOrigin)).StatusCode);
        using var missingOrigin = Mutation(token, null);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(missingOrigin)).StatusCode);
        using var stolenToken = Mutation(token, "https://localhost");
        Assert.Equal(HttpStatusCode.BadRequest, (await other.SendAsync(stolenToken)).StatusCode);
        using var noVersion = Mutation(token, "https://localhost");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(noVersion)).StatusCode);
        using var get = await client.GetAsync("/api/settings");
        using var valid = Mutation(token, "https://localhost"); valid.Headers.IfMatch.Add(get.Headers.ETag!);
        using var updated = await client.SendAsync(valid); Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var stale = Mutation(token, "https://localhost"); stale.Headers.IfMatch.Add(get.Headers.ETag!);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
        Assert.DoesNotContain("session:", await client.GetStringAsync("/api/session"));
    }

    [Fact]
    public async Task Anonymous_browser_subject_is_stable_isolated_and_server_controlled()
    {
        var recording = new RecordingSettingsService();
        await using var host = new AccessHost(recording: recording); using var first = host.Client(); using var second = host.Client();
        async Task Update(HttpClient browser)
        {
            using var current = await browser.GetAsync("/api/settings");
            var token = (await browser.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
            using var mutation = Mutation(token, "https://localhost");
            mutation.Headers.IfMatch.Add(current.Headers.ETag!);
            mutation.Headers.Add("X-Actor-Subject", "attacker"); mutation.Headers.Add("X-Correlation-ID", Guid.Empty.ToString());
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(mutation)).StatusCode);
        }
        await Update(first); await Update(first); await Update(second);
        Assert.Equal(3, recording.Actors.Count);
        Assert.StartsWith("session:", recording.Actors[0].Subject);
        Assert.Equal(recording.Actors[0].Subject, recording.Actors[1].Subject);
        Assert.NotEqual(recording.Actors[0].Subject, recording.Actors[2].Subject);
        Assert.All(recording.Actors, actor => { Assert.Equal("Anonymous", actor.AuthenticationMode); Assert.NotEqual(Guid.Empty, actor.CorrelationId); });
        Assert.Equal(3, recording.Actors.Select(x => x.CorrelationId).Distinct().Count());
    }

    [Fact]
    public async Task Authentication_ticket_does_not_retain_identity_provider_tokens()
    {
        using var issuer = new LocalOidcIssuer(); await using var host = new AccessHost(issuer); using var client = host.Client();
        var cookie = await issuer.SignIn(client);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("cookie");
        var ticket = options.TicketDataFormat.Unprotect(cookie!);
        Assert.NotNull(ticket); Assert.Empty(ticket.Properties.GetTokens());
        Assert.DoesNotContain(ticket.Principal.Claims, claim => claim.Type.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Discovery_metadata_cannot_expand_the_configured_allowed_issuer()
    {
        using var issuer = new LocalOidcIssuer(tokenIssuer: "https://unapproved.example", metadataIssuer: "https://unapproved.example");
        await using var host = new AccessHost(issuer); using var client = host.Client();
        await issuer.SignIn(client, succeeds: false);
        Assert.False((await client.GetFromJsonAsync<SessionView>("/api/session"))!.Authenticated);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Expired_or_wrongly_signed_identity_is_rejected(bool expired, bool wrongKey)
    {
        using var issuer = new LocalOidcIssuer(expired: expired, wrongKey: wrongKey);
        await using var host = new AccessHost(issuer); using var client = host.Client();
        await issuer.SignIn(client, succeeds: false);
        Assert.False((await client.GetFromJsonAsync<SessionView>("/api/session"))!.Authenticated);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Unsolicited_remote_logout_cannot_terminate_a_session(string method)
    {
        using var issuer = new LocalOidcIssuer("Manager"); await using var host = new AccessHost(issuer); using var client = host.Client();
        await issuer.SignIn(client);
        using var remoteLogout = new HttpRequestMessage(new HttpMethod(method), "/signout-oidc");
        remoteLogout.Headers.Add("Origin", "https://attacker.example");
        remoteLogout.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var response = await client.SendAsync(remoteLogout);
        Assert.True((await client.GetFromJsonAsync<SessionView>("/api/session"))!.Authenticated);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            Assert.DoesNotContain(cookies, cookie => cookie.StartsWith("__Host-WorkerDock.Auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_cookies_are_secure_host_scoped_and_logout_is_csrf_protected()
    {
        using var issuer = new LocalOidcIssuer("Manager"); await using var host = new AccessHost(issuer); using var client = host.Client();
        await issuer.SignIn(client);
        using var response = await client.GetAsync("/api/antiforgery");
        foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
        {
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/signout", null)).StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<CsrfView>())!.RequestToken;
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/signout");
        logout.Headers.Add("X-WorkerDock-CSRF", token); logout.Headers.Add("Origin", "https://localhost");
        using var loggedOut = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.OK, loggedOut.StatusCode);
        var logoutBody = await loggedOut.Content.ReadAsStringAsync();
        using var logoutJson = JsonDocument.Parse(logoutBody);
        var providerLogout = new Uri(logoutJson.RootElement.GetProperty("redirectUrl").GetString()!);
        Assert.StartsWith("https://issuer.example/logout?", providerLogout.AbsoluteUri);
        var logoutQuery = QueryHelpers.ParseQuery(providerLogout.Query);
        Assert.True(logoutQuery.TryGetValue("state", out var state) && !string.IsNullOrWhiteSpace(state), "Provider logout must carry protected return state.");
        var callback = logoutQuery["post_logout_redirect_uri"].ToString();
        Assert.Equal("https://localhost/signout-callback-oidc", callback);
        using var returned = await client.GetAsync(QueryHelpers.AddQueryString(callback, "state", state.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        Assert.Equal("/", returned.Headers.Location?.OriginalString);
        Assert.DoesNotContain("id_token", logoutBody, StringComparison.OrdinalIgnoreCase);
        Assert.False((await client.GetFromJsonAsync<SessionView>("/api/session"))!.Authenticated);
    }

    private static HttpRequestMessage Mutation(string token, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(new { displayName = "Equipe", displayTimeZone = "America/Sao_Paulo" }) };
        request.Headers.Add("X-WorkerDock-CSRF", token); if (origin is not null) request.Headers.Add("Origin", origin); return request;
    }
}

internal sealed record SessionView(bool Authenticated, bool CanRead, bool CanManage, string Mode, string Language, string TimeZone);
internal sealed record CsrfView(string RequestToken);

internal sealed class AccessHost : WebApplicationFactory<WorkerDock.Web.WebAssemblyMarker>
{
    private readonly LocalOidcIssuer? issuer; private readonly Dictionary<string, string?> settings; private readonly RecordingSettingsService? recording;
    private readonly string reference = "TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
    public AccessHost(LocalOidcIssuer? issuer = null, string? role = null, string claimType = "roles", Dictionary<string, string?>? extra = null, RecordingSettingsService? recording = null)
    {
        this.issuer = issuer; this.recording = recording;
        settings = new()
        {
            ["Tenant:Adapter"] = "Memory",
            ["Tenant:InstanceId"] = Guid.NewGuid().ToString(),
            ["Access:Authority"] = issuer is null ? null : "https://issuer.example",
            ["Access:ClientId"] = issuer is null ? null : "tenant-client",
            ["Access:ClientSecretReference"] = issuer is null ? null : reference,
            ["Access:RequiredManagementRole"] = role,
            ["Access:RoleClaimType"] = claimType
        };
        if (issuer is not null) Environment.SetEnvironmentVariable("WORKERDOCK_BOOTSTRAP_" + reference, "synthetic-test-credential");
        if (extra is not null) foreach (var item in extra) settings[item.Key] = item.Value;
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        if (recording is not null) builder.ConfigureServices(services => services.AddSingleton<ITenantSettingsService>(recording));
        if (issuer is not null) builder.ConfigureServices(services => services.Configure<OpenIdConnectOptions>("oidc", options => options.Backchannel = new HttpClient(issuer, disposeHandler: false)));
    }
    public HttpClient Client() => CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    protected override void Dispose(bool disposing) { Environment.SetEnvironmentVariable("WORKERDOCK_BOOTSTRAP_" + reference, null); base.Dispose(disposing); }
}

internal sealed class LocalOidcIssuer : HttpMessageHandler
{
    private readonly RSA rsa = RSA.Create(2048); private readonly RsaSecurityKey key;
    private readonly string? role; private readonly string claimType; private readonly string tokenIssuer; private readonly string tokenAudience; private readonly string metadataIssuer; private readonly bool expired; private readonly bool wrongKey;
    private string nonce = ""; private string challenge = "";
    public LocalOidcIssuer(string? role = null, string claimType = "roles", string tokenIssuer = "https://issuer.example", string tokenAudience = "tenant-client", string metadataIssuer = "https://issuer.example", bool expired = false, bool wrongKey = false)
    { this.role = role; this.claimType = claimType; this.tokenIssuer = tokenIssuer; this.tokenAudience = tokenAudience; this.metadataIssuer = metadataIssuer; this.expired = expired; this.wrongKey = wrongKey; key = new(rsa) { KeyId = "local-test-key" }; }
    public async Task<string?> SignIn(HttpClient client, bool succeeds = true, string authCookieName = "__Host-WorkerDock.Auth")
    {
        using var start = await client.GetAsync("/auth/signin"); Assert.True(start.StatusCode == HttpStatusCode.Redirect, await start.Content.ReadAsStringAsync());
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query); nonce = query["nonce"]!; challenge = query["code_challenge"]!;
        Assert.Equal("code", query["response_type"].ToString()); Assert.Equal("S256", query["code_challenge_method"].ToString());
        using var finish = await client.GetAsync(QueryHelpers.AddQueryString("/signin-oidc", new Dictionary<string, string?> { ["code"] = "local-code", ["state"] = query["state"] }));
        Assert.Equal(succeeds ? HttpStatusCode.Redirect : HttpStatusCode.Unauthorized, finish.StatusCode);
        if (succeeds)
        {
            var authCookie = finish.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(authCookieName + "=", StringComparison.Ordinal));
            Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain=", authCookie, StringComparison.OrdinalIgnoreCase);
            return authCookie.Split(';')[0].Split('=', 2)[1];
        }
        return null;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        object body;
        if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
            body = new { issuer = metadataIssuer, authorization_endpoint = "https://issuer.example/authorize", token_endpoint = "https://issuer.example/token", jwks_uri = "https://issuer.example/keys", end_session_endpoint = "https://issuer.example/logout", response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" }, code_challenge_methods_supported = new[] { "S256" } };
        else if (request.RequestUri.AbsolutePath == "/keys")
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
            body = new { keys = new[] { new { kty = "RSA", kid = key.KeyId, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } };
        }
        else if (request.RequestUri.AbsolutePath == "/token")
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(challenge, Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(form["code_verifier"]!))));
            var claims = new List<Claim> { new("sub", "local-viewer"), new("nonce", nonce) }; if (role is not null) claims.Add(new(claimType, role));
            using var otherRsa = RSA.Create(2048);
            var signingKey = wrongKey ? new RsaSecurityKey(otherRsa) { KeyId = key.KeyId } : key;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = tokenIssuer, Audience = tokenAudience, Subject = new ClaimsIdentity(claims), IssuedAt = DateTime.UtcNow.AddMinutes(-20), NotBefore = DateTime.UtcNow.AddMinutes(-20), Expires = DateTime.UtcNow.AddMinutes(expired ? -10 : 5), SigningCredentials = new(signingKey, SecurityAlgorithms.RsaSha256) });
            body = new { id_token = token, access_token = "synthetic-access-token-not-for-browser", token_type = "Bearer", expires_in = 300 };
        }
        else throw new InvalidOperationException("Unexpected local issuer request.");
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
    protected override void Dispose(bool disposing) { if (disposing) rsa.Dispose(); base.Dispose(disposing); }
}

internal sealed class RecordingSettingsService : ITenantSettingsService
{
    private readonly InMemoryTenantSettingsService inner = new(Guid.NewGuid());
    public List<ActorContext> Actors { get; } = [];
    public ValueTask<TenantSettings> GetAsync(CancellationToken ct) => inner.GetAsync(ct);
    public async ValueTask<TenantSettings> UpdateAsync(TenantSettings desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        var result = await inner.UpdateAsync(desired, expectedVersion, actor, ct); Actors.Add(actor); return result;
    }
}
