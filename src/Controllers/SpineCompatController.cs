using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PhoneOrchestrator.Services;

namespace PhoneOrchestrator.Controllers;

/// <summary>
/// Everything the canary's own containers talk to. Three unrelated jobs share
/// this file because they share one property: none of them carries
/// credentials, and all of them are blocked at nginx.
///
///   Telemetry   the Worker posts here instead of Spine, so leaves, events
///               and summaries land in canary_* and never reach the product
///   Relay       WhatsAppSender reads WA_FASTAPI_URL, which also points here;
///               we translate its Spine-shaped request into whatsapp-single's
///   Container   whatsapp-canary reports incoming messages via MANAGER_URL
///
/// Routes are copied from the real Spine router rather than guessed - a
/// method or casing mismatch fails silently at the Worker, which logs a
/// debug line and moves on.
/// </summary>
[ApiController]
public sealed class SpineCompatController : ControllerBase
{
    private readonly CanaryStore _store;
    private readonly CanaryRelay _relay;
    private readonly ILogger<SpineCompatController> _log;

    public SpineCompatController(
        CanaryStore store,
        CanaryRelay relay,
        ILogger<SpineCompatController> log)
    {
        _store = store;
        _relay = relay;
        _log   = log;
    }

    // ══════════════════════════════════════════════════ worker liveness ══

    public sealed record HeartbeatIn(
        [property: JsonPropertyName("phone_id")]    string PhoneId,
        [property: JsonPropertyName("service_name")] string ServiceName,
        [property: JsonPropertyName("port")]         int Port = 9000,
        [property: JsonPropertyName("status")]       string Status = "online");

    /// <summary>
    /// WorkerRegistry posts this every few seconds. It must answer 200 even
    /// before a sweep has ever run, or the Worker fills its log with failures.
    /// </summary>
    [HttpPost("/workers/heartbeat")]
    public IActionResult Heartbeat([FromBody] HeartbeatIn body)
    {
        _store.RecordWorkerHeartbeat(body.PhoneId, body.ServiceName, body.Port, body.Status);
        return Ok(new { ok = true });
    }

    // ═══════════════════════════════════════════════════════════ leaves ══

    public sealed record LeafIn(
        [property: JsonPropertyName("scenario_id")]         string ScenarioId,
        [property: JsonPropertyName("call_id")]             string CallId,
        [property: JsonPropertyName("leaf_id")]             string LeafId,
        [property: JsonPropertyName("step_id")]             string? StepId = null,
        [property: JsonPropertyName("type")]                string? Type = null,
        [property: JsonPropertyName("message_id")]          string? MessageId = null,
        [property: JsonPropertyName("whatsapp_message_id")] string? WhatsappMessageId = null,
        [property: JsonPropertyName("content")]             string? Content = null,
        [property: JsonPropertyName("wa_type")]             string? WaType = null,
        [property: JsonPropertyName("status")]              string? Status = null);

    [HttpPost("/leaves")]
    public async Task<IActionResult> Leaf([FromBody] LeafIn body, CancellationToken ct)
    {
        await _store.RecordEventAsync(body.CallId, "leaf", body, ct);
        return Ok(new { ok = true });
    }

    public sealed record LeafStatusIn(
        [property: JsonPropertyName("call_id")]             string CallId,
        [property: JsonPropertyName("leaf_id")]             string LeafId,
        [property: JsonPropertyName("status")]              string Status,
        [property: JsonPropertyName("scenario_id")]         string? ScenarioId = null,
        [property: JsonPropertyName("message_id")]          string? MessageId = null,
        [property: JsonPropertyName("whatsapp_message_id")] string? WhatsappMessageId = null);

    /// <summary>PATCH, not POST - RuntimeTreeService uses PATCH here.</summary>
    [HttpPatch("/leaves/status")]
    public async Task<IActionResult> LeafStatus([FromBody] LeafStatusIn body, CancellationToken ct)
    {
        await _store.RecordEventAsync(body.CallId, "leaf_status", body, ct);
        return Ok(new { ok = true });
    }

    // ════════════════════════════════════════════════════ worker events ══

