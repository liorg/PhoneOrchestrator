using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Thin PostgREST caller. Everything goes through an RPC - one round trip per
/// endpoint, all queue and eligibility logic stays in the DB layer.
/// </summary>
public sealed class SupabaseRpc
{
    private readonly HttpClient _http;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<SupabaseRpc> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SupabaseRpc(HttpClient http, IOptions<OrchestratorOptions> opt, ILogger<SupabaseRpc> log)
    {
        _opt  = opt.Value;
        _log  = log;
        _http = http;

        if (string.IsNullOrWhiteSpace(_opt.SupabaseUrl))
            throw new InvalidOperationException("Orchestrator:SupabaseUrl is not configured.");

        _http.BaseAddress = new Uri(_opt.SupabaseUrl.TrimEnd('/') + "/");
        _http.Timeout     = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add("apikey", _opt.SupabaseKey);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _opt.SupabaseKey);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Calls rest/v1/rpc/{fn} and returns the raw JSON element.</summary>
    public async Task<JsonElement> CallAsync(string fn, object? args, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(args ?? new { });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"rest/v1/rpc/{fn}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("RPC {Fn} failed {Status}: {Body}", fn, (int)res.StatusCode, text);
            throw new HttpRequestException($"RPC {fn} -> {(int)res.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// PostgREST wraps set-returning functions in an array. Unwraps a single
    /// object result, mirroring the _unwrap() helper on the Python side.
    /// </summary>
    public static JsonElement Unwrap(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
            return el.GetArrayLength() > 0 ? el[0] : default;
        return el;
    }

    /// <summary>Plain table read - only used by the scan loop to get IPs to probe.</summary>
    public async Task<List<AgentHostRow>> GetHostsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync(
            "rest/v1/agent_hosts?select=id,host_name,ip_address,status,last_heartbeat&order=host_name", ct);

        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"agent_hosts read -> {(int)res.StatusCode}: {text}");

        return JsonSerializer.Deserialize<List<AgentHostRow>>(text, JsonOpts) ?? new();
    }
}

public sealed class AgentHostRow
{
    public Guid      Id            { get; set; }
    public string    Host_Name     { get; set; } = "";
    public string?   Ip_Address    { get; set; }
    public string?   Status        { get; set; }
    public DateTime? Last_Heartbeat{ get; set; }
}
