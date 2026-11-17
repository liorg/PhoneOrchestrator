using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

/// <summary>
/// What phone-hub (the docker on the NUC) talks to. Basic auth with HubUser /
/// HubPassword - AuthMiddleware lets nothing else into /api/hub/*.
///
///   POST /api/hub/device-update          track-devices: add / remove + SIMs
///   POST /api/hub/sms                    inbound SMS, idempotent by msg_id → forwarded to WhatsAppManager
///   POST /api/hub/pulls/claim            pending re-verifications of the connected serials
///   POST /api/hub/pulls/{id}/ack         old link removed → delete_old · none → ack
///   POST /api/hub/pulls/{id}/paircode    pair code from the phone's Manager (PairCodeProvisioner)
///   POST /api/hub/pulls/{id}/finish      success / failed → sim.verified + linked_name
///
/// update_from: from the body, else the caller's IP (real, via UseForwardedHeaders).
/// </summary>
[ApiController]
[Route("api/hub")]
public sealed class HubController : ControllerBase
{
    private readonly SupabaseRpc _rpc;
    private readonly PairCodeProvisioner _pairing;
    private readonly OrchestratorOptions _opt;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _cfg;
    private readonly ILogger<HubController> _log;

    public HubController(SupabaseRpc rpc, PairCodeProvisioner pairing, IOptions<OrchestratorOptions> opt,
                         IHttpClientFactory httpFactory, IConfiguration cfg,
                         ILogger<HubController> log)
    {
        _rpc = rpc;
        _pairing = pairing;
        _opt = opt.Value;
        _httpFactory = httpFactory;
        _cfg = cfg;
        _log = log;
    }

    [HttpPost("device-update")]
    public async Task<IActionResult> DeviceUpdate([FromBody] JsonObject body, CancellationToken ct)
    {
        var serial = Str(body, "device_id");
        if (string.IsNullOrWhiteSpace(serial))
            return BadRequest(new { ok = false, reason = "device_id required" });

        body["update_from"] = UpdateFrom(body);
        var res = await Call("rpc_device_update", new { p = body }, ct);
        _log.LogInformation("[HUB] device {Serial} {Status} from={From}",
            serial, Str(res, "status"), body["update_from"]?.ToString());
        return Json(res);
    }

    [HttpPost("sms")]
    public async Task<IActionResult> Sms([FromBody] JsonObject body, CancellationToken ct)
    {
        if (!Guid.TryParse(Str(body, "msg_id"), out _))
            return BadRequest(new { ok = false, reason = "msg_id must be a uuid" });

        var res = await Call("rpc_sms_inbound", new { p = body }, ct);
        if (Bool(res, "inserted"))
        {
            _log.LogInformation("[HUB] sms {Msg} phone={Phone} from={From}",
                Str(body, "msg_id"), Str(res, "phone_id"), Str(body, "from"));

            // שלב 5: SMS → WhatsAppManager → Spine → Worker.
            // ברקע: ה-hub מקבל תשובה מיד, כשל בהעברה לא מחזיר לו שגיאה.
            // idempotency כבר נשמר ב-rpc_sms_inbound (inserted=false בכפילות),
            // ולכן לכל SMS יוצא אירוע אחד בלבד.
            if (Guid.TryParse(Str(res, "phone_id"), out var phoneId))
            {
                var evt = BuildSmsEvent(body, res);
                _ = Task.Run(() => ForwardSmsAsync(phoneId, evt));
            }
            else
            {
                _log.LogWarning("[HUB] sms {Msg} inserted without phone_id — not forwarded", Str(body, "msg_id"));
            }
        }
        return Json(res);
    }

    // ── SMS → WhatsAppManager ────────────────────────────────────────────────
    // ה-Manager של הטלפון עצמו: phones.host_id → agent_hosts.ip_address, כמו ב-provisioner.
    // Orchestrator:ManagerPort          ברירת מחדל 5000 (HostAgent, כמו AGENT_PORT ב-backend)
    // Orchestrator:ManagerWebhookSecret זהה ל-AppSettings:WebhookSecret ב-Manager
    // Orchestrator:AgentToken           זהה ל-AGENT_TOKEN של ה-backend (X-Agent-Token)

    private static readonly int[] ForwardBackoffMs = { 0, 1000, 3000 };

    private static object BuildSmsEvent(JsonObject body, JsonElement res) => new
    {
        msg_id      = Str(body, "msg_id"),
        sms_id      = Str(res, "id") ?? Str(res, "sms_id"),
        // טבלת sms: body = הטקסט, target_phone = השולח (כמו ב-rpc_sms_thread)
        from        = Str(body, "from") ?? Str(body, "target_phone") ?? "",
        text        = Str(body, "body") ?? Str(body, "text") ?? Str(body, "message") ?? "",
        received_at = Str(body, "received_at") ?? Str(body, "delivery_date") ?? DateTimeOffset.UtcNow.ToString("o"),
        raw         = body,
    };

