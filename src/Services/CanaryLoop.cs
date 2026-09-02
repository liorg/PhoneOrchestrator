using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// The only component that starts a canary sweep.
///
/// Everything else in the canary is reactive - the Worker posts telemetry,
/// the container posts events, the controller records them. This is what sets
/// the whole thing in motion, on a timer.
///
/// One sweep:
///   1. find the prober in CONNECT, and its two handshake scenarios
///   2. make sure exactly one webhook is registered on its container
///   3. write CHECKHEALTH onto every monitored phone that lacks it
///   4. open a run, and init the Worker once per monitored phone
///   5. wait, expire whatever never answered, close the run
///   6. notify the customer about failures
///
/// The verdicts arrive asynchronously through SpineCompatController, so this
/// loop never waits on a reply directly - it opens the checks, sleeps, and
/// then reads what came back.
/// </summary>
public sealed class CanaryLoop : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CanaryStore _store;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<CanaryLoop> _log;

    public CanaryLoop(
        IServiceScopeFactory scopes,
        CanaryStore store,
        IOptions<OrchestratorOptions> opt,
        ILogger<CanaryLoop> log)
    {
        _scopes = scopes;
        _store  = store;
        _opt    = opt.Value;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.CanaryEnabled)
        {
            _log.LogWarning("[CANARY] Disabled - set Orchestrator__CanaryEnabled=true to start sweeping");
            return;
        }

        _log.LogInformation("[CANARY] Loop starting | worker={Worker} wa={Wa}",
            _opt.CanaryWorkerUrl, _opt.CanaryWaUrl);

        // The prober's container needs time to authenticate before the first
        // sweep, otherwise every check in it fails for a reason that has
        // nothing to do with the phones being tested.
        await Delay(TimeSpan.FromSeconds(60), ct);

        while (!ct.IsCancellationRequested)
        {
            var cfg = await LoadConfigAsync(ct);

            try
            {
                await SweepAsync(cfg, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[CANARY] Sweep failed");
            }

            if (!await Delay(TimeSpan.FromSeconds(cfg.IntervalSec), ct)) break;
        }
    }

    // ══════════════════════════════════════════════════════════ config ══

    private sealed record Cfg(
        int IntervalSec, int TimeoutSec, bool AutoScenario, bool Notify, bool NotifyOnPass);

    private async Task<Cfg> LoadConfigAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();

            var rows = await rpc.SelectAsync("bot_config", "select=key,value&key=like.canary.*", ct);

            var map = new Dictionary<string, string>();
            foreach (var r in rows.EnumerateArray())
                if (r.TryGetProperty("key", out var k) && r.TryGetProperty("value", out var v))
                    map[k.GetString() ?? ""] = v.GetString() ?? "";

            int I(string k, int d) => map.TryGetValue(k, out var s) && int.TryParse(s, out var n) ? n : d;
            bool B(string k, bool d) => map.TryGetValue(k, out var s) ? s == "true" : d;

            return new Cfg(
                I("canary.interval_sec", 900),
                I("canary.timeout_sec", 120),
                B("canary.auto_scenario", true),
                B("canary.notify", true),
                B("canary.notify_on_pass", false));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CANARY] Could not read config, using defaults");
            return new Cfg(900, 120, true, true, false);
        }
    }

    // ═══════════════════════════════════════════════════════════ sweep ══

    private async Task SweepAsync(Cfg cfg, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var rpc   = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
        var relay = scope.ServiceProvider.GetRequiredService<CanaryRelay>();

        // ── 1. the prober ────────────────────────────────────────────
        var prober = await LoadProberAsync(rpc, ct);
        if (prober == null)
        {
            _log.LogWarning("[CANARY] No prober in CONNECT - nothing to sweep");
            return;
        }

        if (prober.Value.SendScenario == null)
        {
            _log.LogWarning("[CANARY] Prober has no send_scenario - upload it on the provisioning screen");
            return;
        }

        // ── 2. one webhook, ours ─────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(_opt.CanaryWebhookUrl))
            await relay.EnsureWebhookAsync(
                $"{_opt.CanaryWebhookUrl.TrimEnd('/')}/{prober.Value.Id}", ct);

        // ── 3. who is being watched ──────────────────────────────────
        var targets = await LoadTargetsAsync(rpc, prober.Value.Id, ct);
        if (targets.Count == 0)
        {
            _log.LogWarning("[CANARY] Prober has no contacts yet - it must announce itself first");
            return;
        }

        // ── 4. open the run ──────────────────────────────────────────
        var runEl = await rpc.InsertAsync("canary_runs", new
        {
            heartbeat_phone_id = prober.Value.Id,
            status             = "running",
            total              = targets.Count
        }, ct);

        if (!runEl.TryGetProperty("id", out var runIdEl)
            || !Guid.TryParse(runIdEl.GetString(), out var runId))
        {
            _log.LogError("[CANARY] Could not open a run");
            return;
        }

        _log.LogInformation("[CANARY] Run {RunId} starting | {Count} phones", runId, targets.Count);

        var skipped = 0;

        foreach (var t in targets)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var ok = await StartCheckAsync(rpc, relay, cfg, prober.Value, t, runId, ct);
                if (!ok) skipped++;
            }
            catch (Exception ex)
            {
                skipped++;
                _log.LogError(ex, "[CANARY] Could not start check for phone {PhoneId}", t.PhoneId);
            }
        }

        // ── 5. let the replies land, then sweep up ───────────────────
        await Delay(TimeSpan.FromSeconds(cfg.TimeoutSec + 15), ct);
        await _store.ExpireStaleAsync(runId, TimeSpan.FromSeconds(cfg.TimeoutSec), ct);

        await CloseRunAsync(rpc, runId, skipped, ct);

        // ── 6. tell the customer ─────────────────────────────────────
        if (cfg.Notify)
            await NotifyAsync(rpc, runId, cfg.NotifyOnPass, ct);
    }

    // ══════════════════════════════════════════════════════════ pieces ══

    private readonly record struct Prober(
        Guid Id, string PhoneNumber, JsonElement? SendScenario, JsonElement? CheckScenario, DateTime? UpdatedAt);

    private readonly record struct Target(
        Guid ContactId, Guid PhoneId, string? PhoneNumber, Guid? HostId, Guid? UserId);

    private async Task<Prober?> LoadProberAsync(SupabaseRpc rpc, CancellationToken ct)
    {
        var rows = await rpc.SelectAsync("heartbeatphones",
            "select=*&status=eq.CONNECT&limit=1", ct);

        foreach (var r in rows.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
                continue;

            return new Prober(
                id,
                r.TryGetProperty("phone_number", out var n) ? n.GetString() ?? "" : "",
                Opt(r, "send_scenario"),
                Opt(r, "check_scenario"),
                r.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(u.GetString(), out var d) ? d : null);
        }

        return null;

        static JsonElement? Opt(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? v.Clone() : null;
    }

    /// <summary>
    /// The monitored phones are exactly the prober's contacts. Each was
    /// created by WhatsAppDockerManager when the prober announced itself, so
    /// this list grows on its own as phones are provisioned.
    /// </summary>
    private async Task<List<Target>> LoadTargetsAsync(SupabaseRpc rpc, Guid proberId, CancellationToken ct)
    {
        var rows = await rpc.SelectAsync("contacts",
            $"select=id,phone_id&heartbeat_phone_id=eq.{proberId}", ct);

        var list = new List<Target>();

        foreach (var r in rows.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var cEl) || !Guid.TryParse(cEl.GetString(), out var contactId))
                continue;
            if (!r.TryGetProperty("phone_id", out var pEl) || !Guid.TryParse(pEl.GetString(), out var phoneId))
                continue;

            var phones = await rpc.SelectAsync("phones",
                $"select=number,host_id,user_id,status&id=eq.{phoneId}&limit=1", ct);

            string? number = null; Guid? hostId = null; Guid? userId = null; var active = false;

            foreach (var p in phones.EnumerateArray())
            {
                number = p.TryGetProperty("number", out var nn) ? nn.GetString() : null;
                active = p.TryGetProperty("status", out var st)
                         && string.Equals(st.GetString(), "active", StringComparison.OrdinalIgnoreCase);
                if (p.TryGetProperty("host_id", out var h) && Guid.TryParse(h.GetString(), out var hg)) hostId = hg;
                if (p.TryGetProperty("user_id", out var u) && Guid.TryParse(u.GetString(), out var ug)) userId = ug;
            }

            // An inactive phone is not broken, it is retired. Checking it
            // would produce a failure every sweep and train you to ignore them.
            if (!active) continue;

            list.Add(new Target(contactId, phoneId, number, hostId, userId));
        }

        return list;
    }

    private async Task<bool> StartCheckAsync(
        SupabaseRpc rpc, CanaryRelay relay, Cfg cfg,
        Prober prober, Target t, Guid runId, CancellationToken ct)
    {
        // The id the Worker will quote back in every leaf and in the summary.
        // Deliberately not a calls.id - nothing in the product schema knows
        // about it, which is the point.
        var callId = $"canary-{Guid.NewGuid():N}";

        var scenarioOk = cfg.AutoScenario
            ? await EnsureCheckScenarioAsync(rpc, prober, t, ct)
            : (bool?)null;

        var checkEl = await rpc.InsertAsync("canary_checks", new
        {
            run_id         = runId,
            phone_id       = t.PhoneId,
            contact_id     = t.ContactId,
            phone_number   = t.PhoneNumber,
            host_id        = t.HostId,
            status         = "pending",
            scenario_ok    = scenarioOk,
            canary_call_id = callId
        }, ct);

        if (!checkEl.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var checkId))
            return false;

        await _store.RegisterCallAsync(callId, checkId, ct);

        if (scenarioOk == false)
        {
            await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new
            {
                status = "scenario_missing",
                error  = "CHECKHEALTH could not be written to the target phone"
            }, ct);
            return false;
        }

        // Init the Worker. scenario_json goes over as the camelCase ICR shape
        // exported from the Designer - IcrConverter reads it verbatim, so no
        // translation happens anywhere in this path.
        var envelope = new
        {
            typeEvent     = "init",
            phone_id      = prober.Id.ToString(),
            contact_id    = t.ContactId.ToString(),
            contact_phone = t.PhoneNumber,
            call_id       = callId,
            scenario_id   = $"canary-{prober.Id}",
            scenario_json = prober.SendScenario
        };

        using var scope = _scopes.CreateScope();
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);

        var url = $"{_opt.CanaryWorkerUrl.TrimEnd('/')}/webhook/event";

        try
        {
            using var res = await http.PostAsJsonAsync(url, envelope, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                // 409 means the Worker still holds a session for this contact,
                // usually because the previous sweep never finished.
                var reason = (int)res.StatusCode == 409
                    ? "worker still has an open session for this contact"
                    : $"worker returned {(int)res.StatusCode}";

                await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new
                {
                    status = "failed",
                    error  = reason
                }, ct);

                _log.LogWarning("[CANARY] init rejected for {Phone}: {Reason} | {Body}",
                    t.PhoneNumber, reason, body);
                return false;
            }

            await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new
            {
                status  = "sent",
                sent_at = DateTime.UtcNow
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new
            {
                status = "failed",
                error  = $"could not reach worker: {ex.Message}"
            }, ct);
            return false;
        }
    }

    /// <summary>
    /// Writes CHECKHEALTH onto the target phone when it is missing, and
    /// rewrites it only when the prober's copy actually changed.
    ///
    /// The comparison is a hash of the scenario itself rather than a
    /// timestamp: heartbeatphones.updated_at moves for unrelated reasons -
    /// the uid arriving on authentication, an edited identification emoji -
    /// and each of those would otherwise rewrite the scenario on every phone
    /// in the fleet.
    ///
    /// Marked is_system so the Designer leaves it alone and so overwriting
    /// it is safe.
    /// </summary>
    private async Task<bool> EnsureCheckScenarioAsync(
        SupabaseRpc rpc, Prober prober, Target t, CancellationToken ct)
    {
        if (prober.CheckScenario == null) return false;

        try
        {
            var source = prober.CheckScenario.Value.GetRawText();
            var hash   = Sha256(source);

            var existing = await rpc.SelectAsync("scenarios",
                $"select=id,system_hash&phone_id=eq.{t.PhoneId}&is_system=is.true&name=eq.CHECKHEALTH&limit=1", ct);

            foreach (var row in existing.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var idEl)) continue;

                var current = row.TryGetProperty("system_hash", out var h) ? h.GetString() : null;
                if (current == hash) return true;   // unchanged, nothing to do

                await rpc.PatchAsync("scenarios", $"id=eq.{idEl.GetString()}", new
                {
                    config      = BuildDbConfig(prober.CheckScenario.Value),
                    contact_id  = t.ContactId,
                    status      = "active",
                    event_type  = "trigger",
                    system_hash = hash
                }, ct);

                _log.LogWarning("[CANARY] CHECKHEALTH changed - rewritten on {Phone}", t.PhoneNumber);
                return true;
            }

            await rpc.InsertAsync("scenarios", new
            {
                phone_id    = t.PhoneId,
                contact_id  = t.ContactId,
                name        = "CHECKHEALTH",
                status      = "active",
                event_type  = "trigger",
                priority    = 1,
                is_system   = true,
                system_hash = hash,
                config      = BuildDbConfig(prober.CheckScenario.Value)
            }, ct);

            _log.LogWarning("[CANARY] Created CHECKHEALTH on {Phone}", t.PhoneNumber);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Could not ensure CHECKHEALTH on {PhoneId}", t.PhoneId);
            return false;
        }
    }

    private static string Sha256(string s) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(s)))[..32];

    /// <summary>
    /// The Designer exports camelCase; the scenarios table stores snake_case
    /// and keeps event_type in its own column. The Worker reads the camelCase
    /// form straight from the envelope, so this conversion is only needed for
    /// the copy that lives in the database for Spine to find.
    /// </summary>
    private static Dictionary<string, object?> BuildDbConfig(JsonElement icr)
    {
        object? Get(string name) =>
            icr.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? JsonSerializer.Deserialize<object>(v.GetRawText())
                : null;

        return new Dictionary<string, object?>
        {
            ["canvas"]         = Get("canvas"),
            ["interval"]       = Get("interval"),
            ["arrow_data"]     = Get("arrowData")     ?? new Dictionary<string, object>(),
            ["bot_contact"]    = Get("botContact"),
            ["estimated_time"] = Get("estimatedTime"),
            ["use_auto_calc"]  = false,
            ["description"]    = "CHECKHEALTH - managed by PhoneOrchestrator, do not edit"
        };
    }

    private async Task CloseRunAsync(SupabaseRpc rpc, Guid runId, int skipped, CancellationToken ct)
    {
        try
        {
            var rows = await rpc.SelectAsync("canary_checks", $"select=status&run_id=eq.{runId}", ct);

            int passed = 0, failed = 0;
            foreach (var r in rows.EnumerateArray())
            {
                var s = r.TryGetProperty("status", out var v) ? v.GetString() : null;
                if (s == "passed") passed++;
                else if (s != "skipped") failed++;
            }

            await rpc.PatchAsync("canary_runs", $"id=eq.{runId}", new
            {
                status      = "completed",
                finished_at = DateTime.UtcNow,
                passed,
                failed,
                skipped
            }, ct);

            _log.LogWarning("[CANARY] Run {RunId} done | passed={Passed} failed={Failed} skipped={Skipped}",
                runId, passed, failed, skipped);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Could not close run {RunId}", runId);
        }
    }

    /// <summary>
    /// One notification per phone, not per run: the customer cares that their
    /// phone is down, not that a sweep happened.
    /// </summary>
    private async Task NotifyAsync(SupabaseRpc rpc, Guid runId, bool onPass, CancellationToken ct)
    {
        try
        {
            var filter = onPass ? "" : "&status=neq.passed";
            var rows = await rpc.SelectAsync("canary_checks",
                $"select=id,phone_id,phone_number,status,error&run_id=eq.{runId}&notified=is.false{filter}", ct);

            foreach (var r in rows.EnumerateArray())
            {
                if (!r.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var checkId))
                    continue;
                if (!r.TryGetProperty("phone_id", out var pEl) || !Guid.TryParse(pEl.GetString(), out var phoneId))
                    continue;

                var status = r.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
                var number = r.TryGetProperty("phone_number", out var n) ? n.GetString() : null;
                var error  = r.TryGetProperty("error", out var e) ? e.GetString() : null;
                var passed = status == "passed";

                // notifications.user_id comes from the phone, so the alert
                // reaches whoever owns it rather than a system account.
                Guid? userId = null;
                var phones = await rpc.SelectAsync("phones", $"select=user_id&id=eq.{phoneId}&limit=1", ct);
                foreach (var p in phones.EnumerateArray())
                    if (p.TryGetProperty("user_id", out var u) && Guid.TryParse(u.GetString(), out var ug))
                        userId = ug;

                await rpc.InsertAsync("notifications", new
                {
                    user_id   = userId,
                    phone_id  = phoneId,
                    title     = passed ? "בדיקת תקינות עברה" : "בדיקת תקינות נכשלה",
                    message   = passed
                        ? $"הטלפון {number} עבר את בדיקת התקינות."
                        : $"הטלפון {number} לא הגיב לבדיקת התקינות. {error}",
                    log_level = passed ? "info" : "error",
                    source    = "canary",
                    is_read   = false,
                    extra     = new { run_id = runId, check_id = checkId, status }
                }, ct);

                await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new { notified = true }, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Notification pass failed for run {RunId}", runId);
        }
    }

    private static async Task<bool> Delay(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}