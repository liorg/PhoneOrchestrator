using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// OTP for 2FA. Every 2 seconds: takes pending rows from otp_requests
/// (rpc_otp_claim), sends the template through CanaryProber, and writes back
/// sent/failed (rpc_otp_mark_sent). The backend only writes the row and reads the result.
///
/// Supabase and the prober come from the existing OrchestratorOptions
/// (SupabaseUrl / SupabaseKey / CanaryProberUrl). Optional, defaults:
///   Otp__Enabled=true   Otp__Template=check_number   Otp__Language=en
///   Otp__Body={name}|{code}   Otp__Header=   Otp__PollSec=2
/// </summary>
public sealed class OtpSender : BackgroundService
{
    private readonly CanaryProber _prober;
    private readonly HttpClient _http;
    private readonly OrchestratorOptions _opt;
    private readonly IConfiguration _cfg;
    private readonly ILogger<OtpSender> _log;

    public OtpSender(CanaryProber prober, IHttpClientFactory http, IOptions<OrchestratorOptions> opt,
                     IConfiguration cfg, ILogger<OtpSender> log)
    {
        _prober = prober;
        _http   = http.CreateClient(nameof(OtpSender));
        _opt    = opt.Value;
        _cfg    = cfg;
        _log    = log;
    }

    private sealed record Row(
        [property: JsonPropertyName("id")]     string Id,
        [property: JsonPropertyName("mobile")] string Mobile,
        [property: JsonPropertyName("name")]   string? Name,
        [property: JsonPropertyName("code")]   string Code);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.GetValue("Otp:Enabled", true))
        {
            _log.LogInformation("[OTP] disabled");
            return;
        }
        if (string.IsNullOrWhiteSpace(_opt.SupabaseUrl) || string.IsNullOrWhiteSpace(_opt.SupabaseKey))
        {
            _log.LogError("[OTP] SupabaseUrl / SupabaseKey missing — not started");
            return;
        }

        _http.BaseAddress = new Uri($"{_opt.SupabaseUrl.TrimEnd('/')}/rest/v1/rpc/");
        _http.DefaultRequestHeaders.Add("apikey", _opt.SupabaseKey);
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_opt.SupabaseKey}");

        var template = _cfg["Otp:Template"] ?? "check_number";
        var language = _cfg["Otp:Language"] ?? "en";
        var body     = Split(_cfg["Otp:Body"] ?? "{name}|{code}");
        var header   = Split(_cfg["Otp:Header"] ?? "");
        var poll     = TimeSpan.FromSeconds(_cfg.GetValue("Otp:PollSec", 2));

        _log.LogInformation("[OTP] started | template={T}/{L} poll={P}s", template, language, poll.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var res = await _http.PostAsJsonAsync("rpc_otp_claim", new { p_limit = 10 }, ct);
                res.EnsureSuccessStatusCode();
                var rows = await res.Content.ReadFromJsonAsync<List<Row>>(cancellationToken: ct) ?? [];

                foreach (var r in rows)
                {
                    var v = new Dictionary<string, string>
                    {
                        ["code"]   = r.Code,
                        ["name"]   = string.IsNullOrWhiteSpace(r.Name) ? "Customer" : r.Name.Trim(),
                        ["mobile"] = r.Mobile,
                    };

                    var (ok, wamid, error) = await _prober.SendTemplateAsync(
                        r.Mobile, template, language, Render(body, v), Render(header, v), ct);

                    using var mark = await _http.PostAsJsonAsync("rpc_otp_mark_sent",
                        new { p_id = r.Id, p_wa_message_id = wamid, p_error = ok ? null : error }, ct);
                    if (!mark.IsSuccessStatusCode)
                        _log.LogError("[OTP] mark_sent -> {S} | id={Id}", (int)mark.StatusCode, r.Id);

                    if (ok) _log.LogInformation("[OTP] sent | id={Id} to=…{Tail} wamid={W}", r.Id, Tail(r.Mobile), wamid);
                    else    _log.LogError("[OTP] failed | id={Id} to=…{Tail} err={E}", r.Id, Tail(r.Mobile), error);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "[OTP] poll failed");
            }

            try { await Task.Delay(poll, ct); } catch (OperationCanceledException) { }
        }
    }

    private static string[] Split(string s) =>
        s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] Render(string[] parts, Dictionary<string, string> v) =>
        parts.Select(p =>
        {
            foreach (var (k, val) in v) p = p.Replace($"{{{k}}}", val);
            return string.IsNullOrWhiteSpace(p) ? "-" : p;   // Meta rejects an empty param
        }).ToArray();

    private static string Tail(string m) => m.Length > 4 ? m[^4..] : m;
}