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

    /// <summary>
    /// "full" (default) - host orchestration + dashboard, on michal.
    /// "canary"         - canary runner only, on botinbound next to the prober:
    ///                    no host probing, no draining.
    /// </summary>
    public string Mode { get; set; } = "full";

    // ═══════════════════════════════════════════════════════════ canary ══
    //
    // v2: one Cloud API prober, one template, one ACK. No Worker, no
    // scenarios on the monitored phones, no Redis. Tunables that change at
    // runtime (interval, timeout, template) live in bot_config['canary.*'];
    // what is here is wiring that only changes with a redeploy.

    /// <summary>
    /// The prober's whatsapp-cloudapi container, by name on whqueue-net,
    /// e.g. http://canary-prober:8000. Sends the template and answers /health.
    /// </summary>
    public string CanaryProberUrl { get; set; } = "";

    /// <summary>
    /// agent_hosts.id of the canary host (botinbound, kind='canary'). The
    /// runner keeps its last_heartbeat fresh, so no Manager mistakes it for a
    /// dead host and takes over the prober's phone. Empty = no heartbeat.
    /// </summary>
    public string CanaryHostId { get; set; } = "";

    /// <summary>The prober's own number. Excluded from the sweep.</summary>
    public string CanaryProberNumber { get; set; } = "";

    /// <summary>
    /// How the prober reaches us, e.g. http://orchestrator:8080. The webhook
    /// registered on the prober is {this}/canary/inbound/{token}.
    /// </summary>
    public string CanaryCallbackUrl { get; set; } = "";

    /// <summary>
    /// Shared secret in the callback path. The inbound routes are outside
    /// AuthMiddleware (the container sends no credentials), so this is the
    /// only thing standing between the open port and a forged ACK.
    /// </summary>
    public string CanaryInboundToken { get; set; } = "";

    /// <summary>
    /// Does THIS instance run the canary runner: true on botinbound, false on
    /// michal. Whether it actually sweeps is canary_control.enabled, at runtime.
    /// </summary>
    public bool CanaryEnabled { get; set; } = false;

    // ═════════════════════════════════════════════ notification email ══
    //
    // NotificationMailer: notifications (is_send=false) → Resend, per
    // users.notify_email / notify_errors_only. Full mode (michal) only.

    /// <summary>Master switch. false = the mailer does not start.</summary>
    public bool NotifyEmailEnabled { get; set; } = false;

    /// <summary>Resend API key. /run/secrets/resend_api_key wins over the env var.</summary>
    public string ResendApiKey { get; set; } = "";

    /// <summary>Sender - must be on a domain verified in Resend.</summary>
    public string NotifyFromEmail { get; set; } = "noreply@mg.michal-solutions.com";

    /// <summary>Name in the subject and the email header.</summary>
    public string NotifyAppName { get; set; } = "ScenarioBot";

    /// <summary>Frontend - the "open" button and the settings link.</summary>
    public string NotifySiteUrl { get; set; } = "https://grossman.bot";

    /// <summary>Time between sweeps of the queue.</summary>
    public int NotifyIntervalSeconds { get; set; } = 60;

    /// <summary>Rows read from the queue per sweep.</summary>
    public int NotifyBatchSize { get; set; } = 200;

    /// <summary>Most rows in one digest; the rest wait for the next sweep.</summary>
    public int NotifyMaxItemsPerEmail { get; set; } = 20;

    /// <summary>Older unsent rows are skipped ('stale') instead of mailed late.</summary>
    public int NotifyMaxAgeHours { get; set; } = 24;

    /// <summary>Tries per row on retryable errors (429 / 5xx / network).</summary>
    public int NotifyMaxAttempts { get; set; } = 3;
}