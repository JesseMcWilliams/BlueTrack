namespace BlueTrack.Api.Notifications;

/// <summary>
/// Design_Notifications.md, D-115: the actual SMTP send, kept behind an
/// interface the same way IVaultSecretProvider/ILocalSecretProtector are --
/// NotificationCheckBackgroundService and NotificationsController's own
/// "send test email" action both depend on this, not a concrete MailKit
/// type directly.
/// </summary>
public interface INotificationSender
{
    /// <summary>
    /// Sends to every given recipient. Throws on failure (SmtpCommandException,
    /// AuthenticationException, etc.) rather than swallowing it -- callers
    /// decide whether a failure is fatal (the test-email admin action) or
    /// just logged and retried next cycle (the background check).
    /// D-222: with <paramref name="htmlBody"/>, the message is HTML with
    /// <paramref name="body"/> as its plain-text alternative.
    /// </summary>
    Task SendAsync(IReadOnlyList<string> toAddresses, string subject, string body, string? htmlBody = null);
}
