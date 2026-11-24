using Microsoft.AspNetCore.Mvc;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

/// <summary>
/// Dashboard: inbound SMS (table sms) - debug of the whole chain:
/// phone → hub → whqueue → NATS → SmsRelay → Manager. forward_* is what SmsRelay
/// decided; empty = came through the legacy POST /api/hub/sms.
/// Same auth as the rest of the dashboard (cookie / AuthUser basic) - NOT the hub user.
/// </summary>
[ApiController]
[Route("api/sms")]
public sealed class SmsController : ControllerBase
{
    private readonly SupabaseRpc _rpc;

    public SmsController(SupabaseRpc rpc) => _rpc = rpc;

    /// <summary>Newest first. status: forwarded | rejected | retrying | gave_up | too_old | no_phone | no_manager | legacy.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int? pageSize = null,
        [FromQuery] string? device = null, [FromQuery] Guid? simId = null, [FromQuery] Guid? phoneId = null,
        [FromQuery] string? q = null, [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_list_sms", new
        {
            p_page = page, p_page_size = pageSize, p_device = device, p_sim_id = simId,
            p_phone_id = phoneId, p_search = q, p_status = status
        }, ct));
        return Content(el.GetRawText(), "application/json");
    }

    /// <summary>One SMS with everything, raw included.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> One(Guid id, CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_sms", new { p_id = id }, ct));
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object)
            return NotFound(new { ok = false, reason = "not_found" });
        return Content(el.GetRawText(), "application/json");
    }
}
