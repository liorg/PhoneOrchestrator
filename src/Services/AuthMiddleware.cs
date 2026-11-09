using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Guards the dashboard and the API.
///
/// Accepts either the session cookie (browsers) or basic auth (curl, scripts).
/// Keeping basic auth alongside the login screen means every diagnostic
/// command in the runbook still works unchanged.
///
/// Unauthenticated browser requests are redirected to the login page rather
/// than answered with 401, which is what stops the browser from popping its
/// own credentials dialog. API requests still get a clean 401 so callers can
/// detect an expired session instead of parsing an HTML page.
///
/// /api/hub/* (phone-hub on the NUC) is a separate realm: only HubUser /
/// HubPassword get in, and those credentials open nothing else. The dashboard
/// cookie and AuthUser / AuthPassword do not open /api/hub/*. An empty
/// HubPassword closes the hub routes - it never opens them, even when the
/// dashboard auth is off.
/// </summary>
public sealed class AuthMiddleware
{
    private const string HubPrefix = "/api/hub/";

    private readonly RequestDelegate _next;
    private readonly OrchestratorOptions _opt;
    private readonly AuthTokens _tokens;
    private readonly ILogger<AuthMiddleware> _log;

    // Reachable without a session: Swarm's healthcheck, the login page and
    // the assets it needs to render, and the login call itself.
    private static readonly string[] Open =
    {
        "/health",
        "/version",
        "/login.html",
        "/styles.css",
        "/logo2.png",
        "/api/auth/login",
        "/api/auth/logout"
    };

    public AuthMiddleware(
        RequestDelegate next,
        IOptions<OrchestratorOptions> opt,
        AuthTokens tokens,
        ILogger<AuthMiddleware> log)
    {
        _next   = next;
        _opt    = opt.Value;
        _tokens = tokens;
        _log    = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        // ── phone-hub: its own credentials, checked before the "auth off" shortcut
        if (path.StartsWith(HubPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(_opt.HubPassword))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Hub access is not configured." });
                return;
            }

            if (HasBasicAuth(ctx, _opt.HubUser, _opt.HubPassword))
            {
                await _next(ctx);
                return;
            }

            _log.LogWarning("Rejected hub call {Path} from {Ip}", path, ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Not authorized." });
            return;
        }

        // No password configured means auth is off. Program.cs warns loudly
        // at startup so this cannot be an accident nobody notices.
        if (string.IsNullOrEmpty(_opt.AuthPassword))
        {
            await _next(ctx);
            return;
        }

        // The prober's callbacks carry no credentials. CanaryController
        // checks the path token itself and answers 404 without it.
        if (Open.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase))
            || path.StartsWith("/canary/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        if (_tokens.TryValidate(ctx.Request.Cookies[AuthTokens.CookieName], out _)
            || HasBasicAuth(ctx, _opt.AuthUser, _opt.AuthPassword))
        {
            await _next(ctx);
            return;
        }

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Not signed in." });
            return;
        }

        ctx.Response.Redirect("/login.html");
    }

    private static bool HasBasicAuth(HttpContext ctx, string user, string password)
    {
        var raw = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!AuthenticationHeaderValue.TryParse(raw, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch
        {
            return false;
        }

        var split = decoded.IndexOf(':');
        if (split < 0) return false;

        // Both compared, always - && would skip the password on a wrong user
        // and leak through timing which part was wrong.
        var userOk = FixedEquals(decoded[..split], user);
        var passOk = FixedEquals(decoded[(split + 1)..], password);
        return userOk & passOk;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
}
