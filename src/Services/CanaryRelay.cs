using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// The hop from PhoneOrchestrator to the prober's own WhatsApp container.
///
/// Target is FastAPI on port 8000, not Baileys on 3001: Baileys binds to
/// localhost inside the container and is unreachable from the network.
/// </summary>
public sealed class CanaryRelay
{
    private readonly HttpClient _http;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<CanaryRelay> _log;

    public CanaryRelay(HttpClient http, IOptions<OrchestratorOptions> opt, ILogger<CanaryRelay> log)
    {
        _opt  = opt.Value;
        _log  = log;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Baileys addresses recipients by JID. A bare number needs the domain
    /// appended; a LID uses a different domain entirely, and getting that
    /// wrong means the message silently goes nowhere.
    /// </summary>
    public static string ToJid(string contactPhone)
    {
        var s = (contactPhone ?? "").Trim();
        if (s.Contains('@')) return s;

        s = s.TrimStart('+');

        // WhatsApp LIDs run to 15+ digits and are not phone numbers. Israeli
        // MSISDNs sit around 12 with the country code, so the length is the
        // only signal available here without a lookup.
        return s.Length >= 15
            ? $"{s}@lid"
            : $"{s}@s.whatsapp.net";
    }

    public async Task<(bool ok, string? waMessageId, string? error)> SendTextAsync(
        string contactPhone, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.CanaryWaUrl))
            return (false, null, "Orchestrator:CanaryWaUrl is not configured");

        var url = $"{_opt.CanaryWaUrl.TrimEnd('/')}/send/text";
        var jid = ToJid(contactPhone);

        try
        {
            using var res = await _http.PostAsJsonAsync(url, new { jid, text }, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("[CANARY] send/text -> {Status}: {Body}", (int)res.StatusCode, Trim(raw));
                return (false, null, $"{(int)res.StatusCode}: {Trim(raw)}");
            }

            // The container returns the WhatsApp message id under one of a
            // few names depending on version. Any of them is fine; none of
            // them present is a failure, because the Worker rejects a send
            // that comes back without an id.
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var root = doc.RootElement;

            var id = Read(root, "wa_message_id")
                  ?? Read(root, "messageId")
                  ?? Read(root, "message_id")
                  ?? Read(root, "id");

            if (string.IsNullOrWhiteSpace(id))
            {
                _log.LogError("[CANARY] send/text returned no message id: {Body}", Trim(raw));
                return (false, null, "no message id in response");
            }

            return (true, id, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, "timeout talking to whatsapp-canary");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] send/text threw");
            return (false, null, Trim(ex.Message));
        }
    }

    /// <summary>
    /// Makes sure exactly one webhook is registered on the prober, and that
    /// it is ours. Two registrations would fan every incoming message out
    /// twice and produce conflicting verdicts for the same check.
    /// </summary>
    public async Task EnsureWebhookAsync(string ourUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.CanaryWaUrl)) return;

        var baseUrl = _opt.CanaryWaUrl.TrimEnd('/');

        try
        {
            using var listRes = await _http.GetAsync($"{baseUrl}/webhooks", ct);
            var listRaw = await listRes.Content.ReadAsStringAsync(ct);

            var registered = new List<string>();
            if (listRes.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(listRaw))
            {
                using var doc = JsonDocument.Parse(listRaw);
                CollectUrls(doc.RootElement, registered);
            }

            foreach (var stale in registered.Where(u => !string.Equals(u, ourUrl, StringComparison.OrdinalIgnoreCase)))
            {
                _log.LogWarning("[CANARY] Removing stray webhook {Url}", stale);
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/webhooks/unregister")
                {
                    Content = JsonContent.Create(new { url = stale })
                };
                await _http.SendAsync(req, ct);
            }

            if (!registered.Any(u => string.Equals(u, ourUrl, StringComparison.OrdinalIgnoreCase)))
            {
                _log.LogWarning("[CANARY] Registering webhook {Url}", ourUrl);
                await _http.PostAsJsonAsync($"{baseUrl}/webhooks/register", new { url = ourUrl }, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Webhook reconciliation failed");
        }
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