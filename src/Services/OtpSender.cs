using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    private static readonly TimeSpan DbTimeout = TimeSpan.FromSeconds(10);
    private const int MarkAttempts = 3;

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly OrchestratorOptions _opt;
    private readonly IConfiguration _cfg;
    private readonly ILogger<OtpSender> _log;

    private string _rpcBase = "";
    private bool _pollFailing;

    public OtpSender(IHttpClientFactory http, IServiceScopeFactory scopes, IOptions<OrchestratorOptions> opt,
                     IConfiguration cfg, ILogger<OtpSender> log)
    {
        _http   = http;
        _scopes = scopes;
        _opt    = opt.Value;
        _cfg    = cfg;
        _log    = log;
    }

    private sealed record Row(
        [property: JsonPropertyName("id")]     string Id,
        [property: JsonPropertyName("mobile")] string Mobile,
        [property: JsonPropertyName("name")]   string? Name,
        [property: JsonPropertyName("code")]   string Code);

    private sealed record Template(string Name, string Language, string[] Body, string[] Header);

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

        _rpcBase = $"{_opt.SupabaseUrl.TrimEnd('/')}/rest/v1/rpc/";

        var tpl = new Template(
            _cfg["Otp:Template"] ?? "check_number",
            _cfg["Otp:Language"] ?? "en",
            Split(_cfg["Otp:Body"] ?? "{name}|{code}"),
            Split(_cfg["Otp:Header"] ?? ""));
        var poll = TimeSpan.FromSeconds(_cfg.GetValue("Otp:PollSec", 2));

        _log.LogInformation("[OTP] started | template={T}/{L} poll={P}s", tpl.Name, tpl.Language, poll.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(tpl, ct);
            try { await Task.Delay(poll, ct); } catch (OperationCanceledException) { }
        }
    }

    // ═══════════════════════════════════════════════════════════ poll ══

    private async Task PollOnceAsync(Template tpl, CancellationToken ct)
    {
        // A new client and a new prober every round. IHttpClientFactory rotates
        // handlers (DNS, dead sockets) only for clients that are created again;
        // a client a singleton holds for the life of the process keeps its first
        // handler forever. CanaryProber is a typed client — same rule.
        using var scope = _scopes.CreateScope();
        var prober = scope.ServiceProvider.GetRequiredService<CanaryProber>();
        using var db = _http.CreateClient(nameof(OtpSender));
        db.Timeout = DbTimeout;

        List<Row> rows;
        try
        {
            var raw = await CallAsync(db, "rpc_otp_claim", new { p_limit = 10 }, ct);
            rows = string.IsNullOrWhiteSpace(raw) ? [] : JsonSerializer.Deserialize<List<Row>>(raw) ?? [];

            if (_pollFailing)
            {
                _pollFailing = false;
                _log.LogInformation("[OTP] poll recovered");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Once per outage, not every 2 seconds.
            if (!_pollFailing)
            {
                _pollFailing = true;
                _log.LogError(ex, "[OTP] poll failed — retrying every poll, logged once until it recovers");
            }
            return;
        }

        foreach (var r in rows)
        {
            try
            {
                await SendOneAsync(db, prober, tpl, r, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One row that throws must not leave the rest of the batch stuck in 'sending'.
                _log.LogError(ex, "[OTP] row failed | id={Id}", r.Id);
            }
        }
    }

    private async Task SendOneAsync(HttpClient db, CanaryProber prober, Template tpl, Row r, CancellationToken ct)
    {
        var v = new Dictionary<string, string>
        {
            ["code"]   = r.Code,
            ["name"]   = string.IsNullOrWhiteSpace(r.Name) ? "Customer" : r.Name.Trim(),
            ["mobile"] = r.Mobile,
        };

        var (ok, wamid, error) = await prober.SendTemplateAsync(
            r.Mobile, tpl.Name, tpl.Language, Render(tpl.Body, v), Render(tpl.Header, v), ct);

        await MarkAsync(db, r.Id, wamid, ok ? null : error ?? "unknown error", ct);

        if (ok) _log.LogInformation("[OTP] sent | id={Id} to=…{Tail} wamid={W}", r.Id, Tail(r.Mobile), wamid);
        else    _log.LogError("[OTP] failed | id={Id} to=…{Tail} err={E}", r.Id, Tail(r.Mobile), error);
    }

    /// <summary>
    /// After a real send, losing this write leaves the row in 'sending' — and the
    /// user's correct code is rejected, because verify accepts only 'sent'.
    /// Worth a few tries. A row left behind anyway is closed by rpc_otp_claim.
    /// </summary>
    private async Task MarkAsync(HttpClient db, string id, string? wamid, string? error, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CallAsync(db, "rpc_otp_mark_sent",
                    new { p_id = id, p_wa_message_id = wamid, p_error = error }, ct);
                return;
            }
            catch (Exception ex) when (attempt < MarkAttempts && !ct.IsCancellationRequested)
            {
                _log.LogWarning("[OTP] mark_sent attempt {A}/{N} failed | id={Id} err={E}",
                    attempt, MarkAttempts, id, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    // ══════════════════════════════════════════════════════════ helpers ══

    private async Task<string> CallAsync(HttpClient db, string fn, object args, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _rpcBase + fn)
        {
            Content = JsonContent.Create(args)
        };
        req.Headers.Add("apikey", _opt.SupabaseKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.SupabaseKey);

        using var res = await db.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"{fn} -> {(int)res.StatusCode}: {Trim(raw)}");

        return raw;
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

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
