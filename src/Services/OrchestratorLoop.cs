using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// The only component that decides a host is gone.
///
/// Eviction trigger and placement rules are deliberately separate concerns:
///   * a host is EVICTED when it stops answering (unreachable / stale / not active)
///   * a target is CHOSEN by the five gates inside rpc_orch_pick_host
/// A busy host is therefore never drained - it just stops receiving phones.
/// </summary>
public sealed class OrchestratorLoop : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ScanState _state;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<OrchestratorLoop> _log;

    private readonly Dictionary<Guid, int> _failures = new();

    public OrchestratorLoop(
        IServiceScopeFactory scopes,
        ScanState state,
        IOptions<OrchestratorOptions> opt,
        ILogger<OrchestratorLoop> log)
    {
        _scopes = scopes;
        _state  = state;
        _opt    = opt.Value;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation(
            "PhoneOrchestrator {Marker} starting. interval={Interval}s stale={Stale}s " +
            "failuresBeforeDrain={Threshold} autoDrain={AutoDrain}",
            BuildInfo.Marker, _opt.ScanIntervalSeconds, _opt.StaleAfterSeconds,
            _opt.FailuresBeforeDrain, _opt.AutoDrain);

        // Let HostAgents settle before the first verdict.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
                _state.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _state.LastError = ex.Message;
                _log.LogError(ex, "Sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_opt.ScanIntervalSeconds), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var rpc   = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
        var probe = scope.ServiceProvider.GetRequiredService<HostProbe>();

        var hosts = await rpc.GetHostsAsync(ct);

        var probes = await Task.WhenAll(hosts.Select(async h =>
            (Host: h, Result: await probe.ProbeAsync(h.Ip_Address, ct))));

        foreach (var (host, result) in probes)
        {
            // A host taken out of rotation by hand counts as unavailable.
            var active  = string.Equals(host.Status, "active", StringComparison.OrdinalIgnoreCase);
            var healthy = result.Reachable && active;

            int fails;
            if (healthy)
            {
                _failures.Remove(host.Id);
                fails = 0;
            }
            else
            {
                fails = _failures.TryGetValue(host.Id, out var n) ? n + 1 : 1;
                _failures[host.Id] = fails;
            }

            var shouldDrain = fails >= _opt.FailuresBeforeDrain;

            _state.Record(new ProbeSnapshot(
                HostId:              host.Id,
                HostName:            host.Host_Name,
                IpAddress:           host.Ip_Address,
                Reachable:           result.Reachable,
                StatusCode:          result.StatusCode,
                Error:               active ? result.Error : $"host status is '{host.Status}'",
                ElapsedMs:           result.ElapsedMs,
                ConsecutiveFailures: fails,
                DrainPending:        shouldDrain,
                CheckedAtUtc:        DateTime.UtcNow));

            if (!shouldDrain) continue;

            if (!_opt.AutoDrain)
            {
                _log.LogWarning(
                    "Host {Host} unavailable ({Fails} consecutive). AutoDrain is off - not moving phones.",
                    host.Host_Name, fails);
                continue;
            }

            await DrainAsync(rpc, host, fails, ct);
        }

        _state.CompleteScan();
    }

    private async Task DrainAsync(SupabaseRpc rpc, AgentHostRow host, int fails, CancellationToken ct)
    {
        _log.LogWarning("Draining {Host} after {Fails} consecutive failed probes", host.Host_Name, fails);

        try
        {
            var el = await rpc.CallAsync("rpc_orch_drain_host", new
            {
                p_host_id = host.Id,
                p_env     = _opt.Env,
                p_reason  = $"HOST_UNREACHABLE:{fails}"
            }, ct);

            var root   = SupabaseRpc.Unwrap(el);
            var moved  = root.TryGetProperty("moved",  out var m) ? m.GetInt32() : 0;
            var failed = root.TryGetProperty("failed", out var f) ? f.GetInt32() : 0;

            _state.RecordDrain(new DrainRecord(host.Id, host.Host_Name, moved, failed, null, DateTime.UtcNow));
            _log.LogWarning("Drain of {Host} complete: moved={Moved} failed={Failed}",
                host.Host_Name, moved, failed);

            // Successful drain resets the counter so a flapping host does not
            // re-drain an already empty machine every cycle.
            if (failed == 0) _failures[host.Id] = 0;
        }
        catch (Exception ex)
        {
            _state.RecordDrain(new DrainRecord(host.Id, host.Host_Name, 0, 0, ex.Message, DateTime.UtcNow));
            _log.LogError(ex, "Drain of {Host} failed", host.Host_Name);
        }
    }
}
