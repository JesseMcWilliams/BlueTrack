using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTrack.Api.Data;
using BlueTrack.Api.Formatting;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-222: a daily digest of the Risk Exceptions overdue for review -- the
/// Overdue Reviews page's own list (RiskExceptionRepository.
/// GetOverdueReviewAsync: Active and ReviewDate before today, D-19) -- sent
/// at the daily digest time, once a day (DedupKey = the day). Nothing is
/// sent on a day with none overdue.
///
/// D-334: on the same switch and at the same time, each application's owner
/// (dim_application.OwnerEmail) gets an email of their own listing only their
/// applications' exceptions (GetOwnerReviewsAsync; the schedule's two owner
/// options add single-account exceptions and a reminder before the review
/// date). One email per owner per day, with its own DedupKey, so the team's
/// digest and each owner's are claimed and retried separately.
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
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: each result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        var dedupKey = DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime);
        if (dedupKey is null)
        {
            return [];
        }

        // The overdue rule compares ReviewDate with today's UTC date (GetOverdueReviewAsync).
        var todayUtc = timeProvider.GetUtcNow().UtcDateTime.Date;
        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        var results = new List<NotificationCheckResult>();

        if (!await notificationRepository.WasSentAsync(TypeName, dedupKey))
        {
            var overdue = await riskExceptionRepository.GetOverdueReviewAsync();
            if (overdue.Count > 0) results.Add(TeamDigest(overdue, todayUtc, siteUrl, dedupKey));
        }

        var ownerRows = await riskExceptionRepository.GetOwnerReviewsAsync(schedule.OwnerIncludeAccountExceptions, schedule.OwnerReminderDays);
        foreach (var owner in ownerRows.GroupBy(r => r.OwnerEmail, StringComparer.OrdinalIgnoreCase))
        {
            var ownerKey = OwnerDedupKey(dedupKey, owner.Key);
            if (await notificationRepository.WasSentAsync(TypeName, ownerKey)) continue;
            results.Add(OwnerEmail(owner.Key, owner.ToList(), todayUtc, schedule.OwnerReminderDays, siteUrl, ownerKey));
        }
        return results;
    }

    private static NotificationCheckResult TeamDigest(IReadOnlyList<RiskExceptionSummary> overdue, DateTime todayUtc, string? siteUrl, string dedupKey)
    {
        var count = NotificationEmail.Count(overdue.Count);
        var noun = overdue.Count == 1 ? "Risk Exception is" : "Risk Exceptions are";
        var email = new NotificationEmail($"{count} {noun} overdue for review", siteUrl)
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

        return new NotificationCheckResult(
            Subject: $"BlueTrack: {count} Risk {(overdue.Count == 1 ? "Exception" : "Exceptions")} overdue for review",
            Body: email.Text,
            Detail: $"{overdue.Count} overdue ({dedupKey})",
            HtmlBody: email.Html,
            DedupKey: dedupKey);
    }

    private static NotificationCheckResult OwnerEmail(string ownerEmail, IReadOnlyList<OwnerReviewRow> rows, DateTime todayUtc, int reminderDays, string? siteUrl, string ownerKey)
    {
        var overdue = rows.Where(r => r.Kind == "Overdue").ToList();
        var dueSoon = rows.Where(r => r.Kind == "DueSoon").ToList();
        var exceptionCount = rows.Select(r => r.ExceptionKey).Distinct().Count();
        var applications = string.Join(", ", rows.Select(r => r.ApplicationName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));

        var title = overdue.Count > 0
            ? $"{NotificationEmail.Count(exceptionCount)} Risk {(exceptionCount == 1 ? "Exception" : "Exceptions")} for your applications need review"
            : $"{NotificationEmail.Count(exceptionCount)} Risk {(exceptionCount == 1 ? "Exception" : "Exceptions")} for your applications due for review in {NotificationEmail.Count(reminderDays)} days";
        var name = rows.Select(r => r.OwnerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var email = new NotificationEmail(title, siteUrl)
            .Paragraph($"{(name is null ? "Hello" : "Hello " + name.Trim())}, you get this email as the owner, in BlueTrack, of: {applications}.");
        if (overdue.Count > 0)
        {
            email.Heading("Overdue for review")
                .Paragraph("These Active Risk Exceptions are past their review date. Please ask for each to be reviewed: extended with a new review date, or revoked.")
                .Table(["Exception", "Application", "Applies to", "Review date", "Days overdue"],
                    overdue.Take(MaxListed).Select(r => (IReadOnlyList<string>)
                    [
                        r.ExceptionID, r.ApplicationName, $"{r.ScopeType}: {r.ScopeName}",
                        ServerTextCulture.Date(r.ReviewDate), NotificationEmail.Count((int)(todayUtc - r.ReviewDate.Date).TotalDays)
                    ]));
            if (overdue.Count > MaxListed) email.Paragraph($"...and {NotificationEmail.Count(overdue.Count - MaxListed)} more.");
        }
        if (dueSoon.Count > 0)
        {
            email.Heading($"Due for review in {NotificationEmail.Count(reminderDays)} days")
                .Table(["Exception", "Application", "Applies to", "Review date"],
                    dueSoon.Take(MaxListed).Select(r => (IReadOnlyList<string>)
                    [r.ExceptionID, r.ApplicationName, $"{r.ScopeType}: {r.ScopeName}", ServerTextCulture.Date(r.ReviewDate)]));
            if (dueSoon.Count > MaxListed) email.Paragraph($"...and {NotificationEmail.Count(dueSoon.Count - MaxListed)} more.");
        }
        email.SeePage(SiteLinks.RiskExceptions);

        return new NotificationCheckResult(
            Subject: "BlueTrack: " + title,
            Body: email.Text,
            Detail: $"Application owner {ownerEmail}: {overdue.Count} overdue, {dueSoon.Count} due soon",
            HtmlBody: email.Html,
            DedupKey: ownerKey,
            Recipients: [ownerEmail]);
    }

    /// <summary>
    /// The day's key plus a hash of the owner's address (lower case), so the key stays within notification_log's
    /// 200 characters for any address. The address itself is in the Detail.
    /// </summary>
    public static string OwnerDedupKey(string dayKey, string ownerEmail)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ownerEmail.Trim().ToLower(CultureInfo.InvariantCulture)));
        return $"{dayKey}|owner:{Convert.ToHexString(hash)[..32]}";
    }
}
