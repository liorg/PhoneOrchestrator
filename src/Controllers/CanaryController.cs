using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

/// <summary>
/// Canary report + control under /api/canary (behind AuthMiddleware) - this
/// side runs on michal. The runner (same image, Mode=canary) runs on
/// botinbound and owns the
/// prober's callback under /canary/inbound (outside it,
/// guarded by the path token instead - the container sends no credentials).
/// </summary>
[ApiController]
public sealed class CanaryController : ControllerBase
{
    private readonly SupabaseRpc _rpc;
    private readonly CanaryStore _store;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<CanaryController> _log;

    public CanaryController(
        SupabaseRpc rpc, CanaryStore store,
        IOptions<OrchestratorOptions> opt, ILogger<CanaryController> log)
    {
        _rpc   = rpc;
        _store = store;
        _opt   = opt.Value;
        _log   = log;
    }

    // ══════════════════════════════════════════════════════ report ══════

    [HttpGet("api/canary/runs")]
    public async Task<IActionResult> Runs(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1, [FromQuery] int? pageSize = null, CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_canary_list_runs", new
        {
            p_from = from, p_to = to, p_page = page, p_page_size = pageSize
        }, ct));
        return Content(el.GetRawText(), "application/json");
    }

    [HttpGet("api/canary/runs/{runId:guid}/checks")]
    public async Task<IActionResult> Checks(
        Guid runId, [FromQuery] string? status = null,
        [FromQuery] int page = 1, [FromQuery] int? pageSize = null, CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_canary_list_checks", new
        {
            p_run_id = runId, p_status = status, p_page = page, p_page_size = pageSize
        }, ct));
        return Content(el.GetRawText(), "application/json");
    }

    // ═════════════════════════════════════════════════════ control ══════
    // The runner lives on botinbound and is never called directly. The
    // dashboard reads and writes the canary_control row; the runner picks
    // changes up within 30s.

    [HttpGet("api/canary/control")]
    public async Task<IActionResult> Control(CancellationToken ct)
    {
        var rows = await _rpc.SelectAsync("canary_control", "select=*&id=eq.1&limit=1", ct);
        foreach (var r in rows.EnumerateArray())
        {
            var seen = r.TryGetProperty("runner_seen_at", out var v) && v.ValueKind == JsonValueKind.String
                       && DateTime.TryParse(v.GetString(), null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : (DateTime?)null;

            return Ok(new
            {
                control     = r,
                runnerAlive = seen.HasValue && DateTime.UtcNow - seen.Value < TimeSpan.FromSeconds(90)
            });
        }
        return NotFound(new { ok = false, error = "canary_control has no row id=1 - run canary_v2.sql" });
    }

    public sealed record ControlIn(
        bool? Enabled, int? IntervalSec, int? TimeoutSec,
        string? ActiveFrom, string? ActiveTo, bool? CheckCloudApi, bool? Notify);

    /// <summary>On/off and schedule. Only the fields sent are changed.</summary>
    [HttpPatch("api/canary/control")]
    public async Task<IActionResult> UpdateControl([FromBody] ControlIn body, CancellationToken ct)
    {
        var patch = new Dictionary<string, object?> { ["updated_at"] = DateTime.UtcNow };
        if (body.Enabled.HasValue)       patch["enabled"]        = body.Enabled.Value;
        if (body.IntervalSec.HasValue)   patch["interval_sec"]   = Math.Max(60, body.IntervalSec.Value);
        if (body.TimeoutSec.HasValue)    patch["timeout_sec"]    = Math.Max(15, body.TimeoutSec.Value);
        if (body.CheckCloudApi.HasValue) patch["check_cloudapi"] = body.CheckCloudApi.Value;
        if (body.Notify.HasValue)        patch["notify"]         = body.Notify.Value;
        // "" clears the window (24h); otherwise HH:mm
        if (body.ActiveFrom != null) patch["active_from"] = body.ActiveFrom == "" ? null : body.ActiveFrom;
        if (body.ActiveTo   != null) patch["active_to"]   = body.ActiveTo   == "" ? null : body.ActiveTo;

        await _rpc.PatchRawAsync("canary_control", "id=eq.1",
            JsonSerializer.Serialize(patch), ct);

        _log.LogWarning("[CANARY] control updated: {Patch}", JsonSerializer.Serialize(patch));
        return Ok(new { ok = true });
    }

    /// <summary>One sweep now - even when disabled or outside the window.</summary>
    [HttpPost("api/canary/run")]
    public async Task<IActionResult> RunNow(CancellationToken ct)
    {
        await _rpc.PatchAsync("canary_control", "id=eq.1", new { run_now_at = DateTime.UtcNow }, ct);
        return Ok(new { ok = true, note = "the runner picks it up within 30s" });
    }

    // ═════════════════════════════════════════════════════ inbound ══════

    /// <summary>
    /// The prober's only webhook (MANAGER_WEBHOOK_URL, seeded into its
    /// Redis set). Gets every container event - message, authenticated,
    /// disconnected, outgoing echo; only an inbound ACK changes anything.
    /// Always 200 once the token checks out: a non-2xx makes the container
    /// retry, and there is nothing a retry could fix.
    /// </summary>
    [HttpPost("canary/inbound/{token}")]
    public Task<IActionResult> Inbound(string token, CancellationToken ct) => Receive(token, ct);

    private async Task<IActionResult> Receive(string token, CancellationToken ct)
    {
        if (!TokenOk(token)) return NotFound();

        JsonElement body;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            body = doc.RootElement.Clone();
        }
        catch
        {
            return Ok(new { received = true, matched = false, note = "not json" });
        }

        try
        {
            var r = await _store.HandleInboundAsync(body, ct);
            return Ok(new { received = true, matched = r.Matched, r.Verdict, r.Note });
        }
        catch (Exception ex)
        {
            // DB hiccup: let the container retry this one.
            _log.LogError(ex, "[CANARY] Inbound handling failed");
            return StatusCode(503, new { received = false });
        }
    }

    private bool TokenOk(string token)
    {
        var expected = _opt.CanaryInboundToken;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }
}
