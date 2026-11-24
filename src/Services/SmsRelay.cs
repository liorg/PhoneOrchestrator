using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// SMS from phone-hub. Consumes NATS sms.in (stream SMS_IN, published by whqueue /sms),
/// and for every message: rpc_sms_inbound → POST {manager}/api/webhook/sms-event/{phone}.
/// The decision itself is SmsProcessor (no NATS, tested).
///
/// Replaces POST /api/hub/sms → HubController.Sms, which stays live until phone-hub
/// is fully on whqueue (WHQUEUE_SMS_URL). Both paths are idempotent by msg_id.
///
/// botinbound only (Mode=canary): nats-1 is reachable only on whqueue-net. Options, section "Sms":
///   Sms__Enabled=false   Sms__NatsUrl=nats://nats-1:4222   Sms__ForwardMaxAgeSec=600   Sms__MaxDeliver=10
///
/// whqueue creates the stream; here only the durable pull consumer.
/// Ack = final decision. Nak(delay) = Supabase / Manager temporarily down.
/// </summary>
public sealed class SmsRelay : BackgroundService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Reconnect   = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _cfg;
    private readonly OrchestratorOptions _orch;
    private readonly SmsRelayOptions _opt;
    private readonly ILogger<SmsRelay> _log;

    private bool _failing;

    public SmsRelay(IHttpClientFactory http, IServiceScopeFactory scopes, IConfiguration cfg,
                    IOptions<OrchestratorOptions> orch, IOptions<SmsRelayOptions> opt, ILogger<SmsRelay> log)
    {
        _http   = http;
        _scopes = scopes;
        _cfg    = cfg;
        _orch   = orch.Value;
        _opt    = opt.Value;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("[SMS] relay disabled");
            return;
        }
        if (string.IsNullOrWhiteSpace(_orch.SupabaseUrl) || string.IsNullOrWhiteSpace(_orch.SupabaseKey))
        {
            _log.LogError("[SMS] SupabaseUrl / SupabaseKey missing — not started");
            return;
        }

        // Same resolution as SmsProcessor — logged once so a missing key shows at startup, not as 4xx later.
        var keys = new SmsProcessor(null!, null!, _cfg, _opt, _log);
        _log.LogInformation("[SMS] started | nats={Nats} stream={Stream} durable={Durable} max_age={Age}s manager_port={Port} webhook_secret={Secret} agent_token={Token}",
            _opt.NatsUrl, _opt.Stream, _opt.Durable, _opt.ForwardMaxAgeSec, keys.ManagerPort,
            keys.ManagerWebhookSecret.Length > 0 ? "set" : "MISSING", keys.AgentToken.Length > 0 ? "set" : "empty");
        if (keys.ManagerWebhookSecret.Length == 0)
            _log.LogError("[SMS] no Sms__ManagerWebhookSecret / Orchestrator__ManagerWebhookSecret — the Manager will reject sms-event");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_failing)
                {
                    _failing = true;
                    _log.LogError("[SMS] consumer failed: {Err} — reconnecting every {S}s, logged once until it recovers",
                        ex.Message, Reconnect.TotalSeconds);
                }
                try { await Task.Delay(Reconnect, ct); } catch (OperationCanceledException) { break; }
            }
        }

        _log.LogInformation("[SMS] stopped");
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await using var nc = new NatsConnection(new NatsOpts { Url = _opt.NatsUrl, Name = "orch-sms-relay" });
        await nc.ConnectAsync();
        var js = new NatsJSContext(nc);

        // Throws while whqueue has not created SMS_IN yet → reconnect loop.
        var consumer = await js.CreateOrUpdateConsumerAsync(_opt.Stream, new ConsumerConfig(_opt.Durable)
        {
            FilterSubject = _opt.Subject,
            AckPolicy     = ConsumerConfigAckPolicy.Explicit,
            AckWait       = TimeSpan.FromSeconds(60),
            MaxDeliver    = _opt.MaxDeliver + 1,
            MaxAckPending = 20,
        }, ct);

        if (_failing)
        {
            _failing = false;
            _log.LogInformation("[SMS] consumer recovered");
        }
        _log.LogInformation("[SMS] consuming {Subject} (durable={Durable})", _opt.Subject, _opt.Durable);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            // SupabaseRpc via scope — works whatever its registered lifetime is.
            using var scope = _scopes.CreateScope();
            var rpc = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();

            // A new client per message — IHttpClientFactory rotates handlers only for new clients.
            using var http = _http.CreateClient(nameof(SmsRelay));
            http.Timeout = HttpTimeout;
            var proc = new SmsProcessor(rpc, http, _cfg, _opt, _log);

            var delivered = (int)(msg.Metadata?.NumDelivered ?? 1);
            SmsDecision d;
            try
            {
                d = await proc.ProcessAsync(msg.Data ?? Array.Empty<byte>(), delivered, msg.Metadata?.Timestamp, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "[SMS] process failed — nak");
                d = new SmsDecision(false, TimeSpan.FromSeconds(30), SmsOutcome.StoreFailed);
            }

            if (d.Ack) await msg.AckAsync(cancellationToken: ct);
            else       await msg.NakAsync(delay: d.Delay, cancellationToken: ct);
        }
    }
}
