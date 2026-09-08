using BlueTrack.Api.Data;
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
///
/// D-116: the SMTP account itself is now a web.credential
/// (config.SmtpCredentialKey), resolved via CredentialRepository rather than
/// decrypted inline here -- MailKit's own AuthenticateAsync(username,
/// password) negotiates whichever SASL mechanism the server actually
/// advertises (PLAIN/LOGIN/NTLM/CRAM-MD5/...), so a Windows-authenticated
/// local relay (NTLM) needs no special-casing here beyond a plain
/// username/password credential.
/// </summary>
public sealed class SmtpNotificationSender(NotificationRepository repository, CredentialRepository credentialRepository) : INotificationSender
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

        // Confirmed 2026-09-08 against the real internal relay: its certificate
        // passes hostname/chain validation, but has no CRL/OCSP endpoint
        // reachable from this network -- CheckCertificateRevocation = false
        // skips only that lookup, the user's explicit choice over leaving the
        // relay's certificate/CA setup as the thing to fix instead.
        using var client = new SmtpClient { CheckCertificateRevocation = false };
        var secureSocketOptions = config.EnableStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(config.SmtpHost, config.SmtpPort, secureSocketOptions);

        if (config.AuthMethod == "Basic" && config.SmtpCredentialKey is not null)
        {
            var (username, password) = await credentialRepository.ResolveForUseAsync(config.SmtpCredentialKey.Value);
            if (!string.IsNullOrWhiteSpace(username))
            {
                await client.AuthenticateAsync(username, password);
            }
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);
    }
}
