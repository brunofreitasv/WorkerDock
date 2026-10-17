using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Application.Common;

namespace WorkerDock.Infrastructure.Authentication;

public sealed record SessionDto(bool Authenticated, bool CanRead, bool CanManage, string Mode, string Language, string TimeZone);

public static class SessionEndpoints
{
    private const string SubjectItem = "WorkerDock.AnonymousSubject";
    public static IApplicationBuilder UseWorkerDockSessions(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps)
            {
                await Results.Problem(statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "HttpsRequired" }).ExecuteAsync(context);
                return;
            }
        }
        var protector = context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("WorkerDock.AnonymousSession.v1");
        var protectedSubject = context.Request.Cookies["__Host-WorkerDock.Session"];
        string? subject = null;
        if (protectedSubject is not null)
        {
            try
            {
                var value = protector.Unprotect(protectedSubject).Split('|');
                if (value.Length == 2 && Guid.TryParseExact(value[0], "N", out _) && long.TryParse(value[1], out var expires) && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) subject = value[0];
            }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        if (subject is null)
        {
            subject = Guid.NewGuid().ToString("N");
            context.Response.Cookies.Append("__Host-WorkerDock.Session", protector.Protect(subject + "|" + DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()),
                new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(8), IsEssential = true });
        }
        context.Items[SubjectItem] = subject;
        if ((context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/auth/signout") &&
            !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        {
            var origin = context.Request.Headers.Origin;
            var expected = context.Request.Scheme + "://" + context.Request.Host;
            if (origin.Count != 1 || !string.Equals(origin[0], expected, StringComparison.OrdinalIgnoreCase) || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
            { await CsrfProblem(context); return; }
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { await CsrfProblem(context); return; }
        }
        await next(context);
    });

    private static Task CsrfProblem(HttpContext context)
    {
        context.Response.StatusCode = 400;
        return Results.Problem(statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "AntiforgeryFailed", ["field"] = "headers.xWorkerDockCsrf" }).ExecuteAsync(context);
    }

    public static ActorContext Actor(HttpContext context) => new(
        context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst("iss")?.Value + ":" + context.User.FindFirst("sub")!.Value : "session:" + context.Items[SubjectItem],
        context.User.Identity?.IsAuthenticated == true ? "Entra" : "Anonymous", Guid.NewGuid());

    public static void MapWorkerDockSessions(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/session", async (HttpContext context, IAuthorizationService authorization, AccessOptions options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return new SessionDto(context.User.Identity?.IsAuthenticated == true,
                (await authorization.AuthorizeAsync(context.User, null, AccessPolicies.Read)).Succeeded,
                (await authorization.AuthorizeAsync(context.User, null, AccessPolicies.Manage)).Succeeded, options.Mode, "en", "UTC");
        });
        endpoints.MapGet("/api/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { tokens.RequestToken });
        });
        endpoints.MapGet("/auth/signin", (AccessOptions options) => options.AuthenticationConfigured
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["oidc"]) : Results.Redirect("/"));
        endpoints.MapGet("/auth/signout", () => Results.StatusCode(405));
        endpoints.MapPost("/auth/signout", (AccessOptions options) => options.AuthenticationConfigured
            ? Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, ["cookie", "oidc"]) : Results.Ok());
    }
}
