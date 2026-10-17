using System.Globalization;
using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-243: an email for each API key 30 days and 7 days before it expires, from
/// the daily digest time, so the script that uses it can be given a new key
/// first. A key made for 30 days or fewer gets only the 7-day email, and one
/// made for 7 days or fewer none (a warning at creation would be noise). Each
/// key and threshold is emailed once (DedupKey), even across restarts.
/// </summary>
public sealed class ApiKeyExpiringCheck(
    NotificationRepository notificationRepository,
    ApiKeyRepository apiKeyRepository,
    AppConfigRepository appConfigRepository,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "ApiKeyExpiring";

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: every result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        if (DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime) is null) return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        var results = new List<NotificationCheckResult>();
        foreach (var key in await apiKeyRepository.ListExpiringAsync(now, withinDays: 30))
        {
            var daysLeft = (int)Math.Ceiling((key.ExpiresAt - now).TotalDays);
            var lifetimeDays = (key.ExpiresAt - key.CreatedAt).TotalDays;
            int? threshold = daysLeft <= 7 && lifetimeDays > 7 ? 7 : daysLeft <= 30 && lifetimeDays > 30 ? 30 : null;
            if (threshold is null) continue;

            var dedupKey = $"apikey-{key.ApiKeyKey}-{threshold}";
            if (await notificationRepository.WasSentAsync(TypeName, dedupKey)) continue;

            var noun = daysLeft == 1 ? "day" : "days";
            var email = new NotificationEmail($"API key \"{key.DisplayName}\" expires in {daysLeft} {noun}", siteUrl)
                .Paragraph("Scripts and systems that use this key stop getting tokens on the expiry date. Create a new key (Rotate on the API Keys page makes one with the same role), give it to the caller, then delete this one.")
                .Facts([
                    ("Key", key.DisplayName),
                    ("Starts with", key.KeyPrefix),
                    ("Role", key.RoleName),
                    ("Expires", key.ExpiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ])
                .SeePage(SiteLinks.ApiKeys);
            results.Add(new NotificationCheckResult(
                Subject: $"BlueTrack: API key \"{key.DisplayName}\" expires in {daysLeft} {noun}",
                Body: email.Text,
                Detail: $"{key.KeyPrefix}, {daysLeft} days left ({dedupKey})",
                HtmlBody: email.Html,
                DedupKey: dedupKey));
        }
        return results;
    }
}
