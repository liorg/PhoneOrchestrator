using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Log of the canary phone's WhatsApp traffic (the prober, +972507251926) for the
/// dashboard (canary-wa.html): every event the prober posts to /canary/inbound -
/// incoming messages, ACKs, echoes of outgoing, delivery statuses, connection
/// events - and every /send/template the runner makes (CHECK, OTP).
///
/// Fire-and-forget: the inbound endpoint and the sweep never wait on it. A bounded
/// channel drained by one loop; full → the entry is dropped (a debug log is not
/// worth back-pressure on the canary). Table canary_wa_events (13_canary_wa.sql).
///
/// OTP codes are never stored: outgoing text has every 4-8 digit run masked, and
/// OtpSender's template params are not logged at all (sensitive).
/// </summary>
public sealed partial class CanaryWaLog : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CanaryWaLog> _log;
    private readonly Channel<JsonObject> _q = Channel.CreateBounded<JsonObject>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private bool _failing;

    public CanaryWaLog(IServiceScopeFactory scopes, ILogger<CanaryWaLog> log)
    {
        _scopes = scopes;
        _log    = log;
    }

    // ═════════════════════════════════════════════════════════ entries ══

    /// <summary>Whatever the prober posted to /canary/inbound, with what CanaryStore made of it.</summary>
    public void Inbound(JsonElement payload, string? verdict, string? note)
    {
        try
        {
            var e = Summarize(payload);
            e["verdict"] = verdict;
            e["note"]    = note;
            _q.Writer.TryWrite(e);
        }
        catch (Exception ex)
        {
            _log.LogDebug("[CANARY-WA] summarize failed: {Err}", ex.Message);
        }
    }

    /// <summary>A /send/template the runner made. body = null → not logged (OTP).</summary>
    public void Outbound(string to, string template, string language, string[]? body,
                         bool ok, string? wamid, string? error)
    {
        var text = body is null ? $"[{template}] ***" : $"[{template}] {string.Join(" | ", body)}";
        _q.Writer.TryWrite(new JsonObject
        {
            ["event"]         = "send_template",
            ["direction"]     = "out",
            ["wa_to"]         = to,
            ["text"]          = MaskDigits(text),
            ["wa_message_id"] = wamid,
            ["check_code"]    = CheckRegex().Match(text) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null,
            ["verdict"]       = ok ? "sent" : "send_failed",
            ["note"]          = error,
            ["raw"]           = new JsonObject
            {
                ["to"] = to, ["template"] = template, ["language"] = language,
                ["params"] = body is null ? "***" : new JsonArray(body.Select(b => (JsonNode?)MaskDigits(b)).ToArray()),
                ["ok"] = ok, ["wa_message_id"] = wamid, ["error"] = error,
            },
        });
    }

    // ════════════════════════════════════════════════════════════ loop ══

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var e in _q.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
                await rpc.CallAsync("rpc_canary_wa_log", new { p = e }, ct);
                if (_failing) { _failing = false; _log.LogInformation("[CANARY-WA] log recovered"); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Once per outage (e.g. 13_canary_wa.sql not run yet) - never per event.
                if (!_failing)
                {
                    _failing = true;
                    _log.LogWarning("[CANARY-WA] log write failed: {Err} — dropping entries until it recovers", ex.Message);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════ summarize ══

    private static readonly HashSet<string> SystemEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "connection.update", "authenticated", "disconnected", "connected", "qr", "logout",
        "ready", "registered", "unregistered", "error", "health",
    };

    private static readonly string[] TextKeys   = { "text", "body", "conversation", "caption", "message", "content" };
    private static readonly string[] FromKeys   = { "from", "sender", "participant", "wa_id" };
    private static readonly string[] ToKeys     = { "send_to", "sendTo", "to", "remoteJid", "jid", "recipient_id" };
    private static readonly string[] IdKeys     = { "messageId", "message_id", "wa_message_id", "wamid" };

    /// <summary>
    /// Shape-agnostic: the prober's payload differs between the webhook and the
    /// container-event shapes and between image versions, so fields are found by
    /// name anywhere in the tree. raw is always kept for what this misses.
    /// </summary>
    public static JsonObject Summarize(JsonElement p)
    {
        var ev       = Str(p, "event") ?? Str(p, "type");
        var fromMe   = FindBool(p, "fromMe") ?? FindBool(p, "from_me");
        var text     = FindStr(p, TextKeys);
        var status   = ev == "message_status" && p.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number
                       ? st.GetInt32() : (int?)null;
        var wamid    = FindStr(p, IdKeys) ?? FindWamid(p);

        string dir;
        if (ev == "message_status" || status.HasValue)                 dir = "status";
        else if (ev != null && SystemEvents.Contains(ev))               dir = "system";
        else if (fromMe == true)                                        dir = "out";
        else if (text != null)                                          dir = "in";
        else                                                            dir = "system";

        if (dir == "out" && text != null) text = MaskDigits(text);

        string? code = null;
        if (text != null)
        {
            var m = AckRegex().Match(text);
            if (!m.Success) m = CheckRegex().Match(text);
            if (m.Success) code = m.Groups[1].Value.ToUpperInvariant();
        }

        var raw = JsonNode.Parse(p.GetRawText());
        if (dir == "out") MaskTree(raw, null);

        return new JsonObject
        {
            ["event"]         = ev ?? (dir == "in" || dir == "out" ? "message" : null),
            ["direction"]     = dir,
            ["wa_from"]       = dir == "out" ? null : FindStr(p, FromKeys),
            ["wa_to"]         = dir == "out" ? FindStr(p, ToKeys) : null,
            ["text"]          = text is { Length: > 2000 } ? text[..2000] : text,
            ["wa_message_id"] = wamid,
            ["status"]        = status,
            ["check_code"]    = code,
            ["raw"]           = raw,
        };
    }

    // ACK from the phone, CHECK ID in the template - same alphabet as CanaryStore.
    [GeneratedRegex(@"\bACK[\s:#\-]+([0-9A-Z]{6,16})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AckRegex();

    [GeneratedRegex(@"CHECK\s*ID[\s:#\-]+([0-9A-Z]{6,16})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CheckRegex();

    // OTP codes are 4-8 digits; phone numbers (9+) and base32 CHECK codes are left alone.
    [GeneratedRegex(@"(?<![\w])\d{4,8}(?![\w])")]
    private static partial Regex DigitsRegex();

    public static string MaskDigits(string s) => DigitsRegex().Replace(s, m => new string('•', m.Length));

    // Ids, numbers and timestamps keep their digits; only free text is masked.
    private static readonly HashSet<string> NoMaskKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "messageId", "message_id", "wa_message_id", "wamid", "jid", "remoteJid", "from", "to", "sender",
        "send_to", "sendTo", "phone", "phone_number_id", "wa_id", "timestamp", "status", "participant",
    };

    private static void MaskTree(JsonNode? n, string? key)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                {
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        if (!NoMaskKeys.Contains(k) && !k.EndsWith("_at", StringComparison.OrdinalIgnoreCase)
                            && !DateTimeOffset.TryParse(s, out _))
                            o[k] = MaskDigits(s);
                    }
                    else MaskTree(v, k);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue jv && jv.TryGetValue<string>(out var s)) a[i] = MaskDigits(s);
                    else MaskTree(a[i], key);
                }
                break;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>First non-empty string under any of the keys, depth-first.</summary>
    private static string? FindStr(JsonElement e, string[] keys)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var k in keys)
                    if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                        return v.GetString();
                foreach (var p in e.EnumerateObject())
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && FindStr(p.Value, keys) is { } r)
                        return r;
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray())
                    if (FindStr(i, keys) is { } r) return r;
                break;
        }
        return null;
    }

    private static string? FindWamid(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s
                        && s.StartsWith("wamid.", StringComparison.Ordinal))
                        return s;
                    if (FindWamid(p.Value) is { } r) return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray())
                    if (FindWamid(i) is { } r) return r;
                break;
        }
        return null;
    }

    private static bool? FindBool(JsonElement el, string name)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.NameEquals(name) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return p.Value.GetBoolean();
                    if (FindBool(p.Value, name) is { } r) return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var i in el.EnumerateArray())
                    if (FindBool(i, name) is { } r) return r;
                break;
        }
        return null;
    }
}
