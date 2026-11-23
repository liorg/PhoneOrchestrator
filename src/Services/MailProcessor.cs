using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneOrchestrator.Services;

// ═════════════════════════════════════════════════════════════════════════════
// MailProcessor — ההחלטה על מייל אחד מ-mail.in. בלי NATS, כדי שיהיה בדיק.
//
//   rpc_mail_inbound   שמירה ב-mail_inbound + dedupe + נמען → phone_id + manager_url
//   POST {manager}/api/webhook/mail-event/{phone_id}   (X-Webhook-Secret)
//   rpc_mail_forwarded forwarded_at / forward_error
//
// Ack = ההחלטה סופית. Nak(delay) = כשל זמני (Supabase / Manager).
// מייל ישן מ-ForwardMaxAgeSec נשמר ולא מועבר: ה-OTP פג, וה-buffer של ה-Worker
// (10 דק') לא היה מחזיק אותו בכל מקרה.
// ═════════════════════════════════════════════════════════════════════════════

public sealed class MailRelayOptions
{
    public bool    Enabled               { get; set; }
    public string  NatsUrl               { get; set; } = "nats://nats-1:4222";
    public string  Stream                { get; set; } = "MAIL_IN";
    public string  Subject               { get; set; } = "mail.in";
    public string  Durable               { get; set; } = "mail-relay";
    public int     MaxDeliver            { get; set; } = 20;
    public int     ForwardMaxAgeSec      { get; set; } = 600;
    public int     RetentionDays         { get; set; } = 7;
    /// <summary>AppSettings:WebhookSecret של ה-Manager (אותו של sms-event)</summary>
    public string  ManagerWebhookSecret  { get; set; } = "manager-secret";
    /// <summary>בדיקות בלבד: במקום agent_hosts.ip_address:5000</summary>
    public string? ManagerUrlOverride    { get; set; }
    public int     TextMax               { get; set; } = 50_000;
}

public enum MailOutcome
{
    Forwarded, Duplicate, Unmapped, NoHost, Stale, PhoneNotFound, Rejected, BadJson,
    StoreFailed, ManagerFailed, GaveUp,
}

public readonly record struct MailDecision(bool Ack, TimeSpan Delay, MailOutcome Outcome);

public sealed class MailProcessor
{
    private readonly HttpClient _http;
    private readonly MailRelayOptions _opt;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private readonly ILogger _log;

    public MailProcessor(HttpClient http, MailRelayOptions opt, string supabaseUrl, string supabaseKey, ILogger log)
    {
        _http = http;
        _opt = opt;
        _supabaseUrl = supabaseUrl.TrimEnd('/');
        _supabaseKey = supabaseKey;
        _log = log;
    }

    private MailDecision Retry(int delivered, MailOutcome outcome)
    {
        if (delivered >= _opt.MaxDeliver) return new(true, TimeSpan.Zero, MailOutcome.GaveUp);
        var sec = Math.Min(5 * Math.Pow(2, Math.Max(delivered - 1, 0)), 120);
        return new(false, TimeSpan.FromSeconds(sec), outcome);
    }

    // ── Supabase RPC ─────────────────────────────────────────────────────────

    public async Task<JsonNode?> RpcAsync(string name, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/rest/v1/rpc/{name}")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("apikey", _supabaseKey);
        req.Headers.Add("Authorization", $"Bearer {_supabaseKey}");

        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode)
            throw new HttpRequestException($"{name}: HTTP {(int)r.StatusCode} {Trunc(text, 200)}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private async Task MarkAsync(string? mailId, string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mailId)) return;
        try
        {
            await RpcAsync("rpc_mail_forwarded", new { p_id = mailId, p_error = error }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("[MAIL] rpc_mail_forwarded {Id} failed: {Err}", mailId, ex.Message);
        }
    }

    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var n = await RpcAsync("rpc_mail_purge", new { p_days = _opt.RetentionDays }, ct);
        return n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    }

    // ── מייל אחד ─────────────────────────────────────────────────────────────

