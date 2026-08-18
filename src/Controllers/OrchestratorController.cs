using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

[ApiController]
[Route("api/orchestrator")]
public sealed class OrchestratorController : ControllerBase
{
    private readonly SupabaseRpc _rpc;
    private readonly ScanState _state;
    private readonly OrchestratorOptions _opt;

    public OrchestratorController(SupabaseRpc rpc, ScanState state, IOptions<OrchestratorOptions> opt)
    {
        _rpc   = rpc;
        _state = state;
        _opt   = opt.Value;
    }

    /// <summary>What the loop is doing right now.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        marker              = BuildInfo.Marker,
        version             = BuildInfo.Version,
        autoDrain           = _opt.AutoDrain,
        env                 = _opt.Env ?? "(from bot_config)",
        scanIntervalSeconds = _opt.ScanIntervalSeconds,
        staleAfterSeconds   = _opt.StaleAfterSeconds,
        failuresBeforeDrain = _opt.FailuresBeforeDrain,
        lastScanUtc         = _state.LastScanUtc,
        scanCount           = _state.ScanCount,
        lastError           = _state.LastError,
        drains              = _state.RecentDrains()
    });

    /// <summary>Preview the target host the gates would choose. Read-only.</summary>
    [HttpGet("pick-host")]
    public async Task<IActionResult> PickHost(
        [FromQuery] string? env = null,
        [FromQuery] Guid? exclude = null,
        CancellationToken ct = default)
    {
        var el = await _rpc.CallAsync("rpc_orch_pick_host",
            new { p_env = env ?? _opt.Env, p_exclude_host = exclude }, ct);

        var one = SupabaseRpc.Unwrap(el);
        if (one.ValueKind is System.Text.Json.JsonValueKind.Undefined
                          or System.Text.Json.JsonValueKind.Null)
            return Ok(new { ok = false, error = "NO_ELIGIBLE_HOST" });

        return Content(one.GetRawText(), "application/json");
    }

    /// <summary>Manual drain of one host.</summary>
    [HttpPost("hosts/{hostId:guid}/drain")]
    public async Task<IActionResult> Drain(Guid hostId, [FromQuery] string? reason = null,
        CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_drain_host", new
        {
            p_host_id = hostId,
            p_env     = _opt.Env,
            p_reason  = reason ?? "MANUAL_DRAIN"
        }, ct));

        return Content(el.GetRawText(), "application/json");
    }

    /// <summary>Manual migration of a single phone.</summary>
    [HttpPost("phones/{phoneId:guid}/migrate")]
    public async Task<IActionResult> Migrate(Guid phoneId, [FromQuery] string? reason = null,
        CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_migrate_phone", new
        {
            p_phone_id = phoneId,
            p_env      = _opt.Env,
            p_reason   = reason ?? "MANUAL_MIGRATE"
        }, ct));

        return Content(el.GetRawText(), "application/json");
    }
}
