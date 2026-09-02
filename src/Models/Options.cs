namespace PhoneOrchestrator.Models;

public sealed class OrchestratorOptions
{
    /// <summary>Supabase project URL, e.g. https://xxxx.supabase.co</summary>
    public string SupabaseUrl { get; set; } = "";

    /// <summary>Service-role key. Never the anon key - the RPCs write to phones.</summary>
    public string SupabaseKey { get; set; } = "";

    /// <summary>Scheme used to reach HostAgent on each host.</summary>
    public string HostAgentScheme { get; set; } = "http";

    /// <summary>HostAgent listens here (systemd, not containerised).</summary>
    public int HostAgentPort { get; set; } = 5000;

    /// <summary>Matches HostAgent's [HttpGet("heartbeat")] route.</summary>
    public string HeartbeatPath { get; set; } = "/api/host/heartbeat";

    /// <summary>Passed as ?staleAfterSeconds= to HostAgent.</summary>
    public int StaleAfterSeconds { get; set; } = 90;

    /// <summary>How often the loop sweeps every host.</summary>
    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>Per-probe HTTP timeout.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 5;

    /// <summary>Consecutive failed probes before a host is drained. Anti-flap.</summary>
    public int FailuresBeforeDrain { get; set; } = 3;

    /// <summary>
    /// Observe-only window after this service starts. Exists for the reboot
    /// case: when the whole cluster comes up, every HostAgent is briefly down.
    /// Without a grace period the loop would declare all of them dead and pile
    /// the entire fleet onto whichever host finished booting first.
    /// Should comfortably exceed the slowest HostAgent's boot time.
    /// </summary>
    public int StartupGraceSeconds { get; set; } = 180;

    /// <summary>null -> read bot_config['orchestrator.env']. Otherwise "dev" | "preprod".</summary>
    public string? Env { get; set; }

    /// <summary>Username for the dashboard and API.</summary>
    public string AuthUser { get; set; } = "admin";

    /// <summary>
    /// Password for the dashboard and API. Empty disables auth entirely,
    /// which Program.cs warns about loudly at startup. Set it via
    /// Orchestrator__AuthPassword - never commit a value here.
    /// </summary>
    public string AuthPassword { get; set; } = "";

    /// <summary>
    /// Master switch. false = observe and report only, never touch phones.
    /// Ships false on purpose - flip it once you trust the dashboard.
    /// </summary>
    public bool AutoDrain { get; set; } = false;

    // ════════════════════════════════════════════════════════════════════════
//  Add to OrchestratorOptions.
// ════════════════════════════════════════════════════════════════════════

    /// <summary>Redis for the sub-event trail. Empty disables the trail.</summary>
    public string RedisUrl { get; set; } = "";

    /// <summary>
    /// The prober's FastAPI wrapper. Port 8000, not 3001 - Baileys binds to
    /// localhost inside the container and cannot be reached over the network.
    /// </summary>
    public string CanaryWaUrl { get; set; } = "";

    /// <summary>The prober's dedicated Worker, for the init call.</summary>
    public string CanaryWorkerUrl { get; set; } = "";

    /// <summary>
    /// Where the prober's container should post its events. Read by
    /// whatsapp-canary, so it must be a service name rather than localhost.
    /// </summary>
    public string CanaryWebhookUrl { get; set; } = "";

    /// <summary>
    /// Master switch for the canary sweep. Ships false: the services come up
    /// and connect, but no message reaches a customer phone until this is on.
    /// </summary>
    public bool CanaryEnabled { get; set; } = false;
}
