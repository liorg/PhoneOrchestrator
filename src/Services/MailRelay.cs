using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Mail for mail_expect. Consumes NATS mail.in (stream MAIL_IN, published by
/// whqueue /mail), and for every message: rpc_mail_inbound (store + dedupe +
/// recipient → phone + Manager) → POST {manager}/api/webhook/mail-event/{phone}
/// → rpc_mail_forwarded. The decision itself is MailProcessor (no NATS, tested).
///
/// botinbound only (Mode=canary): nats-1 is reachable only on whqueue-net.
/// Supabase comes from the existing OrchestratorOptions (SupabaseUrl /
/// SupabaseKey, incl. /run/secrets). Options, section "Mail":
///   Mail__Enabled=false   Mail__NatsUrl=nats://nats-1:4222
///   Mail__ManagerWebhookSecret   Mail__ForwardMaxAgeSec=600   Mail__RetentionDays=7
///
/// whqueue creates the stream; here only the durable pull consumer.
/// Ack = final decision. Nak(delay) = Supabase / Manager temporarily down.
/// </summary>
public sealed class MailRelay : BackgroundService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Reconnect   = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PurgeEvery  = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _http;
    private readonly OrchestratorOptions _orch;
    private readonly MailRelayOptions _opt;
    private readonly ILogger<MailRelay> _log;

    private DateTime _lastPurge = DateTime.MinValue;
    private bool _failing;

    public MailRelay(IHttpClientFactory http, IOptions<OrchestratorOptions> orch,
                     IOptions<MailRelayOptions> opt, ILogger<MailRelay> log)
    {
        _http = http;
        _orch = orch.Value;
        _opt  = opt.Value;
        _log  = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("[MAIL] disabled");
            return;
        }
        if (string.IsNullOrWhiteSpace(_orch.SupabaseUrl) || string.IsNullOrWhiteSpace(_orch.SupabaseKey))
        {
            _log.LogError("[MAIL] SupabaseUrl / SupabaseKey missing — not started");
            return;
        }

        _log.LogInformation("[MAIL] started | nats={Nats} stream={Stream} durable={Durable} max_age={Age}s",
            _opt.NatsUrl, _opt.Stream, _opt.Durable, _opt.ForwardMaxAgeSec);

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
                // Once per outage, not every 5 seconds.
                if (!_failing)
                {
                    _failing = true;
                    _log.LogError("[MAIL] consumer failed: {Err} — reconnecting every {S}s, logged once until it recovers",
                        ex.Message, Reconnect.TotalSeconds);
                }
                try { await Task.Delay(Reconnect, ct); } catch (OperationCanceledException) { break; }
            }
        }

        _log.LogInformation("[MAIL] stopped");
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await using var nc = new NatsConnection(new NatsOpts { Url = _opt.NatsUrl, Name = "orch-mail-relay" });
        await nc.ConnectAsync();
        var js = new NatsJSContext(nc);

        // Throws while whqueue has not created MAIL_IN yet → reconnect loop.
        var consumer = await js.CreateOrUpdateConsumerAsync(_opt.Stream, new ConsumerConfig(_opt.Durable)
        {
            FilterSubject = _opt.Subject,
            AckPolicy     = ConsumerConfigAckPolicy.Explicit,
            AckWait       = TimeSpan.FromSeconds(90),
            MaxDeliver    = _opt.MaxDeliver + 1,
            MaxAckPending = 20,
        }, ct);

        if (_failing)
        {
            _failing = false;
            _log.LogInformation("[MAIL] consumer recovered");
        }
        _log.LogInformation("[MAIL] consuming {Subject} (durable={Durable})", _opt.Subject, _opt.Durable);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            // A new client per message — same rule as OtpSender: IHttpClientFactory
            // rotates handlers only for clients that are created again.
            using var http = _http.CreateClient(nameof(MailRelay));
            http.Timeout = HttpTimeout;
            var proc = new MailProcessor(http, _opt, _orch.SupabaseUrl!, _orch.SupabaseKey!, _log);

            await PurgeIfDueAsync(proc, ct);

            var delivered = (int)(msg.Metadata?.NumDelivered ?? 1);
            MailDecision d;
            try
            {
                d = await proc.ProcessAsync(msg.Data ?? Array.Empty<byte>(), delivered, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One message that throws must not stop the consumer.
                _log.LogError(ex, "[MAIL] process failed — nak");
                d = new MailDecision(false, TimeSpan.FromSeconds(30), MailOutcome.StoreFailed);
            }

            if (d.Ack) await msg.AckAsync(cancellationToken: ct);
            else       await msg.NakAsync(delay: d.Delay, cancellationToken: ct);
        }
    }

    private async Task PurgeIfDueAsync(MailProcessor proc, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastPurge < PurgeEvery) return;
        _lastPurge = DateTime.UtcNow;
        try
        {
            var n = await proc.PurgeAsync(ct);
            if (n > 0) _log.LogInformation("[MAIL] purged {N} mail(s) older than {D}d", n, _opt.RetentionDays);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("[MAIL] purge failed: {Err}", ex.Message);
        }
    }
}