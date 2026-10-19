using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// The hop from PhoneOrchestrator to the prober - a whatsapp-cloudapi
/// container that lives in the whqueue stack, on whqueue-net, next to NATS.
///
/// /health before a sweep, /send/template per phone (canary + OTP), and
/// /webhooks reconciliation so the ACKs find their way back here.
/// </summary>
public sealed class CanaryProber
{
    private readonly HttpClient _http;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<CanaryProber> _log;

    public CanaryProber(HttpClient http, IOptions<OrchestratorOptions> opt, ILogger<CanaryProber> log)
    {
        _opt  = opt.Value;
        _log  = log;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private string Base => _opt.CanaryProberUrl.TrimEnd('/');

    public static string ToJid(string number)
    {
        var s = (number ?? "").Trim();
        if (s.Contains('@')) return s;
        return $"{s.TrimStart('+')}@s.whatsapp.net";
    }

    // ═════════════════════════════════════════════════════════ health ══

    public sealed record Health(bool Reachable, bool Ready, string? Reason, JsonElement? Raw);

    /// <summary>
    /// Ready means the prober can both send and hear back. A container that
    /// sends fine but lost NATS would turn every check in the sweep into a
    /// false timeout - and every customer into a false alarm - so the sweep
    /// does not start unless both sides are up.
    /// </summary>
    public async Task<Health> HealthAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.CanaryProberUrl))
            return new Health(false, false, "Orchestrator:CanaryProberUrl is not configured", null);

        try
        {
            using var res = await _http.GetAsync($"{Base}/health", ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            JsonElement? el = null;
            try { using var doc = JsonDocument.Parse(raw); el = doc.RootElement.Clone(); } catch { }

            if (!res.IsSuccessStatusCode)
                return new Health(true, false, $"/health -> {(int)res.StatusCode}", el);

            // Fields are read only when present: an older image that does not
            // report them is not treated as broken.
            if (el is { ValueKind: JsonValueKind.Object } h)
            {
                if (IsFalse(h, "nats_connected")) return new Health(true, false, "nats_connected=false", el);
                if (IsFalse(h, "token_ok"))       return new Health(true, false, "token_ok=false", el);
                if (IsFalse(h, "registered"))     return new Health(true, false, "registered=false", el);
                // Zero webhooks: ACKs reach the prober and go nowhere.
                if (h.TryGetProperty("webhooks_count", out var wc) && wc.ValueKind == JsonValueKind.Number
                    && wc.GetInt32() == 0)
                    return new Health(true, false, "webhooks_count=0", el);
            }

            return new Health(true, true, null, el);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Health(false, false, $"unreachable: {Trim(ex.Message)}", null);
        }
    }

    private static bool IsFalse(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.False;

    // ═══════════════════════════════════════════════════════ template ══

    /// <summary>
    /// One CHECK per phone: the approved template with the code as its only
    /// body parameter.
    /// </summary>
    public Task<(bool ok, string? waMessageId, string? error)> SendCheckAsync(
        string number, string template, string language, string code, CancellationToken ct)
        => SendTemplateAsync(number, template, language, [code], null, ct);

    /// <summary>
    /// Any approved template. params is {"body": [...]} or {"header": [...], "body": [...]}
    /// - a dict, not a list. Used by the canary (check_id) and by OTP (check_number).
    /// </summary>
    public async Task<(bool ok, string? waMessageId, string? error)> SendTemplateAsync(
        string number, string template, string language,
        string[] body, string[]? header, CancellationToken ct)
    {
        object prms = header is { Length: > 0 }
            ? new { header, body }
            : new { body };

        var payload = new
        {
            jid      = ToJid(number),
            name     = template,
            language,
            @params  = prms
        };

        try
        {
            using var res = await _http.PostAsJsonAsync($"{Base}/send/template", payload, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
                return (false, null, $"{(int)res.StatusCode}: {Trim(raw)}");

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                return (false, null, Trim(raw));

            var id = Read(root, "wa_message_id") ?? Read(root, "messageId")
                  ?? Read(root, "message_id")    ?? Read(root, "id")
                  ?? FirstMessageId(root);

            // No wamid means Graph never accepted it, whatever the HTTP code.
            return string.IsNullOrWhiteSpace(id)
                ? (false, null, $"no message id: {Trim(raw)}")
                : (true, id, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, null, Trim(ex.Message));
        }
    }

    /// <summary>Raw Graph shape: {"messages":[{"id":"wamid..."}]}.</summary>
    private static string? FirstMessageId(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array
        && m.GetArrayLength() > 0
            ? Read(m[0], "id")
            : null;

    // ════════════════════════════════════════════════════════ webhook ══

    /// <summary>
    /// Exactly one webhook on the prober, and it is ours. A stray second
    /// registration would deliver every ACK twice - harmless for the verdict
    /// (the write is compare-and-set) but noise in the log.
    /// </summary>
    public async Task EnsureWebhookAsync(string ourUrl, CancellationToken ct)
    {
        try
        {
            using var listRes = await _http.GetAsync($"{Base}/webhooks", ct);
            var listRaw = await listRes.Content.ReadAsStringAsync(ct);

            var registered = new List<string>();
            if (listRes.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(listRaw))
            {
                using var doc = JsonDocument.Parse(listRaw);
                CollectUrls(doc.RootElement, registered);
            }

            foreach (var stale in registered.Where(u => !Same(u, ourUrl)))
            {
                _log.LogWarning("[CANARY] Removing stray webhook {Url}", Redact(stale));
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/webhooks/unregister")
                {
                    Content = JsonContent.Create(new { url = stale })
                };
                await _http.SendAsync(req, ct);
            }

            if (!registered.Any(u => Same(u, ourUrl)))
            {
                _log.LogWarning("[CANARY] Registering webhook {Url}", Redact(ourUrl));
                using var r = await _http.PostAsJsonAsync($"{Base}/webhooks/register", new { url = ourUrl }, ct);
                if (!r.IsSuccessStatusCode)
                    _log.LogError("[CANARY] webhook register -> {Status}", (int)r.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "[CANARY] Webhook reconciliation failed");
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The token is in the path; keep it out of the logs.</summary>
    private static string Redact(string url)
    {
        var i = url.IndexOf("/canary/", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? url : url[..i] + "/canary/…";
    }

    private static void CollectUrls(JsonElement el, List<string> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) CollectUrls(item, into);
                break;
            case JsonValueKind.Object:
                var u = Read(el, "url") ?? Read(el, "callback_url");
                if (!string.IsNullOrWhiteSpace(u)) into.Add(u);
                else foreach (var p in el.EnumerateObject()) CollectUrls(p.Value, into);
                break;
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s) && s.StartsWith("http")) into.Add(s);
                break;
        }
    }

    private static string? Read(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "...";
}