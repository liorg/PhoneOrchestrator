using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

/// <summary>
/// Back office under /api/admin (behind AuthMiddleware, like /api/canary):
/// plans, customers + debts, bot_config. Every read and write is one RPC
/// (sql/22_admin.sql) — paging, totals and validation live in the DB.
/// Writes are logged at Warning, the same way canary control changes are.
/// Pages: plans.html, customers.html, config.html.
/// </summary>
[ApiController]
public sealed class AdminController : ControllerBase
{
    private readonly SupabaseRpc _rpc;
    private readonly ILogger<AdminController> _log;

    public AdminController(SupabaseRpc rpc, ILogger<AdminController> log)
    {
        _rpc = rpc;
        _log = log;
    }

    /// <summary>Who made the change, for catalog_history / user_plan_history: the caller's IP
    /// (one dashboard password, so there is no user name to record).</summary>
    private string Actor => "admin@" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");

    private async Task<IActionResult> Rpc(string fn, object args, CancellationToken ct)
    {
        try
        {
            var el = SupabaseRpc.Unwrap(await _rpc.CallAsync(fn, args, ct));
            if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return NotFound(new { ok = false, reason = "not_found" });
            return Content(el.GetRawText(), "application/json");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // raise exception in the RPC (validation) → PostgREST 400 → shown in the page banner
            _log.LogWarning("[ADMIN] {Fn} failed: {Err}", fn, ex.Message);
            return BadRequest(new { ok = false, error = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════ plans ══

    [HttpGet("api/admin/plans")]
    public Task<IActionResult> Plans(CancellationToken ct) =>
        Rpc("rpc_admin_plans", new { }, ct);

    /// <summary>Insert or update. JSON null in phones/bots/schedules = unlimited; price null = custom.</summary>
    [HttpPut("api/admin/plans/{id}")]
    public async Task<IActionResult> SavePlan(string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
        node["id"] = id;
        _log.LogWarning("[ADMIN] plan saved: {Plan}", node.ToJsonString());
        return await Rpc("rpc_admin_plan_save", new { p = node, p_actor = Actor }, ct);
    }

    // ═══════════════════════════════════════════════ customers & debts ══

    [HttpGet("api/admin/customers")]
    public Task<IActionResult> Customers(
        [FromQuery] int page = 1, [FromQuery] string? q = null,
        [FromQuery] string? plan = null, [FromQuery] bool debt = false, CancellationToken ct = default) =>
        Rpc("rpc_admin_customers", new { p_page = page, p_search = q, p_plan = plan, p_debt_only = debt }, ct);

    /// <summary>Accrues the customer's charges first, then plan, usage, months, payments.</summary>
    [HttpGet("api/admin/customers/{userId:guid}")]
    public Task<IActionResult> Customer(Guid userId, CancellationToken ct) =>
        Rpc("rpc_admin_customer", new { p_user_id = userId }, ct);

    public sealed record SetPlanIn(string PlanId);

    [HttpPut("api/admin/customers/{userId:guid}/plan")]
    public async Task<IActionResult> SetPlan(Guid userId, [FromBody] SetPlanIn body, CancellationToken ct)
    {
        _log.LogWarning("[ADMIN] plan of {User} → {Plan}", userId, body.PlanId);
        return await Rpc("rpc_admin_set_plan", new { p_user_id = userId, p_plan_id = body.PlanId, p_actor = Actor }, ct);
    }

    public sealed record PaymentIn(decimal Amount, string? Note);

    /// <summary>Bank transfer / cash / write-off. A negative amount reverses one.</summary>
    [HttpPost("api/admin/customers/{userId:guid}/payments")]
    public async Task<IActionResult> AddPayment(Guid userId, [FromBody] PaymentIn body, CancellationToken ct)
    {
        _log.LogWarning("[ADMIN] manual payment {User} {Amount} {Note}", userId, body.Amount, body.Note);
        return await Rpc("rpc_admin_add_payment", new { p_user_id = userId, p_amount = body.Amount, p_note = body.Note }, ct);
    }

    /// <summary>Recalculate charges (plan months + rentals) for every customer.</summary>
    [HttpPost("api/admin/billing/accrue")]
    public Task<IActionResult> AccrueAll(CancellationToken ct) =>
        Rpc("rpc_billing_accrue_all", new { }, ct);

    // ═════════════════════════════════════════════════════════ rentals ══

    /// <summary>Rental lengths and their price (rent_options).</summary>
    [HttpGet("api/admin/rent/options")]
    public Task<IActionResult> RentOptions(CancellationToken ct) =>
        Rpc("rpc_admin_rent_options", new { }, ct);

    /// <summary>Insert or update one length. Never deleted — set isActive=false.</summary>
    [HttpPut("api/admin/rent/options/{hours:int}")]
    public async Task<IActionResult> SaveRentOption(int hours, [FromBody] JsonElement body, CancellationToken ct)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
        node["hours"] = hours;
        _log.LogWarning("[ADMIN] rent option saved: {Opt}", node.ToJsonString());
        return await Rpc("rpc_admin_rent_option_save", new { p = node, p_actor = Actor }, ct);
    }

    /// <summary>status: active | ended | (empty) = all. q: phone number or customer email.</summary>
    [HttpGet("api/admin/rentals")]
    public Task<IActionResult> Rentals(
        [FromQuery] int page = 1, [FromQuery] string? status = null, [FromQuery] string? q = null,
        CancellationToken ct = default) =>
        Rpc("rpc_admin_rentals", new { p_page = page, p_status = status, p_search = q }, ct);

    // ═══════════════════════════════════════════════════ change history ══

    /// <summary>kind: plan | rent. ref: one plan id / one length, or empty for all.</summary>
    [HttpGet("api/admin/history/{kind:regex(^(plan|rent)$)}")]
    public Task<IActionResult> History(
        string kind, [FromQuery] int page = 1, [FromQuery] string? @ref = null, CancellationToken ct = default) =>
        Rpc("rpc_admin_history", new { p_kind = kind, p_page = page, p_ref = @ref }, ct);

    // ══════════════════════════════════════════════════════ bot_config ══

    [HttpGet("api/admin/config")]
    public Task<IActionResult> Config([FromQuery] int page = 1, [FromQuery] string? q = null, CancellationToken ct = default) =>
        Rpc("rpc_admin_config_list", new { p_page = page, p_search = q }, ct);

    public sealed record ConfigIn(string Key, string? Value, string? Description);

    /// <summary>Insert or update one key. Description null = keep the existing one.</summary>
    [HttpPut("api/admin/config")]
    public async Task<IActionResult> SetConfig([FromBody] ConfigIn body, CancellationToken ct)
    {
        _log.LogWarning("[ADMIN] bot_config {Key} = {Value}", body.Key, body.Value);
        return await Rpc("rpc_admin_config_set",
            new { p_key = body.Key, p_value = body.Value, p_description = body.Description }, ct);
    }
}
