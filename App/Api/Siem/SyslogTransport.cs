using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace BlueTrack.Api.Siem;

/// <summary>D-234: sends syslog frames to the collector. An interface so tests can count connections or fail on demand.</summary>
public interface ISyslogTransport
{
    /// <summary>Opens a connection, writes every frame, closes it. Throws if any step fails, so the caller resends the batch.</summary>
    Task SendAsync(SiemForwardingSettings settings, IReadOnlyList<byte[]> frames, CancellationToken cancellationToken);
}

/// <summary>
/// D-234: TCP (RFC 6587) or TLS (RFC 5425). One connection per batch: connect, write the frames, then close
/// our side (TLS close_notify, then TCP shutdown) and wait briefly for the collector to close or reset. Syslog
/// has no acknowledgement, so this is the only sign that it took the data: a refused or reset connection
/// fails the batch, which is then sent again (the SIEM can drop repeats by externalId). A collector that just
/// keeps the connection open isn't a failure.
///
/// TLS: TLS 1.2 or later (the operating system's choice; RFC 5425 4.2 requires 1.2). The collector's
/// certificate is checked by the mode chosen: System (Windows trust store and the host name), Fingerprint
/// (RFC 5425 4.2.2: the certificate's own SHA-256 or SHA-1 fingerprint must match; for a self-signed
/// collector), or CaCertificate (the given CA is the only trust anchor; chain and host name checked).
/// Revocation is checked online unless turned off. There is no "ignore certificate errors" option.
/// </summary>
public sealed class SyslogTransport : ISyslogTransport
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    public async Task SendAsync(SiemForwardingSettings settings, IReadOnlyList<byte[]> frames, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Host)) throw new InvalidOperationException("No collector host is set.");
        var timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);

        using var client = new TcpClient();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(settings.Host, settings.Port, connectTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Connecting to {settings.Host}:{settings.Port} took longer than {settings.TimeoutSeconds} seconds.");
            }
        }

        client.NoDelay = true;
        Stream stream = client.GetStream();
        SslStream? ssl = null;
        try
        {
            if (settings.Transport == SiemTransports.Tls)
            {
                ssl = new SslStream(stream, leaveInnerStreamOpen: true);
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeTimeout.CancelAfter(timeout);
                await ssl.AuthenticateAsClientAsync(ClientOptions(settings), handshakeTimeout.Token);
                stream = ssl;
            }

            using (var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                writeTimeout.CancelAfter(timeout);
                foreach (var frame in frames)
                {
                    await stream.WriteAsync(frame, writeTimeout.Token);
                }
                await stream.FlushAsync(writeTimeout.Token);
            }

            if (ssl is not null) await ssl.ShutdownAsync();
            client.Client.Shutdown(SocketShutdown.Send);
            await WaitForCloseAsync(client.Client, cancellationToken);
        }
        finally
        {
            if (ssl is not null) await ssl.DisposeAsync();
        }
    }

    /// <summary>Reads (and ignores) anything until the collector closes or CloseWait passes; a reset throws.</summary>
    private static async Task WaitForCloseAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(CloseWait);
        var buffer = new byte[256];
        try
        {
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, wait.Token) > 0)
            {
                // A collector doesn't normally answer; discard anything it sends.
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Still open after CloseWait: the collector kept the connection. Not a failure.
        }
    }

    public static SslClientAuthenticationOptions ClientOptions(SiemForwardingSettings settings) => new()
    {
        TargetHost = settings.Host,
        EnabledSslProtocols = SslProtocols.None,   // the operating system's default (TLS 1.2 / 1.3)
        CertificateRevocationCheckMode = settings.TlsCheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = (_, certificate, chain, errors) => ValidateServerCertificate(settings, certificate, errors)
    };

    /// <summary>The certificate check for each trust mode. Public for tests.</summary>
    public static bool ValidateServerCertificate(SiemForwardingSettings settings, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        switch (settings.TlsTrustMode)
        {
            case SiemTlsTrustModes.Fingerprint:
            {
                var pinned = SiemForwardingValidation.NormalizeFingerprint(settings.TlsFingerprint);
                if (pinned is null) return false;
                var algorithm = pinned[..pinned.IndexOf(':')];
                return string.Equals(SiemForwardingValidation.Fingerprint(certificate, algorithm), pinned, StringComparison.Ordinal);
            }
            case SiemTlsTrustModes.CaCertificate:
            {
                if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var ca = settings.TlsCaCertificate is null ? null : SiemForwardingValidation.TryLoadCertificate(settings.TlsCaCertificate);
                if (ca is null) return false;
                using var serverCertificate = new X509Certificate2(certificate);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = settings.TlsCheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                if (!chain.Build(serverCertificate)) return false;
                // The chain must end at the given CA itself.
                var root = chain.ChainElements[^1].Certificate;
                return root.RawData.AsSpan().SequenceEqual(ca.RawData);
            }
            default:
                return errors == SslPolicyErrors.None;
        }
    }
}
