using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly OrchestratorOptions _opt;
    private readonly AuthTokens _tokens;
    private readonly ILogger<AuthController> _log;

    public AuthController(IOptions<OrchestratorOptions> opt, AuthTokens tokens, ILogger<AuthController> log)
    {
        _opt    = opt.Value;
        _tokens = tokens;
        _log    = log;
    }

    public sealed record LoginRequest(string User, string Password);

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest req)
    {
        if (req is null || string.IsNullOrEmpty(req.User))
            return BadRequest(new { ok = false, error = "Enter a username and password." });

        var ok = FixedEquals(req.User, _opt.AuthUser)
                 && FixedEquals(req.Password ?? "", _opt.AuthPassword);

        if (!ok)
        {
            _log.LogWarning("Failed sign-in for '{User}' from {Ip}",
                req.User, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { ok = false, error = "Wrong username or password." });
        }

        Response.Cookies.Append(AuthTokens.CookieName, _tokens.Issue(req.User, Lifetime), new CookieOptions
        {
            HttpOnly = true,                    // unreadable from JS, so XSS cannot steal it
            SameSite = SameSiteMode.Strict,
            Secure   = Request.IsHttps,         // true once TLS is in front
            Expires  = DateTimeOffset.UtcNow.Add(Lifetime),
            Path     = "/"
        });

        return Ok(new { ok = true, user = req.User });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(AuthTokens.CookieName, new CookieOptions { Path = "/" });
        return Ok(new { ok = true });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        var token = Request.Cookies[AuthTokens.CookieName];
        return _tokens.TryValidate(token, out var user)
            ? Ok(new { ok = true, user })
            : Unauthorized(new { ok = false });
    }

    /// <summary>
    /// Constant-time compare - a plain == returns as soon as two bytes differ,
    /// leaking how much of the password was right through response timing.
    /// </summary>
    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
}
