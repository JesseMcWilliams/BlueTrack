using System.Globalization;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-288: once a day, from the daily digest time, the accounts whose CPM status became failure since the last
/// CpmHealth email (the first one: in the last day). Nothing is sent when there are none. Reports > CPM Health lists
/// every failing account; this names only the new ones, so a long-standing failure isn't repeated every day.
/// </summary>
public sealed class CpmHealthCheck(
    NotificationRepository notificationRepository,
    CpmHealthRepository cpmHealthRepository,
    AppConfigRepository appConfigRepository,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "CpmHealth";
    public const int MaxListed = 100;

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: the result has a DedupKey (the day).

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        if (DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime) is not { } dueKey) return [];
        var dedupKey = $"cpmhealth-{dueKey}";
        if (await notificationRepository.WasSentAsync(TypeName, dedupKey)) return [];

        var failing = await cpmHealthRepository.GetNewlyFailingAsync();
        if (failing.Count == 0) return [];

        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        var heading = failing.Count == 1 ? "1 account's CPM status is now failure" : $"{failing.Count} accounts' CPM status is now failure";
        var email = new NotificationEmail(heading, siteUrl)
            .Paragraph("The CPM couldn't verify, change or reconcile these accounts at its last try. Until it can, their passwords may not match what the Vault holds.")
            .Table(["Account", "Safe", "Platform", "Last CPM action", "Since (UTC)"],
                failing.Take(MaxListed).Select(r => (IReadOnlyList<string>)[
                    r.AccountName, r.SafeName ?? "", r.PlatformId ?? "",
                    r.LastCPMAction is null ? "" : $"{r.LastCPMAction} ({r.LastCPMActionResult})",
                    r.CpmFailureSince?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? ""]));
        if (failing.Count > MaxListed) email.Paragraph($"... and {failing.Count - MaxListed} more.");
        email.SeePage(SiteLinks.CpmHealth);
        return [new NotificationCheckResult($"BlueTrack: {heading}", email.Text, $"{failing.Count} newly failing ({dedupKey})", email.Html, dedupKey)];
    }
}
