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
///   POST /api/hub/sms                    inbound SMS, idempotent by msg_id
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
    private readonly ILogger<HubController> _log;

    public HubController(SupabaseRpc rpc, PairCodeProvisioner pairing, IOptions<OrchestratorOptions> opt,
                         ILogger<HubController> log)
    {
        _rpc = rpc;
        _pairing = pairing;
        _opt = opt.Value;
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
            // TODO (שלב 5): להעביר ל-WhatsAppManager → Worker כאירוע "sms received"
        }
        return Json(res);
    }

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
