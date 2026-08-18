using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Asks each HostAgent whether it is alive, via
/// GET {scheme}://{ip}:{port}/api/host/heartbeat?staleAfterSeconds=N
/// </summary>
public sealed class HostProbe
{
    private readonly HttpClient _http;
    private readonly OrchestratorOptions _opt;

    public HostProbe(HttpClient http, IOptions<OrchestratorOptions> opt)
    {
        _opt  = opt.Value;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(_opt.ProbeTimeoutSeconds);
    }

    public sealed record Result(
        bool             Reachable,
        int?             StatusCode,
        string?          Error,
        long             ElapsedMs,
        HeartbeatPayload? Payload);

    public async Task<Result> ProbeAsync(string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return new Result(false, null, "host has no ip_address", 0, null);

        var url = $"{_opt.HostAgentScheme}://{ip}:{_opt.HostAgentPort}" +
                  $"{_opt.HeartbeatPath}?staleAfterSeconds={_opt.StaleAfterSeconds}";

        var sw = Stopwatch.StartNew();
        try
        {
            using var res  = await _http.GetAsync(url, ct);
            var       body = await res.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!res.IsSuccessStatusCode)
                return new Result(false, (int)res.StatusCode, Trim(body), sw.ElapsedMilliseconds, null);

            HeartbeatPayload? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<HeartbeatPayload>(
                    body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // Body shape is advisory only. 200 already answered the question.
            }

            // HostAgent may report staleness in-band even while returning 200.
            var stale = payload?.IsStale == true || payload?.Healthy == false;

            return new Result(!stale, (int)res.StatusCode,
                              stale ? "agent reports stale heartbeat" : null,
                              sw.ElapsedMilliseconds, payload);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new Result(false, null, $"timeout after {_opt.ProbeTimeoutSeconds}s", sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Result(false, null, Trim(ex.Message), sw.ElapsedMilliseconds, null);
        }
    }

    private static string Trim(string s) =>
        s.Length <= 300 ? s : s[..300] + "...";
}
