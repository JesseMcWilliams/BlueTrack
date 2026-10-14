namespace BlueTrack.Api.Models;

/// <summary>
/// web.dim_notification_type row, for the Notifications admin page's new
/// "Notification Types" section (D-116) -- lets an admin assign an optional
/// target role per alert kind, additive to the flat recipient list.
/// </summary>
public sealed class NotificationTypeSummary
{
    public int NotificationTypeKey { get; init; }
    public required string NotificationTypeName { get; init; }
    public string? Description { get; init; }
    public int? TargetRoleKey { get; init; }
    public string? TargetRoleName { get; init; }

    /// <summary>D-222: a disabled type is never evaluated or sent.</summary>
    public bool IsEnabled { get; init; }
}

public sealed class SetNotificationTypeTargetRoleRequest
{
    public int? TargetRoleKey { get; init; }
}

/// <summary>D-222: turns one notification type on or off.</summary>
public sealed class SetNotificationTypeEnabledRequest
{
    public bool IsEnabled { get; init; }
}

/// <summary>
/// D-222: when the scheduled digests go out, server local time. Times are
/// "HH:mm"; the day is a three-letter English name (Sun..Sat).
/// DailyDigestTime is shared by every daily digest (OverdueRiskExceptions).
/// </summary>
public sealed class NotificationSchedule
{
    public required string DailyDigestTime { get; init; }
    public required string WeeklySummaryDay { get; init; }
    public required string WeeklySummaryTime { get; init; }
}
