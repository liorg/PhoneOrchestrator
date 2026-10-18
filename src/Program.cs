using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Swarm mounts secrets as files. A file wins over the env var so the
// service-role key never has to appear in `docker service inspect`.
foreach (var (secretFile, configKey) in new[]
{
    ("/run/secrets/supabase_key", "Orchestrator:SupabaseKey"),
    ("/run/secrets/supabase_url",         "Orchestrator:SupabaseUrl"),
    ("/run/secrets/canary_inbound_token", "Orchestrator:CanaryInboundToken"),
    ("/run/secrets/resend_api_key",       "Orchestrator:ResendApiKey")
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
// Mode=canary (botinbound) runs the canary runner only - two instances
// probing and draining the same hosts would race each other.
var canaryOnly = string.Equals(builder.Configuration["Orchestrator:Mode"], "canary",
                               StringComparison.OrdinalIgnoreCase);
if (!canaryOnly)
    builder.Services.AddHostedService<OrchestratorLoop>();

// ── notification email ───────────────────────────────────────────────
// notifications → Resend. Full instance only, for the same reason as the
// loop above; the claim in NotificationMailer makes a second one harmless
// anyway. Starts only when Orchestrator__NotifyEmailEnabled=true.
builder.Services.AddHttpClient<ResendClient>();
if (!canaryOnly)
    builder.Services.AddHostedService<NotificationMailer>();

// ── canary ───────────────────────────────────────────────────────────
// v2: Cloud API prober + CHECK template + ACK. No Redis, no Worker.
builder.Services.AddSingleton<CanaryStore>();
builder.Services.AddHttpClient<CanaryProber>();
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