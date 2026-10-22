using System.Globalization;
using BlueTrack.Api.Data;
using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-222: a daily digest of the Risk Exceptions overdue for review -- the
/// Overdue Reviews page's own list (RiskExceptionRepository.
/// GetOverdueReviewAsync: Active and ReviewDate before today, D-19) -- sent
/// at the daily digest time, once a day (DedupKey = the day). Nothing is
/// sent on a day with none overdue.
/// </summary>
public sealed class OverdueRiskExceptionsCheck(
    NotificationRepository notificationRepository,
    RiskExceptionRepository riskExceptionRepository,
    AppConfigRepository appConfigRepository,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "OverdueRiskExceptions";
    public const int MaxListed = 100;

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: the result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        var dedupKey = DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime);
        if (dedupKey is null || await notificationRepository.WasSentAsync(TypeName, dedupKey))
        {
            return [];
        }

        var overdue = await riskExceptionRepository.GetOverdueReviewAsync();
        if (overdue.Count == 0)
        {
            return [];
        }

        // The overdue rule compares ReviewDate with today's UTC date (GetOverdueReviewAsync).
        var todayUtc = timeProvider.GetUtcNow().UtcDateTime.Date;
        var count = NotificationEmail.Count(overdue.Count);
        var noun = overdue.Count == 1 ? "Risk Exception is" : "Risk Exceptions are";
        var email = new NotificationEmail($"{count} {noun} overdue for review", await appConfigRepository.GetSiteUrlAsync())
            .Paragraph("These Active Risk Exceptions are past their review date. Review each one: extend it with a new review date, or revoke it.")
            .Table(["Exception", "Applies to", "Review date", "Days overdue", "Approved by"],
                overdue.Take(MaxListed).Select(e => (IReadOnlyList<string>)
                [
                    e.ExceptionID,
                    $"{e.ScopeType}: {e.ScopeName}",
                    ServerTextCulture.Date(e.ReviewDate),
                    NotificationEmail.Count((int)(todayUtc - e.ReviewDate.Date).TotalDays),
                    e.ApprovedByName ?? ""
                ]));
        if (overdue.Count > MaxListed)
        {
            email.Paragraph($"...and {NotificationEmail.Count(overdue.Count - MaxListed)} more.");
        }
        email.SeePage(SiteLinks.OverdueReviews);

        return
        [
            new NotificationCheckResult(
                Subject: $"BlueTrack: {count} Risk {(overdue.Count == 1 ? "Exception" : "Exceptions")} overdue for review",
                Body: email.Text,
                Detail: $"{overdue.Count} overdue ({dedupKey})",
                HtmlBody: email.Html,
                DedupKey: dedupKey)
        ];
    }
}
