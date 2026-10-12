using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Thin PostgREST caller. Anything with real logic goes through an RPC - one
/// round trip per endpoint, all queue and eligibility rules stay in the DB.
///
/// The generic table helpers at the bottom exist for the canary tables only.
/// They hold at most a handful of rows each and every operation on them is a
/// plain single-table read or write, so an RPC layer would be ceremony.
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

    // Nulls in a PATCH body would blank the column rather than leave it be,
    // which is never what a partial update means here.
    private static readonly JsonSerializerOptions PatchOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
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

    /// <summary>Plain table read - only used by the scan loop to get IPs to probe. Manager hosts only.</summary>
    public async Task<List<AgentHostRow>> GetHostsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync(
            "rest/v1/agent_hosts?select=id,host_name,ip_address,status,last_heartbeat" +
            // kind='canary' (botinbound) has no HostAgent on :5000 - probing it
            // would fail every scan and, with AutoDrain on, drain its phone.
            "&or=(kind.is.null,kind.neq.canary)&order=host_name", ct);

        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"agent_hosts read -> {(int)res.StatusCode}: {text}");

        return JsonSerializer.Deserialize<List<AgentHostRow>>(text, JsonOpts) ?? new();
    }

    // ══════════════════════════════════════════════ generic table access ══

    /// <summary>GET rest/v1/{table}?{query} - returns the raw array.</summary>
    public async Task<JsonElement> SelectAsync(string table, string query, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"rest/v1/{table}?{query}", ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("Select {Table} failed {Status}: {Body}", table, (int)res.StatusCode, text);
            throw new HttpRequestException($"select {table} -> {(int)res.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// POST rest/v1/{table}. Prefer: return=representation so the caller gets
    /// the row back with its generated id rather than having to read again.
    /// Accepts a single object or a list, for batch inserts.
    /// </summary>
    public async Task<JsonElement> InsertAsync(string table, object row, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"rest/v1/{table}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(row), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Prefer", "return=representation");

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("Insert {Table} failed {Status}: {Body}", table, (int)res.StatusCode, text);
            throw new HttpRequestException($"insert {table} -> {(int)res.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text);
        var root = doc.RootElement;

        return (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            ? root[0]
            : root).Clone();
    }

    /// <summary>
    /// PATCH rest/v1/{table}?{filter}. The filter is not optional: PostgREST
    /// applies an unfiltered PATCH to every row in the table.
    /// </summary>
    public async Task PatchAsync(string table, string filter, object patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filter))
            throw new ArgumentException(
                "A filter is required - an unfiltered PATCH rewrites the whole table.", nameof(filter));

        using var req = new HttpRequestMessage(HttpMethod.Patch, $"rest/v1/{table}?{filter}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(patch, PatchOpts), Encoding.UTF8, "application/json")
        };

        using var res = await _http.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            _log.LogError("Patch {Table} failed {Status}: {Body}", table, (int)res.StatusCode, text);
            throw new HttpRequestException($"patch {table} -> {(int)res.StatusCode}: {text}");
        }
    }

    /// <summary>PATCH with a pre-serialised body - for writing explicit nulls.</summary>
    public async Task PatchRawAsync(string table, string filter, string json, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filter))
            throw new ArgumentException("A filter is required.", nameof(filter));

        using var req = new HttpRequestMessage(HttpMethod.Patch, $"rest/v1/{table}?{filter}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            _log.LogError("Patch {Table} failed {Status}: {Body}", table, (int)res.StatusCode, text);
            throw new HttpRequestException($"patch {table} -> {(int)res.StatusCode}: {text}");
        }
    }

    /// <summary>
    /// PATCH that returns the rows it actually changed. The filter doubles as
    /// a compare-and-set: with status=eq.sent in it, the second of two
    /// concurrent writers gets an empty array instead of overwriting.
    /// </summary>
    public async Task<JsonElement> PatchReturningAsync(
        string table, string filter, object patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filter))
            throw new ArgumentException(
                "A filter is required - an unfiltered PATCH rewrites the whole table.", nameof(filter));

        using var req = new HttpRequestMessage(HttpMethod.Patch, $"rest/v1/{table}?{filter}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(patch, PatchOpts), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Prefer", "return=representation");

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("Patch {Table} failed {Status}: {Body}", table, (int)res.StatusCode, text);
            throw new HttpRequestException($"patch {table} -> {(int)res.StatusCode}: {text}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text);
        return doc.RootElement.Clone();
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