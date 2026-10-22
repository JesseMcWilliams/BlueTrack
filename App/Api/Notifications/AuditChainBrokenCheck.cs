using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-295: once a day, from the daily digest time, checks the audit log's seals (AuditChainService.VerifyAsync) and
/// emails when the chain is broken: an audit event or seal was changed or deleted outside BlueTrack. Nothing is sent
/// while it is intact. The check runs only while this notification type is on.
/// </summary>
public sealed class AuditChainBrokenCheck(
    NotificationRepository notificationRepository,
    AuditChainService auditChain,
    AppConfigRepository appConfigRepository,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "AuditChainBroken";

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: the result has a DedupKey (the day).

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        if (DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime) is not { } dueKey) return [];
        var dedupKey = $"auditchain-{dueKey}";
        if (await notificationRepository.WasSentAsync(TypeName, dedupKey)) return [];

        var result = await auditChain.VerifyAsync();
        if (result.Intact) return [];

        const string heading = "The audit log was changed outside BlueTrack";
        var email = new NotificationEmail(heading, await appConfigRepository.GetSiteUrlAsync())
            .Paragraph("The daily check of the audit log's seals found audit events or seals that were changed or deleted after BlueTrack wrote them. BlueTrack itself never changes or deletes them, except for the retention purge, which keeps the chain whole.")
            .Table(["Seal", "Audit event", "Problem"],
                result.Problems.Select(p => (IReadOnlyList<string>)[
                    p.ChainSeq is { } seq ? Formatting.ServerTextCulture.Count(seq) : "",
                    p.AuditEventKey?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    Describe(p.Kind)]));
        if (result.MoreProblems) email.Paragraph($"... and more; only the first {AuditChainService.MaxProblemsListed} are listed.");
        email.Paragraph("Compare with the copy in your SIEM (if audit forwarding is on) and the SQL Server audit in the Windows Security log, which record who changed what.");
        email.SeePage(SiteLinks.AuditLog);
        return [new NotificationCheckResult($"BlueTrack: {heading}", email.Text, $"{result.Summary} ({dedupKey})", email.Html, dedupKey)];
    }

    public static string Describe(string kind) => kind switch
    {
        "Edited" => "The event or its seal was changed",
        "EventDeleted" => "The event was deleted (its seal remains)",
        "SealMissing" => "A seal was deleted",
        "SealsMissingAtEnd" => "The newest seals were deleted",
        _ => kind
    };
}
