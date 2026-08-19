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
/// </summary>
public sealed class AuthMiddleware
{
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
        // No password configured means auth is off. Program.cs warns loudly
        // at startup so this cannot be an accident nobody notices.
        if (string.IsNullOrEmpty(_opt.AuthPassword))
        {
            await _next(ctx);
            return;
        }

        var path = ctx.Request.Path.Value ?? "";

        if (Open.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(ctx);
            return;
        }

        if (_tokens.TryValidate(ctx.Request.Cookies[AuthTokens.CookieName], out _)
            || HasValidBasicAuth(ctx))
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

    private bool HasValidBasicAuth(HttpContext ctx)
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

        return FixedEquals(decoded[..split], _opt.AuthUser)
            && FixedEquals(decoded[(split + 1)..], _opt.AuthPassword);
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
}
