namespace BlueTrack.Api.Notifications;

/// <summary>
/// Design_Notifications.md, D-115: one implementation per alert kind
/// (web.dim_notification_type row) -- NotificationCheckBackgroundService
/// evaluates every registered INotificationCheck each cycle, so adding a
/// future trigger (e.g. D-19's overdue Risk Exceptions) is a new class +
/// a new dim_notification_type row, not a change to the background
/// service itself.
/// </summary>
public interface INotificationCheck
{
    /// <summary>Must match a NotificationTypeName already seeded in web.dim_notification_type.</summary>
    string NotificationTypeName { get; }

    /// <summary>How long a triggered alert stays "already sent" before it can fire again.</summary>
    TimeSpan Cooldown { get; }

    /// <summary>Returns null if nothing to alert on right now, otherwise the result to send/log.</summary>
    Task<NotificationCheckResult?> EvaluateAsync();
}

public sealed record NotificationCheckResult(string Subject, string Body, string? Detail);
