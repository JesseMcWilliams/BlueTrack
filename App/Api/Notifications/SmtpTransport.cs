using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-223: the few MailKit SmtpClient calls SmtpNotificationSender makes,
/// behind an interface so tests can check which sign-in each mode uses
/// without a real SMTP server.
/// </summary>
public interface ISmtpTransport : IDisposable
{
    bool IsConnected { get; }
    bool IsSecure { get; }
    Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default);

    /// <summary>Username and password: MailKit picks the strongest mechanism the server offers (PLAIN/LOGIN/NTLM/...; D-116).</summary>
    Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>A specific SASL mechanism (D-223: SaslMechanismOAuth2, i.e. XOAUTH2).</summary>
    Task AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default);

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default);
    Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default);
}

/// <summary>Creates a transport per send, with the TLS overrides (D-116 follow-up) applied.</summary>
public interface ISmtpTransportFactory
{
    ISmtpTransport Create(bool ignoreCrlErrors, bool ignoreSslErrors);
}

public sealed class MailKitSmtpTransportFactory : ISmtpTransportFactory
{
    public ISmtpTransport Create(bool ignoreCrlErrors, bool ignoreSslErrors)
    {
        var client = new SmtpClient { CheckCertificateRevocation = !ignoreCrlErrors };
        if (ignoreSslErrors)
        {
            // Broader than IgnoreCrlErrors -- skips hostname/chain/expiry
            // validation entirely, not just the CRL/OCSP lookup. Off by
            // default; an admin opts in per-environment after seeing a real
            // TLS failure, same as IgnoreCrlErrors.
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
        return new MailKitSmtpTransport(client);
    }

    private sealed class MailKitSmtpTransport(SmtpClient client) : ISmtpTransport
    {
        public bool IsConnected => client.IsConnected;
        public bool IsSecure => client.IsSecure;
        public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default) =>
            client.ConnectAsync(host, port, options, cancellationToken);
        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default) =>
            client.AuthenticateAsync(userName, password, cancellationToken);
        public Task AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default) =>
            client.AuthenticateAsync(mechanism, cancellationToken);
        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default) =>
            client.SendAsync(message, cancellationToken);
        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default) =>
            client.DisconnectAsync(quit, cancellationToken);
        public void Dispose() => client.Dispose();
    }
}
