using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Controllers;
using PhoneOrchestrator.Models;
using StackExchange.Redis;

namespace PhoneOrchestrator.Services;

/// <summary>
/// State and verdicts for the canary.
///
/// Two tiers, on purpose. Sub-events - leaves, leaf statuses, worker events -
/// arrive for every step of every check, which on a fleet of twenty phones
/// every fifteen minutes is thousands of rows a day that nobody reads. They
/// go to Redis under a TTL. When a check fails they are flushed into
/// canary_events, because that is the one time the trail matters. When it
/// passes they expire quietly.
///
/// Verdicts always go to Postgres: canary_checks is the health history and
/// has to outlive a Redis restart.
///
/// Redis being unavailable degrades the trail, never the verdict - every
/// Redis call here is wrapped and failure is logged, not thrown.
/// </summary>
public sealed class CanaryStore
{
    private readonly SupabaseRpc _rpc;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<CanaryStore> _log;
    private readonly IConnectionMultiplexer? _redis;

    // Worker liveness. In-memory on purpose: a restarted orchestrator should
    // start from "unknown" rather than inherit a stale claim that the worker
    // is up.
    private readonly ConcurrentDictionary<string, WorkerState> _workers = new();

    private static readonly TimeSpan TrailTtl = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public sealed record WorkerState(
        string PhoneId, string ServiceName, int Port, string Status, DateTime SeenUtc);

    public sealed record ProberState(string State, string? Number, DateTime AtUtc);

    public ProberState? Prober { get; private set; }

    public CanaryStore(
        SupabaseRpc rpc,
        IOptions<OrchestratorOptions> opt,
        ILogger<CanaryStore> log,
        IConnectionMultiplexer? redis = null)
    {
        _rpc   = rpc;
        _opt   = opt.Value;
        _log   = log;
        _redis = redis;
    }

