using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-243: an email to the administrators (this type's recipients, Admin > Notifications) for each API key that
/// expired, from the daily digest time on the day it expired, so they can see which script is about to fail.
/// A key whose owner is told as well (ApiKeyExpiringCheck) is still reported here. Each key and expiry date is
/// emailed once (DedupKey), even across restarts.
/// </summary>
public sealed class ApiKeyExpiredCheck(
    NotificationRepository notificationRepository,
    ApiKeyRepository apiKeyRepository,
    AppConfigRepository appConfigRepository,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "ApiKeyExpired";

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: every result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        if (DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime) is null) return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        var results = new List<NotificationCheckResult>();
        foreach (var key in await apiKeyRepository.ListRecentlyExpiredAsync(now, ApiKeyExpiringCheck.ExpiredWithinDays))
        {
            var dedupKey = $"apikey-{key.ApiKeyKey}-{key.ExpiresAt:yyyyMMdd}-expired";
            if (await notificationRepository.WasSentAsync(TypeName, dedupKey)) continue;

            var owner = string.IsNullOrWhiteSpace(key.OwnerEmail) ? "It has no owner email, so nobody else was told." : $"Its owner, {key.OwnerEmail}, was told too.";
            var email = ApiKeyExpiringCheck.Build(key, $"API key \"{key.DisplayName}\" has expired", siteUrl,
                $"Scripts and systems that use this key can no longer get tokens. Renew its expiry, or create a new key and give it to the caller. {owner}");
            results.Add(new NotificationCheckResult($"BlueTrack: API key \"{key.DisplayName}\" has expired", email.Text,
                $"{key.KeyPrefix}, expired ({dedupKey})", email.Html, dedupKey));
        }
        return results;
    }
}
