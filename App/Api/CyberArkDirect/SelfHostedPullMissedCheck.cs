using BlueTrack.Api.Data;
using BlueTrack.Api.Notifications;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-363: the nightly Self-Hosted Direct pull time passed and no pull started (<see cref="MissedPullDetector"/>).
/// SelfHostedPullFailed (D-250) only mails a pull that ran; this one covers the night nothing ran, which until now
/// showed only as the Import failing. One email per missed pull time (the DedupKey is the time), whether the API was
/// running then, and what to do. Sent by the API, so it goes out once the API is running again.
/// </summary>
public sealed class SelfHostedPullMissedCheck(MissedPullDetector detector, AppConfigRepository appConfigRepository, TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "SelfHostedPullMissed";

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: the result has a DedupKey.

    public static string DedupKeyFor(DateTime scheduledAtUtc) => "missed:" + scheduledAtUtc.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        if (await detector.DetectAsync() is not { } missed) return [];
        var zone = timeProvider.LocalTimeZone;
        var email = new NotificationEmail("BlueTrack: the nightly Self-Hosted Direct pull didn't start", await appConfigRepository.GetSiteUrlAsync());
        var facts = new List<(string, string)>
        {
            ("Pull time (server time)", missed.ScheduledAtLocal.ToString("yyyy-MM-dd HH:mm")),
            ("API running then", missed.Api switch
            {
                MissedPullDetector.ApiState.Running => "Yes",
                MissedPullDetector.ApiState.NotRunning => "No",
                _ => "Unknown (no record from before then)"
            }),
        };
        if (missed.ApiStartedAfterUtc is { } startedAt)
            facts.Add(("API started again (server time)", TimeZoneInfo.ConvertTimeFromUtc(startedAt, zone).ToString("yyyy-MM-dd HH:mm")));
        email.Facts(facts);
        email.Paragraph(missed.Describe(zone));
        email.Paragraph("The pull is started by the BlueTrack API, so it runs only while IIS has the API loaded. If the API wasn't running, " +
            "check the BlueTrack application pool in IIS (start mode AlwaysRunning, idle time-out 0, preload on the /BlueTrack application, " +
            "IIS Application Initialization installed) and the System event log for application pool events. To catch up now, use Pull now.");
        email.SeePage(SiteLinks.DataSources, "to pull now");
        return
        [
            new NotificationCheckResult(
                Subject: "BlueTrack: the nightly Self-Hosted Direct pull didn't start",
                Body: email.Text,
                Detail: missed.Describe(zone),
                HtmlBody: email.Html,
                DedupKey: DedupKeyFor(missed.ScheduledAtUtc))
        ];
    }
}
