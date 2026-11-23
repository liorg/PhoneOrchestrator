using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Inbox per phone for mail_expect. Every Mail__ProvisionIntervalSec: phones
/// with a number and no active mailbox → Cloudflare Email Routing rule
/// (to = 972504476645@grossmanbot.download → Worker mail-worker) + a row in
/// mailboxes. Idempotent: an existing rule for the address is reused, the
/// mailbox upsert is keyed on address.
///
/// Routing does not depend on the rule — the catch-all already sends every
/// address to the Worker, and mailboxes is what maps it to a phone. The rule
/// only makes the address visible in Cloudflare. Cloudflare allows 200 rules
/// per zone; past Mail__CfMaxRules the row is still written, without a rule.
///
/// Owner change (rental ended → user_id = system, or a new rental): trigger trg_mail_phone_owner
/// (sql/15) deletes that phone's mail_inbound rows and moves the mailbox to the new owner.
/// As a fallback, every sweep first calls rpc_mail_release_stale (sql/14), which does the same.
///
/// botinbound only (Mode=canary), next to MailRelay: one instance, so two
/// sweeps can never create the same rule twice. Skips the canary prober number.
///   Mail__ProvisionEnabled=false   Mail__ProvisionIntervalSec=300
///   Mail__Domain=grossmanbot.download   Mail__WorkerName=mail-worker
///   Mail__CfZoneId   Mail__CfApiToken (/run/secrets/cf_api_token wins)   Mail__CfMaxRules=190
/// </summary>
public sealed class MailboxProvisioner : BackgroundService
{
    private const string CfApi = "https://api.cloudflare.com/client/v4";
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _http;
    private readonly OrchestratorOptions _orch;
    private readonly MailProvisionOptions _opt;
    private readonly ILogger<MailboxProvisioner> _log;
    private bool _failing;

