namespace BlueTrack.Api.Models;

/// <summary>
/// web.notification_recipient -- a dedicated, admin-managed alert
/// recipient list (Design_Notifications.md, D-115), independent of role
/// membership. Resolved directly with the user: web.app_user.Email is
/// unusable for this today (WindowsIntegrated, the only real auth method
/// wired, never supplies an email claim), so this doesn't derive from
/// "who holds the Admin role."
/// </summary>
public sealed class NotificationRecipient
{
    public int RecipientKey { get; init; }
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public bool IsActive { get; init; }
}

public sealed class SaveNotificationRecipientRequest
{
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public bool IsActive { get; init; }
}
