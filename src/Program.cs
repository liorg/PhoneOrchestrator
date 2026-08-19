using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;

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
builder.Services.AddControllers();

var app = builder.Build();

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
