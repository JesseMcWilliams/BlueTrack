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
///
/// D-116 follow-up: IgnoreCrlErrors/IgnoreSslErrors were found necessary
/// testing against the real provisioned relay (its cert had no reachable
/// CRL/OCSP endpoint) and are now admin-editable per-environment checkboxes
/// on the Notifications page rather than a permanent hardcoded behavior --
/// both default off (secure by default).
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

        using var client = new SmtpClient { CheckCertificateRevocation = !config.IgnoreCrlErrors };
        if (config.IgnoreSslErrors)
        {
            // Broader than IgnoreCrlErrors -- skips hostname/chain/expiry
            // validation entirely, not just the CRL/OCSP lookup. Off by
            // default; an admin opts in per-environment after seeing a real
            // TLS failure, same as IgnoreCrlErrors.
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
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
