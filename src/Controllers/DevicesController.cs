using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

/// <summary>
/// Dashboard: physical (rented) phones. Device list → SIM drill-down, with
/// re-verify (device_pull) and release-from-customer (wipe → system user).
/// Same auth as the rest of the dashboard (cookie / AuthUser basic) - NOT the hub user.
/// </summary>
[ApiController]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase
{
    // Templates that survive the wipe - the seed + the provider catalog one.
    // Same list RentDevice.py passes on rent start/end.
    private static readonly string[] KeepTemplates = { "check_contact", "hello_world" };

    private readonly SupabaseRpc _rpc;
    private readonly ILogger<DevicesController> _log;

    public DevicesController(SupabaseRpc rpc, ILogger<DevicesController> log)
    {
        _rpc = rpc;
        _log = log;
    }

    /// <summary>Paged device list: USB status, adb state, docker IP, SIM / verified / rented counts.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_list_devices",
            new { p_page = page, p_page_size = pageSize }, ct));
        return Content(el.GetRawText(), "application/json");
    }

    /// <summary>Drill-down: the SIMs of one device, with owner, active rent and last pull.</summary>
    [HttpGet("{deviceId:guid}/sims")]
    public async Task<IActionResult> Sims(Guid deviceId, CancellationToken ct = default)
    {
        // Returns a jsonb array - not unwrapped, a one-SIM array is a valid answer.
        var el = await _rpc.CallAsync("rpc_orch_list_device_sims", new { p_device_id = deviceId }, ct);
        return Content(el.GetRawText(), "application/json");
    }

    /// <summary>
    /// Re-verify: opens a device_pull for the SIM's phone. The docker unlinks the
    /// old device, asks for a pair code and links again. A pending pull already
    /// open for the SIM is left as is (created = false).
    /// </summary>
    [HttpPost("sims/{simId:guid}/reverify")]
    public async Task<IActionResult> Reverify(Guid simId, CancellationToken ct = default)
    {
        var phoneId = await PhoneOfSim(simId, ct);
        if (phoneId is null)
            return Conflict(new { ok = false, reason = "no_phone" });

        var res = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_pull_create", new { p_phone_id = phoneId }, ct));
        _log.LogInformation("[DEVICES] reverify sim={Sim} phone={Phone} -> {Res}", simId, phoneId, res.GetRawText());
        return FromRpc(res);
    }

    /// <summary>
    /// Release from the customer: ends the active rent (end_reason = admin), or
    /// for a customer phone without a rent, wipes it and hands it to the system user.
    /// </summary>
    [HttpPost("sims/{simId:guid}/release")]
    public async Task<IActionResult> Release(Guid simId, CancellationToken ct = default)
    {
        var res = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_release_sim",
            new { p_sim_id = simId, p_keep_templates = KeepTemplates }, ct));
        _log.LogWarning("[DEVICES] release sim={Sim} -> {Res}", simId, res.GetRawText());
        return FromRpc(res);
    }

    private async Task<Guid?> PhoneOfSim(Guid simId, CancellationToken ct)
    {
        var rows = await _rpc.SelectAsync("phones", $"select=id&sim_id=eq.{simId}&limit=1", ct);
        if (rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0
            && Guid.TryParse(rows[0].GetProperty("id").GetString(), out var id))
            return id;
        return null;
    }

    /// <summary>RPC answered {ok:false, reason} → 409 with the same body, so the page can show the reason.</summary>
    private IActionResult FromRpc(JsonElement res)
    {
        var ok = res.ValueKind == JsonValueKind.Object
                 && res.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.True;
        return new ContentResult
        {
            StatusCode  = ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict,
            ContentType = "application/json",
            Content     = res.GetRawText()
        };
    }
}
