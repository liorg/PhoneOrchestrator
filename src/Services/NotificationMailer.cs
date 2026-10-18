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
/// Delivery is at-most-once. Rows are claimed (is_send=false → true) with a
/// compare-and-set before the send, so two instances never mail the same row;
/// a crash between claim and send leaves the row at 'sending' rather than
/// risking a duplicate.
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

    private static readonly HashSet<string> ErrorLevels =
        new(StringComparer.OrdinalIgnoreCase) { "error", "critical", "fatal" };

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
        Guid Id, Guid? UserId, string Title, string Message, string Level,
        DateTime CreatedUtc, int Attempts);

    private sealed record User(
        Guid Id, string? Email, string Lang, bool NotifyEmail, bool ErrorsOnly, string TimeZone);

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var rpc    = scope.ServiceProvider.GetRequiredService<SupabaseRpc>();
        var resend = scope.ServiceProvider.GetRequiredService<ResendClient>();

        // ── 1. too old to be useful → out of the queue without a mail ─────
        var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, _opt.NotifyMaxAgeHours));
        await rpc.PatchAsync("notifications",
            $"is_send=eq.false&created_at=lt.{Uri.EscapeDataString(cutoff.ToString("o"))}",
            new { is_send = true, email_status = "skipped", email_error = "stale" }, ct);

        // ── 2. queue ──────────────────────────────────────────────────────
        var raw = await rpc.SelectAsync("notifications",
            "select=id,user_id,title,message,log_level,created_at,email_attempts" +
            $"&is_send=eq.false&order=created_at.asc&limit={Math.Max(1, _opt.NotifyBatchSize)}", ct);

        var rows = raw.EnumerateArray().Select(ParseRow).Where(r => r != null).Select(r => r!).ToList();
        if (rows.Count == 0) return;

        // ── 3. owners ─────────────────────────────────────────────────────
        var userIds = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var users   = await LoadUsersAsync(rpc, userIds, ct);

        int sent = 0, skipped = 0, failed = 0;

        foreach (var group in rows.GroupBy(r => r.UserId))
        {
            if (ct.IsCancellationRequested) break;
            var items = group.ToList();

            if (group.Key is not { } uid)
            {
                skipped += await SkipAsync(rpc, items, "no_user", ct);
                continue;
            }
            if (!users.TryGetValue(uid, out var user))
            {
                skipped += await SkipAsync(rpc, items, "user_not_found", ct);
                continue;
            }
            if (!user.NotifyEmail)
            {
                skipped += await SkipAsync(rpc, items, "notify_email_off", ct);
                continue;
            }
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                skipped += await SkipAsync(rpc, items, "no_email", ct);
                continue;
            }

            var wanted = user.ErrorsOnly ? items.Where(r => ErrorLevels.Contains(r.Level)).ToList() : items;
            if (wanted.Count < items.Count)
                skipped += await SkipAsync(rpc, items.Except(wanted).ToList(), "errors_only", ct);
            if (wanted.Count == 0) continue;

            // Cap per mail; the rest stay queued for the next sweep.
            wanted = wanted.Take(Math.Max(1, _opt.NotifyMaxItemsPerEmail)).ToList();

            var claimed = await ClaimAsync(rpc, wanted, ct);
            if (claimed.Count == 0) continue;

            var (subject, html) = MailTemplate.Render(user, claimed, _opt);
            var (ok, retryable, error) = await resend.SendAsync(
                user.Email!, subject, html, IdempotencyKey(claimed), ct);

            if (ok)
            {
                await rpc.PatchAsync("notifications", IdFilter(claimed), new
                {
                    email_status = "sent",
                    emailed_at   = DateTime.UtcNow,
                    email_attempts = claimed.Max(r => r.Attempts) + 1
                }, ct);
                sent += claimed.Count;
                _log.LogInformation("[MAIL] Sent {Count} notification(s) to user {User}", claimed.Count, uid);
            }
            else
            {
                failed += await FailAsync(rpc, claimed, retryable, error, ct);
                _log.LogWarning("[MAIL] Send to user {User} failed (retryable={Retry}): {Error}", uid, retryable, error);
            }

            // Resend's default limit is 2 req/s.
            if (!await Delay(TimeSpan.FromMilliseconds(600), ct)) break;
        }

        if (sent + skipped + failed > 0)
            _log.LogInformation("[MAIL] Sweep | sent={Sent} skipped={Skipped} failed={Failed}", sent, skipped, failed);
    }

    // ══════════════════════════════════════════════════════════ helpers ══

    private static Row? ParseRow(JsonElement r)
    {
        if (!r.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id)) return null;

        Guid? uid = r.TryGetProperty("user_id", out var u) && u.ValueKind == JsonValueKind.String
                    && Guid.TryParse(u.GetString(), out var ug) ? ug : null;

        var created = r.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String
                      && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTime.UtcNow;

        return new Row(
            id, uid,
            Str(r, "title"), Str(r, "message"),
            Str(r, "log_level") is { Length: > 0 } lvl ? lvl : "info",
            created,
            r.TryGetProperty("email_attempts", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0);
    }

    private static string Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<Dictionary<Guid, User>> LoadUsersAsync(
        SupabaseRpc rpc, List<Guid> ids, CancellationToken ct)
    {
        var map = new Dictionary<Guid, User>();
        if (ids.Count == 0) return map;

        var rows = await rpc.SelectAsync("users",
            "select=id,email,lang,notify_email,notify_errors_only,timezone" +
            $"&id=in.({string.Join(",", ids)})", ct);

        foreach (var r in rows.EnumerateArray())
        {
            if (!Guid.TryParse(Str(r, "id"), out var id)) continue;

            bool B(string k, bool d) =>
                r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : d;

            map[id] = new User(
                id,
                Str(r, "email"),
                Str(r, "lang") is { Length: > 0 } l ? l : "he",
                B("notify_email", false),
                // null means the UI default: errors only.
                B("notify_errors_only", true),
                Str(r, "timezone") is { Length: > 0 } tz ? tz : "Asia/Jerusalem");
        }
        return map;
    }

    private static string IdFilter(IEnumerable<Row> rows) =>
        $"id=in.({string.Join(",", rows.Select(r => r.Id))})";

    /// <summary>
    /// is_send false → true with the old value in the filter: the second of
    /// two concurrent claimers gets back only what it actually won.
    /// </summary>
    private static async Task<List<Row>> ClaimAsync(SupabaseRpc rpc, List<Row> rows, CancellationToken ct)
    {
        var won = await rpc.PatchReturningAsync("notifications",
            IdFilter(rows) + "&is_send=eq.false",
            new { is_send = true, email_status = "sending" }, ct);

        var ids = won.EnumerateArray()
            .Select(r => Guid.TryParse(Str(r, "id"), out var g) ? g : Guid.Empty)
            .ToHashSet();
        return rows.Where(r => ids.Contains(r.Id)).ToList();
    }

    private static async Task<int> SkipAsync(SupabaseRpc rpc, List<Row> rows, string reason, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        await rpc.PatchAsync("notifications", IdFilter(rows) + "&is_send=eq.false",
            new { is_send = true, email_status = "skipped", email_error = reason }, ct);
        return rows.Count;
    }

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
