using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Gets a fresh Baileys pairing code for a phone from its Manager (WhatsAppDockerManager),
/// for the re-verify flow: the docker has just removed the old linked device, now it
/// needs a code to type into WhatsApp.
///
/// The Manager is reached the same way the scan loop reaches it: agent_hosts.ip_address
/// of phones.host_id, on HostAgentScheme://ip:HostAgentPort.
///
///   use_pairing_code = false → POST /api/phones/provision {userId, phoneNumber, usePairingCode:true}
///                              (sets pairing mode and restarts the container)
///   then, until a code or the budget runs out:
///     POST /api/phones/{id}/pairing-code/refresh
///       connected        → the unlink has not reached Baileys yet - wait and retry
///       pairing_ready    → code
///       pairing_pending  → creds cleared + restart → poll GET /api/phones/{id}/qrcode
/// </summary>
public sealed class PairCodeProvisioner
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly SupabaseRpc _rpc;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<PairCodeProvisioner> _log;

    public PairCodeProvisioner(HttpClient http, SupabaseRpc rpc, IOptions<OrchestratorOptions> opt,
                               ILogger<PairCodeProvisioner> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(20);
        _rpc = rpc;
        _opt = opt.Value;
        _log = log;
    }

    public sealed record Result(bool Ok, string? Code, string Reason, string? Detail = null);

    public async Task<Result> RequestAsync(Guid phoneId, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;

        // ── phone + host ─────────────────────────────────────────────────
        var rows = await _rpc.SelectAsync("phones",
            $"select=id,number,user_id,host_id,provider,use_pairing_code&id=eq.{phoneId}&limit=1", ct);
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            return new(false, null, "no_phone");
        var phone = rows[0];

        if (!string.Equals(Str(phone, "provider") ?? "baileys", "baileys", StringComparison.OrdinalIgnoreCase))
            return new(false, null, "not_baileys", Str(phone, "provider"));

        var hostId = Str(phone, "host_id");
        if (string.IsNullOrEmpty(hostId))
            return new(false, null, "no_host");

        var hosts = await _rpc.SelectAsync("agent_hosts", $"select=ip_address&id=eq.{hostId}&limit=1", ct);
        var ip = hosts.ValueKind == JsonValueKind.Array && hosts.GetArrayLength() > 0 ? Str(hosts[0], "ip_address") : null;
        if (string.IsNullOrEmpty(ip))
            return new(false, null, "no_host_ip", hostId);

        var root = $"{_opt.HostAgentScheme}://{ip}:{_opt.HostAgentPort}/api/phones";
        _log.LogInformation("[PAIRCODE] phone={Phone} number={Number} manager={Root}", phoneId, Str(phone, "number"), root);

        string? lastStatus = null, lastError = null;

        // ── pairing mode ─────────────────────────────────────────────────
        if (!Bool(phone, "use_pairing_code"))
        {
            var (code, status, err) = await Post($"{root}/provision", new
            {
                userId = Str(phone, "user_id"), phoneNumber = Str(phone, "number"), usePairingCode = true
            }, ct);
            if (code is not null) return Done(phoneId, code, "provision");
            (lastStatus, lastError) = (status, err);
        }

        // ── refresh until a code ─────────────────────────────────────────
        var restarted = false;
        while (DateTime.UtcNow < deadline && !restarted)
        {
            var (code, status, err) = await Post($"{root}/{phoneId}/pairing-code/refresh", null, ct);
            if (code is not null) return Done(phoneId, code, "refresh");
            (lastStatus, lastError) = (status ?? lastStatus, err);
            restarted = status == "pairing_pending";
            if (!restarted) await Task.Delay(status == "connected" ? TimeSpan.FromSeconds(5) : PollEvery, ct);
        }

        // ── container restarting with clean creds → wait for the code ────
        while (DateTime.UtcNow < deadline)
        {
            var (code, status, err) = await Get($"{root}/{phoneId}/qrcode", ct);
            if (code is not null) return Done(phoneId, code, "qrcode");
            (lastStatus, lastError) = (status ?? lastStatus, err ?? lastError);
            await Task.Delay(PollEvery, ct);
        }

        _log.LogWarning("[PAIRCODE] ✗ timeout phone={Phone} last={Status} {Error}", phoneId, lastStatus, lastError);
        return new(false, null, lastStatus == "connected" ? "still_connected" : "timeout",
                   $"last status={lastStatus ?? "-"} {lastError}".Trim());
    }

    private Result Done(Guid phoneId, string code, string via)
    {
        _log.LogInformation("[PAIRCODE] ✓ phone={Phone} via={Via}", phoneId, via);
        return new(true, code, "ok");
    }

    private async Task<(string? code, string? status, string? error)> Post(string url, object? body, CancellationToken ct)
    {
        try
        {
            using var res = body is null
                ? await _http.PostAsync(url, null, ct)
                : await _http.PostAsJsonAsync(url, body, ct);
            return await Read(res, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, null, e.Message);
        }
    }

    private async Task<(string? code, string? status, string? error)> Get(string url, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(url, ct);
            return await Read(res, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, null, e.Message);
        }
    }

    // ProvisionResponse / refresh / qrcode all answer camelCase: status + pairingCode
    private static async Task<(string? code, string? status, string? error)> Read(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var r = doc.RootElement;
            var code = Str(r, "pairingCode");
            var status = Str(r, "status");
            var error = res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode} {Str(r, "error") ?? Str(r, "message")}";
            return (string.IsNullOrWhiteSpace(code) ? null : code.Trim(), status, error);
        }
        catch (JsonException)
        {
            return (null, null, $"HTTP {(int)res.StatusCode} {text[..Math.Min(text.Length, 200)]}");
        }
    }

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Bool(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
}