    public async Task<MailDecision> ProcessAsync(ReadOnlyMemory<byte> data, int delivered, CancellationToken ct)
    {
        JsonObject p;
        try
        {
            p = JsonNode.Parse(data.Span) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            _log.LogError("[MAIL] bad JSON on {Subject} — dropped", _opt.Subject);
            return new(true, TimeSpan.Zero, MailOutcome.BadJson);
        }

        var msgId = Str(p, "msg_id");

        JsonNode? res;
        try
        {
            res = await RpcAsync("rpc_mail_inbound", new { p }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("[MAIL] {MsgId}: store failed ({Err}) — retry", msgId, ex.Message);
            return Retry(delivered, MailOutcome.StoreFailed);
        }

        if (res is not JsonObject r || r["ok"]?.GetValue<bool>() != true)
        {
            _log.LogError("[MAIL] {MsgId} rejected by rpc_mail_inbound: {Res}", msgId, res?.ToJsonString());
            return new(true, TimeSpan.Zero, MailOutcome.Rejected);
        }

        var mailId  = Str(r, "id");
        var phoneId = Str(r, "phone_id");

        if (r["forwarded"]?.GetValue<bool>() == true)
            return new(true, TimeSpan.Zero, MailOutcome.Duplicate);

        if (string.IsNullOrEmpty(phoneId))
        {
            _log.LogInformation("[MAIL] {MsgId} to={To} — no mailbox, stored only", msgId, Str(p, "to"));
            return new(true, TimeSpan.Zero, MailOutcome.Unmapped);
        }

        var manager = !string.IsNullOrWhiteSpace(_opt.ManagerUrlOverride) ? _opt.ManagerUrlOverride : Str(r, "manager_url");
        if (string.IsNullOrEmpty(manager))
        {
            await MarkAsync(mailId, "no_host", ct);
            _log.LogWarning("[MAIL] {MsgId} phone={PhoneId} — phone has no host", msgId, phoneId);
            return new(true, TimeSpan.Zero, MailOutcome.NoHost);
        }

        var age = AgeSeconds(Str(p, "received_at"));
        if (age > _opt.ForwardMaxAgeSec)
        {
            await MarkAsync(mailId, $"stale ({(int)age}s)", ct);
            _log.LogWarning("[MAIL] {MsgId} phone={PhoneId} — {Age}s old, not forwarded", msgId, phoneId, (int)age);
            return new(true, TimeSpan.Zero, MailOutcome.Stale);
        }

        var url = $"{manager!.TrimEnd('/')}/api/webhook/mail-event/{phoneId}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(EventBody(p, mailId)) };
            req.Headers.Add("X-Webhook-Secret", _opt.ManagerWebhookSecret);
            using var resp = await _http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                await MarkAsync(mailId, null, ct);
                _log.LogInformation("[MAIL] {MsgId} → phone={PhoneId} from={From} forwarded", msgId, phoneId, Str(p, "from"));
                return new(true, TimeSpan.Zero, MailOutcome.Forwarded);
            }

            await MarkAsync(mailId, $"manager HTTP {(int)resp.StatusCode}", ct);
            _log.LogWarning("[MAIL] {MsgId} → {Url} HTTP {Status}", msgId, url, (int)resp.StatusCode);
            return (int)resp.StatusCode == 404
                ? new(true, TimeSpan.Zero, MailOutcome.PhoneNotFound)
                : Retry(delivered, MailOutcome.ManagerFailed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await MarkAsync(mailId, Trunc($"manager: {ex.Message}", 500), ct);
            _log.LogWarning("[MAIL] {MsgId} → {Url} failed: {Err}", msgId, url, ex.Message);
            return Retry(delivered, MailOutcome.ManagerFailed);
        }
    }

    /// <summary>MailEventPayload של ה-Manager. ה-html לא עובר — רק ב-DB.</summary>
    private object EventBody(JsonObject p, string? mailId) => new Dictionary<string, object?>
    {
        ["msg_id"]      = Str(p, "msg_id"),
        ["mail_id"]     = mailId,
        ["from"]        = Str(p, "from") ?? "",
        ["from_name"]   = Str(p, "from_name") ?? "",
        ["to"]          = Str(p, "to") ?? "",
        ["subject"]     = Str(p, "subject") ?? "",
        ["text"]        = Trunc(Str(p, "text") ?? "", _opt.TextMax),
        ["received_at"] = Str(p, "received_at"),
    };

    private static double AgeSeconds(string? iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? (DateTimeOffset.UtcNow - t).TotalSeconds : 0;

    private static string? Str(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;
}