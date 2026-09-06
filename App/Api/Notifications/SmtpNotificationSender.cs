using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// Design_Notifications.md, D-115: STARTTLS + username/password only --
/// resolved directly with the user rather than assumed. A direct M365/
/// Google Workspace mailbox with Basic Auth disabled would need OAuth2/
/// XOAUTH2 instead, a materially bigger build (app registration, token
/// acquisition/refresh) -- out of scope for this pass; this targets an
/// on-prem Exchange or internal relay/smart-host, the common pattern for
/// letting an internal system send mail without implementing OAuth2 itself.
/// </summary>
public sealed class SmtpNotificationSender(NotificationRepository repository, ILocalSecretProtector localSecretProtector) : INotificationSender
{
    public async Task SendAsync(IReadOnlyList<string> toAddresses, string subject, string body)
    {
        var config = await repository.GetConfigForSendingAsync();

        if (string.IsNullOrWhiteSpace(config.SmtpHost) || string.IsNullOrWhiteSpace(config.FromAddress))
        {
            throw new InvalidOperationException("SMTP is not configured -- set SmtpHost and FromAddress on the Notifications admin page first.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(config.FromDisplayName ?? config.FromAddress, config.FromAddress));
        foreach (var to in toAddresses)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        var secureSocketOptions = config.EnableStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(config.SmtpHost, config.SmtpPort, secureSocketOptions);

        if (config.AuthMethod == "Basic" && !string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrWhiteSpace(config.PasswordSecretReference))
        {
            var password = localSecretProtector.Unprotect(config.PasswordSecretReference);
            await client.AuthenticateAsync(config.Username, password);
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);
    }
}
