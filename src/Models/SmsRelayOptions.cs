namespace PhoneOrchestrator.Models;

/// <summary>
/// Section "Sms":
///   Sms__Enabled=false   Sms__NatsUrl=nats://nats-1:4222   Sms__ForwardMaxAgeSec=600
///   Sms__ManagerWebhookSecret   Sms__AgentToken   Sms__ManagerPort
/// The Manager keys are separate, like Mail__ManagerWebhookSecret: canary-runner
/// (botinbound) has no Orchestrator__ManagerWebhookSecret / AgentToken. Empty →
/// falls back to Orchestrator:ManagerWebhookSecret / AgentToken / ManagerPort (HubController's keys).
/// </summary>
public sealed class SmsRelayOptions
{
    public bool   Enabled          { get; set; }
    public string NatsUrl          { get; set; } = "nats://nats-1:4222";
    public string Stream           { get; set; } = "SMS_IN";
    public string Subject          { get; set; } = "sms.in";
    public string Durable          { get; set; } = "orch-sms-relay";
    /// <summary>Deliveries before giving up (consumer MaxDeliver = this + 1, as in MailRelay).</summary>
    public int    MaxDeliver       { get; set; } = 10;
    /// <summary>Stored always; forwarded to the Manager only if younger than this (stale OTP = useless). 0 = no limit.</summary>
    public int    ForwardMaxAgeSec { get; set; } = 600;

    /// <summary>X-Webhook-Secret → Manager. = AppSettings:WebhookSecret in the Manager (same as sms-event today).</summary>
    public string? ManagerWebhookSecret { get; set; }
    /// <summary>X-Agent-Token → Manager. = AGENT_TOKEN of the backend.</summary>
    public string? AgentToken           { get; set; }
    /// <summary>Manager port on agent_hosts.ip_address. Default 5000.</summary>
    public int?    ManagerPort          { get; set; }
}
