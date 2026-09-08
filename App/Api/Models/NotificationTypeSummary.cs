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
}

public sealed class SetNotificationTypeTargetRoleRequest
{
    public int? TargetRoleKey { get; init; }
}
