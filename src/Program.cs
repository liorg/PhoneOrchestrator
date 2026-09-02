using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Swarm mounts secrets as files. A file wins over the env var so the
// service-role key never has to appear in `docker service inspect`.
foreach (var (secretFile, configKey) in new[]
{
    ("/run/secrets/supabase_key", "Orchestrator:SupabaseKey"),
    ("/run/secrets/supabase_url",         "Orchestrator:SupabaseUrl")
})
{
    if (File.Exists(secretFile))
        builder.Configuration[configKey] = File.ReadAllText(secretFile).Trim();
}

builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection("Orchestrator"));

builder.Services.AddSingleton<ScanState>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<AuthTokens>();
builder.Services.AddHttpClient<SupabaseRpc>();
builder.Services.AddHttpClient<HostProbe>();
builder.Services.AddHostedService<OrchestratorLoop>();

// ── canary ───────────────────────────────────────────────────────────
// Redis is optional by design: without it the canary loses its sub-event
// trail but still produces verdicts, so an outage degrades diagnostics
// rather than stopping health checks.
var redisUrl = builder.Configuration["Orchestrator:RedisUrl"];
if (!string.IsNullOrWhiteSpace(redisUrl))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var cfg = ConfigurationOptions.Parse(redisUrl);
        cfg.AbortOnConnectFail = false;   // keep retrying instead of throwing at startup
        cfg.ConnectRetry       = 3;
        cfg.ConnectTimeout     = 5000;
        return ConnectionMultiplexer.Connect(cfg);
    });
}

builder.Services.AddSingleton<CanaryStore>();
builder.Services.AddHttpClient<CanaryRelay>();
builder.Services.AddHostedService<CanaryLoop>();


builder.Services.AddControllers();

var app = builder.Build();

// nginx terminates TLS, so without this the app sees every request as plain
// HTTP from 127.0.0.1: the session cookie would never be marked Secure, and
// every failed sign-in would be logged as coming from the proxy rather than
// from whoever actually tried. Defaults trust loopback proxies only, which
// is exactly our case - nginx runs on the same host.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
if (string.IsNullOrEmpty(builder.Configuration["Orchestrator:AuthPassword"]))
{
    app.Logger.LogWarning(
        "Orchestrator:AuthPassword is empty - the dashboard and API are UNAUTHENTICATED.");
}

app.UseMiddleware<AuthMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

// Liveness for Swarm.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Unique marker per build - the reliable way to confirm what Swarm is running.
app.MapGet("/version", () => Results.Ok(new
{
    service = "PhoneOrchestrator",
    version = BuildInfo.Version,
    marker  = BuildInfo.Marker,
    utc     = DateTime.UtcNow
}));

app.Run();
