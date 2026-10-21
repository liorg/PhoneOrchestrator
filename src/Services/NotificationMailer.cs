using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PhoneOrchestrator.Models;

namespace PhoneOrchestrator.Services;

/// <summary>
/// Emails rows from <c>notifications</c> to their owners, per the owner's
/// settings in <c>users</c>:
///
///   notify_email = false          → nothing is sent
///   notify_errors_only = true     → only log_level error / critical / fatal
///   notify_errors_only = false    → every level
///
/// Queue = <c>notifications.is_send = false</c>. Every row leaves the queue
/// exactly once, with the outcome in <c>email_status</c>:
///   sent | skipped (+ reason in email_error) | failed | retry (back in queue)
///
/// One email per user per sweep - a digest of up to NotifyMaxItemsPerEmail
/// rows - so a burst of call summaries is one mail, not twenty.
///
/// Selection, filtering and claiming are one RPC (rpc_notify_claim_batch):
/// it returns only rows that are valid to send, already claimed with
/// FOR UPDATE SKIP LOCKED, so two instances never mail the same row.
/// Delivery is at-most-once: a crash between claim and send leaves the row
/// at 'sending' rather than risking a duplicate.
///
/// Runs on the "full" instance only (michal) - see Program.cs.
/// </summary>
public sealed class NotificationMailer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OrchestratorOptions _opt;
    private readonly ILogger<NotificationMailer> _log;

    public NotificationMailer(
        IServiceScopeFactory scopes,
        IOptions<OrchestratorOptions> opt,
        ILogger<NotificationMailer> log)
    {
        _scopes = scopes;
        _opt    = opt.Value;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.NotifyEmailEnabled)
        {
            _log.LogInformation("[MAIL] Notification mailer off (Orchestrator__NotifyEmailEnabled=false)");
            return;
        }

        if (string.IsNullOrWhiteSpace(_opt.ResendApiKey))
        {
            _log.LogError("[MAIL] Orchestrator__ResendApiKey is empty - mailer not started");
            return;
        }

        _log.LogInformation(
            "[MAIL] Notification mailer starting | interval={Interval}s batch={Batch} maxAge={Age}h from={From}",
            _opt.NotifyIntervalSeconds, _opt.NotifyBatchSize, _opt.NotifyMaxAgeHours, _opt.NotifyFromEmail);

        if (!await Delay(TimeSpan.FromSeconds(20), ct)) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[MAIL] Sweep failed");
            }

            if (!await Delay(TimeSpan.FromSeconds(Math.Max(15, _opt.NotifyIntervalSeconds)), ct)) break;
        }
    }

    // ═══════════════════════════════════════════════════════════ sweep ══

    private sealed record Row(
        Guid Id, Guid UserId, string Title, string Message, string Level,
        DateTime CreatedUtc, int Attempts);

    private sealed record User(Guid Id, string Email, string Lang, string TimeZone);

    /// <summary>
    /// One RPC does stale + eligibility + claim and returns only rows that are
    /// valid to send, joined with their owner (sql/rpc_notify_claim_batch.sql).
    /// Per sweep: 1 round trip, then 1 send + 1 status write per user.
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var rpc    = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
        var resend = scope.ServiceProvider.GetRequiredService<ResendClient>();

        var raw = await rpc.CallAsync("rpc_notify_claim_batch", new
        {
            p_max_age_hours = Math.Max(1, _opt.NotifyMaxAgeHours),
            p_batch         = Math.Max(1, _opt.NotifyBatchSize),
            p_per_user      = Math.Max(1, _opt.NotifyMaxItemsPerEmail)
        }, ct);

        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() == 0) return;

        var users = new Dictionary<Guid, User>();
        var rows  = new List<Row>();
        foreach (var r in raw.EnumerateArray())
        {
            if (!Guid.TryParse(Str(r, "id"), out var id) || !Guid.TryParse(Str(r, "user_id"), out var uid)) continue;

            rows.Add(new Row(id, uid, Str(r, "title"), Str(r, "message"), Str(r, "log_level"),
                ParseUtc(Str(r, "created_at")),
                r.TryGetProperty("email_attempts", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0));

            users.TryAdd(uid, new User(uid, Str(r, "email"), Str(r, "lang"), Str(r, "timezone")));
        }

        int sent = 0, failed = 0;

        foreach (var group in rows.GroupBy(r => r.UserId))
        {
            if (ct.IsCancellationRequested) break;

            var user    = users[group.Key];
            var claimed = group.ToList();

            var (subject, html) = MailTemplate.Render(user, claimed, _opt);
            var (ok, retryable, error) = await resend.SendAsync(
                user.Email, subject, html, IdempotencyKey(claimed), ct);

            if (ok)
            {
                await rpc.PatchAsync("notifications", IdFilter(claimed), new
                {
                    email_status   = "sent",
                    emailed_at     = DateTime.UtcNow,
                    email_attempts = claimed.Max(r => r.Attempts) + 1
                }, ct);
                sent += claimed.Count;
                _log.LogInformation("[MAIL] Sent {Count} notification(s) to user {User}", claimed.Count, user.Id);
            }
            else
            {
                failed += await FailAsync(rpc, claimed, retryable, error, ct);
                _log.LogWarning("[MAIL] Send to user {User} failed (retryable={Retry}): {Error}", user.Id, retryable, error);
            }

            // Resend's default limit is 2 req/s.
            if (!await Delay(TimeSpan.FromMilliseconds(600), ct)) break;
        }

        _log.LogInformation("[MAIL] Sweep | sent={Sent} failed={Failed}", sent, failed);
    }

    // ══════════════════════════════════════════════════════════ helpers ══

    private static string Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime ParseUtc(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow;

    private static string IdFilter(IEnumerable<Row> rows) =>
        $"id=in.({string.Join(",", rows.Select(r => r.Id))})";

    private async Task<int> FailAsync(
        SupabaseRpc rpc, List<Row> rows, bool retryable, string? error, CancellationToken ct)
    {
        var attempts = rows.Max(r => r.Attempts) + 1;
        var giveUp   = !retryable || attempts >= Math.Max(1, _opt.NotifyMaxAttempts);

        await rpc.PatchAsync("notifications", IdFilter(rows), new
        {
            // Back into the queue for another try, or out of it for good.
            is_send        = giveUp,
            email_status   = giveUp ? "failed" : "retry",
            email_error    = error,
            email_attempts = attempts
        }, ct);
        return rows.Count;
    }

    /// <summary>Stable for the same set of rows - a retry of the same digest dedupes at Resend.</summary>
    private static string IdempotencyKey(IEnumerable<Row> rows)
    {
        var joined = string.Join(",", rows.Select(r => r.Id).OrderBy(g => g));
        var hash   = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return "notif-" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static async Task<bool> Delay(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    // ═════════════════════════════════════════════════════════ template ══

    private static class MailTemplate
    {
        private sealed record T(
            string Dir, string SubjectMany, string Heading, string Intro,
            string Open, string Footer, string Settings, string Regards);

        private static readonly Dictionary<string, T> Texts = new()
        {
            ["he"] = new("rtl", "{0} התראות חדשות", "התראות חדשות", "אלה ההתראות האחרונות מהחשבון שלך:",
                         "פתח את המערכת", "קיבלת מייל זה כי הפעלת התראות במייל.", "לשינוי ההגדרות", "בברכה, צוות {0}"),
            ["en"] = new("ltr", "{0} new notifications", "New notifications", "These are the latest notifications from your account:",
                         "Open the dashboard", "You received this email because email notifications are on.", "Change settings", "Best regards, {0} Team"),
            ["ar"] = new("rtl", "{0} إشعارات جديدة", "إشعارات جديدة", "هذه أحدث الإشعارات من حسابك:",
                         "افتح لوحة التحكم", "تلقيت هذا البريد لأن إشعارات البريد مفعّلة.", "تغيير الإعدادات", "{0} مع تحيات فريق"),
            ["ru"] = new("ltr", "{0} новых уведомлений", "Новые уведомления", "Последние уведомления из вашего аккаунта:",
                         "Открыть панель", "Вы получили это письмо, потому что включены уведомления по email.", "Изменить настройки", "С уважением, команда {0}"),
        };

        private static (string Color, string Bg) LevelColors(string level) => level.ToLowerInvariant() switch
        {
            "error" or "critical" or "fatal" => ("#C92A2A", "#FFF5F5"),
            "warning" or "warn"              => ("#E67700", "#FFF9DB"),
            "success"                        => ("#2B8A3E", "#EBFBEE"),
            _                                => ("#1971C2", "#E7F5FF"),
        };

        public static (string Subject, string Html) Render(User user, List<Row> rows, OrchestratorOptions opt)
        {
            var t   = Texts.TryGetValue(user.Lang, out var x) ? x : Texts["he"];
            var app = opt.NotifyAppName;
            var e   = (Func<string?, string>)(s => WebUtility.HtmlEncode(s ?? ""));

            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone); }
            catch { tz = TimeZoneInfo.Utc; }

            var subject = rows.Count == 1
                ? $"{app} · {rows[0].Title}"
                : $"{app} · {string.Format(t.SubjectMany, rows.Count)}";

            var site = opt.NotifySiteUrl.TrimEnd('/');
            var sb   = new StringBuilder();

            sb.Append($"""
                <div dir="{t.Dir}" style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                  <div style="text-align: center; margin-bottom: 24px;">
                    <h1 style="color: #3B5BDB; margin: 0;">{e(app)}</h1>
                  </div>
                  <h2 style="color: #333; margin: 0 0 8px;">{t.Heading}</h2>
                  <p style="color: #555; font-size: 15px; margin: 0 0 20px;">{t.Intro}</p>
                """);

            foreach (var r in rows)
            {
                var (color, bg) = LevelColors(r.Level);
                var when = TimeZoneInfo.ConvertTimeFromUtc(r.CreatedUtc, tz)
                                       .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

                sb.Append($"""
                      <div style="background: {bg}; border-inline-start: 4px solid {color}; border-radius: 8px; padding: 12px 16px; margin-bottom: 12px;">
                        <div style="font-size: 15px; font-weight: bold; color: {color};">{e(r.Title)}</div>
                        <div style="font-size: 14px; color: #333; margin-top: 6px; white-space: pre-wrap;">{e(r.Message)}</div>
                        <div style="font-size: 12px; color: #868E96; margin-top: 6px;" dir="ltr">{when} · {e(r.Level)}</div>
                      </div>
                    """);
            }

            sb.Append($"""
                  <div style="text-align: center; margin: 28px 0;">
                    <a href="{e(site)}" style="background: linear-gradient(135deg, #3B5BDB 0%, #228BE6 100%); color: white; padding: 14px 36px; border-radius: 10px; text-decoration: none; font-weight: bold; font-size: 15px; display: inline-block;">{t.Open}</a>
                  </div>
                  <p style="color: #999; font-size: 12px;">{t.Footer} <a href="{e(site)}/settings" style="color: #3B5BDB;">{t.Settings}</a></p>
                  <hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;">
                  <p style="color: #999; font-size: 12px; text-align: center;">{e(string.Format(t.Regards, app))}</p>
                </div>
                """);

            return (subject, sb.ToString());
        }
    }
}