using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace WorkerDock.Infrastructure.Authentication;

public static class AccessPolicies
{
    public const string Read = "WorkerDock.Read";
    public const string Manage = "WorkerDock.Manage";
    public static IServiceCollection AddWorkerDockAccess(this IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            var options = new AccessOptions(config["Access:Authority"], config["Access:ClientId"], config["Access:ClientSecretReference"], config["Access:RequiredManagementRole"], config["Access:RoleClaimType"] ?? "roles");
            if (AccessOptions.ValidateAccessOptions(options).Count != 0 || (options.AuthenticationConfigured && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKERDOCK_BOOTSTRAP_" + options.ClientSecretReference))))
                throw new InvalidOperationException("AccessConfigurationInvalid");
            var directory = config["DataProtection:KeyDirectory"];
            if ((!environment.IsDevelopment() && (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsWindows())) ||
                (directory is not null && !Path.IsPathFullyQualified(directory))) throw new InvalidOperationException("DataProtectionConfigurationInvalid");
            return options;
        });
        services.AddDataProtection();
        services.AddOptions<DataProtectionOptions>().Configure<IConfiguration>((o, config) => o.ApplicationDiscriminator = "WorkerDock.Tenant." + config["Tenant:InstanceId"]);
        services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, ILoggerFactory>((o, config, logger) =>
        {
            var directory = config["DataProtection:KeyDirectory"];
            if (!string.IsNullOrWhiteSpace(directory))
            {
                o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(directory), logger);
                if (OperatingSystem.IsWindows()) o.XmlEncryptor = new DpapiXmlEncryptor(false, logger);
            }
        });
        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-WorkerDock-CSRF"; o.Cookie.Name = "__Host-WorkerDock.Csrf";
            o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.Path = "/";
        });
        var auth = services.AddAuthentication(o => { o.DefaultScheme = "cookie"; o.DefaultChallengeScheme = "cookie"; })
            .AddCookie("cookie", o =>
            {
                o.Cookie.Name = "__Host-WorkerDock.Auth"; o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax; o.Cookie.Path = "/"; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        auth.AddOpenIdConnect("oidc", _ => { });
        services.AddOptions<OpenIdConnectOptions>("oidc").Configure<AccessOptions>((o, options) =>
        {
            if (!options.AuthenticationConfigured) return;
            o.SignInScheme = "cookie"; o.Authority = options.Authority; o.ClientId = options.ClientId; o.ClientSecret = Environment.GetEnvironmentVariable("WORKERDOCK_BOOTSTRAP_" + options.ClientSecretReference);
            o.ResponseType = OpenIdConnectResponseType.Code; o.ResponseMode = OpenIdConnectResponseMode.Query; o.UsePkce = true;
            o.SaveTokens = false; o.MapInboundClaims = false; o.RequireHttpsMetadata = true;
            o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile");
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Authority!.TrimEnd('/'),
                ValidateAudience = true,
                ValidAudience = options.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                RoleClaimType = options.RoleClaimType,
                NameClaimType = "sub",
                ClockSkew = TimeSpan.FromMinutes(1),
                IssuerValidator = (issuer, _, _) => string.Equals(issuer, options.Authority.TrimEnd('/'), StringComparison.Ordinal)
                    ? issuer : throw new SecurityTokenInvalidIssuerException("InvalidIssuer")
            };
            o.Events.OnTokenValidated = c =>
            {
                if (string.IsNullOrWhiteSpace(c.Principal?.FindFirst("sub")?.Value)) c.Fail("InvalidIdentity");
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToIdentityProviderForSignOut = c =>
            {
                c.HandleResponse(); c.Response.StatusCode = 200; c.Response.Headers.Remove("Location");
                return c.Response.WriteAsJsonAsync(new { redirectUrl = c.ProtocolMessage.CreateLogoutRequestUrl() });
            };
            o.Events.OnRemoteFailure = c => { c.HandleResponse(); c.Response.StatusCode = 401; return c.Response.WriteAsJsonAsync(new { code = "AuthenticationFailed" }); };
        });
        services.AddAuthorization();
        services.AddOptions<Microsoft.AspNetCore.Authorization.AuthorizationOptions>().Configure<AccessOptions>((o, options) =>
        {
            o.AddPolicy(Read, p => p.RequireAssertion(c => !options.AuthenticationConfigured || c.User.Identity?.IsAuthenticated == true));
            o.AddPolicy(Manage, p => p.RequireAssertion(c => !options.AuthenticationConfigured ||
                (c.User.Identity?.IsAuthenticated == true && (options.RequiredManagementRole is null ||
                    c.User.Claims.Any(x => x.Type == options.RoleClaimType && string.Equals(x.Value, options.RequiredManagementRole, StringComparison.Ordinal))))));
        });
        return services;
    }
}
