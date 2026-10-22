using System.Globalization;
using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Data;
using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-243: an email to an API key's owner (the owner email on the key) the lead time before the key expires
/// (Global Application Configuration, 7 days by default) and on the day it expires, from the daily digest time,
/// so the script that uses it can be given a new key in time. A key with no owner email is skipped (its
/// administrators are told on the day by ApiKeyExpiredCheck). A key made for no more than the lead time gets
/// only the expiry email (a reminder at creation would be noise). Each key, expiry date and kind is emailed once
/// (DedupKey), even across restarts; renewing a key's expiry earns new emails.
/// </summary>
public sealed class ApiKeyExpiringCheck(
    NotificationRepository notificationRepository,
    ApiKeyRepository apiKeyRepository,
    AppConfigRepository appConfigRepository,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "ApiKeyExpiring";

    /// <summary>A key that expired longer ago than this is not reported (a restart or a long outage shouldn't email old news).</summary>
    public const int ExpiredWithinDays = 2;

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: every result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        if (DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime) is null) return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lead = await apiKeyRepository.GetReminderLeadDaysAsync();
        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        var results = new List<NotificationCheckResult>();

        foreach (var key in await apiKeyRepository.ListExpiringAsync(now, lead))
        {
            if (string.IsNullOrWhiteSpace(key.OwnerEmail)) continue;
            if ((key.ExpiresAt - key.CreatedAt).TotalDays <= lead) continue;

            var dedupKey = $"apikey-{key.ApiKeyKey}-{key.ExpiresAt:yyyyMMdd}-lead";
            if (await notificationRepository.WasSentAsync(TypeName, dedupKey)) continue;

            var daysLeft = (int)Math.Ceiling((key.ExpiresAt - now).TotalDays);
            var noun = daysLeft == 1 ? "day" : "days";
            var email = Build(key, $"API key \"{key.DisplayName}\" expires in {daysLeft} {noun}", siteUrl,
                "Scripts and systems that use this key stop getting tokens on the expiry date. Ask an administrator to renew its expiry, or to create a new key (Rotate on the API Keys page makes one with the same role), give it to the caller, then delete this one.");
            results.Add(new NotificationCheckResult($"BlueTrack: API key \"{key.DisplayName}\" expires in {daysLeft} {noun}", email.Text,
                $"{key.KeyPrefix}, {daysLeft} days left ({dedupKey})", email.Html, dedupKey, [key.OwnerEmail]));
        }

        foreach (var key in await apiKeyRepository.ListRecentlyExpiredAsync(now, ExpiredWithinDays))
        {
            if (string.IsNullOrWhiteSpace(key.OwnerEmail)) continue;

            var dedupKey = $"apikey-{key.ApiKeyKey}-{key.ExpiresAt:yyyyMMdd}-expired";
            if (await notificationRepository.WasSentAsync(TypeName, dedupKey)) continue;

            var email = Build(key, $"API key \"{key.DisplayName}\" has expired", siteUrl,
                "Scripts and systems that use this key can no longer get tokens. Ask an administrator to renew its expiry, or to create a new key and give it to the caller.");
            results.Add(new NotificationCheckResult($"BlueTrack: API key \"{key.DisplayName}\" has expired", email.Text,
                $"{key.KeyPrefix}, expired ({dedupKey})", email.Html, dedupKey, [key.OwnerEmail]));
        }
        return results;
    }

    internal static NotificationEmail Build(ApiKeySummary key, string heading, string? siteUrl, string advice) =>
        new NotificationEmail(heading, siteUrl)
            .Paragraph(advice)
            .Facts([
                ("Key", key.DisplayName),
                ("Starts with", key.KeyPrefix),
                ("Role", key.RoleName),
                ("Expires", ServerTextCulture.Date(key.ExpiresAt)),
            ])
            .SeePage(SiteLinks.ApiKeys);
}
