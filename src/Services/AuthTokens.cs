using Microsoft.AspNetCore.DataProtection;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Issues and validates the session cookie.
///
/// Uses ASP.NET data protection, so the cookie is encrypted and signed with a
/// key the client never sees - it cannot be forged or edited. The key ring
/// lives in the container filesystem and is NOT persisted, which means every
/// deploy or restart invalidates outstanding cookies and everyone signs in
/// again. For a single-operator dashboard that is the right trade: no volume
/// to mount, no key material to rotate.
/// </summary>
public sealed class AuthTokens
{
    public const string CookieName = "orch_session";

    private readonly IDataProtector _protector;

    public AuthTokens(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("PhoneOrchestrator.Auth.v1");

    public string Issue(string user, TimeSpan lifetime)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        return _protector.Protect($"{user}|{expires}");
    }

    public bool TryValidate(string? token, out string user)
    {
        user = "";
        if (string.IsNullOrWhiteSpace(token)) return false;

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch
        {
            // Tampered, or issued under a key ring this instance no longer has.
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[1], out var expires)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow) return false;

        user = parts[0];
        return true;
    }
}
