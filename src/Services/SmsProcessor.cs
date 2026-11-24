using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

public enum SmsOutcome { Forwarded, Duplicate, BadPayload, NoPhone, NoManager, Rejected, TooOld, StoreFailed, LookupFailed, ManagerFailed, GaveUp }

public sealed record SmsDecision(bool Ack, TimeSpan Delay, SmsOutcome Outcome);

/// <summary>
/// One sms.in message → decision. No NATS here (testable).
/// Same flow as HubController.Sms (which stays as-is for the legacy path):
///   rpc_sms_inbound (idempotent by msg_id) → phones.host_id → agent_hosts.ip_address
///   → POST http://ip:ManagerPort/api/webhook/sms-event/{phoneId}
///
/// Difference: one HTTP attempt per delivery, retries = NATS Nak(backoff) instead of
/// in-process Task.Run. Redelivery after a failed forward gets inserted=false from the
/// RPC, so for delivered>1 we forward anyway (needs phone_id in the duplicate response).
/// </summary>
public sealed class SmsProcessor
{
    private readonly SupabaseRpc _rpc;
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly SmsRelayOptions _opt;
    private readonly ILogger _log;

    public SmsProcessor(SupabaseRpc rpc, HttpClient http, IConfiguration cfg, SmsRelayOptions opt, ILogger log)
    {
        _rpc  = rpc;
        _http = http;
        _cfg  = cfg;
        _opt  = opt;
        _log  = log;
    }

    // נקבעים ב-DecideAsync, נכתבים ל-sms.forward_* (מסך SMS ב-PhoneOrchestrator)
    private string? _msgId;
    private string? _error;

    public async Task<SmsDecision> ProcessAsync(byte[] data, int delivered, DateTimeOffset? enqueuedAt, CancellationToken ct)
    {
        _msgId = null;
        _error = null;
        var d = await DecideAsync(data, delivered, enqueuedAt, ct);
        await RecordAsync(d, delivered, ct);
        return d;
    }

