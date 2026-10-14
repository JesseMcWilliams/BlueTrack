namespace BlueTrack.Api.Notifications;

/// <summary>
/// Design_Notifications.md, D-115: one implementation per alert kind
/// (web.dim_notification_type row) -- NotificationCheckBackgroundService
/// evaluates every registered INotificationCheck each cycle, so adding a
/// trigger is a new class + a new dim_notification_type row, not a change
/// to the background service itself.
/// </summary>
public interface INotificationCheck
{
    /// <summary>Must match a NotificationTypeName already seeded in web.dim_notification_type.</summary>
    string NotificationTypeName { get; }

    /// <summary>How long a triggered alert stays "already sent" before it can fire again. Ignored for a result with a DedupKey.</summary>
    TimeSpan Cooldown { get; }

    /// <summary>What to send right now: empty if nothing, more than one when several runs (D-222) each need their own email.</summary>
    Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync();
}

/// <summary>
/// One email. D-222: HtmlBody, when set, is sent with Body as the plain-text
/// alternative. DedupKey, when set, replaces the cooldown: the send is
/// claimed in web.notification_log under that key (unique per type), so a
/// given run, day or week is emailed once, even across restarts.
/// </summary>
public sealed record NotificationCheckResult(string Subject, string Body, string? Detail, string? HtmlBody = null, string? DedupKey = null);
