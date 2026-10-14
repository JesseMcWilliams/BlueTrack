using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Notifications.OAuth;
using MailKit.Security;
using MimeKit;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// Design_Notifications.md. D-115: STARTTLS + username/password, aimed at
/// an on-prem Exchange or internal relay/smart-host.
///
/// D-116: the SMTP account itself is a web.credential
/// (config.SmtpCredentialKey), resolved via CredentialRepository rather than
/// decrypted inline here -- MailKit's own AuthenticateAsync(username,
/// password) negotiates whichever SASL mechanism the server actually
/// advertises (PLAIN/LOGIN/NTLM/CRAM-MD5/...), so a Windows-authenticated
/// local relay (NTLM) needs no special-casing here beyond a plain
/// username/password credential.
///
/// D-116 follow-up: IgnoreCrlErrors/IgnoreSslErrors are admin-editable
/// per-environment checkboxes, both off by default (applied by
/// ISmtpTransportFactory).
///
/// D-223: three OAuth2 modes (Microsoft365, GoogleWorkspace, GenericOAuth2)
/// sign in with SASL XOAUTH2 (MailKit's SaslMechanismOAuth2(user, token)).
/// The same credential row then holds the client secret, the PEM
/// certificate and key, or the Google service account key; the access token
/// comes from SmtpOAuthTokenService (cached until shortly before it
/// expires). If the server rejects a token, a fresh one is fetched and the
/// sign-in tried once more.
/// </summary>
public sealed class SmtpNotificationSender(
    NotificationRepository repository,
    CredentialRepository credentialRepository,
    ISmtpTransportFactory transportFactory,
    SmtpOAuthTokenService tokenService) : INotificationSender
{
    public async Task SendAsync(IReadOnlyList<string> toAddresses, string subject, string body, string? htmlBody = null)
    {
        var config = await repository.GetConfigForSendingAsync();
        await SendAsync(config, credentialRepository.ResolveForUseAsync, toAddresses, subject, body, htmlBody);
    }

    /// <summary>The send itself, given the settings and how to resolve a credential (tests pass their own).</summary>
    public async Task SendAsync(
        NotificationConfig config,
        Func<int, Task<(string? Username, string Password)>> resolveCredential,
        IReadOnlyList<string> toAddresses, string subject, string body, string? htmlBody = null)
    {
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
        // D-222: HTML with a plain-text alternative (multipart/alternative) when the caller has one.
        message.Body = htmlBody is null
            ? new TextPart("plain") { Text = body }
            : new BodyBuilder { TextBody = body, HtmlBody = htmlBody }.ToMessageBody();

        // Resolved before connecting, so a bad key or token endpoint is reported without touching the SMTP server.
        string? oauthSecret = null;
        SmtpOAuthSettings? oauthSettings = null;
        SmtpAccessToken? token = null;
        if (SmtpAuthMethods.IsOAuth(config.AuthMethod))
        {
            if (config.SmtpCredentialKey is null)
            {
                throw new InvalidOperationException("OAuth2 sign-in needs a credential: pick one in the SMTP settings.");
            }
            oauthSettings = SmtpOAuthSettings.From(config);
            (_, oauthSecret) = await resolveCredential(config.SmtpCredentialKey.Value);
            token = await tokenService.GetTokenAsync(oauthSettings, oauthSecret);
        }

        using var client = transportFactory.Create(config.IgnoreCrlErrors, config.IgnoreSslErrors);
        var secureSocketOptions = config.EnableStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(config.SmtpHost, config.SmtpPort, secureSocketOptions);

        if (config.AuthMethod == SmtpAuthMethods.Basic && config.SmtpCredentialKey is not null)
        {
            // D-198: never send a stored password over an unencrypted connection,
            // whatever was saved before STARTTLS became required for Basic auth.
            if (!client.IsSecure)
            {
                throw new InvalidOperationException("Basic authentication needs STARTTLS: the SMTP connection isn't encrypted, so the credential was not sent. Turn STARTTLS on in the SMTP settings.");
            }
            var (username, password) = await resolveCredential(config.SmtpCredentialKey.Value);
            if (!string.IsNullOrWhiteSpace(username))
            {
                await client.AuthenticateAsync(username, password);
            }
        }
        else if (oauthSettings is not null && token is not null && oauthSecret is not null)
        {
            // D-223: a bearer token is as good as a password while it lasts.
            if (!client.IsSecure)
            {
                throw new InvalidOperationException("OAuth2 sign-in needs STARTTLS: the SMTP connection isn't encrypted, so the access token was not sent. Turn STARTTLS on in the SMTP settings.");
            }
            try
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(oauthSettings.Mailbox, token.Value));
            }
            catch (AuthenticationException)
            {
                // Refresh once: the cached token may have been revoked, or the
                // permissions behind it changed since it was issued.
                token = await tokenService.GetTokenAsync(oauthSettings, oauthSecret, forceRefresh: true);
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(config.SmtpHost, config.SmtpPort, secureSocketOptions);
                }
                try
                {
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(oauthSettings.Mailbox, token.Value));
                }
                catch (AuthenticationException ex)
                {
                    throw new InvalidOperationException(
                        $"The SMTP server rejected the OAuth2 sign-in for '{oauthSettings.Mailbox}', with a newly issued token too: {ex.Message} {SignInHint(config.AuthMethod)}", ex);
                }
            }
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(quit: true);
    }

    /// <summary>What to check when a valid token is still refused (Design_Notifications.md, D-223 prerequisites).</summary>
    private static string SignInHint(string authMethod) => authMethod switch
    {
        SmtpAuthMethods.Microsoft365 => "Check that the app has the SMTP.SendAsApp permission with admin consent, its service principal is registered in Exchange Online (New-ServicePrincipal) with FullAccess to the mailbox (Add-MailboxPermission), and SMTP AUTH is enabled for the mailbox.",
        SmtpAuthMethods.GoogleWorkspace => "Check that the service account's client ID has domain-wide delegation for https://mail.google.com/ in the Admin console, and that the mailbox is a user in the domain.",
        _ => "Check that the token's scope and audience are ones the SMTP server accepts, and the XOAUTH2 username is the mailbox the token is for."
    };
}
