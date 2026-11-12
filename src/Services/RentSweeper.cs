using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Rented phones: every RentSweepSeconds calls <c>rpc_rent_sweep</c> (sql/11_rent_notify.sql), which
///
///   1. warns the customer once when bot_config rent.warn_percent of the rental has passed
///      → notifications (log_level warning)
///   2. ends every rental whose end_time passed → wipe + back to the pool (user_id = system)
///      → notifications (log_level error) "rental ended"
///
/// The emails go out through NotificationMailer like any other notification.
/// Safe with more than one runner (this one and RentDevice.py in the backend): the RPC
/// claims rows with FOR UPDATE SKIP LOCKED and the warn/end flags are set once.
///
/// Runs on the full instance (not canary-only) - see Program.cs.
/// </summary>
public sealed class RentSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<RentSweeper> _log;

    public RentSweeper(IServiceScopeFactory scopes, IOptions<OrchestratorOptions> opt, ILogger<RentSweeper> log)
    {
        _scopes = scopes;
        _opt    = opt.Value;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.RentSweepEnabled)
        {
            _log.LogInformation("[RENT] sweeper off (Orchestrator__RentSweepEnabled=false)");
            return;
        }

        var every = TimeSpan.FromSeconds(Math.Max(10, _opt.RentSweepSeconds));
        _log.LogInformation("[RENT] sweeper starting | every={Every}s keep={Keep}",
            every.TotalSeconds, string.IsNullOrWhiteSpace(_opt.RentKeepTemplates) ? "(bot_config)" : _opt.RentKeepTemplates);

        if (!await Delay(TimeSpan.FromSeconds(15), ct)) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[RENT] sweep failed");
            }

            if (!await Delay(every, ct)) break;
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();

        string[]? keep = string.IsNullOrWhiteSpace(_opt.RentKeepTemplates)
            ? null
            : _opt.RentKeepTemplates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var res = SupabaseRpc.Unwrap(await rpc.CallAsync("rpc_rent_sweep", new { p_keep_templates = keep }, ct));

        foreach (var (kind, label) in new[] { ("warned", "warned"), ("expired", "expired → pool") })
        {
            if (res.ValueKind != JsonValueKind.Object || !res.TryGetProperty(kind, out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var r in arr.EnumerateArray())
                _log.LogInformation("[RENT] {Label} phone={Phone} number={Number} user={User}",
                    label, Str(r, "phone_id"), Str(r, "number"), Str(r, "user_id"));
        }
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<bool> Delay(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
