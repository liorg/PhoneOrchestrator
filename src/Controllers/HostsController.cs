using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

[ApiController]
[Route("api/hosts")]
public sealed class HostsController : ControllerBase
{
    private readonly SupabaseRpc _rpc;
    private readonly ScanState _state;

    public HostsController(SupabaseRpc rpc, ScanState state)
    {
        _rpc   = rpc;
        _state = state;
    }

    /// <summary>Dashboard: paged host list, DB view merged with the live probe.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var el   = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_list_hosts",
                       new { p_page = page, p_page_size = pageSize }, ct));
        var node = JsonSerializer.Deserialize<JsonElement>(el.GetRawText());

        var items = new List<Dictionary<string, object?>>();
        if (node.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in arr.EnumerateArray())
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.GetRawText())
                           ?? new Dictionary<string, object?>();

                if (row.TryGetProperty("id", out var idEl) &&
                    Guid.TryParse(idEl.GetString(), out var id))
                {
                    var snap = _state.Get(id);
                    dict["probe"] = snap is null ? null : new
                    {
                        reachable            = snap.Reachable,
                        statusCode           = snap.StatusCode,
                        error                = snap.Error,
                        elapsedMs            = snap.ElapsedMs,
                        consecutiveFailures  = snap.ConsecutiveFailures,
                        drainPending         = snap.DrainPending,
                        checkedAtUtc         = snap.CheckedAtUtc
                    };
                }
                items.Add(dict);
            }
        }

        return Ok(new
        {
            items,
            total     = Num(node, "total"),
            page      = Num(node, "page"),
            page_size = Num(node, "page_size"),
            pages     = Num(node, "pages")
        });
    }

    /// <summary>Drill-down: phones on one host, paged.</summary>
    [HttpGet("{hostId:guid}/phones")]
    public async Task<IActionResult> Phones(
        Guid hostId,
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var el = SupabaseRpc.Unwrap(await _rpc.CallAsync("rpc_orch_list_host_phones",
            new { p_host_id = hostId, p_page = page, p_page_size = pageSize }, ct));

        return Content(el.GetRawText(), "application/json");
    }

    private static int Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
}