    private async Task ForwardSmsAsync(Guid phoneId, object evt)
    {
        string? baseUrl;
        try
        {
            baseUrl = await ResolveManagerUrlAsync(phoneId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError("[HUB] sms phone={Phone} host lookup failed: {Msg}", phoneId, ex.Message);
            return;
        }
        if (baseUrl == null) return;   // הסיבה כבר נרשמה ב-ResolveManagerUrlAsync

        var url    = $"{baseUrl.TrimEnd('/')}/api/webhook/sms-event/{phoneId}";
        var secret = _cfg["Orchestrator:ManagerWebhookSecret"] ?? "";
        // אותו header שה-backend (phones.py → _agent_headers) שולח ל-Manager
        var agentToken = _cfg["Orchestrator:AgentToken"] ?? "";

        foreach (var delay in ForwardBackoffMs)
        {
            if (delay > 0) await Task.Delay(delay);
            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(5);

                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(evt) };
                if (secret.Length > 0)     req.Headers.Add("X-Webhook-Secret", secret);
                if (agentToken.Length > 0) req.Headers.Add("X-Agent-Token", agentToken);

                using var r = await http.SendAsync(req);
                if (r.IsSuccessStatusCode)
                {
                    _log.LogInformation("[HUB] sms → manager phone={Phone} {Status}", phoneId, (int)r.StatusCode);
                    return;
                }

                // 4xx לא ישתפר בניסיון חוזר (secret / phone לא קיים)
                if ((int)r.StatusCode is >= 400 and < 500)
                {
                    _log.LogWarning("[HUB] sms → manager phone={Phone} rejected {Status}: {Body}",
                        phoneId, (int)r.StatusCode, await r.Content.ReadAsStringAsync());
                    return;
                }

                _log.LogWarning("[HUB] sms → manager phone={Phone} {Status}, retrying", phoneId, (int)r.StatusCode);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[HUB] sms → manager phone={Phone} failed: {Msg}", phoneId, ex.Message);
            }
        }