    public sealed record EventIn(
        [property: JsonPropertyName("call_id")]    string CallId,
        [property: JsonPropertyName("phone_id")]   string? PhoneId = null,
        [property: JsonPropertyName("event_type")] string? EventType = null,
        [property: JsonPropertyName("step_id")]    string? StepId = null,
        [property: JsonPropertyName("step_type")]  string? StepType = null,
        [property: JsonPropertyName("data")]       JsonElement? Data = null);

    [HttpPost("/events")]
    public async Task<IActionResult> Event([FromBody] EventIn body, CancellationToken ct)
    {
        await _store.RecordEventAsync(body.CallId, "worker_event", body, ct);
        return Ok(new { ok = true });
    }

    // ══════════════════════════════════════════════════════════ verdict ══

    public sealed record SummaryIn(
        [property: JsonPropertyName("call_id")]          string? CallId = null,
        [property: JsonPropertyName("scenario_id")]      string? ScenarioId = null,
        [property: JsonPropertyName("phone_id")]         string? PhoneId = null,
        [property: JsonPropertyName("contact_id")]       string? ContactId = null,

        // completed | failed | aborted | expired | timeout
        [property: JsonPropertyName("status")]           string Status = "completed",

        [property: JsonPropertyName("started_at")]       DateTime? StartedAt = null,
        [property: JsonPropertyName("finished_at")]      DateTime? FinishedAt = null,
        [property: JsonPropertyName("duration_seconds")] int DurationSeconds = 0,
        [property: JsonPropertyName("last_step_id")]     string? LastStepId = null,
        [property: JsonPropertyName("variables")]        JsonElement? Variables = null,
        [property: JsonPropertyName("sender_count")]     int SenderCount = 0,
        [property: JsonPropertyName("expected_count")]   int ExpectedCount = 0,
        [property: JsonPropertyName("mismatch_count")]   int MismatchCount = 0);

    /// <summary>
    /// End of scenario, and the only endpoint here that decides anything.
    /// Unlike Spine it closes no call and promotes no queue - there is no
    /// queue. It resolves the canary_check and, on failure, writes the
    /// notification the customer sees.
    /// </summary>
    [HttpPost("/calls/{callId}/summary")]
    public async Task<IActionResult> Summary(
        string callId, [FromBody] SummaryIn body, CancellationToken ct)
    {
        // The id arrives in both the route and the body. Trust the route: it
        // is what the Worker was handed at init.
        var resolved = string.IsNullOrWhiteSpace(callId) ? body.CallId : callId;

        _log.LogInformation(
            "[CANARY] Summary | call={CallId} status={Status} steps={Sent}/{Expected} " +
            "mismatch={Mismatch} dur={Dur}s",
            resolved, body.Status, body.SenderCount, body.ExpectedCount,
            body.MismatchCount, body.DurationSeconds);

        var closed = await _store.CloseCheckAsync(resolved ?? "", body, ct);

        if (!closed)
            _log.LogWarning("[CANARY] Summary for unknown call {CallId} - ignored", resolved);

        return Ok(new { ok = true, matched = closed });
    }

    // ════════════════════════════════════════════════════════════ relay ══

    public sealed record SpineSendIn(
        [property: JsonPropertyName("contact_id")]    string ContactId,
        [property: JsonPropertyName("contact_phone")] string ContactPhone,
        [property: JsonPropertyName("message_type")]  string MessageType,
        [property: JsonPropertyName("content")]       string? Content = null,
        [property: JsonPropertyName("metadata")]      JsonElement? Metadata = null,
        [property: JsonPropertyName("scenario_id")]   string? ScenarioId = null,
        [property: JsonPropertyName("call_id")]       string? CallId = null,
        [property: JsonPropertyName("leaf_id")]       string? LeafId = null);

