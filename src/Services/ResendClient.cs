using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Minimal Resend client - one POST /emails. No SDK: the API is a single call
/// and the official .NET package would be the only dependency in the project.
/// </summary>
public sealed class ResendClient
{
    private readonly HttpClient _http;
    private readonly OrchestratorOptions _opt;

    public ResendClient(HttpClient http, IOptions<OrchestratorOptions> opt)
    {
        _http = http;
        _opt  = opt.Value;

        _http.BaseAddress = new Uri("https://api.resend.com/");
        _http.Timeout     = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _opt.ResendApiKey);
    }

    /// <param name="idempotencyKey">
    /// Same key within 24h → Resend sends once. Guards the retry path: a
    /// timeout after Resend accepted the mail must not produce a second copy.
    /// </param>
    /// <returns>
    /// ok, and when not ok whether a later retry can help (429 / 5xx /
    /// network) or the request itself is wrong (4xx - bad address, unverified
    /// domain) and retrying would only fail again.
    /// </returns>
    public async Task<(bool Ok, bool Retryable, string? Error)> SendAsync(
        string to, string subject, string html, string idempotencyKey, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            from    = _opt.NotifyFromEmail,
            to      = new[] { to },
            subject,
            html
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);

        try
        {
            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return (true, false, null);

            var text = await res.Content.ReadAsStringAsync(ct);
            var code = (int)res.StatusCode;
            var retryable = res.StatusCode == HttpStatusCode.TooManyRequests || code >= 500;
            return (false, retryable, $"resend {code}: {Trim(text)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, true, $"resend request failed: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500];
}