        _log.LogError("[HUB] sms → manager phone={Phone} gave up after {N} attempts", phoneId, ForwardBackoffMs.Length);
    }

    /// <summary>
    /// phones.host_id → agent_hosts.ip_address → http://ip:ManagerPort.
    /// null (עם log) כשאין host, אין IP, או IP של loopback — אותו כלל כמו ב-provisioner.
    /// </summary>
    private async Task<string?> ResolveManagerUrlAsync(Guid phoneId, CancellationToken ct)
    {
        var phones = await _rpc.SelectAsync("phones", $"id=eq.{phoneId}&select=host_id", ct);
        var hostId = phones.ValueKind == JsonValueKind.Array && phones.GetArrayLength() > 0
            ? Str(phones[0], "host_id") : null;
        if (string.IsNullOrWhiteSpace(hostId))
        {
            _log.LogWarning("[HUB] sms phone={Phone} has no host_id — not forwarded", phoneId);
            return null;
        }

        var hosts = await _rpc.SelectAsync("agent_hosts",
            $"id=eq.{hostId}&select=id,host_name,ip_address", ct);
        var ip = hosts.ValueKind == JsonValueKind.Array && hosts.GetArrayLength() > 0
            ? Str(hosts[0], "ip_address") : null;

        if (!IsValidAgentIp(ip))
        {
            _log.LogError("[HUB] sms phone={Phone} host={Host} ip={Ip} invalid/loopback — refusing",
                phoneId, hostId, ip ?? "(none)");
            return null;
        }

        var port = int.TryParse(_cfg["Orchestrator:ManagerPort"], out var p) && p > 0 ? p : 5000;
        return $"http://{ip}:{port}";
    }

    private static bool IsValidAgentIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip)
        && System.Net.IPAddress.TryParse(ip.Trim(), out var a)
        && !System.Net.IPAddress.IsLoopback(a)
        && !a.Equals(System.Net.IPAddress.Any)
        && !a.Equals(System.Net.IPAddress.IPv6Any);

    public sealed record ClaimRequest(string[]? Serials, string? Update_From);

    [HttpPost("pulls/claim")]
    public async Task<IActionResult> Claim([FromBody] ClaimRequest req, CancellationToken ct)
    {
        var serials = (req.Serials ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        var from = string.IsNullOrWhiteSpace(req.Update_From) ? RemoteIp() : req.Update_From!.Trim();

        // jsonb array - not unwrapped, a one-pull array is a valid answer
        var pulls = await _rpc.CallAsync("rpc_pull_claim", new { p_update_from = from, p_serials = serials }, ct);
        if (pulls.ValueKind == JsonValueKind.Array)
            foreach (var p in pulls.EnumerateArray())
                _log.LogInformation("[HUB] pull {Id} serial={Serial} slot={Slot} by={From}",
                    Str(p, "id"), Str(p, "serial"), p.TryGetProperty("slot", out var s) ? s.ToString() : "?", from);

        return Content($"{{\"pulls\":{(pulls.ValueKind == JsonValueKind.Array ? pulls.GetRawText() : "[]")}}}",
                       "application/json");
    }

    public sealed record AckRequest(bool Unlinked, string? Unlinked_Name, JsonElement? Result);

    [HttpPost("pulls/{id:guid}/ack")]
    public async Task<IActionResult> Ack(Guid id, [FromBody] AckRequest req, CancellationToken ct)
    {
        var res = await Call("rpc_pull_ack", new
        {
            p_id = id, p_unlinked = req.Unlinked, p_result = AsObject(req.Result), p_unlinked_name = req.Unlinked_Name
        }, ct);
        _log.LogInformation("[HUB] pull {Id} → {Status}", id, Str(res, "status") ?? Str(res, "reason"));
        return FromRpc(res);
    }

    [HttpPost("pulls/{id:guid}/paircode")]
    public async Task<IActionResult> PairCode(Guid id, CancellationToken ct)
    {
        var pull = await Call("rpc_pull_paircode", new { p_id = id }, ct);
        if (!Bool(pull, "ok"))
            return FromRpc(pull);

        // לבדיקה בלבד: קוד קבוע, רק כש-SelfTestEnabled
        if (_opt.SelfTestEnabled && !string.IsNullOrWhiteSpace(_opt.HubFakePairCode))
        {
            _log.LogWarning("[HUB] pull {Id} → FAKE pair code (HubFakePairCode)", id);
            return Ok(new { code = _opt.HubFakePairCode, pkg = Str(pull, "pkg"),
                            expires_at = Str(pull, "expires_at"), fake = true });
        }

        if (!Guid.TryParse(Str(pull, "phone_id"), out var phoneId))
            return Conflict(new { ok = false, reason = "no_phone" });

        // עד 10 שנ' לפני שה-pull פג, ולא יותר מ-110 שנ'
        var budget = TimeSpan.FromSeconds(110);
        if (DateTimeOffset.TryParse(Str(pull, "expires_at"), out var exp))
        {
            var left = exp - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
            if (left < budget) budget = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        var r = await _pairing.RequestAsync(phoneId, budget, ct);
        if (!r.Ok)
        {
            _log.LogWarning("[HUB] pull {Id} paircode ✗ {Reason} {Detail}", id, r.Reason, r.Detail);
            return StatusCode(StatusCodes.Status502BadGateway, new { ok = false, reason = r.Reason, detail = r.Detail });
        }

        // name: null — ה-hub משתמש ב-Grossman-<ספרות>. ב-Baileys צריך browser זהה כדי ש-wa_unlink ימצא אותו
        return Ok(new { code = r.Code, pkg = Str(pull, "pkg"), expires_at = Str(pull, "expires_at"),
                        name = (string?)null });
    }

    public sealed record FinishRequest(bool Ok, string? Error, string? Linked_Name, JsonElement? Result);

    [HttpPost("pulls/{id:guid}/finish")]
    public async Task<IActionResult> Finish(Guid id, [FromBody] FinishRequest req, CancellationToken ct)
    {
        var res = await Call("rpc_pull_finish", new
        {
            p_id = id, p_ok = req.Ok, p_error = Trim(req.Error, 1000), p_result = AsObject(req.Result),
            p_linked_name = req.Linked_Name
        }, ct);
        _log.LogInformation("[HUB] pull {Id} → {Status} {Error}", id,
            Str(res, "status") ?? Str(res, "reason"), req.Ok ? "" : req.Error);
        return FromRpc(res);
    }

    // ───────────────────────────────────────────────────────────── helpers ──

    private async Task<JsonElement> Call(string fn, object args, CancellationToken ct) =>
        SupabaseRpc.Unwrap(await _rpc.CallAsync(fn, args, ct));

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UpdateFrom(JsonObject body)
    {
        var v = Str(body, "update_from");
        return string.IsNullOrWhiteSpace(v) ? RemoteIp() : v.Trim();
    }

    private static object? AsObject(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Object } ? e : null;

    private static string? Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];

    private static string? Str(JsonObject o, string p) =>
        o.TryGetPropertyValue(p, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Bool(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

    private IActionResult Json(JsonElement res) => Content(res.GetRawText(), "application/json");

    /// <summary>{ok:false, reason} → 409 with the same body.</summary>
    private IActionResult FromRpc(JsonElement res) => new ContentResult
    {
        StatusCode  = Bool(res, "ok") ? StatusCodes.Status200OK : StatusCodes.Status409Conflict,
        ContentType = "application/json",
        Content     = res.GetRawText()
    };
}