    private IDatabase? Db
    {
        get
        {
            try { return _redis?.GetDatabase(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[CANARY] Redis unavailable");
                return null;
            }
        }
    }

    // ══════════════════════════════════════════════════════ worker state ══

    public void RecordWorkerHeartbeat(string phoneId, string serviceName, int port, string status)
        => _workers[serviceName] = new WorkerState(phoneId, serviceName, port, status, DateTime.UtcNow);

    public IReadOnlyCollection<WorkerState> Workers() => _workers.Values.ToList();

    public bool WorkerAlive(TimeSpan within) =>
        _workers.Values.Any(w => DateTime.UtcNow - w.SeenUtc < within);

    /// <summary>
    /// Connection state of the prober's container.
    ///
    /// On authentication the number and LID are also written back to the
    /// heartbeatphones row: neither is knowable when the prober is registered
    /// by hand - WhatsApp assigns the LID - so the row is completed here
    /// rather than left half-filled.
    /// </summary>
    public async Task RecordProberStateAsync(
        string state, string? number, string? uid, CancellationToken ct)
    {
        Prober = new ProberState(state, number, DateTime.UtcNow);

        if (state != "authenticated") return;
        if (string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(uid)) return;

        try
        {
            var patch = new Dictionary<string, object?> { ["updated_at"] = DateTime.UtcNow };
            if (!string.IsNullOrWhiteSpace(number)) patch["phone_number"] = number.TrimStart('+');
            if (!string.IsNullOrWhiteSpace(uid))    patch["uid"]          = uid;

            await _rpc.PatchAsync("heartbeatphones", "status=eq.CONNECT", patch, ct);

            _log.LogWarning("[CANARY] Prober row completed | number={Number} uid={Uid}", number, uid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Could not write prober identity");
        }
    }

    /// <summary>
    /// Last inbound message seen by the prober's container. Kept only so a
    /// failed check can show what actually arrived against what the scenario
    /// expected - the Worker handles the reply itself.
    /// </summary>
    public async Task RecordInboundAsync(string? jid, JsonElement? data, CancellationToken ct)
    {
        var db = Db;
        if (db == null) return;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                jid,
                data = data?.GetRawText(),
                at   = DateTime.UtcNow
            }, Json);

            await db.ListLeftPushAsync("canary:inbound", payload);
            await db.ListTrimAsync("canary:inbound", 0, 99);
            await db.KeyExpireAsync("canary:inbound", TrailTtl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CANARY] Failed to record inbound");
        }
    }

    // ═══════════════════════════════════════════════════════════ trail ══

    /// <summary>
    /// Appends to the Redis trail for one call. Never writes to Postgres:
    /// the flush happens once, at the verdict, and only for failures.
    /// </summary>
    public async Task RecordEventAsync(string callId, string kind, object detail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callId)) return;

        var db = Db;
        if (db == null)
        {
            _log.LogDebug("[CANARY] {Kind} for {CallId} dropped - no Redis", kind, callId);
            return;
        }

        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                kind,
                detail,
                at = DateTime.UtcNow
            }, Json);

            var key = TrailKey(callId);
            await db.ListRightPushAsync(key, entry);
            await db.KeyExpireAsync(key, TrailTtl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CANARY] Failed to record {Kind} for {CallId}", kind, callId);
        }
    }

    // ══════════════════════════════════════════════════════════ checks ══

    /// <summary>
    /// Binds a canary_call_id to the check row it belongs to, so the summary
    /// can find its way home. Cached in Redis to save a lookup, but the
    /// database is the source of truth if the cache is cold.
    /// </summary>
    public async Task RegisterCallAsync(string callId, Guid checkId, CancellationToken ct)
    {
        var db = Db;
        if (db == null) return;

        try
        {
            await db.StringSetAsync($"canary:check:{callId}", checkId.ToString(), TrailTtl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CANARY] Failed to cache call mapping");
        }
    }

    public async Task MarkSentAsync(string callId, CancellationToken ct)
    {
        var checkId = await ResolveCheckAsync(callId, ct);
        if (checkId == null) return;

        await _rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new
        {
            status  = "sent",
            sent_at = DateTime.UtcNow
        }, ct);
    }

    /// <summary>
    /// The verdict.
    ///
    /// A scenario can come back "completed" and still be a failure: the
    /// Worker counts a mismatch whenever a reply arrived but did not match
    /// what the step expected. Treating status alone as the answer would mark
    /// a phone healthy while it was answering with the wrong text - the exact
    /// breakage this check exists to catch.
    /// </summary>
    public async Task<bool> CloseCheckAsync(
        string callId, SpineCompatController.SummaryIn s, CancellationToken ct)
    {
        var checkId = await ResolveCheckAsync(callId, ct);
        if (checkId == null) return false;

        var completed = string.Equals(s.Status, "completed", StringComparison.OrdinalIgnoreCase);
        var passed    = completed && s.MismatchCount == 0;

        var status = passed ? "passed"
                   : (string.Equals(s.Status, "timeout", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(s.Status, "expired", StringComparison.OrdinalIgnoreCase)
                        ? "timeout"
                        : "failed");

        var error = passed ? null
                  : (s.MismatchCount > 0
                      ? $"{s.MismatchCount} step(s) did not match the expected reply"
                      : $"scenario {s.Status} after {s.SenderCount}/{s.ExpectedCount} steps");

        var replied = s.FinishedAt ?? DateTime.UtcNow;

        await _rpc.PatchAsync("canary_checks", $"id=eq.{checkId}", new
        {
            status,
            replied_at = replied,
            latency_ms = s.DurationSeconds > 0 ? s.DurationSeconds * 1000 : (int?)null,
            error
        }, ct);

        await AppendEventAsync(checkId.Value, "summary", new
        {
            s.Status, s.SenderCount, s.ExpectedCount, s.MismatchCount, s.DurationSeconds, s.LastStepId
        }, ct);

        // The trail is only worth keeping when something went wrong.
        if (!passed)
            await FlushTrailAsync(callId, checkId.Value, ct);

        await ClearCallAsync(callId);

        _log.LogWarning("[CANARY] Check {CheckId} -> {Status}", checkId, status);
        return true;
    }

    /// <summary>
    /// Closes checks whose Worker never reported. Without this a crashed
    /// Worker leaves a check sitting at 'sent' forever and the run never
    /// completes.
    /// </summary>
    public async Task<int> ExpireStaleAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - timeout;

        var rows = await _rpc.SelectAsync("canary_checks",
            $"select=id&run_id=eq.{runId}&status=in.(pending,sent)&sent_at=lt.{cutoff:O}", ct);

        var n = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("id", out var idEl)) continue;
            if (!Guid.TryParse(idEl.GetString(), out var id)) continue;

            await _rpc.PatchAsync("canary_checks", $"id=eq.{id}", new
            {
                status     = "timeout",
                replied_at = DateTime.UtcNow,
                error      = $"no summary within {timeout.TotalSeconds:0}s"
            }, ct);

            await AppendEventAsync(id, "timeout", new { timeoutSeconds = timeout.TotalSeconds }, ct);
            n++;
        }

        if (n > 0) _log.LogWarning("[CANARY] Expired {Count} stale checks in run {RunId}", n, runId);
        return n;
    }

    // ═══════════════════════════════════════════════════════ internals ══

    private static string TrailKey(string callId) => $"canary:events:{callId}";

    private async Task<Guid?> ResolveCheckAsync(string callId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callId)) return null;

        var db = Db;
        if (db != null)
        {
            try
            {
                var cached = await db.StringGetAsync($"canary:check:{callId}");
                if (cached.HasValue && Guid.TryParse(cached.ToString(), out var hit))
                    return hit;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[CANARY] Cache read failed, falling back to DB");
            }
        }

        try
        {
            var rows = await _rpc.SelectAsync("canary_checks",
                $"select=id&canary_call_id=eq.{Uri.EscapeDataString(callId)}&limit=1", ct);

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var idEl)) continue;
                if (Guid.TryParse(idEl.GetString(), out var id)) return id;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Could not resolve check for call {CallId}", callId);
        }

        return null;
    }

    private async Task ClearCallAsync(string callId)
    {
        var db = Db;
        if (db == null) return;

        try
        {
            await db.KeyDeleteAsync($"canary:check:{callId}");
            await db.KeyDeleteAsync(TrailKey(callId));
        }
        catch { /* TTL will collect it */ }
    }

    /// <summary>Moves the Redis trail into canary_events, in one batch.</summary>
    private async Task FlushTrailAsync(string callId, Guid checkId, CancellationToken ct)
    {
        var db = Db;
        if (db == null) return;

        try
        {
            var entries = await db.ListRangeAsync(TrailKey(callId));
            if (entries.Length == 0) return;

            var runId = await RunIdForCheckAsync(checkId, ct);
            if (runId == null) return;

            var rows = new List<object>(entries.Length);

            foreach (var raw in entries)
            {
                if (raw.IsNullOrEmpty) continue;

                try
                {
                    using var doc = JsonDocument.Parse(raw.ToString());
                    var root = doc.RootElement;

                    rows.Add(new
                    {
                        check_id   = checkId,
                        run_id     = runId,
                        kind       = root.TryGetProperty("kind", out var k) ? k.GetString() : "unknown",
                        detail     = root.TryGetProperty("detail", out var d)
                                        ? JsonSerializer.Deserialize<object>(d.GetRawText())
                                        : null,
                        created_at = root.TryGetProperty("at", out var a) ? a.GetString() : null
                    });
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[CANARY] Skipping malformed trail entry");
                }
            }

            if (rows.Count > 0)
            {
                await _rpc.InsertAsync("canary_events", rows, ct);
                _log.LogInformation("[CANARY] Flushed {Count} trail entries for check {CheckId}",
                    rows.Count, checkId);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Trail flush failed for check {CheckId}", checkId);
        }
    }

    private async Task<Guid?> RunIdForCheckAsync(Guid checkId, CancellationToken ct)
    {
        try
        {
            var rows = await _rpc.SelectAsync("canary_checks",
                $"select=run_id&id=eq.{checkId}&limit=1", ct);

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("run_id", out var v)) continue;
                if (Guid.TryParse(v.GetString(), out var id)) return id;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CANARY] Could not resolve run for check {CheckId}", checkId);
        }

        return null;
    }

    private async Task AppendEventAsync(Guid checkId, string kind, object detail, CancellationToken ct)
    {
        try
        {
            var runId = await RunIdForCheckAsync(checkId, ct);
            if (runId == null) return;

            await _rpc.InsertAsync("canary_events", new
            {
                check_id = checkId,
                run_id   = runId,
                kind,
                detail
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CANARY] Failed to append {Kind} for check {CheckId}", kind, checkId);
        }
    }
}