using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Guards the dashboard and the API with HTTP basic auth.
///
/// Deliberately skips /health so Swarm's healthcheck keeps working - a 401
/// there would put the container back in a restart loop.
///
/// Basic auth sends the password on every request, base64 encoded, not
/// hashed. Over plain HTTP that is readable by anyone on the path, so this
/// only keeps out casual traffic. Put TLS in front before exposing 8090
/// beyond the VPC.
/// </summary>
public sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<BasicAuthMiddleware> _log;

    private static readonly string[] Open = { "/health" };

    public BasicAuthMiddleware(
        RequestDelegate next,
        IOptions<OrchestratorOptions> opt,
        ILogger<BasicAuthMiddleware> log)
    {
        _next = next;
        _opt  = opt.Value;
        _log  = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        // No password configured means auth is off. Logged once at startup
        // by Program.cs so this cannot be an accident nobody notices.
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

        if (TryGetCredentials(ctx, out var user, out var pass)
            && FixedEquals(user, _opt.AuthUser)
            && FixedEquals(pass, _opt.AuthPassword))
        {
            await _next(ctx);
            return;
        }

        _log.LogWarning("Rejected {Method} {Path} from {Ip}",
            ctx.Request.Method, path, ctx.Connection.RemoteIpAddress);

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"PhoneOrchestrator\"";
        await ctx.Response.WriteAsync("Sign in to continue.");
    }

    private static bool TryGetCredentials(HttpContext ctx, out string user, out string pass)
    {
        user = pass = "";

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

        user = decoded[..split];
        pass = decoded[(split + 1)..];
        return true;
    }

    /// <summary>
    /// Constant-time compare. A plain == returns as soon as two bytes differ,
    /// which leaks how much of the password was right through response timing.
    /// </summary>
    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
}