    /// <summary>
    /// WhatsAppSender posts here because WA_FASTAPI_URL points at us. We
    /// translate to whatsapp-canary's own shape and forward.
    ///
    /// The response must carry a real wa_message_id: WhatsAppSender treats a
    /// blank one as a failure even when the HTTP call succeeded, so echoing
    /// back an invented id would make every send look broken - or worse, a
    /// send that failed look fine.
    /// </summary>
    [HttpPost("/send/{phoneId}")]
    public async Task<IActionResult> Send(
        string phoneId, [FromBody] SpineSendIn body, CancellationToken ct)
    {
        var text = body.Content ?? "";

        _log.LogInformation(
            "[CANARY] Relay | phone={PhoneId} call={CallId} to={To} type={Type} len={Len}",
            phoneId, body.CallId, body.ContactPhone, body.MessageType, text.Length);

        if (!string.Equals(body.MessageType, "text", StringComparison.OrdinalIgnoreCase))
        {
            // The handshake is text only. Anything else means the uploaded
            // scenario has a step the canary was never meant to run.
            _log.LogWarning("[CANARY] Relay rejected unsupported type {Type}", body.MessageType);
            return Ok(new { ok = false, status = "unsupported_type" });
        }

        var (ok, waMessageId, error) =
            await _relay.SendTextAsync(body.ContactPhone, text, ct);

        if (!string.IsNullOrEmpty(body.CallId))
        {
            await _store.RecordEventAsync(
                body.CallId,
                ok ? "probe_sent" : "error",
                new { to = body.ContactPhone, waMessageId, error, leafId = body.LeafId },
                ct);

            if (ok) await _store.MarkSentAsync(body.CallId, ct);
        }

        if (!ok)
        {
            _log.LogError("[CANARY] Relay failed | call={CallId} error={Error}", body.CallId, error);
            return Ok(new { ok = false, status = "send_failed" });
        }

        return Ok(new
        {
            ok             = true,
            wa_message_id  = waMessageId,
            message_id     = waMessageId,
            status         = "sent"
        });
    }

    // ══════════════════════════════════════════════ container callbacks ══

    public sealed record ContainerEventIn(
        [property: JsonPropertyName("event")]     string? Event = null,
        [property: JsonPropertyName("messageId")] string? MessageId = null,
        [property: JsonPropertyName("jid")]       string? Jid = null,
        [property: JsonPropertyName("type")]      string? Type = null,
        [property: JsonPropertyName("data")]      JsonElement? Data = null,
        [property: JsonPropertyName("phone")]     string? Phone = null,
        [property: JsonPropertyName("creds_b64")] string? CredsB64 = null,
        [property: JsonPropertyName("status")]    int? Status = null);

    /// <summary>
    /// whatsapp-canary reports here because its MANAGER_URL points at us
    /// rather than at the product's manager. Left at the image default these
    /// events would land in the product tables - the exact contamination the
    /// canary exists to avoid.
    ///
    /// Nothing here needs to reach the Worker: the Worker learns about the
    /// reply through its own webhook registration on the container. This is
    /// connection bookkeeping and an audit trail.
    /// </summary>
    [HttpPost("/api/webhook/container-event/{phoneId}")]
    public async Task<IActionResult> ContainerEvent(
        string phoneId, [FromBody] ContainerEventIn body, CancellationToken ct)
    {
        switch (body.Event)
        {
            case "authenticated":
                // The LID is the prober's real identity and is only knowable
                // once WhatsApp has assigned it, which is now.
                string? lid = null;
                if (body.Data is { } d && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("lid", out var lidEl))
                    lid = lidEl.GetString()?.Split('@')[0];

                _log.LogWarning("[CANARY] Prober authenticated as {Phone} lid={Lid}", body.Phone, lid);
                await _store.RecordProberStateAsync("authenticated", body.Phone, lid, ct);
                break;

            case "disconnected":
                _log.LogError("[CANARY] Prober disconnected - checks will fail until it returns");
                await _store.RecordProberStateAsync("disconnected", null, null, ct);
                break;

            case "qr":
            case "pairing_code":
                _log.LogWarning("[CANARY] Prober waiting to be paired ({Event})", body.Event);
                await _store.RecordProberStateAsync("pairing", null, null, ct);
                break;

            case "message":
                // Useful when a check fails and the trail needs to show what
                // actually came back versus what the scenario expected.
                await _store.RecordInboundAsync(body.Jid, body.Data, ct);
                break;
        }

        return Ok(new { received = true });
    }
}