    /// <summary>
    /// sms.forward_status / error / attempts. Best effort: a failed write never changes
    /// the decision (the SMS is already stored; this is only for the dashboard).
    /// Not written: BadPayload (no row), Duplicate (the first delivery already wrote it),
    /// StoreFailed (no row yet).
    /// </summary>
    private async Task RecordAsync(SmsDecision d, int delivered, CancellationToken ct)
    {
        var status = d.Outcome switch
        {
            SmsOutcome.Forwarded     => "forwarded",
            SmsOutcome.Rejected      => "rejected",
            SmsOutcome.GaveUp        => "gave_up",
            SmsOutcome.TooOld        => "too_old",
            SmsOutcome.NoPhone       => "no_phone",
            SmsOutcome.NoManager     => "no_manager",
            SmsOutcome.ManagerFailed => "retrying",
            SmsOutcome.LookupFailed  => "retrying",
            _                        => null,
        };
        if (status == null || !Guid.TryParse(_msgId, out var id)) return;
        try
        {
            await _rpc.CallAsync("rpc_sms_forwarded",
                new { p_msg_id = id, p_status = status, p_error = _error, p_attempts = delivered }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("[SMS] {Msg} forward status not saved ({Status}): {Err}", _msgId, status, ex.Message);
        }
    }

    private async Task<SmsDecision> DecideAsync(byte[] data, int delivered, DateTimeOffset? enqueuedAt, CancellationToken ct)
    {
        JsonObject? body;
        try { body = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { body = null; }
        if (body is null)
        {
            _log.LogError("[SMS] bad payload ({Len} bytes) — dropped", data.Length);
            return Done(SmsOutcome.BadPayload);
        }

        var msgId = Str(body, "msg_id");
        if (!Guid.TryParse(msgId, out _))
        {
            _log.LogError("[SMS] msg_id {Msg} is not a uuid — dropped", msgId ?? "(none)");
            return Done(SmsOutcome.BadPayload);
        }
        _msgId = msgId;

        // 1. store (idempotent)
        JsonElement res;
        try
        {
            res = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_sms_inbound", new { p = body }, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("[SMS] {Msg} rpc_sms_inbound failed: {Err}", msgId, ex.Message);
            return Retry(delivered, SmsOutcome.StoreFailed, msgId!);
        }

        var inserted = Bool(res, "inserted");
        if (!inserted && delivered <= 1)
        {
            _log.LogInformation("[SMS] {Msg} duplicate — already stored", msgId);
            return Done(SmsOutcome.Duplicate);
        }

        if (!Guid.TryParse(Str(res, "phone_id"), out var phoneId))
        {
            _log.LogWarning("[SMS] {Msg} {What} without phone_id — not forwarded",
                msgId, inserted ? "inserted" : "redelivered");
            _error = "no phone for device/slot/source_phone";
            return Done(SmsOutcome.NoPhone);
        }

        _log.LogInformation("[SMS] sms {Msg} phone={Phone} from={From} delivered={N}",
            msgId, phoneId, Str(body, "from"), delivered);

        // 2. stale → stored, not forwarded
        if (_opt.ForwardMaxAgeSec > 0 && enqueuedAt is { } at
            && DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(_opt.ForwardMaxAgeSec))
        {
            _log.LogWarning("[SMS] {Msg} phone={Phone} older than {S}s — stored, not forwarded",
                msgId, phoneId, _opt.ForwardMaxAgeSec);
            _error = $"older than {_opt.ForwardMaxAgeSec}s";
            return Done(SmsOutcome.TooOld);
        }

        // 3. Manager of the phone
        string? baseUrl;
        try
        {
            baseUrl = await ResolveManagerUrlAsync(phoneId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("[SMS] {Msg} phone={Phone} host lookup failed: {Err}", msgId, phoneId, ex.Message);
            _error = "host lookup: " + ex.Message;
            return Retry(delivered, SmsOutcome.LookupFailed, msgId!);
        }
        if (baseUrl == null)   // reason already logged
        {
            _error = "no host_id / invalid agent ip";
            return Done(SmsOutcome.NoManager);
        }

        // 4. forward — one attempt, NATS does the retries
        var url        = $"{baseUrl.TrimEnd('/')}/api/webhook/sms-event/{phoneId}";
        var secret     = ManagerWebhookSecret;
        var agentToken = AgentToken;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(BuildSmsEvent(body, res)) };
            if (secret.Length > 0)     req.Headers.Add("X-Webhook-Secret", secret);
            if (agentToken.Length > 0) req.Headers.Add("X-Agent-Token", agentToken);

            using var r = await _http.SendAsync(req, ct);
            if (r.IsSuccessStatusCode)
            {
                _log.LogInformation("[SMS] {Msg} → manager phone={Phone} {Status}", msgId, phoneId, (int)r.StatusCode);
                return Done(SmsOutcome.Forwarded);
            }
            if ((int)r.StatusCode is >= 400 and < 500)   // secret / phone missing — retry won't help
            {
                var rb = await r.Content.ReadAsStringAsync(ct);
                _log.LogWarning("[SMS] {Msg} → manager phone={Phone} rejected {Status}: {Body}",
                    msgId, phoneId, (int)r.StatusCode, rb);
                _error = $"{url} → {(int)r.StatusCode} {rb}";
                return Done(SmsOutcome.Rejected);
            }
            _log.LogWarning("[SMS] {Msg} → manager phone={Phone} {Status}", msgId, phoneId, (int)r.StatusCode);
            _error = $"{url} → {(int)r.StatusCode}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("[SMS] {Msg} → manager phone={Phone} failed: {Err}", msgId, phoneId, ex.Message);
            _error = $"{url} → {ex.Message}";
        }
        return Retry(delivered, SmsOutcome.ManagerFailed, msgId!);
    }

    // ───────────────────────────────────────────────────────────── helpers ──

    private static SmsDecision Done(SmsOutcome o) => new(true, TimeSpan.Zero, o);

    private SmsDecision Retry(int delivered, SmsOutcome o, string msgId)
    {
        if (delivered >= _opt.MaxDeliver)
        {
            _log.LogError("[SMS] {Msg} gave up after {N} deliveries ({Outcome})", msgId, delivered, o);
            _error = $"gave up after {delivered} ({o}): {_error}";
            return Done(SmsOutcome.GaveUp);
        }
        var sec = Math.Min(300, 5 * (1 << Math.Clamp(delivered - 1, 0, 6)));   // 5,10,20,40,80,160,300…
        return new SmsDecision(false, TimeSpan.FromSeconds(sec), o);
    }

    // Same shape as HubController.BuildSmsEvent — the Manager sees no difference.
    private static object BuildSmsEvent(JsonObject body, JsonElement res) => new
    {
        msg_id      = Str(body, "msg_id"),
        sms_id      = Str(res, "id") ?? Str(res, "sms_id"),
        from        = Str(body, "from") ?? Str(body, "target_phone") ?? "",
        text        = Str(body, "body") ?? Str(body, "text") ?? Str(body, "message") ?? "",
        received_at = Str(body, "received_at") ?? Str(body, "delivery_date") ?? DateTimeOffset.UtcNow.ToString("o"),
        raw         = body,
    };

    /// <summary>phones.host_id → agent_hosts.ip_address → http://ip:ManagerPort (copy of HubController's rule).</summary>
    private async Task<string?> ResolveManagerUrlAsync(Guid phoneId, CancellationToken ct)
    {
        var phones = await _rpc.SelectAsync("phones", $"id=eq.{phoneId}&select=host_id", ct);
        var hostId = phones.ValueKind == JsonValueKind.Array && phones.GetArrayLength() > 0
            ? Str(phones[0], "host_id") : null;
        if (string.IsNullOrWhiteSpace(hostId))
        {
            _log.LogWarning("[SMS] phone={Phone} has no host_id — not forwarded", phoneId);
            return null;
        }

        var hosts = await _rpc.SelectAsync("agent_hosts", $"id=eq.{hostId}&select=id,host_name,ip_address", ct);
        var ip = hosts.ValueKind == JsonValueKind.Array && hosts.GetArrayLength() > 0
            ? Str(hosts[0], "ip_address") : null;

        if (!IsValidAgentIp(ip))
        {
            _log.LogError("[SMS] phone={Phone} host={Host} ip={Ip} invalid/loopback — refusing",
                phoneId, hostId, ip ?? "(none)");
            return null;
        }

        return $"http://{ip}:{ManagerPort}";
    }

    // Sms:* first, then the Orchestrator:* keys HubController uses.
    public string ManagerWebhookSecret =>
        NonEmpty(_opt.ManagerWebhookSecret) ?? NonEmpty(_cfg["Orchestrator:ManagerWebhookSecret"]) ?? "";

    public string AgentToken =>
        NonEmpty(_opt.AgentToken) ?? NonEmpty(_cfg["Orchestrator:AgentToken"]) ?? "";

    public int ManagerPort =>
        _opt.ManagerPort is > 0 ? _opt.ManagerPort.Value
        : int.TryParse(_cfg["Orchestrator:ManagerPort"], out var p) && p > 0 ? p : 5000;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool IsValidAgentIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip)
        && System.Net.IPAddress.TryParse(ip.Trim(), out var a)
        && !System.Net.IPAddress.IsLoopback(a)
        && !a.Equals(System.Net.IPAddress.Any)
        && !a.Equals(System.Net.IPAddress.IPv6Any);

    private static string? Str(JsonObject o, string p) =>
        o.TryGetPropertyValue(p, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Bool(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
}