    public MailboxProvisioner(IHttpClientFactory http, IOptions<OrchestratorOptions> orch,
                              IOptions<MailProvisionOptions> opt, ILogger<MailboxProvisioner> log)
    {
        _http = http;
        _orch = orch.Value;
        _opt  = opt.Value;
        _log  = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.ProvisionEnabled)
        {
            _log.LogInformation("[MAILBOX] disabled");
            return;
        }
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_orch.SupabaseUrl)) missing.Add("Orchestrator:SupabaseUrl");
        if (string.IsNullOrWhiteSpace(_orch.SupabaseKey)) missing.Add("Orchestrator:SupabaseKey");
        if (string.IsNullOrWhiteSpace(_opt.CfZoneId))     missing.Add("Mail:CfZoneId");
        if (string.IsNullOrWhiteSpace(_opt.CfApiToken))   missing.Add("Mail:CfApiToken");
        if (missing.Count > 0)
        {
            _log.LogError("[MAILBOX] missing {Keys} — not started", string.Join(", ", missing));
            return;
        }

        var every = TimeSpan.FromSeconds(Math.Max(60, _opt.ProvisionIntervalSec));
        _log.LogInformation("[MAILBOX] started | domain={D} worker={W} every={S}s max_rules={M}",
            _opt.Domain, _opt.WorkerName, every.TotalSeconds, _opt.CfMaxRules);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = _http.CreateClient(nameof(MailboxProvisioner));
                http.Timeout = HttpTimeout;
                var r = await SweepAsync(http, ct);

                if (_failing)
                {
                    _failing = false;
                    _log.LogInformation("[MAILBOX] sweep recovered");
                }
                if (r.Created + r.Reused + r.Failed + r.Released > 0)
                    _log.LogInformation("[MAILBOX] sweep | released={X} created={C} reused={R} no_rule={N} failed={F} rules={T}",
                        r.Released, r.Created, r.Reused, r.NoRule, r.Failed, r.RulesTotal);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (!_failing)
                {
                    _failing = true;
                    _log.LogError("[MAILBOX] sweep failed: {Err} — retrying every sweep, logged once until it recovers", ex.Message);
                }
            }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { }
        }
    }

    public sealed record SweepResult(int Candidates, int Created, int Reused, int NoRule, int Failed, int RulesTotal, int Released = 0);

    /// <summary>One sweep. Public for tests.</summary>
    public async Task<SweepResult> SweepAsync(HttpClient http, CancellationToken ct)
    {
        var released = await ReleaseStaleAsync(http, ct);

        var phones = await SupaGetAsync(http, "phones?select=id,number,user_id&number=not.is.null", ct);
        var boxes  = await SupaGetAsync(http, "mailboxes?select=phone_id&is_active=eq.true", ct);
        var have   = boxes.Select(b => Str(b, "phone_id")).Where(s => s != null).ToHashSet();
        var skip   = Digits(_orch.CanaryProberNumber);

        var todo = phones
            .Where(p => !have.Contains(Str(p, "id")))
            .Where(p => string.IsNullOrEmpty(skip) || Digits(Str(p, "number")) != skip)
            .ToList();
        if (todo.Count == 0) return new(0, 0, 0, 0, 0, -1, released);

        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await CfRulesAsync(http, ct))
            if (RuleAddress(r) is { } a && Str(r, "id") is { } id) rules[a] = id;

        int created = 0, reused = 0, noRule = 0, failed = 0;
        foreach (var p in todo)
        {
            var phoneId = Str(p, "id")!;
            var addr = AddressFor(Str(p, "number"), _opt.Domain);
            if (addr == null) continue;

            try
            {
                string? ruleId;
                if (rules.TryGetValue(addr, out var existing))
                {
                    ruleId = existing; reused++;
                }
                else if (rules.Count >= _opt.CfMaxRules)
                {
                    ruleId = null; noRule++;   // catch-all still routes it; mailboxes is what maps
                }
                else
                {
                    ruleId = await CfCreateAsync(http, addr, phoneId, ct);
                    rules[addr] = ruleId;
                    created++;
                }

                await SupaUpsertMailboxAsync(http, new JsonObject
                {
                    ["address"]        = addr,
                    ["phone_id"]       = phoneId,
                    ["user_id"]        = Str(p, "user_id"),
                    ["is_active"]      = true,
                    ["cf_rule_id"]     = ruleId,
                    ["provisioned_at"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["last_error"]     = null,
                }, ct);

                _log.LogInformation("[MAILBOX] {Addr} → phone={Phone} rule={Rule}", addr, phoneId, ruleId ?? "(catch-all)");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                _log.LogError("[MAILBOX] {Addr} phone={Phone} failed: {Err}", addr, phoneId, ex.Message);
            }
        }
        return new(todo.Count, created, reused, noRule, failed, rules.Count, released);
    }

    /// <summary>Fallback: owner changed → old owner's mails deleted, mailbox moved to the new owner.</summary>
    private async Task<int> ReleaseStaleAsync(HttpClient http, CancellationToken ct)
    {
        using var req = Supa(HttpMethod.Post, "rpc/rpc_mail_release_stale");
        req.Content = JsonContent.Create(new { });
        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"rpc_mail_release_stale -> {(int)res.StatusCode}: {Trim(raw)}");

        var arr = (JsonNode.Parse(raw) as JsonObject)?["released"] as JsonArray;
        if (arr == null) return 0;
        foreach (var r in arr.OfType<JsonObject>())
            _log.LogInformation("[MAILBOX] released {Addr} phone={Phone} old_user={Old} new_user={New} mails_deleted={N}",
                Str(r, "address"), Str(r, "phone_id"), Str(r, "old_user"), Str(r, "new_user"), r["mails"]?.ToString());
        return arr.Count;
    }

    // ═══════════════════════════════════════════════════════ address ══

    /// <summary>+972-50-447-6645 → 972504476645@grossmanbot.download. Same as mail_provision.py.</summary>
    public static string? AddressFor(string? number, string domain)
    {
        var d = Digits(number);
        if (d.StartsWith("00")) d = d[2..];
        else if (d.StartsWith('0')) d = "972" + d[1..];
        return d.Length >= 8 ? $"{d}@{domain.ToLowerInvariant()}" : null;
    }

    private static string Digits(string? s) => Regex.Replace(s ?? "", @"\D", "");

    // ═══════════════════════════════════════════════════════ supabase ══

    private HttpRequestMessage Supa(HttpMethod m, string path)
    {
        var req = new HttpRequestMessage(m, $"{_orch.SupabaseUrl.TrimEnd('/')}/rest/v1/{path}");
        req.Headers.Add("apikey", _orch.SupabaseKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _orch.SupabaseKey);
        return req;
    }

    private async Task<List<JsonObject>> SupaGetAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var req = Supa(HttpMethod.Get, path);
        using var res = await http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"GET {path.Split('?')[0]} -> {(int)res.StatusCode}: {Trim(raw)}");
        return (JsonNode.Parse(raw) as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
    }

    private async Task SupaUpsertMailboxAsync(HttpClient http, JsonObject row, CancellationToken ct)
    {
        using var req = Supa(HttpMethod.Post, "mailboxes?on_conflict=address");
        req.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");
        req.Content = JsonContent.Create(row);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"upsert mailboxes -> {(int)res.StatusCode}: {Trim(await res.Content.ReadAsStringAsync(ct))}");
    }

    // ═════════════════════════════════════════════════════ cloudflare ══

    private HttpRequestMessage Cf(HttpMethod m, string suffix = "")
    {
        var req = new HttpRequestMessage(m, $"{CfApi}/zones/{_opt.CfZoneId}/email/routing/rules{suffix}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.CfApiToken);
        return req;
    }

    private static async Task<JsonObject> CfOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var raw = await res.Content.ReadAsStringAsync(ct);
        var j = (raw.Length > 0 ? JsonNode.Parse(raw) : null) as JsonObject ?? new JsonObject();
        if (!res.IsSuccessStatusCode || j["success"]?.GetValue<bool>() != true)
        {
            var errs = j["errors"] is JsonArray a && a.Count > 0
                ? string.Join("; ", a.OfType<JsonObject>().Select(e => $"{e["code"]}: {e["message"]}"))
                : Trim(raw);
            throw new HttpRequestException($"cloudflare {(int)res.StatusCode}: {errs}");
        }
        return j;
    }

    private async Task<List<JsonObject>> CfRulesAsync(HttpClient http, CancellationToken ct)
    {
        var all = new List<JsonObject>();
        for (var page = 1; ; page++)
        {
            using var req = Cf(HttpMethod.Get, $"?page={page}&per_page=50");
            using var res = await http.SendAsync(req, ct);
            var j = await CfOkAsync(res, ct);
            all.AddRange((j["result"] as JsonArray ?? new JsonArray()).OfType<JsonObject>());
            var pages = j["result_info"]?["total_pages"]?.GetValue<int>() ?? 1;
            if (page >= pages) return all;
        }
    }

    private async Task<string> CfCreateAsync(HttpClient http, string address, string phoneId, CancellationToken ct)
    {
        using var req = Cf(HttpMethod.Post);
        req.Content = JsonContent.Create(new
        {
            name     = $"mail:{phoneId}",
            enabled  = true,
            matchers = new[] { new { type = "literal", field = "to", value = address } },
            actions  = new[] { new { type = "worker", value = new[] { _opt.WorkerName } } },
        });
        using var res = await http.SendAsync(req, ct);
        var j = await CfOkAsync(res, ct);
        return j["result"]?["id"]?.GetValue<string>() ?? throw new HttpRequestException("cloudflare: no rule id");
    }

    private static string? RuleAddress(JsonObject rule) =>
        (rule["matchers"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(m => Str(m, "type") == "literal" && Str(m, "field") == "to") is { } m
            ? Str(m, "value")?.ToLowerInvariant() : null;

    private static string? Str(JsonObject? o, string k) =>
        o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "...";
}

public sealed class MailProvisionOptions
{
    public bool   ProvisionEnabled     { get; set; }
    public int    ProvisionIntervalSec { get; set; } = 300;
    public string Domain               { get; set; } = "grossmanbot.download";
    public string WorkerName           { get; set; } = "mail-worker";
    public string CfZoneId             { get; set; } = "";
    /// <summary>Zone → Email Routing Rules → Edit. /run/secrets/cf_api_token wins.</summary>
    public string CfApiToken           { get; set; } = "";
    public int    CfMaxRules           { get; set; } = 190;
}
