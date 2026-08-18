using PhoneOrchestrator.Models;
using PhoneOrchestrator.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection("Orchestrator"));

builder.Services.AddSingleton<ScanState>();
builder.Services.AddHttpClient<SupabaseRpc>();
builder.Services.AddHttpClient<HostProbe>();
builder.Services.AddHostedService<OrchestratorLoop>();
builder.Services.AddControllers();

var app = builder.Build();

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
