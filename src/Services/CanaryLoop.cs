using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// The canary sweep, v2.
///
///   1. prober /health - NATS connected, token valid. Otherwise no sweep:
///      a deaf prober would fail every phone and alert every customer.
///   2. webhook on the prober points at /canary/inbound/{token}
///   3. every phones.status = 'active' gets the CHECK template with a fresh
///      code; Cloud API targets are skipped (business→business is unproven)
///   4. wait timeout_sec after the last send, then timeout what never ACKed
///   5. close the run; notify only on state change (down / recovered)
///
/// The ACKs arrive on their own through CanaryController → CanaryStore while
/// this loop sleeps in step 4.
/// </summary>
public sealed class CanaryLoop : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CanaryStore _store;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<CanaryLoop> _log;

    public CanaryLoop(
        IServiceScopeFactory scopes, CanaryStore store,
        IOptions<OrchestratorOptions> opt, ILogger<CanaryLoop> log)
    {
        _scopes = scopes;
        _store  = store;
        _opt    = opt.Value;
        _log    = log;
    }

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Everything is driven from the single row in canary_control:
    ///   enabled = false        → no sweeps; the runner keeps reporting status
    ///   interval_sec           → time between sweeps
    ///   active_from/active_to  → optional daily window (in tz); outside it, no sweeps
    ///   run_now_at             → set by the dashboard; runs once, even when
    ///                            disabled or outside the window, then cleared
    /// The row is re-read every 30s, so a change takes effect without a deploy.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.CanaryEnabled)
        {
            _log.LogInformation("[CANARY] Runner not on this instance (Orchestrator__CanaryEnabled=false)");
            return;
        }

        if (string.IsNullOrWhiteSpace(_opt.CanaryInboundToken))
        {
            _log.LogError("[CANARY] Orchestrator__CanaryInboundToken is empty - refusing to start: " +
                          "the inbound route would accept ACKs from anyone");
            return;
        }

        _log.LogInformation("[CANARY] Runner starting | prober={Prober}", _opt.CanaryProberUrl);

        // Let the prober finish /register with whqueue before judging it.
        if (!await Delay(TimeSpan.FromSeconds(30), ct)) return;

        DateTime nextDue = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var cfg = await LoadControlAsync(ct);

            try
            {
                var now     = DateTime.UtcNow;
                var manual  = cfg.RunNowAt.HasValue;
                var inHours = InWindow(cfg, now);
                var due     = cfg.Enabled && inHours && now >= nextDue;

                _store.Mode = !cfg.Enabled ? "disabled" : (!inHours ? "outside_hours" : "scheduled");

                if (manual || due)
                {
                    if (manual) await ClearRunNowAsync(ct);

                    _store.Mode = manual ? "running_manual" : "running";
                    await PublishStatusAsync(cfg, nextDue, ct);

                    await SweepAsync(cfg, ct);
                    _store.LastSweepUtc = DateTime.UtcNow;
                    nextDue = DateTime.UtcNow.AddSeconds(cfg.IntervalSec);
                    _store.Mode = cfg.Enabled ? "scheduled" : "disabled";
                }
                else
                {
                    // Between sweeps: keep the prober's health fresh for the dashboard.
                    await RefreshHealthAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _store.LastSweepResult = $"error: {ex.Message}";
                _log.LogError(ex, "[CANARY] Tick failed");
                nextDue = DateTime.UtcNow.AddSeconds(cfg.IntervalSec);
            }

            await PublishStatusAsync(cfg, nextDue, ct);
            if (!await Delay(Tick, ct)) break;
        }
    }

    // ═════════════════════════════════════════════════════════ control ══

    private sealed record Cfg(
        bool Enabled, int IntervalSec, int TimeoutSec, int SendGapMs,
        string Template, string Language, bool CheckCloudApi, bool Notify,
        TimeSpan? ActiveFrom, TimeSpan? ActiveTo, string TimeZone, DateTime? RunNowAt);

    private static readonly Cfg Off = new(false, 900, 120, 1500, "check_id", "en", false, true,
                                          null, null, "Asia/Jerusalem", null);

    private async Task<Cfg> LoadControlAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var rpc  = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
            var rows = await rpc.SelectAsync("canary_control", "select=*&id=eq.1&limit=1", ct);

            foreach (var r in rows.EnumerateArray())
            {
                bool   B(string k, bool d)   => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
                int    I(string k, int d)    => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
                string S(string k, string d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : d;
                TimeSpan? T(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                                         && TimeSpan.TryParse(v.GetString(), out var t) ? t : null;
                DateTime? D(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                                         && DateTime.TryParse(v.GetString(), null,
                                              System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

                return new Cfg(
                    Enabled:       B("enabled", false),
                    IntervalSec:   Math.Max(60, I("interval_sec", 900)),
                    TimeoutSec:    Math.Max(15, I("timeout_sec", 120)),
                    SendGapMs:     Math.Max(0,  I("send_gap_ms", 1500)),
                    Template:      S("template_name", "check_id"),
                    Language:      S("template_lang", "en"),
                    CheckCloudApi: B("check_cloudapi", false),
                    Notify:        B("notify", true),
                    ActiveFrom:    T("active_from"),
                    ActiveTo:      T("active_to"),
                    TimeZone:      S("timezone", "Asia/Jerusalem"),
                    RunNowAt:      D("run_now_at"));
            }

            _log.LogWarning("[CANARY] canary_control has no row id=1 - treating as disabled");
        }
        catch (Exception ex)
        {
            // Fail closed: an unreadable control row must not start messaging customers.
            _log.LogWarning(ex, "[CANARY] Could not read canary_control - treating as disabled");
        }
        return Off;
    }

    /// <summary>Daily window in the configured zone. Handles windows past midnight (22:00-06:00).</summary>
    private bool InWindow(Cfg cfg, DateTime utcNow)
    {
        if (cfg.ActiveFrom is not { } from || cfg.ActiveTo is not { } to) return true;

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(cfg.TimeZone); }
        catch { tz = TimeZoneInfo.Utc; _log.LogWarning("[CANARY] Unknown timezone {Tz}, using UTC", cfg.TimeZone); }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz).TimeOfDay;
        return from <= to ? local >= from && local < to
                          : local >= from || local < to;
    }

    private async Task ClearRunNowAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
        // JSON null, not omitted - PatchAsync drops nulls, so send an explicit dictionary.
        await rpc.PatchRawAsync("canary_control", "id=eq.1", "{\"run_now_at\":null}", ct);
    }

    private async Task RefreshHealthAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var prober = scope.ServiceProvider.GetRequiredService<CanaryProber>();
        _store.Prober = await prober.HealthAsync(ct);
        _store.ProberCheckedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// The runner's view of itself, written to canary_control. The dashboard
    /// on the other VM reads it from there - nothing calls the runner directly.
    /// runner_seen_at older than a minute means the runner is down.
    /// </summary>
    private async Task PublishStatusAsync(Cfg cfg, DateTime nextDue, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
            var p = _store.Prober;

            await rpc.PatchAsync("canary_control", "id=eq.1", new
            {
                runner_seen_at  = DateTime.UtcNow,
                runner_version  = $"{BuildInfo.Version}/{BuildInfo.Marker}",
                runner_mode     = _store.Mode,
                next_sweep_at   = cfg.Enabled ? nextDue : (DateTime?)null,
                last_sweep_at   = _store.LastSweepUtc,
                last_result     = _store.LastSweepResult,
                prober_ready    = p?.Ready,
                prober_reason   = p?.Reason ?? (p is { Ready: true } ? "" : null),
                prober_health   = p?.Raw,
                prober_checked_at = _store.ProberCheckedUtc
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CANARY] Could not publish status");
        }

        // The canary host's heartbeat. Written regardless of enabled: the
        // host is alive whenever the runner is, sweeping or not.
        if (Guid.TryParse(_opt.CanaryHostId, out var hostId))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
                await rpc.PatchAsync("agent_hosts", $"id=eq.{hostId}&kind=eq.canary",
                    new { last_heartbeat = DateTime.UtcNow }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[CANARY] Could not write host heartbeat");
            }
        }
    }

    private static async Task<bool> Delay(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    // ═══════════════════════════════════════════════════════════ sweep ══

    private sealed record Target(Guid PhoneId, string Number, Guid? HostId, Guid? UserId, bool IsCloudApi);

    private async Task SweepAsync(Cfg cfg, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var rpc    = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
        var prober = scope.ServiceProvider.GetRequiredService<CanaryProber>();

        // ── 1. ACKs come back here - before /health, which counts webhooks
        if (!string.IsNullOrWhiteSpace(_opt.CanaryCallbackUrl))
            await prober.EnsureWebhookAsync(
                $"{_opt.CanaryCallbackUrl.TrimEnd('/')}/canary/inbound/{_opt.CanaryInboundToken}", ct);

        // ── 2. prober can send and hear ───────────────────────────────
        var health = await prober.HealthAsync(ct);
        _store.Prober = health;
        _store.ProberCheckedUtc = DateTime.UtcNow;

        if (!health.Ready)
        {
            _store.LastSweepResult = $"skipped - prober not ready: {health.Reason}";
            _log.LogError("[CANARY] Prober not ready ({Reason}) - sweep skipped, customers not alerted", health.Reason);

            // A row per skipped sweep, so a dead prober (revoked/expired token,
            // NATS down) shows up in the run history instead of as silence.
            await rpc.InsertAsync("canary_runs", new
            {
                status        = "aborted",
                total         = 0,
                prober_number = _opt.CanaryProberNumber,
                error         = $"prober not ready: {health.Reason}",
                finished_at   = DateTime.UtcNow
            }, ct);
            return;
        }

        // ── 3. who ────────────────────────────────────────────────────
        var targets = await LoadTargetsAsync(rpc, ct);
        if (targets.Count == 0)
        {
            _store.LastSweepResult = "no active phones";
            return;
        }

        var run = await rpc.InsertAsync("canary_runs", new
        {
            status        = "running",
            total         = targets.Count,
            prober_number = _opt.CanaryProberNumber
        }, ct);

        if (!run.TryGetProperty("id", out var runIdEl) || !Guid.TryParse(runIdEl.GetString(), out var runId))
        {
            _log.LogError("[CANARY] Could not open a run");
            return;
        }

        _log.LogInformation("[CANARY] Run {RunId} | {Count} phones | template={Tpl}/{Lang}",
            runId, targets.Count, cfg.Template, cfg.Language);

        // ── 4. send ───────────────────────────────────────────────────
        foreach (var t in targets)
        {
            if (ct.IsCancellationRequested) break;
            var sent = await CheckOneAsync(rpc, prober, cfg, runId, t, ct);

            // Meta rate-limits bursts from a fresh number. Gap only after a
            // real send - skipped phones cost nothing.
            if (sent && cfg.SendGapMs > 0) await Task.Delay(cfg.SendGapMs, ct);
        }

        // ── 5. wait for ACKs, then close ──────────────────────────────
        await Task.Delay(TimeSpan.FromSeconds(cfg.TimeoutSec), ct);

        // Three different failures behind one "no ACK", told apart by Meta's
        // delivery receipts:
        //   -1        Meta rejected the CHECK          (prober / template side)
        //   2..4      reached the phone, no ACK        (Manager / auto-reply side)
        //   null, 1   never reached the phone          (phone offline / WhatsApp disconnected)
        var f = $"run_id=eq.{runId}&status=eq.sent";
        await rpc.PatchAsync("canary_checks", f + "&delivery_status=eq.-1", new
        {
            status = "send_failed"
        }, ct);
        await rpc.PatchAsync("canary_checks", f + "&delivery_status=gte.2", new
        {
            status = "timeout",
            error  = $"delivered to the phone, no ACK within {cfg.TimeoutSec}s - Manager auto-reply or outbound broken"
        }, ct);
        await rpc.PatchAsync("canary_checks", f, new
        {
            status = "timeout",
            error  = $"not delivered within {cfg.TimeoutSec}s - phone offline or WhatsApp disconnected"
        }, ct);

        var (passed, failed, skipped) = await CloseRunAsync(rpc, runId, ct);
        _store.LastSweepResult = $"passed={passed} failed={failed} skipped={skipped}";

        if (cfg.Notify)
            await NotifyTransitionsAsync(rpc, runId, ct);
    }

    private async Task<List<Target>> LoadTargetsAsync(SupabaseRpc rpc, CancellationToken ct)
    {
        // One query. The old design walked the prober's contacts and then
        // looked up each phone - which meant a phone was only watched after
        // an emoji handshake created that contact.
        var rows = await rpc.SelectAsync("phones",
            "select=id,number,host_id,user_id,phone_number_id&status=eq.active&order=number", ct);

        var list = new List<Target>();
        foreach (var p in rows.EnumerateArray())
        {
            if (!Guid.TryParse(p.GetProperty("id").GetString(), out var id)) continue;

            var number = p.TryGetProperty("number", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(number)) continue;

            // The prober never checks itself.
            if (CanaryStore.SameNumber(number, _opt.CanaryProberNumber)) continue;

            Guid? host = p.TryGetProperty("host_id", out var h) && Guid.TryParse(h.GetString(), out var hg) ? hg : null;
            Guid? user = p.TryGetProperty("user_id", out var u) && Guid.TryParse(u.GetString(), out var ug) ? ug : null;
            var cloud  = p.TryGetProperty("phone_number_id", out var pn)
                         && pn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pn.GetString());

            list.Add(new Target(id, number!, host, user, cloud));
        }
        return list;
    }

    /// <returns>true when a template actually went out.</returns>
    private async Task<bool> CheckOneAsync(
        SupabaseRpc rpc, CanaryProber prober, Cfg cfg, Guid runId, Target t, CancellationToken ct)
    {
        var code = CanaryStore.NewCode();

        if (t.IsCloudApi && !cfg.CheckCloudApi)
        {
            await rpc.InsertAsync("canary_checks", new
            {
                run_id = runId, phone_id = t.PhoneId, phone_number = t.Number, host_id = t.HostId,
                check_code = code, status = "skipped",
                error = "Cloud API target - business→business delivery is unverified"
            }, ct);
            return false;
        }

        var row = await rpc.InsertAsync("canary_checks", new
        {
            run_id = runId, phone_id = t.PhoneId, phone_number = t.Number, host_id = t.HostId,
            check_code = code, status = "pending"
        }, ct);

        if (!row.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var checkId))
            return false;

        // sent_at goes in before the HTTP call returns: a fast phone can ACK
        // while /send/template is still in flight, and the ACK handler only
        // accepts rows already in 'sent'.
        await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new
        {
            status = "sent", sent_at = DateTime.UtcNow
        }, ct);

        var (ok, wamid, error) = await prober.SendCheckAsync(t.Number, cfg.Template, cfg.Language, code, ct);

        if (ok)
        {
            await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new { wa_message_id = wamid }, ct);
            return true;
        }

        await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}&status=eq.sent", new
        {
            status = "send_failed", error
        }, ct);
        _log.LogWarning("[CANARY] Send to {Number} failed: {Error}", t.Number, error);
        return false;
    }

    private async Task<(int passed, int failed, int skipped)> CloseRunAsync(
        SupabaseRpc rpc, Guid runId, CancellationToken ct)
    {
        var rows = await rpc.SelectAsync("canary_checks", $"select=status&run_id=eq.{runId}", ct);

        int passed = 0, failed = 0, skipped = 0;
        foreach (var r in rows.EnumerateArray())
        {
            switch (r.GetProperty("status").GetString())
            {
                case "passed":  passed++;  break;
                case "skipped": skipped++; break;
                // send_failed means the prober could not send - not the
                // target's fault, but still not a healthy phone we can vouch for.
                default:        failed++;  break;
            }
        }

        await rpc.PatchAsync("canary_runs", $"id=eq.{runId}", new
        {
            status = "completed", finished_at = DateTime.UtcNow, passed, failed, skipped
        }, ct);

        _log.LogWarning("[CANARY] Run {RunId} done | passed={P} failed={F} skipped={S}",
            runId, passed, failed, skipped);
        return (passed, failed, skipped);
    }

    // ═════════════════════════════════════════════════════════ notify ══

    private static bool IsHealthy(string? s) => s is "passed" or "late_ack";
    private static bool Counts(string? s)    => s is not ("skipped" or "send_failed" or null);

    /// <summary>
    /// One notification when a phone goes down, one when it comes back -
    /// not one every 15 minutes while it stays down. The previous verdict
    /// for the same phone decides; skipped and send_failed carry no verdict
    /// about the phone, so they are looked past.
    /// </summary>
    private async Task NotifyTransitionsAsync(SupabaseRpc rpc, Guid runId, CancellationToken ct)
    {
        try
        {
            var rows = await rpc.SelectAsync("canary_checks",
                $"select=id,phone_id,phone_number,status,error,created_at&run_id=eq.{runId}&notified=is.false", ct);

            foreach (var r in rows.EnumerateArray())
            {
                var status = r.GetProperty("status").GetString();
                if (!Counts(status)) continue;

                var checkId = r.GetProperty("id").GetString();
                var phoneId = r.GetProperty("phone_id").GetString();
                var number  = r.TryGetProperty("phone_number", out var n) ? n.GetString() : null;
                var error   = r.TryGetProperty("error", out var e) ? e.GetString() : null;
                var created = r.GetProperty("created_at").GetString();

                var prev = await rpc.SelectAsync("canary_checks",
                    $"select=status&phone_id=eq.{phoneId}&created_at=lt.{Uri.EscapeDataString(created!)}" +
                    "&status=not.in.(skipped,send_failed,pending,sent)&order=created_at.desc&limit=1", ct);

                string? prevStatus = null;
                foreach (var p in prev.EnumerateArray()) prevStatus = p.GetProperty("status").GetString();

                var nowOk  = IsHealthy(status);
                var wasOk  = prevStatus == null || IsHealthy(prevStatus);   // first ever check: assume it was fine
                if (nowOk == wasOk) continue;

                Guid? userId = null;
                var owner = await rpc.SelectAsync("phones", $"select=user_id&id=eq.{phoneId}&limit=1", ct);
                foreach (var o in owner.EnumerateArray())
                    if (o.TryGetProperty("user_id", out var u) && Guid.TryParse(u.GetString(), out var ug)) userId = ug;

                await rpc.InsertAsync("notifications", new
                {
                    user_id   = userId,
                    phone_id  = phoneId,
                    title     = nowOk ? "הטלפון חזר לתקינות" : "בדיקת תקינות נכשלה",
                    message   = nowOk
                        ? $"הטלפון {number} עונה שוב לבדיקת התקינות."
                        : $"הטלפון {number} לא אישר את בדיקת התקינות. {error}",
                    log_level = nowOk ? "info" : "error",
                    source    = "canary",
                    is_read   = false,
                    extra     = new { run_id = runId, check_id = checkId, status, previous = prevStatus }
                }, ct);

                await rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new { notified = true }, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Notification pass failed for run {RunId}", runId);
        }
    }
}
