using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-348: one email per pull, Load procedure, Import or Snapshot that has stopped moving (RunStallDetector), with
/// what its SQL Server session is waiting on. A stall already mailed (its DedupKey is in web.notification_log) isn't
/// mailed again; a Load that moves on to its next procedure and stalls there is a new stall.
/// </summary>
public sealed class RunStalledCheck(IDbConnectionFactory connectionFactory, RunStallDetector detector, AppConfigRepository appConfigRepository, TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "RunStalled";

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: every result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var report = await detector.DetectAsync();
        if (report.Stalls.Count == 0) return [];

        using var connection = connectionFactory.Create();
        var sent = (await connection.QueryAsync<string>("""
            SELECT nl.DedupKey FROM web.notification_log nl
            JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
            WHERE nt.NotificationTypeName = @TypeName AND nl.DedupKey IN @keys
            """, new { TypeName, keys = report.Stalls.Select(s => s.DedupKey).ToList() })).ToHashSet(StringComparer.Ordinal);

        var fresh = report.Stalls.Where(s => !sent.Contains(s.DedupKey)).ToList();
        if (fresh.Count == 0) return [];
        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        return fresh.Select(s => Build(s, report.StallMinutes, siteUrl)).ToList();
    }

    private NotificationCheckResult Build(RunStall stall, int stallMinutes, string? siteUrl)
    {
        var heading = $"BlueTrack: {stall.What} has not moved for {stall.Minutes} minutes";
        var email = new NotificationEmail(heading, siteUrl);
        email.Facts(
        [
            ("What", stall.What),
            ("Since (server time)", NotificationEmail.ServerTime(stall.Since, timeProvider)),
            ("Alert after", $"{stallMinutes} minutes"),
        ]);
        email.Paragraph(stall.Detail);
        email.Paragraph(stall.Kind == "Pull"
            ? "A pull that stops reading usually means PVWA or the credential lookup isn't answering. The next pull marks it failed."
            : "A Load procedure can take long on a large vault; one that is blocked or waiting on the same thing for a long time is stuck. Import and Load on Data Sources shows the session and has Cancel load.");
        email.SeePage(SiteLinks.DataSources, "to see where it is and cancel it");
        return new NotificationCheckResult(
            Subject: heading,
            Body: email.Text,
            Detail: $"{stall.What}: no progress for {stall.Minutes} minutes. {stall.Detail}",
            HtmlBody: email.Html,
            DedupKey: stall.DedupKey);
    }
}
