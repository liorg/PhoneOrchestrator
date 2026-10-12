using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Verdicts for the canary, and the runner's in-memory state.
///
/// A check is one row in canary_checks with a unique check_code. The prober
/// sends "CHECK ID: {code}" in a template; the phone under test answers
/// "ACK {code}"; the ACK lands here and flips exactly that row to passed.
/// Everything else - timeout, wrong sender, send failure - is a failure with
/// a reason on the same row. No Redis, no trail: two messages per check do
/// not need a sub-event store.
/// </summary>
public sealed partial class CanaryStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CanaryStore> _log;

    public CanaryStore(IServiceScopeFactory scopes, ILogger<CanaryStore> log)
    {
        _scopes = scopes;
        _log    = log;
    }

    // ═══════════════════════════════════════════════════ runner state ══
    // In-memory scratch for the loop; published to canary_control every tick.

    public string    Mode              { get; set; } = "starting";
    public DateTime? LastSweepUtc      { get; set; }
    public string?   LastSweepResult   { get; set; }
    public CanaryProber.Health? Prober { get; set; }
    public DateTime? ProberCheckedUtc  { get; set; }

    // ═══════════════════════════════════════════════════════════ codes ══

    // Crockford base32: no I, L, O, U - nothing a human or an autocorrect
    // can confuse when reading the code back off a screen.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewCode(int length = 8)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }

    // The phone's reply: "ACK X", "ACK: X", "ack #X", with text around it.
    // At least one separator, so a word like ACKNOWLEDGED never reads as a code.
    [GeneratedRegex(@"\bACK[\s:#\-]+([0-9A-Z]{6,16})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AckRegex();

    // ═════════════════════════════════════════════════════════ inbound ══

    public sealed record InboundResult(bool Matched, string? Code, string? Verdict, string? Note);

    /// <summary>
    /// Takes whatever the prober posted - the shape differs between the
    /// webhook payload and the container-event payload, and between image
    /// versions - and looks for an ACK in it. Walking every string is what
    /// makes this immune to the shape: the code is unique, so finding it
    /// anywhere is enough to know which check it answers.
    /// </summary>
    public async Task<InboundResult> HandleInboundAsync(JsonElement payload, CancellationToken ct)
    {
        // Delivery receipts for the CHECK the prober sent. They are what tells
        // "never reached the phone" apart from "reached it, nobody answered".
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("event", out var ev) && ev.GetString() == "message_status")
            return await HandleStatusAsync(payload, ct);

        // The prober echoes its own outgoing messages. Those carry
        // "CHECK ID", never "ACK", but skip them explicitly anyway.
        if (FindBool(payload, "fromMe") == true || FindBool(payload, "from_me") == true)
            return new InboundResult(false, null, null, "outgoing echo");

        var strings = new List<string>();
        var senders = new List<string>();
        Walk(payload, null, strings, senders);

        string? code = null;
        foreach (var s in strings)
        {
            var m = AckRegex().Match(s);
            if (m.Success) { code = m.Groups[1].Value.ToUpperInvariant(); break; }
        }

        if (code == null) return new InboundResult(false, null, null, "no ACK in payload");

        using var scope = _scopes.CreateScope();
        var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();

        var rows = await rpc.SelectAsync("canary_checks",
            $"select=id,run_id,phone_number,status,sent_at&check_code=eq.{code}&limit=1", ct);

        Guid checkId = default; string? number = null; string? status = null; DateTime? sentAt = null;
        foreach (var r in rows.EnumerateArray())
        {
            Guid.TryParse(r.GetProperty("id").GetString(), out checkId);
            number = r.TryGetProperty("phone_number", out var n) ? n.GetString() : null;
            status = r.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (r.TryGetProperty("sent_at", out var sa) && sa.ValueKind == JsonValueKind.String
                && DateTime.TryParse(sa.GetString(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
                sentAt = d;
        }

        if (checkId == default)
        {
            _log.LogWarning("[CANARY] ACK {Code} matches no check", code);
            return new InboundResult(false, code, null, "unknown code");
        }

        var from = senders.FirstOrDefault();

        // An ACK must come from the phone it was sent to. The code alone is
        // already unguessable, so this is less about forgery than about
        // catching a routing bug - a reply surfacing on the wrong number.
        var senderOk = senders.Count == 0 || senders.Any(s => SameNumber(s, number));

        var now = DateTime.UtcNow;
        var latency = sentAt.HasValue ? (int)Math.Max(0, (now - sentAt.Value).TotalMilliseconds) : (int?)null;

        // Late ACKs (after the sweep already called timeout) still get
        // recorded as late_ack: the phone is alive, just slow, and that is
        // worth seeing on the dashboard rather than silently dropping.
        var newStatus = senderOk ? "passed" : "failed";
        var error     = senderOk ? null : $"ACK arrived from {from}, expected {number}";

        var changed = await rpc.PatchReturningAsync("canary_checks",
            $"id=eq.{checkId}&status=eq.sent",
            new { status = newStatus, replied_at = now, latency_ms = latency, ack_from = from, error }, ct);

        if (changed.ValueKind == JsonValueKind.Array && changed.GetArrayLength() > 0)
        {
            _log.LogInformation("[CANARY] ACK {Code} from {From} -> {Status} in {Latency}ms",
                code, from, newStatus, latency);
            return new InboundResult(true, code, newStatus, null);
        }

        if (status == "timeout")
        {
            await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}&status=eq.timeout", new
            {
                status = "late_ack", replied_at = now, latency_ms = latency, ack_from = from
            }, ct);
            _log.LogWarning("[CANARY] Late ACK {Code} after {Latency}ms", code, latency);
            return new InboundResult(true, code, "late_ack", null);
        }

        return new InboundResult(true, code, status, "already closed");
    }

    /// <summary>
    /// whatsapp-cloudapi reports Meta's statuses as
    ///   {"event":"message_status","messageId":"wamid…","status":N}
    /// on its own scale: 1 sent · 2 delivered · 3 read · 4 played · -1 failed.
    /// Only ever moves forward - Meta delivers statuses out of order, and a
    /// late "sent" must not overwrite "read".
    /// </summary>
    private async Task<InboundResult> HandleStatusAsync(JsonElement p, CancellationToken ct)
    {
        var wamid = p.TryGetProperty("messageId", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(wamid)
            || !p.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.Number)
            return new InboundResult(false, null, null, "status without id");

        var code = st.GetInt32();

        using var scope = _scopes.CreateScope();
        var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();

        var rows = await rpc.SelectAsync("canary_checks",
            $"select=id,delivery_status&wa_message_id=eq.{Uri.EscapeDataString(wamid)}&limit=1", ct);

        foreach (var r in rows.EnumerateArray())
        {
            var id  = r.GetProperty("id").GetString();
            int? cur = r.TryGetProperty("delivery_status", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt32() : null;

            // failed (-1) is terminal; otherwise forward only.
            if (cur == -1 || (code != -1 && cur.HasValue && code <= cur.Value))
                return new InboundResult(true, null, null, "status not newer");

            var now = DateTime.UtcNow;
            await rpc.PatchAsync("canary_checks", $"id=eq.{id}", new
            {
                delivery_status = code,
                delivered_at    = code >= 2 ? now : (DateTime?)null,
                read_at         = code >= 3 ? now : (DateTime?)null,
                // Meta refused the CHECK after accepting it - nothing will come back.
                error           = code == -1
                    ? (p.TryGetProperty("errorMessage", out var em) ? em.GetString() : null) ?? "Meta rejected the CHECK"
                    : null
            }, ct);

            return new InboundResult(true, null, $"delivery={code}", null);
        }

        return new InboundResult(false, null, null, "status for unknown message");
    }

    /// <summary>Suffix match on the last 9 digits - survives +, 0-prefix and @domain.</summary>
    public static bool SameNumber(string? a, string? b)
    {
        static string D(string? s) => new((s ?? "").Split('@')[0].Where(char.IsDigit).ToArray());
        var x = D(a); var y = D(b);
        if (x.Length < 7 || y.Length < 7) return false;
        var n = Math.Min(9, Math.Min(x.Length, y.Length));
        return x[^n..] == y[^n..];
    }

    private static readonly HashSet<string> SenderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Not "phone": in a container event that is the prober's own number.
        "from", "sender", "sendTo", "send_to", "remoteJid", "jid", "wa_id"
    };

    private static void Walk(JsonElement el, string? key, List<string> strings, List<string> senders)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject()) Walk(p.Value, p.Name, strings, senders);
                break;
            case JsonValueKind.Array:
                foreach (var i in el.EnumerateArray()) Walk(i, key, strings, senders);
                break;
            case JsonValueKind.String:
                var s = el.GetString() ?? "";
                strings.Add(s);
                // LIDs are not phone numbers; they can never match and would
                // make a healthy reply look like it came from the wrong phone.
                if (key != null && SenderKeys.Contains(key) && !s.EndsWith("@lid") && s.Any(char.IsDigit))
                    senders.Add(s);
                break;
        }
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
                    var r = FindBool(p.Value, name);
                    if (r != null) return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var i in el.EnumerateArray())
                {
                    var r = FindBool(i, name);
                    if (r != null) return r;
                }
                break;
        }
        return null;
    }
}
