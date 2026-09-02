using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

/// <summary>
/// Everything the two canary screens need: the run report and the prober's
/// own provisioning.
///
/// Sits under /api/ so AuthMiddleware guards it. The Worker never calls
/// anything here - its endpoints live in SpineCompatController and are
/// exempt from auth but blocked at nginx.
/// </summary>
[ApiController]
[Route("api/canary")]
public sealed class CanaryController : ControllerBase
{
    private readonly SupabaseRpc _rpc;
    private readonly ILogger<CanaryController> _log;

    public CanaryController(SupabaseRpc rpc, ILogger<CanaryController> log)
    {
        _rpc = rpc;
        _log = log;
    }

    // ══════════════════════════════════════════════════════ report ══════

    /// <summary>Sweeps in a date range. Null dates default to the last 7 days.</summary>
    [HttpGet("runs")]
    public async Task<IActionResult> Runs(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_canary_list_runs", new
        {
            p_from      = from,
            p_to        = to,
            p_page      = page,
            p_page_size = pageSize
        }, ct));

        return Content(el.GetRawText(), "application/json");
    }

    /// <summary>Per-phone verdicts inside one sweep. status filters to failures.</summary>
    [HttpGet("runs/{runId:guid}/checks")]
    public async Task<IActionResult> Checks(
        Guid runId,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_canary_list_checks", new
        {
            p_run_id    = runId,
            p_status    = status,
            p_page      = page,
            p_page_size = pageSize
        }, ct));

        return Content(el.GetRawText(), "application/json");
    }

    /// <summary>The sub-event trail under one check. Unpaged - it is short.</summary>
    [HttpGet("checks/{checkId:guid}/events")]
    public async Task<IActionResult> Events(Guid checkId, CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_canary_list_events", new
        {
            p_check_id = checkId
        }, ct));

        return Content(el.GetRawText(), "application/json");
    }

    // ═════════════════════════════════════════════════ provisioning ══════

    /// <summary>The prober currently in CONNECT, plus recent history.</summary>
    [HttpGet("prober")]
    public async Task<IActionResult> Prober(CancellationToken ct = default)
    {
        var rows = await _rpc.SelectAsync(
            "heartbeatphones",
            "select=*&order=created_at.desc&limit=20", ct);

        JsonElement? active = null;
        foreach (var r in rows.EnumerateArray())
        {
            if (r.TryGetProperty("status", out var s) &&
                string.Equals(s.GetString(), "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                active = r;
                break;
            }
        }

        return Ok(new
        {
            active  = active,
            history = JsonSerializer.Deserialize<List<JsonElement>>(rows.GetRawText())
        });
    }

    public sealed record ProberIn(
        string PhoneNumber,
        string? Uid,
        string IdentificationType);

    /// <summary>
    /// Register a new prober. Any existing CONNECT row is logged off first:
    /// two live probers would each register a webhook on the container, and
    /// every incoming message would fan out to both.
    /// </summary>
    [HttpPost("prober")]
    public async Task<IActionResult> CreateProber([FromBody] ProberIn body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body.PhoneNumber))
            return BadRequest(new { ok = false, error = "phone_number is required" });
        if (string.IsNullOrWhiteSpace(body.IdentificationType))
            return BadRequest(new { ok = false, error = "identification_type is required" });

        await _rpc.PatchAsync("heartbeatphones", "status=eq.CONNECT",
            new { status = "LOGOFF", updated_at = DateTime.UtcNow }, ct);

        var created = await _rpc.InsertAsync("heartbeatphones", new
        {
            status              = "CONNECT",
            phone_number        = body.PhoneNumber.Trim(),
            uid                 = body.Uid?.Trim(),
            identification_type = body.IdentificationType.Trim()
        }, ct);

        _log.LogWarning("[CANARY] New prober registered: {Number}", body.PhoneNumber);
        return Content(created.GetRawText(), "application/json");
    }

    /// <summary>Edit identification or number without replacing the prober.</summary>
    [HttpPut("prober/{id:guid}")]
    public async Task<IActionResult> UpdateProber(
        Guid id, [FromBody] ProberIn body, CancellationToken ct = default)
    {
        await _rpc.PatchAsync("heartbeatphones", $"id=eq.{id}", new
        {
            phone_number        = body.PhoneNumber?.Trim(),
            uid                 = body.Uid?.Trim(),
            identification_type = body.IdentificationType?.Trim(),
            updated_at          = DateTime.UtcNow
        }, ct);

        return Ok(new { ok = true });
    }

    [HttpPost("prober/{id:guid}/logoff")]
    public async Task<IActionResult> Logoff(Guid id, CancellationToken ct = default)
    {
        await _rpc.PatchAsync("heartbeatphones", $"id=eq.{id}",
            new { status = "LOGOFF", updated_at = DateTime.UtcNow }, ct);

        _log.LogWarning("[CANARY] Prober {Id} logged off", id);
        return Ok(new { ok = true });
    }

    /// <summary>Make a previously logged-off prober live again.</summary>
    [HttpPost("prober/{id:guid}/switch")]
    public async Task<IActionResult> Switch(Guid id, CancellationToken ct = default)
    {
        await _rpc.PatchAsync("heartbeatphones", "status=eq.CONNECT",
            new { status = "LOGOFF", updated_at = DateTime.UtcNow }, ct);

        await _rpc.PatchAsync("heartbeatphones", $"id=eq.{id}",
            new { status = "CONNECT", updated_at = DateTime.UtcNow }, ct);

        _log.LogWarning("[CANARY] Switched active prober to {Id}", id);
        return Ok(new { ok = true });
    }

    public sealed record ScenarioIn(JsonElement Scenario);

    /// <summary>
    /// Upload one of the two handshake scenarios. Stored as-is: the Worker's
    /// IcrConverter reads the camelCase export shape verbatim, so the file
    /// exported from the Designer needs no transformation.
    ///
    /// kind is "send" (scheduler, runs on the prober) or "check" (trigger,
    /// written onto every monitored phone).
    /// </summary>
    [HttpPut("prober/{id:guid}/scenario/{kind}")]
    public async Task<IActionResult> UploadScenario(
        Guid id, string kind, [FromBody] ScenarioIn body, CancellationToken ct = default)
    {
        var column = kind.ToLowerInvariant() switch
        {
            "send"  => "send_scenario",
            "check" => "check_scenario",
            _       => null
        };

        if (column == null)
            return BadRequest(new { ok = false, error = "kind must be send or check" });

        var expected = column == "send_scenario" ? "scheduler" : "trigger";
        var actual   = body.Scenario.TryGetProperty("eventType", out var et) ? et.GetString() : null;

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new
            {
                ok = false,
                error = $"eventType must be '{expected}' for kind '{kind}', got '{actual ?? "missing"}'"
            });

        if (!body.Scenario.TryGetProperty("canvas", out var canvas)
            || canvas.ValueKind != JsonValueKind.Array
            || canvas.GetArrayLength() == 0)
            return BadRequest(new { ok = false, error = "canvas is missing or empty" });

        var patch = new Dictionary<string, object?>
        {
            [column]     = JsonSerializer.Deserialize<object>(body.Scenario.GetRawText()),
            ["updated_at"] = DateTime.UtcNow
        };

        await _rpc.PatchAsync("heartbeatphones", $"id=eq.{id}", patch, ct);

        _log.LogWarning("[CANARY] {Column} updated on prober {Id} ({Steps} steps)",
            column, id, canvas.GetArrayLength());

        return Ok(new { ok = true, column, steps = canvas.GetArrayLength() });
    }
}