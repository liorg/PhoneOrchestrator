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

    /// <summary>null -> read bot_config['orchestrator.env']. Otherwise "dev" | "preprod".</summary>
    public string? Env { get; set; }

    /// <summary>
    /// Master switch. false = observe and report only, never touch phones.
    /// Ships false on purpose - flip it once you trust the dashboard.
    /// </summary>
    public bool AutoDrain { get; set; } = false;
}
